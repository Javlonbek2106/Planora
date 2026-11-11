using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingDraft.Application;
using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BuildingDraft.Api.Tests;

// Runs the real HTTP pipeline (including the background export worker) against a SQLite file. A file, not a shared
// in-memory connection: requests and the worker run on different threads and each needs its own connection.
public class ApiFactory : WebApplicationFactory<Program>
{
    readonly string databasePath = Path.Combine(Path.GetTempPath(), $"building-draft-{Guid.NewGuid():N}.db");

    public Guid UserId { get; set; } = Guid.NewGuid();

    sealed class FixedUser(ApiFactory factory) : ICurrentUser
    {
        public Guid UserId => factory.UserId;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<BuildingDraftDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<BuildingDraftDbContext>>();
            services.AddDbContext<BuildingDraftDbContext>(o => o.UseSqlite($"Data Source={databasePath}").UseSnakeCaseNamingConvention());
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<IFileStorage, InMemoryFileStorage>();
            services.RemoveAll<ICurrentUser>();
            services.AddScoped<ICurrentUser>(_ => new FixedUser(this));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BuildingDraftDbContext>();
        db.Database.EnsureCreated();
        // WAL lets readers and the background worker's writes overlap; the default journal makes SQLite answer
        // "database is locked" immediately when they collide.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;
        SqliteConnection.ClearAllPools();
        File.Delete(databasePath);
    }
}

sealed class InMemoryFileStorage : IFileStorage
{
    readonly Dictionary<string, byte[]> files = [];

    public Task SaveAsync(string key, byte[] content, CancellationToken ct)
    {
        lock (files)
            files[key] = content;
        return Task.CompletedTask;
    }

    public Task<byte[]?> ReadAsync(string key, CancellationToken ct)
    {
        lock (files)
            return Task.FromResult(files.GetValueOrDefault(key));
    }
}

// Thin client that speaks the API's JSON dialect and hides the repeated plumbing.
public sealed class ApiClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public Task<HttpResponseMessage> Post(string url, object? body = null) => http.PostAsJsonAsync(url, body ?? new { }, Json);
    public Task<HttpResponseMessage> Put(string url, object body) => http.PutAsJsonAsync(url, body, Json);
    public Task<HttpResponseMessage> Get(string url) => http.GetAsync(url);
    public Task<HttpResponseMessage> Delete(string url) => http.DeleteAsync(url);

    public static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public async Task<(Guid ProjectId, Guid VariantId, Guid FloorId)> CreateProjectWithFloor(string name = "Flat 12")
    {
        var project = await Body(await Post("/api/v1/projects", new { name, address = "Tashkent" }));
        var projectId = project.GetProperty("id").GetGuid();
        var variantId = project.GetProperty("variants")[0].GetProperty("id").GetGuid();
        var floor = await Body(await Post($"/api/v1/projects/{projectId}/floors", new { floorNumber = 1 }));
        return (projectId, variantId, floor.GetProperty("id").GetGuid());
    }

    public Task<HttpResponseMessage> Command(Guid variantId, int expectedVersion, string type, object payload, Guid? operationId = null) =>
        Post($"/api/v1/variants/{variantId}/geometry/commands", new
        {
            clientOperationId = operationId ?? Guid.NewGuid(),
            expectedVersion,
            commandType = type,
            payload
        });

    // Draws a closed rectangle of four walls plus a room; returns the final version number.
    public async Task<int> DrawRoom(Guid variantId, Guid floorId, double width, double depth, string name = "Living room", string areaType = "enclosed", double originX = 0)
    {
        var nodes = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var walls = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        double[][] corners = [[originX, 0], [originX + width, 0], [originX + width, depth], [originX, depth]];
        var version = await Version(variantId);

        for (var i = 0; i < 4; i++)
        {
            var next = (i + 1) % 4;
            var response = await Command(variantId, version, "create_wall", new
            {
                wallId = walls[i], floorId,
                startNodeId = nodes[i], startXMm = corners[i][0], startYMm = corners[i][1],
                endNodeId = nodes[next], endXMm = corners[next][0], endYMm = corners[next][1],
                thicknessMm = 200
            });
            version = (await Body(response)).GetProperty("version").GetInt32();
        }

        var room = await Command(variantId, version, "create_room", new { roomId = Guid.NewGuid(), floorId, name, wallIds = walls, areaType });
        return (await Body(room)).GetProperty("version").GetInt32();
    }

    public async Task<int> Version(Guid variantId) =>
        (await Body(await Get($"/api/v1/variants/{variantId}/geometry"))).GetProperty("version").GetInt32();
}
