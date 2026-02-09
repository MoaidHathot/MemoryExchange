using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemoryExchange.Core.Configuration;
using MemoryExchange.Core.Models;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryExchange.Indexing;

/// <summary>
/// Scans a memory exchange directory for markdown files and detects changes
/// since the last indexing run using SHA256 hashes.
/// </summary>
public class FileScanner
{
    private const string StateFileName = ".memory-exchange-state.json";
    private static readonly string[] AlwaysExcludedDirectories = ["personal"];

    private readonly ILogger<FileScanner> _logger;
    private readonly Matcher? _excludeMatcher;

    public FileScanner(ILogger<FileScanner> logger, IOptions<MemoryExchangeOptions> options)
    {
        _logger = logger;

        var patterns = options.Value.ExcludePatterns;
        if (patterns is { Count: > 0 })
        {
            _excludeMatcher = new Matcher();
            foreach (var pattern in patterns)
            {
                _excludeMatcher.AddInclude(pattern);
            }
            _logger.LogInformation("Configured {Count} exclude pattern(s): {Patterns}",
                patterns.Count, string.Join(", ", patterns));
        }
    }

    /// <summary>
    /// Result of scanning the source directory.
    /// </summary>
    public record ScanResult(
        List<string> ChangedFiles,
        List<string> DeletedFiles,
        List<string> AllFiles,
        IndexState PreviousState,
        IndexState NewState);

    /// <summary>
    /// Scans the source directory and detects changes.
    /// </summary>
    /// <param name="MemoryExchangePath">Root path of the source directory.</param>
    /// <param name="forceFullRebuild">If true, treats all files as changed.</param>
    /// <param name="indexName">Name of the search index (for state tracking).</param>
    public async Task<ScanResult> ScanAsync(string MemoryExchangePath, bool forceFullRebuild, string indexName)
    {
        var previousState = await LoadStateAsync(MemoryExchangePath);
        var newState = new IndexState { IndexName = indexName };

        // Find all markdown files, excluding personal/ directory
        var allFiles = Directory.EnumerateFiles(MemoryExchangePath, "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(MemoryExchangePath, f))
            .Where(f => !IsExcluded(f))
            .OrderBy(f => f)
            .ToList();

        _logger.LogInformation("Found {Count} markdown files in source directory", allFiles.Count);

        // Compute hashes for all files
        foreach (var file in allFiles)
        {
            var fullPath = Path.Combine(MemoryExchangePath, file);
            var hash = await ComputeFileHashAsync(fullPath);
            newState.FileHashes[NormalizePath(file)] = hash;
        }

        // Detect changes
        var changedFiles = new List<string>();
        var deletedFiles = new List<string>();

        if (forceFullRebuild || previousState.FileHashes.Count == 0)
        {
            _logger.LogInformation("Full rebuild requested — all files marked as changed");
            changedFiles.AddRange(allFiles);
            newState.LastFullIndexUtc = DateTimeOffset.UtcNow;
        }
        else
        {
            // Find changed and new files
            foreach (var file in allFiles)
            {
                var normalizedFile = NormalizePath(file);
                if (!previousState.FileHashes.TryGetValue(normalizedFile, out var prevHash) ||
                    prevHash != newState.FileHashes[normalizedFile])
                {
                    changedFiles.Add(file);
                }
            }

            // Find deleted files
            var currentFileSet = allFiles.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var prevFile in previousState.FileHashes.Keys)
            {
                if (!currentFileSet.Contains(prevFile))
                    deletedFiles.Add(prevFile);
            }

            newState.LastIncrementalIndexUtc = DateTimeOffset.UtcNow;
        }

        _logger.LogInformation("Scan complete: {Changed} changed, {Deleted} deleted, {Total} total",
            changedFiles.Count, deletedFiles.Count, allFiles.Count);

        return new ScanResult(changedFiles, deletedFiles, allFiles, previousState, newState);
    }

    /// <summary>
    /// Saves the index state to the source directory.
    /// </summary>
    public async Task SaveStateAsync(string MemoryExchangePath, IndexState state)
    {
        var statePath = Path.Combine(MemoryExchangePath, StateFileName);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(statePath, json);
        _logger.LogInformation("Index state saved to {Path}", statePath);
    }

    private static async Task<IndexState> LoadStateAsync(string MemoryExchangePath)
    {
        var statePath = Path.Combine(MemoryExchangePath, StateFileName);
        if (!File.Exists(statePath))
            return new IndexState();

        var json = await File.ReadAllTextAsync(statePath);
        return JsonSerializer.Deserialize<IndexState>(json) ?? new IndexState();
    }

    private static async Task<string> ComputeFileHashAsync(string filePath)
    {
        var bytes = await File.ReadAllBytesAsync(filePath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private bool IsExcluded(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');

        // Always exclude hardcoded directories (e.g., "personal/")
        if (AlwaysExcludedDirectories.Any(dir =>
            normalized.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(dir, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Apply user-configured glob patterns
        if (_excludeMatcher is not null)
        {
            var result = _excludeMatcher.Match(normalized);
            return result.HasMatches;
        }

        return false;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
