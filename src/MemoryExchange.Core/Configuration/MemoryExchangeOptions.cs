namespace MemoryExchange.Core.Configuration;

/// <summary>
/// Root configuration for the Memory Exchange system.
/// Bound from appsettings.json section "MemoryExchange".
/// Provider-specific options (Azure, Local) live in their respective provider projects.
/// </summary>
public class MemoryExchangeOptions
{
    public const string SectionName = "MemoryExchange";

    /// <summary>
    /// Root path to the memory exchange markdown files.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Logical name of the search index (e.g., "memory-exchange").
    /// Used by all providers to identify the index/database.
    /// </summary>
    public string IndexName { get; set; } = "memory-exchange";

    /// <summary>
    /// Which search/embedding provider to use.
    /// </summary>
    public ProviderType Provider { get; set; } = ProviderType.Local;

    /// <summary>
    /// Optional glob patterns to exclude files/directories from indexing.
    /// The "personal/" directory is always excluded regardless of this setting.
    /// Examples: "**/archive/**", "**/drafts/**", "temp-*.md"
    /// </summary>
    public List<string> ExcludePatterns { get; set; } = [];
}

/// <summary>
/// Selects which search and embedding provider implementation to use.
/// </summary>
public enum ProviderType
{
    /// <summary>
    /// Fully local provider using SQLite + sqlite-vec + ONNX embeddings.
    /// No cloud dependencies.
    /// </summary>
    Local,

    /// <summary>
    /// Cloud provider using Azure AI Search + Azure OpenAI embeddings.
    /// </summary>
    Azure
}
