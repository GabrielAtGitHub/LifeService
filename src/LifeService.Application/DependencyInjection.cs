using LifeService.Domain.Abstractions;
using LifeService.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LifeService.Application;

/// <summary>
/// Registers application use-case services (and the infrastructure they depend on).
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddLifeApplication(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLifeInfrastructure(configuration);

        // Scoped so it can depend on a (possibly scoped) storage provider such as EF Core.
        services.AddScoped<ILifeComputeService, LifeComputeService>();
        return services;
    }
}
