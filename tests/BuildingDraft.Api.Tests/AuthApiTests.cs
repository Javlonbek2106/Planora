using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BuildingDraft.Api.Tests;

// The real authentication pipeline (Jwt mode, shared-secret signing). It uses a SQLite file, not a shared in-memory
// connection, so concurrent requests get their own connections just like they do with PostgreSQL.
public sealed class JwtApiFactory : WebApplicationFactory<Program>
{
    public const string Key = "test-signing-key-that-is-at-least-32-bytes-long!";
    public const string Audience = "building-draft-api";
    public const string Issuer = "https://issuer.test";

    readonly string databasePath = Path.Combine(Path.GetTempPath(), $"building-draft-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Mode", "Jwt");
        builder.UseSetting("Auth:SigningKey", Key);
        builder.UseSetting("Auth:Audience", Audience);
        builder.UseSetting("Auth:Issuer", Issuer);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<BuildingDraftDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<BuildingDraftDbContext>>();
            services.AddDbContext<BuildingDraftDbContext>(o => o.UseSqlite($"Data Source={databasePath}").UseSnakeCaseNamingConvention());
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<IFileStorage, InMemoryFileStorage>();
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

    public static string Token(string? subject, string? email = null, TimeSpan? lifetime = null, string key = Key, string audience = Audience)
    {
        var claims = new Dictionary<string, object>();
        if (subject is not null)
            claims["sub"] = subject;
        if (email is not null)
            claims["email"] = email;

        var expired = lifetime is { } l && l < TimeSpan.Zero;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            Issuer = Issuer,
            Audience = audience,
            NotBefore = expired ? DateTime.UtcNow.AddHours(-2) : null,
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)),
            SigningCredentials = new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
        });
    }

    public HttpClient ClientFor(string? token)
    {
        var client = CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

public class AuthApiTests : IClassFixture<JwtApiFactory>
{
    readonly JwtApiFactory factory;

    public AuthApiTests(JwtApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Requests_without_a_token_are_unauthorized()
    {
        var response = await factory.ClientFor(null).GetAsync("/api/v1/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("wrong-key")]
    [InlineData("expired")]
    [InlineData("wrong-audience")]
    public async Task Invalid_tokens_are_unauthorized(string kind)
    {
        var token = kind switch
        {
            "garbage" => "not.a.jwt",
            "wrong-key" => JwtApiFactory.Token("alice", key: "another-signing-key-that-is-also-32-bytes!!"),
            "expired" => JwtApiFactory.Token("alice", lifetime: TimeSpan.FromMinutes(-5)),
            _ => JwtApiFactory.Token("alice", audience: "someone-else")
        };

        var response = await factory.ClientFor(token).GetAsync("/api/v1/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_without_a_subject_is_unauthorized()
    {
        var response = await factory.ClientFor(JwtApiFactory.Token(null)).GetAsync("/api/v1/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_works_and_each_subject_sees_only_its_own_data()
    {
        var alice = factory.ClientFor(JwtApiFactory.Token("alice", "alice@example.com"));
        var bob = factory.ClientFor(JwtApiFactory.Token("bob"));

        var created = await alice.PostAsJsonAsync("/api/v1/projects", new { name = "Alice flat" }, ApiClient.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ApiClient.Body(created)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/v1/projects/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/projects/{id}")).StatusCode);
        Assert.Equal(0, (await ApiClient.Body(await bob.GetAsync("/api/v1/projects"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await ApiClient.Body(await alice.GetAsync("/api/v1/projects"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_same_subject_is_always_the_same_user_even_for_simultaneous_first_requests()
    {
        var token = JwtApiFactory.Token("carol", "carol@example.com");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => factory.ClientFor(token).GetAsync("/api/v1/projects")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BuildingDraftDbContext>();
        var users = await db.Users.Where(u => u.ExternalSubject == "carol").ToListAsync();
        Assert.Single(users);
        Assert.Equal("carol@example.com", users[0].Email);
    }

    [Fact]
    public async Task A_deactivated_user_is_forbidden()
    {
        var client = factory.ClientFor(JwtApiFactory.Token("dave"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/projects")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BuildingDraftDbContext>();
            (await db.Users.SingleAsync(u => u.ExternalSubject == "dave")).Deactivate();
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/v1/projects");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("USER_INACTIVE", (await ApiClient.Body(response)).GetProperty("errorCode").GetString());
    }
}

public class AuthConfigurationTests
{
    [Fact]
    public void Dev_mode_refuses_to_start_outside_development()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseEnvironment("Production").UseSetting("Auth:Mode", "Dev"));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Development", ex.Message);
    }

    [Fact]
    public void Jwt_mode_without_any_key_or_authority_refuses_to_start()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseEnvironment("Production").UseSetting("Auth:Mode", "Jwt"));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Auth:Authority", ex.Message);
    }
}
