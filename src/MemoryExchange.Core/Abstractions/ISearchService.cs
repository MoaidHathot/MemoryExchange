namespace MemoryExchange.Core.Abstractions;

/// <summary>
/// Read-side interface for querying the search index.
/// Returns raw search results without application-level boosting or formatting —
/// that logic lives in <see cref="MemoryExchange.Core.Search.SearchOrchestrator"/>.
/// Implemented by provider-specific services (e.g., Azure AI Search hybrid, SQLite FTS5 + sqlite-vec RRF).
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Performs a hybrid search (vector + keyword) against the index.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <param name="queryEmbedding">Pre-computed embedding vector for the query.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <returns>Raw search hits ordered by provider-native relevance score.</returns>
    Task<List<SearchHit>> SearchAsync(string query, float[] queryEmbedding, int topK);
}
