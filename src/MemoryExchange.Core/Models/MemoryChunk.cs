namespace MemoryExchange.Core.Models;

/// <summary>
/// Represents a single chunk of memory exchange content, ready for embedding and indexing.
/// This is a provider-agnostic domain model — search-provider-specific attributes
/// (e.g., Azure AI Search field annotations) belong in the provider project's own DTO.
/// </summary>
public class MemoryChunk
{
    /// <summary>
    /// Deterministic ID derived from source file path + chunk index.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The actual text content of the chunk.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Relative path of the source markdown file (e.g., "da/dataAccessPatterns.md").
    /// </summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>
    /// Hierarchical heading path (e.g., "Architecture > Database > Connection Pooling").
    /// </summary>
    public string HeadingPath { get; set; } = string.Empty;

    /// <summary>
    /// Domain identifier (e.g., "da", "rp", "deploy", "ccg").
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Extracted tags: class names, service names, file paths mentioned in the chunk.
    /// </summary>
    public IList<string> Tags { get; set; } = new List<string>();

    /// <summary>
    /// Cross-referenced markdown files (relative paths of linked files).
    /// </summary>
    public IList<string> RelatedFiles { get; set; } = new List<string>();

    /// <summary>
    /// Whether this chunk comes from an .instructions.md file (higher priority content).
    /// </summary>
    public bool IsInstruction { get; set; }

    /// <summary>
    /// Vector embedding of the Content field. Dimensions depend on the embedding provider
    /// (e.g., 1536 for text-embedding-3-small, 384 for all-MiniLM-L6-v2).
    /// </summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// Timestamp of when this chunk was last indexed.
    /// </summary>
    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>
    /// Zero-based index of this chunk within its source file.
    /// </summary>
    public int ChunkIndex { get; set; }
}
