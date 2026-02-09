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
}
