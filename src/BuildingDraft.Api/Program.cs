using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingDraft.Api.Endpoints;
using BuildingDraft.Api.Infrastructure;
using BuildingDraft.Application;
using BuildingDraft.Infrastructure;
using BuildingDraft.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration.GetConnectionString("Default")
                       ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured."))
    .AddScoped<ICurrentUser, DevCurrentUser>()
    .AddExceptionHandler<DomainExceptionHandler>()
    .AddProblemDetails();

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<BuildingDraftDbContext>().Database.Migrate();
}

app.UseExceptionHandler();

var api = app.MapGroup("/api/v1");
api.MapProjects();
api.MapVariants();
api.MapGeometry();
api.MapMeasurements();
api.MapSync();

app.Run();

public partial class Program;
