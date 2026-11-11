using BuildingDraft.Domain.Abstractions;
using BuildingDraft.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingDraft.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString) => services
        .AddDbContext<BuildingDraftDbContext>(o => o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention())
        .AddRepositories();

    public static IServiceCollection AddRepositories(this IServiceCollection services) => services
        .AddScoped<IProjectRepository, EfProjectRepository>()
        .AddScoped<IGeometryVersionRepository, EfGeometryVersionRepository>()
        .AddScoped<IGeometryOperationRepository, EfGeometryOperationRepository>()
        .AddScoped<IMeasurementRepository, EfMeasurementRepository>()
        .AddScoped<IUnitOfWork, EfUnitOfWork>();
}

// Lets `dotnet ef` create migrations without starting the API.
sealed class DesignTimeFactory : IDesignTimeDbContextFactory<BuildingDraftDbContext>
{
    public BuildingDraftDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<BuildingDraftDbContext>()
            .UseNpgsql("Host=localhost;Database=building_draft")
            .UseSnakeCaseNamingConvention()
            .Options);
}
