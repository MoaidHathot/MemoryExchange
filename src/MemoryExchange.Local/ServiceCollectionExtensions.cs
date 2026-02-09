using MemoryExchange.Core.Abstractions;
using MemoryExchange.Local.Configuration;
using MemoryExchange.Local.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryExchange.Local;

/// <summary>
/// Registers local SQLite + ONNX provider services into the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds local SQLite + ONNX as the memory exchange provider.
    /// Expects "MemoryExchange:Local" configuration section (optional — has sensible defaults).
    /// </summary>
    public static IServiceCollection AddLocalMemoryExchange(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LocalProviderOptions>(configuration.GetSection(LocalProviderOptions.SectionName));

        // SqliteSearchIndex is registered as concrete type too, because SqliteSearchService needs
        // direct access to the connection via the concrete type.
        services.AddSingleton<SqliteSearchIndex>();
        services.AddSingleton<ISearchIndex>(sp => sp.GetRequiredService<SqliteSearchIndex>());
        services.AddSingleton<ISearchService, SqliteSearchService>();
        services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();

        return services;
    }
}
