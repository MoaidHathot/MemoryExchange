using MemoryExchange.Core.Models;

namespace MemoryExchange.Core.Abstractions;

/// <summary>
/// Write-side interface for managing the search index (create, upsert, delete).
/// Implemented by provider-specific services (e.g., Azure AI Search, SQLite + sqlite-vec).
/// </summary>
public interface ISearchIndex
{
    /// <summary>
    /// Ensures the search index exists with the correct schema.
    /// Creates or updates the index as needed.
    /// </summary>
    Task EnsureIndexAsync();

    /// <summary>
    /// Upserts a batch of chunks into the search index.
    /// </summary>
    /// <param name="chunks">The chunks to upsert.</param>
    Task UpsertChunksAsync(IEnumerable<MemoryChunk> chunks);

    /// <summary>
    /// Deletes all chunks associated with a given source file from the index.
    /// </summary>
    /// <param name="sourceFile">Relative path of the source file whose chunks should be removed.</param>
    Task DeleteChunksForFileAsync(string sourceFile);

    /// <summary>
    /// Returns the total number of chunks in the index.
    /// Returns 0 if the index doesn't exist or is empty.
    /// </summary>
    int GetChunkCount() => 0;

    /// <summary>
    /// Returns the number of distinct source files in the index.
    /// Returns 0 if the index doesn't exist or is empty.
    /// </summary>
    int GetSourceFileCount() => 0;

    /// <summary>
    /// Returns the most recent last_updated timestamp from the index.
    /// Returns null if the index is empty.
    /// </summary>
    DateTimeOffset? GetLastIndexedTime() => null;
}
