using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingDraft.Api.Endpoints;
using BuildingDraft.Api.Infrastructure;
using BuildingDraft.Application;
using BuildingDraft.Infrastructure;
using BuildingDraft.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

// Logs: structured JSON on the console unless switched off (local development). Traces, metrics and logs are
// exported over OTLP only when an endpoint is configured (OTEL_EXPORTER_OTLP_ENDPOINT).
if (builder.Configuration.GetValue<bool>("Logging:Json"))
    builder.Logging.AddJsonConsole();
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
});

var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("building-draft-api"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddNpgsql())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddMeter(Telemetry.MeterName));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    telemetry.UseOtlpExporter();

builder.Services.AddHealthChecks().AddDbContextCheck<BuildingDraftDbContext>("database", tags: ["ready"]);
builder.Services.AddOpenApi();

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration.GetConnectionString("Default")
                       ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured."),
        Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Storage:Root"] ?? "storage"))
    .AddApiAuthentication(builder.Configuration, builder.Environment)
    .AddHostedService<ExportWorker>()
    .AddExceptionHandler<DomainExceptionHandler>()
    .AddProblemDetails();

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<BuildingDraftDbContext>().Database.Migrate();
}

app.UseExceptionHandler();

// Liveness says the process is up; readiness also checks the database. Neither needs a token.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

var api = app.MapGroup("/api/v1");
if (!app.Configuration.IsDevMode())
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<UserProvisioningMiddleware>();
    api.RequireAuthorization();
}

api.MapProjects();
api.MapVariants();
api.MapGeometry();
api.MapMeasurements();
api.MapSync();
api.MapExports();
api.MapScans();

app.Run();

public partial class Program;
