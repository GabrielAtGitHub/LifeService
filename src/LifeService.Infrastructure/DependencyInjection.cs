using LifeService.Domain.Abstractions;
using LifeService.Domain.Diagnostics;
using LifeService.Infrastructure.Compute;
using LifeService.Infrastructure.Persistence;
using LifeService.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LifeService.Infrastructure;

/// <summary>
/// Registers infrastructure services (compute engine, storage, metrics) into the DI container.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddLifeInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Metrics meter is process-wide; register as a singleton.
        services.AddSingleton<LifeMetrics>();

        // The compute engine is stateless; safe as a singleton.
        services.AddSingleton<ILifeComputeProvider, LifeComputeProvider>();

        AddStorage(services, configuration);

        return services;
    }

    /// <summary>
    /// Selects the storage provider from "Life:Storage:Provider" ("InMemory" | "Sqlite").
    /// In-memory is the default; SQLite uses the "Life:Storage:SqliteConnectionString" value.
    /// </summary>
    private static void AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Life:Storage:Provider"] ?? "InMemory";

        if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration["Life:Storage:SqliteConnectionString"]
                ?? "Data Source=life.db";
            services.AddDbContext<LifeDbContext>(options => options.UseSqlite(connectionString));
            // Scoped: a DbContext (and thus the provider) lives for the duration of a request.
            services.AddScoped<ILifeStorageProvider, EfLifeStorageProvider>();
        }
        else
        {
            // Default development/test persistence. Singleton so state survives across requests.
            services.AddSingleton<ILifeStorageProvider, InMemoryLifeStorageProvider>();
        }
    }

    /// <summary>
    /// Ensures the relational schema exists when the SQLite provider is in use. No-op otherwise.
    /// </summary>
    public static async Task InitializeLifeStorageAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetService<LifeDbContext>();
        if (db is not null)
        {
            await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }
    }
}
