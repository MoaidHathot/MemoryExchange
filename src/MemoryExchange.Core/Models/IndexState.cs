using System.Text.Json.Serialization;

namespace MemoryExchange.Core.Models;

/// <summary>
/// Tracks the state of indexed files for incremental indexing.
/// Serialized to .memory-exchange-state.json alongside the source directory.
/// </summary>
public class IndexState
{
    /// <summary>
    /// Map of relative file path to its last-known SHA256 hash.
    /// </summary>
    [JsonPropertyName("fileHashes")]
    public Dictionary<string, string> FileHashes { get; set; } = new();

    /// <summary>
    /// Timestamp of the last full indexing run.
    /// </summary>
    [JsonPropertyName("lastFullIndexUtc")]
    public DateTimeOffset? LastFullIndexUtc { get; set; }

    /// <summary>
    /// Timestamp of the last incremental indexing run.
    /// </summary>
    [JsonPropertyName("lastIncrementalIndexUtc")]
    public DateTimeOffset? LastIncrementalIndexUtc { get; set; }

    /// <summary>
    /// The name of the search index these files were indexed into.
    /// </summary>
    [JsonPropertyName("indexName")]
    public string IndexName { get; set; } = string.Empty;
}
