using MemoryExchange.Azure.Configuration;
using MemoryExchange.Azure.Services;
using MemoryExchange.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryExchange.Azure;

/// <summary>
/// Registers Azure provider services (embedding, search index, search service) into the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Azure AI Search + Azure OpenAI as the memory exchange provider.
    /// Expects "MemoryExchange:AzureSearch" and "MemoryExchange:AzureOpenAI" configuration sections.
    /// </summary>
    public static IServiceCollection AddAzureMemoryExchange(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AzureSearchOptions>(configuration.GetSection(AzureSearchOptions.SectionName));
        services.Configure<AzureOpenAIOptions>(configuration.GetSection(AzureOpenAIOptions.SectionName));

        services.AddSingleton<IEmbeddingService, AzureEmbeddingService>();
        services.AddSingleton<ISearchIndex, AzureSearchIndex>();
        services.AddSingleton<ISearchService, AzureSearchService>();

        return services;
    }
}
