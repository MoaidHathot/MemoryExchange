using MemoryExchange.Core.Abstractions;
using MemoryExchange.Core.Chunking;
using MemoryExchange.Core.Models;
using Microsoft.Extensions.Logging;

namespace MemoryExchange.Indexing;

/// <summary>
/// Orchestrates the full indexing pipeline: scan -> chunk -> embed -> upsert -> save state.
/// </summary>
public class IndexingPipeline
{
    private readonly FileScanner _scanner;
    private readonly IEmbeddingService _embeddingService;
    private readonly ISearchIndex _searchIndex;
    private readonly ILogger<IndexingPipeline> _logger;

    public IndexingPipeline(
        FileScanner scanner,
        IEmbeddingService embeddingService,
        ISearchIndex searchIndex,
        ILogger<IndexingPipeline> logger)
    {
        _scanner = scanner;
        _embeddingService = embeddingService;
        _searchIndex = searchIndex;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full indexing pipeline against the given source directory.
    /// </summary>
    /// <param name="sourcePath">Absolute path to the source directory.</param>
    /// <param name="forceRebuild">If true, re-indexes all files regardless of cached state.</param>
    /// <param name="indexName">The search index name (used for state tracking).</param>
    public async Task RunAsync(string sourcePath, bool forceRebuild, string indexName)
    {
        _logger.LogInformation("=== Memory Exchange Indexer ===");
        _logger.LogInformation("Source: {Source}", sourcePath);
        _logger.LogInformation("Force rebuild: {Force}", forceRebuild);
        _logger.LogInformation("Index: {IndexName}", indexName);

        // 1. Ensure the search index exists
        await _searchIndex.EnsureIndexAsync();

        // 2. Scan for changes
        var scanResult = await _scanner.ScanAsync(sourcePath, forceRebuild, indexName);

        if (scanResult.ChangedFiles.Count == 0 && scanResult.DeletedFiles.Count == 0)
        {
            _logger.LogInformation("No changes detected. Index is up to date.");
            return;
        }

        // 3. Load domain routing map
        DomainRoutingMap? routingMap = null;
        var managementFile = Path.Combine(sourcePath, "MemoryExchangeManagement.md");
        if (File.Exists(managementFile))
        {
            var managementContent = await File.ReadAllTextAsync(managementFile);
            routingMap = DomainRoutingMap.Parse(managementContent);
            _logger.LogInformation("Loaded domain routing map with {Count} domains", routingMap.Domains.Count);
        }

        // 4. Delete chunks for deleted files
        foreach (var deletedFile in scanResult.DeletedFiles)
        {
            await _searchIndex.DeleteChunksForFileAsync(deletedFile);
        }

        // 5. Process changed files
        var allChunks = new List<MemoryChunk>();

        foreach (var file in scanResult.ChangedFiles)
        {
            var fullPath = Path.Combine(sourcePath, file);
            var content = await File.ReadAllTextAsync(fullPath);
            var domain = DomainRoutingMap.GetDomainFromFilePath(file);

            _logger.LogInformation("Chunking {File} (domain: {Domain})...", file, domain);
            var chunks = MarkdownChunker.ChunkFile(content, file, domain);
            _logger.LogDebug("  -> {Count} chunks", chunks.Count);

            // Delete old chunks for this file before upserting new ones
            await _searchIndex.DeleteChunksForFileAsync(file.Replace('\\', '/'));

            allChunks.AddRange(chunks);
        }

        _logger.LogInformation("Total chunks to embed: {Count}", allChunks.Count);

        // 6. Generate embeddings
        var texts = allChunks.Select(c => c.Content).ToList();
        var embeddings = await _embeddingService.GenerateEmbeddingsAsync(texts);

        for (int i = 0; i < allChunks.Count; i++)
        {
            allChunks[i].Embedding = embeddings[i];
        }

        // 7. Upsert to search index
        await _searchIndex.UpsertChunksAsync(allChunks);

        // 8. Save state
        await _scanner.SaveStateAsync(sourcePath, scanResult.NewState);

        _logger.LogInformation("=== Indexing complete ===");
        _logger.LogInformation("  Files processed: {Count}", scanResult.ChangedFiles.Count);
        _logger.LogInformation("  Files deleted: {Count}", scanResult.DeletedFiles.Count);
        _logger.LogInformation("  Chunks indexed: {Count}", allChunks.Count);
    }
}
