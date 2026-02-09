using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using MemoryExchange.Azure.Configuration;
using MemoryExchange.Azure.Models;
using MemoryExchange.Core.Abstractions;
using MemoryExchange.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryExchange.Azure.Services;

/// <summary>
/// Write-side implementation for Azure AI Search: index creation, document upsert, and deletion.
/// Implements <see cref="ISearchIndex"/>.
/// </summary>
public class AzureSearchIndex : ISearchIndex
{
    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _searchClient;
    private readonly string _indexName;
    private readonly ILogger<AzureSearchIndex> _logger;

    public AzureSearchIndex(IOptions<AzureSearchOptions> options, ILogger<AzureSearchIndex> logger)
    {
        _logger = logger;
        var config = options.Value;
        _indexName = config.IndexName;

        var endpoint = new Uri(config.Endpoint);
        var credential = new AzureKeyCredential(config.ApiKey);
        _indexClient = new SearchIndexClient(endpoint, credential);
        _searchClient = new SearchClient(endpoint, _indexName, credential);
    }

    /// <inheritdoc />
    public async Task EnsureIndexAsync()
    {
        _logger.LogInformation("Ensuring search index '{IndexName}' exists...", _indexName);

        const string vectorProfileName = "default-vector-profile";
        const string hnswConfigName = "default-hnsw-config";

        var index = new SearchIndex(_indexName)
        {
            Fields = new FieldBuilder().Build(typeof(AzureSearchDocument)),
            VectorSearch = new()
            {
                Profiles =
                {
                    new VectorSearchProfile(vectorProfileName, hnswConfigName)
                },
                Algorithms =
                {
                    new HnswAlgorithmConfiguration(hnswConfigName)
                    {
                        Parameters = new HnswParameters
                        {
                            Metric = VectorSearchAlgorithmMetric.Cosine,
                            M = 4,
                            EfConstruction = 400,
                            EfSearch = 500
                        }
                    }
                }
            }
        };

        try
        {
            await _indexClient.CreateOrUpdateIndexAsync(index);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == "OperationNotAllowed")
        {
            _logger.LogWarning(
                "Index '{IndexName}' has incompatible schema changes. Deleting and recreating...",
                _indexName);

            await _indexClient.DeleteIndexAsync(_indexName);
            _logger.LogInformation("Deleted index '{IndexName}'", _indexName);

            await _indexClient.CreateOrUpdateIndexAsync(index);
        }

        _logger.LogInformation("Search index '{IndexName}' is ready", _indexName);
    }

    /// <inheritdoc />
    public async Task UpsertChunksAsync(IEnumerable<MemoryChunk> chunks)
    {
        var chunkList = chunks.ToList();
        if (chunkList.Count == 0) return;

        _logger.LogInformation("Upserting {Count} chunks to index '{IndexName}'...", chunkList.Count, _indexName);

        // Convert to Azure documents
        var docs = chunkList.Select(AzureSearchDocument.FromChunk).ToList();

        // Process in batches of 100 (Azure AI Search batch limit)
        const int batchSize = 100;
        for (int i = 0; i < docs.Count; i += batchSize)
        {
            var batch = docs.Skip(i).Take(batchSize).ToList();
            var actions = batch.Select(d => IndexDocumentsAction.MergeOrUpload(d));
            var indexBatch = IndexDocumentsBatch.Create(actions.ToArray());

            var response = await _searchClient.IndexDocumentsAsync(indexBatch);

            var failedCount = response.Value.Results.Count(r => !r.Succeeded);
            if (failedCount > 0)
            {
                _logger.LogWarning("{FailedCount} documents failed to index in batch starting at {Start}",
                    failedCount, i);
                foreach (var result in response.Value.Results.Where(r => !r.Succeeded))
                {
                    _logger.LogWarning("  Failed document {Key}: {Message}", result.Key, result.ErrorMessage);
                }
            }
        }

        _logger.LogInformation("Upsert complete for {Count} chunks", chunkList.Count);
    }

    /// <inheritdoc />
    public async Task DeleteChunksForFileAsync(string sourceFile)
    {
        _logger.LogInformation("Deleting chunks for file '{SourceFile}'...", sourceFile);

        var searchOptions = new SearchOptions
        {
            Filter = $"SourceFile eq '{sourceFile.Replace("'", "''")}'",
            Select = { "Id" },
            Size = 1000
        };

        var searchResults = await _searchClient.SearchAsync<AzureSearchDocument>("*", searchOptions);
        var idsToDelete = new List<string>();

        await foreach (var result in searchResults.Value.GetResultsAsync())
        {
            idsToDelete.Add(result.Document.Id);
        }

        if (idsToDelete.Count == 0)
        {
            _logger.LogDebug("No chunks found for file '{SourceFile}'", sourceFile);
            return;
        }

        const int batchSize = 100;
        for (int i = 0; i < idsToDelete.Count; i += batchSize)
        {
            var batch = idsToDelete.Skip(i).Take(batchSize)
                .Select(id => IndexDocumentsAction.Delete("Id", id));
            await _searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Create(batch.ToArray()));
        }

        _logger.LogInformation("Deleted {Count} chunks for file '{SourceFile}'", idsToDelete.Count, sourceFile);
    }
}
