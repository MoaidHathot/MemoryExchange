using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using MemoryExchange.Azure.Configuration;
using MemoryExchange.Azure.Models;
using MemoryExchange.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryExchange.Azure.Services;

/// <summary>
/// Read-side Azure AI Search implementation: performs hybrid search (vector + BM25).
/// Returns raw <see cref="SearchHit"/> results without boosting or formatting.
/// Implements <see cref="ISearchService"/>.
/// </summary>
public class AzureSearchService : ISearchService
{
    private readonly SearchClient _searchClient;
    private readonly ILogger<AzureSearchService> _logger;

    public AzureSearchService(IOptions<AzureSearchOptions> options, ILogger<AzureSearchService> logger)
    {
        _logger = logger;
        var config = options.Value;

        _searchClient = new SearchClient(
            new Uri(config.Endpoint),
            config.IndexName,
            new AzureKeyCredential(config.ApiKey));
    }

    /// <inheritdoc />
    public async Task<List<SearchHit>> SearchAsync(string query, float[] queryEmbedding, int topK)
    {
        _logger.LogDebug("Executing Azure hybrid search: topK={TopK}", topK);

        var searchOptions = new SearchOptions
        {
            Size = topK,
            Select =
            {
                "Id", "Content", "SourceFile", "HeadingPath", "Domain",
                "Tags", "RelatedFiles", "IsInstruction", "ChunkIndex"
            },
            VectorSearch = new()
            {
                Queries =
                {
                    new VectorizedQuery(queryEmbedding)
                    {
                        KNearestNeighborsCount = topK,
                        Fields = { "Embedding" }
                    }
                }
            }
        };

        var response = await _searchClient.SearchAsync<AzureSearchDocument>(query, searchOptions);

        var results = new List<SearchHit>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            var score = result.Score ?? 0;
            results.Add(new SearchHit(result.Document.ToChunk(), score));
        }

        _logger.LogDebug("Azure search returned {Count} results", results.Count);
        return results;
    }
}
