using System.Text.Json;
using MemoryExchange.Core.Abstractions;
using MemoryExchange.Core.Models;
using MemoryExchange.Local.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryExchange.Local.Services;

/// <summary>
/// Write-side search index implementation using SQLite with FTS5 for full-text search.
/// Embeddings are stored as BLOBs for later in-memory cosine similarity computation.
/// </summary>
public sealed class SqliteSearchIndex : ISearchIndex, IDisposable
{
    private readonly LocalProviderOptions _options;
    private readonly ILogger<SqliteSearchIndex> _logger;
    private SqliteConnection? _connection;

    public SqliteSearchIndex(IOptions<LocalProviderOptions> options, ILogger<SqliteSearchIndex> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EnsureIndexAsync()
    {
        var conn = GetConnection();

        // Main chunks table
        using var createTable = conn.CreateCommand();
        createTable.CommandText = """
            CREATE TABLE IF NOT EXISTS chunks (
                id TEXT PRIMARY KEY,
                content TEXT NOT NULL,
                source_file TEXT NOT NULL,
                heading_path TEXT NOT NULL DEFAULT '',
                domain TEXT NOT NULL DEFAULT '',
                tags TEXT NOT NULL DEFAULT '[]',
                related_files TEXT NOT NULL DEFAULT '[]',
                is_instruction INTEGER NOT NULL DEFAULT 0,
                embedding BLOB,
                last_updated TEXT NOT NULL,
                chunk_index INTEGER NOT NULL DEFAULT 0
            );
            """;
        await createTable.ExecuteNonQueryAsync();

        // Index on source_file for delete operations
        using var createIdx = conn.CreateCommand();
        createIdx.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_chunks_source_file ON chunks(source_file);
            """;
        await createIdx.ExecuteNonQueryAsync();

        // FTS5 virtual table for keyword search
        using var createFts = conn.CreateCommand();
        createFts.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
                id UNINDEXED,
                content,
                heading_path,
                domain,
                tags,
                content=chunks,
                content_rowid=rowid
            );
            """;
        await createFts.ExecuteNonQueryAsync();

        // Triggers to keep FTS5 in sync with chunks table
        using var createTriggers = conn.CreateCommand();
        createTriggers.CommandText = """
            CREATE TRIGGER IF NOT EXISTS chunks_ai AFTER INSERT ON chunks BEGIN
                INSERT INTO chunks_fts(rowid, id, content, heading_path, domain, tags)
                VALUES (new.rowid, new.id, new.content, new.heading_path, new.domain, new.tags);
            END;

            CREATE TRIGGER IF NOT EXISTS chunks_ad AFTER DELETE ON chunks BEGIN
                INSERT INTO chunks_fts(chunks_fts, rowid, id, content, heading_path, domain, tags)
                VALUES ('delete', old.rowid, old.id, old.content, old.heading_path, old.domain, old.tags);
            END;

            CREATE TRIGGER IF NOT EXISTS chunks_au AFTER UPDATE ON chunks BEGIN
                INSERT INTO chunks_fts(chunks_fts, rowid, id, content, heading_path, domain, tags)
                VALUES ('delete', old.rowid, old.id, old.content, old.heading_path, old.domain, old.tags);
                INSERT INTO chunks_fts(rowid, id, content, heading_path, domain, tags)
                VALUES (new.rowid, new.id, new.content, new.heading_path, new.domain, new.tags);
            END;
            """;
        await createTriggers.ExecuteNonQueryAsync();

        _logger.LogInformation("SQLite search index ensured at {DatabasePath}", _options.DatabasePath);
    }

    /// <inheritdoc />
    public async Task UpsertChunksAsync(IEnumerable<MemoryChunk> chunks)
    {
        var conn = GetConnection();
        using var transaction = conn.BeginTransaction();

        try
        {
            foreach (var chunk in chunks)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO chunks 
                        (id, content, source_file, heading_path, domain, tags, related_files, is_instruction, embedding, last_updated, chunk_index)
                    VALUES
                        (@id, @content, @sourceFile, @headingPath, @domain, @tags, @relatedFiles, @isInstruction, @embedding, @lastUpdated, @chunkIndex)
                    """;

                cmd.Parameters.AddWithValue("@id", chunk.Id);
                cmd.Parameters.AddWithValue("@content", chunk.Content);
                cmd.Parameters.AddWithValue("@sourceFile", chunk.SourceFile);
                cmd.Parameters.AddWithValue("@headingPath", chunk.HeadingPath);
                cmd.Parameters.AddWithValue("@domain", chunk.Domain);
                cmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(chunk.Tags));
                cmd.Parameters.AddWithValue("@relatedFiles", JsonSerializer.Serialize(chunk.RelatedFiles));
                cmd.Parameters.AddWithValue("@isInstruction", chunk.IsInstruction ? 1 : 0);
                cmd.Parameters.AddWithValue("@embedding", chunk.Embedding != null ? EmbeddingToBlob(chunk.Embedding) : DBNull.Value);
                cmd.Parameters.AddWithValue("@lastUpdated", chunk.LastUpdated.ToString("o"));
                cmd.Parameters.AddWithValue("@chunkIndex", chunk.ChunkIndex);

                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeleteChunksForFileAsync(string sourceFile)
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chunks WHERE source_file = @sourceFile";
        cmd.Parameters.AddWithValue("@sourceFile", sourceFile);
        var deleted = await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("Deleted {Count} chunks for source file {SourceFile}", deleted, sourceFile);
    }

    /// <summary>
    /// Gets the open SQLite connection (for use by SqliteSearchService).
    /// </summary>
    internal SqliteConnection GetConnection()
    {
        if (_connection == null)
        {
            _connection = new SqliteConnection($"Data Source={_options.DatabasePath}");
            _connection.Open();

            // Enable WAL mode for better concurrent read performance
            using var walCmd = _connection.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            walCmd.ExecuteNonQuery();
        }

        return _connection;
    }

    /// <summary>
    /// Converts a float[] embedding to a byte[] BLOB for SQLite storage.
    /// </summary>
    internal static byte[] EmbeddingToBlob(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// Converts a byte[] BLOB from SQLite back to a float[] embedding.
    /// </summary>
    internal static float[] BlobToEmbedding(byte[] blob)
    {
        var embedding = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, embedding, 0, blob.Length);
        return embedding;
    }

    /// <summary>
    /// Returns the total number of chunks in the index.
    /// Returns 0 if the table doesn't exist yet.
    /// </summary>
    public int GetChunkCount()
    {
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chunks";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Returns the number of distinct source files in the index.
    /// Returns 0 if the table doesn't exist yet.
    /// </summary>
    public int GetSourceFileCount()
    {
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(DISTINCT source_file) FROM chunks";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Returns the most recent last_updated timestamp from the index.
    /// Returns null if the index is empty.
    /// </summary>
    public DateTimeOffset? GetLastIndexedTime()
    {
        try
        {
            var conn = GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(last_updated) FROM chunks";
            var result = cmd.ExecuteScalar();
            if (result is string s && !string.IsNullOrEmpty(s))
                return DateTimeOffset.Parse(s);
            return null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;
    }
}
