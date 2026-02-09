using System.Text.Json;
using MemoryExchange.Core.Abstractions;
using MemoryExchange.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MemoryExchange.Local.Services;

/// <summary>
/// Read-side search implementation using SQLite FTS5 for keyword search and
/// in-memory cosine similarity for vector search, merged via Reciprocal Rank Fusion (RRF).
/// </summary>
public sealed class SqliteSearchService : ISearchService
{
    /// <summary>
    /// RRF constant k — controls how much lower-ranked results contribute to the fused score.
    /// </summary>
    private const int RrfK = 60;

    private readonly SqliteSearchIndex _index;
    private readonly ILogger<SqliteSearchService> _logger;

    public SqliteSearchService(SqliteSearchIndex index, ILogger<SqliteSearchService> logger)
    {
        _index = index;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<SearchHit>> SearchAsync(string query, float[] queryEmbedding, int topK)
    {
        var conn = _index.GetConnection();

        // Run FTS5 keyword search and vector similarity in parallel concept
        // (both use the same connection so we do them sequentially, but each is fast)
        var ftsResults = await FtsSearchAsync(conn, query, topK * 3);
        var vectorResults = await VectorSearchAsync(conn, queryEmbedding, topK * 3);

        // Merge via RRF
        var merged = ReciprocalRankFusion(ftsResults, vectorResults);

        // Take top K
        var results = merged
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();

        _logger.LogDebug("Search returned {Count} results (FTS: {FtsCount}, Vector: {VectorCount})",
            results.Count, ftsResults.Count, vectorResults.Count);

        return results;
    }

    /// <summary>
    /// Performs FTS5 BM25 keyword search.
    /// </summary>
    private async Task<List<RankedResult>> FtsSearchAsync(SqliteConnection conn, string query, int limit)
    {
        var results = new List<RankedResult>();

        // Escape FTS5 special characters and wrap in quotes for phrase matching fallback
        var sanitizedQuery = SanitizeFtsQuery(query);
        if (string.IsNullOrWhiteSpace(sanitizedQuery)) return results;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.*, bm25(chunks_fts) as rank
            FROM chunks_fts fts
            JOIN chunks c ON c.id = fts.id
            WHERE chunks_fts MATCH @query
            ORDER BY rank
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", sanitizedQuery);
        cmd.Parameters.AddWithValue("@limit", limit);

        try
        {
            using var reader = await cmd.ExecuteReaderAsync();
            int rank = 0;
            while (await reader.ReadAsync())
            {
                var chunk = ReadChunk(reader);
                var bm25Score = reader.GetDouble(reader.GetOrdinal("rank"));
                results.Add(new RankedResult(chunk, rank++, -bm25Score)); // BM25 returns negative scores
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // FTS5 query syntax error — try simpler query
            _logger.LogDebug("FTS5 query failed, falling back to simple search: {Error}", ex.Message);
            results = await SimpleFtsSearchAsync(conn, query, limit);
        }

        return results;
    }

    /// <summary>
    /// Fallback simple search when FTS5 query syntax is invalid.
    /// </summary>
    private async Task<List<RankedResult>> SimpleFtsSearchAsync(SqliteConnection conn, string query, int limit)
    {
        var results = new List<RankedResult>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.*
            FROM chunks c
            WHERE c.content LIKE @query
            ORDER BY c.last_updated DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", $"%{query}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        int rank = 0;
        while (await reader.ReadAsync())
        {
            var chunk = ReadChunk(reader);
            results.Add(new RankedResult(chunk, rank++, 1.0));
        }

        return results;
    }

    /// <summary>
    /// Performs vector similarity search by loading all embeddings and computing cosine similarity in memory.
    /// Efficient for memory exchange sizes (typically &lt;5000 chunks).
    /// </summary>
    private async Task<List<RankedResult>> VectorSearchAsync(SqliteConnection conn, float[] queryEmbedding, int limit)
    {
        var results = new List<RankedResult>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, source_file, heading_path, domain, tags, related_files, 
                   is_instruction, embedding, last_updated, chunk_index
            FROM chunks
            WHERE embedding IS NOT NULL
            """;

        var candidates = new List<(MemoryChunk Chunk, float Similarity)>();

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var chunk = ReadChunk(reader);
            var embeddingOrdinal = reader.GetOrdinal("embedding");
            if (reader.IsDBNull(embeddingOrdinal)) continue;

            var blob = (byte[])reader.GetValue(embeddingOrdinal);
            var embedding = SqliteSearchIndex.BlobToEmbedding(blob);
            var similarity = CosineSimilarity(queryEmbedding, embedding);
            candidates.Add((chunk, similarity));
        }

        // Sort by similarity descending, take top limit
        candidates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        for (int i = 0; i < Math.Min(limit, candidates.Count); i++)
        {
            results.Add(new RankedResult(candidates[i].Chunk, i, candidates[i].Similarity));
        }

        return results;
    }

    /// <summary>
    /// Merges two ranked result lists using Reciprocal Rank Fusion.
    /// RRF score = Σ 1/(k + rank_i) for each ranking system.
    /// </summary>
    private static List<SearchHit> ReciprocalRankFusion(List<RankedResult> ftsResults, List<RankedResult> vectorResults)
    {
        var scores = new Dictionary<string, (MemoryChunk Chunk, double Score)>();

        foreach (var result in ftsResults)
        {
            var rrfScore = 1.0 / (RrfK + result.Rank);
            if (scores.TryGetValue(result.Chunk.Id, out var existing))
            {
                scores[result.Chunk.Id] = (existing.Chunk, existing.Score + rrfScore);
            }
            else
            {
                scores[result.Chunk.Id] = (result.Chunk, rrfScore);
            }
        }

        foreach (var result in vectorResults)
        {
            var rrfScore = 1.0 / (RrfK + result.Rank);
            if (scores.TryGetValue(result.Chunk.Id, out var existing))
            {
                scores[result.Chunk.Id] = (existing.Chunk, existing.Score + rrfScore);
            }
            else
            {
                scores[result.Chunk.Id] = (result.Chunk, rrfScore);
            }
        }

        return scores.Values
            .Select(s => new SearchHit(s.Chunk, s.Score))
            .ToList();
    }

    /// <summary>
    /// Computes cosine similarity between two normalized vectors.
    /// Since embeddings are L2-normalized, this is just the dot product.
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        var minLen = Math.Min(a.Length, b.Length);
        float dot = 0;
        for (int i = 0; i < minLen; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
    }

    /// <summary>
    /// Reads a MemoryChunk from a SqliteDataReader.
    /// </summary>
    private static MemoryChunk ReadChunk(SqliteDataReader reader)
    {
        return new MemoryChunk
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Content = reader.GetString(reader.GetOrdinal("content")),
            SourceFile = reader.GetString(reader.GetOrdinal("source_file")),
            HeadingPath = reader.GetString(reader.GetOrdinal("heading_path")),
            Domain = reader.GetString(reader.GetOrdinal("domain")),
            Tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("tags"))) ?? new List<string>(),
            RelatedFiles = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("related_files"))) ?? new List<string>(),
            IsInstruction = reader.GetInt32(reader.GetOrdinal("is_instruction")) == 1,
            LastUpdated = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_updated"))),
            ChunkIndex = reader.GetInt32(reader.GetOrdinal("chunk_index"))
        };
    }

    /// <summary>
    /// Sanitizes a query string for FTS5 by removing special characters.
    /// Splits into individual terms for OR matching.
    /// </summary>
    private static string SanitizeFtsQuery(string query)
    {
        // Remove FTS5 special characters
        var sanitized = query
            .Replace("\"", "")
            .Replace("*", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace(":", "")
            .Replace("^", "")
            .Replace("{", "")
            .Replace("}", "")
            .Replace("~", "");

        // Split into words and join with OR for broader matching
        var words = sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return string.Empty;

        // Use OR to combine terms for broader recall
        return string.Join(" OR ", words.Select(w => $"\"{w}\""));
    }

    /// <summary>
    /// Internal ranked result used during RRF merge.
    /// </summary>
    private record RankedResult(MemoryChunk Chunk, int Rank, double RawScore);
}
