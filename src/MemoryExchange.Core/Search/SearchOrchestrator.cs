using System.Text;
using MemoryExchange.Core.Abstractions;
using MemoryExchange.Core.Chunking;
using Microsoft.Extensions.Logging;

namespace MemoryExchange.Core.Search;

/// <summary>
/// Provider-agnostic search orchestrator that sits above any ISearchService implementation.
/// Handles: query embedding, domain boosting, instruction boosting, over-fetch + rerank,
/// and result formatting. This is application logic, not provider logic.
/// </summary>
public class SearchOrchestrator
{
    private readonly ISearchService _searchService;
    private readonly IEmbeddingService _embeddingService;
    private readonly DomainRoutingMap? _routingMap;
    private readonly string? _sourcePath;
    private readonly ILogger<SearchOrchestrator> _logger;

    /// <summary>
    /// Domain boost multiplier for chunks matching the user's current file domain.
    /// </summary>
    private const double DomainBoostFactor = 1.3;

    /// <summary>
    /// Instruction boost multiplier for chunks from .instructions.md files.
    /// </summary>
    private const double InstructionBoostFactor = 1.2;

    /// <summary>
    /// Over-fetch multiplier — we fetch this many times topK from the provider,
    /// then rerank and trim to the requested topK.
    /// </summary>
    private const int OverFetchMultiplier = 2;

    public SearchOrchestrator(
        ISearchService searchService,
        IEmbeddingService embeddingService,
        ILogger<SearchOrchestrator> logger,
        DomainRoutingMap? routingMap = null,
        string? sourcePath = null)
    {
        _searchService = searchService;
        _embeddingService = embeddingService;
        _logger = logger;
        _routingMap = routingMap;
        _sourcePath = sourcePath != null ? Path.GetFullPath(sourcePath) : null;
    }

    /// <summary>
    /// Searches the memory exchange with domain/instruction boosting and result formatting.
    /// </summary>
    /// <param name="query">User's search query.</param>
    /// <param name="currentFilePath">Optional path of the file the user is currently editing (for domain boosting).</param>
    /// <param name="topK">Number of results to return.</param>
    /// <returns>Formatted search results as a string.</returns>
    public async Task<string> SearchAsync(string query, string? currentFilePath = null, int topK = 5)
    {
        _logger.LogInformation("Searching memory exchange: query='{Query}', currentFile='{File}', topK={TopK}",
            query, currentFilePath ?? "(none)", topK);

        // Generate query embedding
        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(query);

        // Detect relevant domains for boosting
        List<string>? relevantDomains = null;
        if (!string.IsNullOrWhiteSpace(currentFilePath) && _routingMap != null)
        {
            relevantDomains = _routingMap.GetDomainsForCodePath(currentFilePath);
            if (relevantDomains.Count > 0)
            {
                _logger.LogDebug("Detected relevant domains for boosting: {Domains}",
                    string.Join(", ", relevantDomains));
            }
        }

        // Over-fetch from the provider
        var overFetchK = topK * OverFetchMultiplier;
        var rawHits = await _searchService.SearchAsync(query, queryEmbedding, overFetchK);

        if (rawHits.Count == 0)
        {
            _logger.LogInformation("No results found for query");
            return "No relevant memory exchange entries found for your query.";
        }

        // Apply domain and instruction boosting, then rerank
        var boostedResults = rawHits
            .Select(hit =>
            {
                var adjustedScore = hit.Score;

                if (relevantDomains != null && relevantDomains.Count > 0)
                {
                    if (relevantDomains.Contains(hit.Chunk.Domain, StringComparer.OrdinalIgnoreCase))
                        adjustedScore *= DomainBoostFactor;

                    if (hit.Chunk.IsInstruction)
                        adjustedScore *= InstructionBoostFactor;
                }

                return (Chunk: hit.Chunk, OriginalScore: hit.Score, AdjustedScore: adjustedScore);
            })
            .OrderByDescending(r => r.AdjustedScore)
            .Take(topK)
            .ToList();

        return FormatResults(boostedResults, _sourcePath);
    }

    private static string FormatResults(
        List<(Models.MemoryChunk Chunk, double OriginalScore, double AdjustedScore)> results,
        string? sourcePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} relevant memory exchange entries:\n");

        for (int i = 0; i < results.Count; i++)
        {
            var (chunk, _, _) = results[i];
            sb.AppendLine($"--- Result {i + 1} ---");

            // Emit absolute path when source path is known, otherwise relative
            var displayPath = sourcePath != null
                ? Path.Combine(sourcePath, chunk.SourceFile.Replace('/', Path.DirectorySeparatorChar))
                : chunk.SourceFile;
            sb.AppendLine($"Source: {displayPath}");

            if (!string.IsNullOrWhiteSpace(chunk.HeadingPath))
                sb.AppendLine($"Section: {chunk.HeadingPath}");

            sb.AppendLine($"Domain: {chunk.Domain}");

            if (chunk.Tags.Count > 0)
                sb.AppendLine($"Tags: {string.Join(", ", chunk.Tags.Take(10))}");

            sb.AppendLine();
            sb.AppendLine(chunk.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
