using BuildingDraft.Domain.Export;
using BuildingDraft.Domain.Geometry.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingDraft.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Every exporter in the domain assembly is registered, so a new format needs no wiring.
        foreach (var type in typeof(IGeometryExporter).Assembly.GetTypes()
                     .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IGeometryExporter).IsAssignableFrom(t)))
            services.AddSingleton(typeof(IGeometryExporter), type);

        return services
        .AddSingleton(TimeProvider.System)
        .AddSingleton(GeometryProcessor.Default)
        .AddScoped<ProjectService>()
        .AddScoped<VariantService>()
        .AddScoped<GeometryVersionWriter>()
        .AddScoped<GeometryService>()
        .AddScoped<MeasurementService>()
        .AddScoped<SyncService>()
        .AddScoped<ExportService>()
        .AddScoped<ScanService>()
        .AddScoped<UserService>();
    }
}
