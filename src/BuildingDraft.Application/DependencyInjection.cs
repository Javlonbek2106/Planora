using BuildingDraft.Domain.Geometry.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingDraft.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services) => services
        .AddSingleton(TimeProvider.System)
        .AddSingleton(GeometryProcessor.Default)
        .AddScoped<ProjectService>()
        .AddScoped<VariantService>()
        .AddScoped<GeometryService>();
}
