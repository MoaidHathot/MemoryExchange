namespace MemoryExchange.Azure.Configuration;

/// <summary>
/// Azure AI Search connection configuration.
/// Bound from appsettings.json section "MemoryExchange:AzureSearch".
/// </summary>
public class AzureSearchOptions
{
    public const string SectionName = "MemoryExchange:AzureSearch";

    /// <summary>
    /// Azure AI Search service endpoint (e.g., "https://my-search.search.windows.net").
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Admin API key for Azure AI Search.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Name of the search index (e.g., "memory-exchange").
    /// </summary>
    public string IndexName { get; set; } = "memory-exchange";
}
