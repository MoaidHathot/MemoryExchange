namespace MemoryExchange.Azure.Configuration;

/// <summary>
/// Azure OpenAI connection configuration for embeddings.
/// Bound from appsettings.json section "MemoryExchange:AzureOpenAI".
/// </summary>
public class AzureOpenAIOptions
{
    public const string SectionName = "MemoryExchange:AzureOpenAI";

    /// <summary>
    /// Azure OpenAI endpoint (e.g., "https://my-openai.openai.azure.com").
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// API key for Azure OpenAI.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Deployment name for the embedding model (e.g., "text-embedding-3-small").
    /// </summary>
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// Dimensions of the embedding vector (1536 for text-embedding-3-small).
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 1536;
}
