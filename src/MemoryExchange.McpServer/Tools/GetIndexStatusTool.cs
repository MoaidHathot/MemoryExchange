using System.ComponentModel;
using System.Text;
using MemoryExchange.Core.Abstractions;
using MemoryExchange.Core.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MemoryExchange.McpServer.Tools;

/// <summary>
/// MCP tool that reports the current state of the memory exchange index.
/// Useful for diagnosing why search returns no results.
/// </summary>
[McpServerToolType]
public sealed class GetIndexStatusTool
{
    private readonly MemoryExchangeOptions _options;
    private readonly ISearchIndex _index;

    public GetIndexStatusTool(IOptions<MemoryExchangeOptions> options, ISearchIndex index)
    {
        _options = options.Value;
        _index = index;
    }

    /// <summary>
    /// Returns diagnostic information about the memory exchange index.
    /// </summary>
    [McpServerTool(
        Name = "get_index_status",
        ReadOnly = true,
        Idempotent = true)]
    [Description(
        "Returns diagnostic information about the memory exchange index, including " +
        "the number of indexed chunks, source files, and last indexed time. " +
        "Use this tool to verify the index is populated before searching, or to diagnose " +
        "why search_memory_bank returns no results.")]
    public string GetIndexStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Memory Exchange Index Status");
        sb.AppendLine();
        sb.AppendLine($"- **Source path:** {(string.IsNullOrWhiteSpace(_options.SourcePath) ? "(not configured)" : _options.SourcePath)}");
        sb.AppendLine($"- **Provider:** {_options.Provider}");
        sb.AppendLine($"- **Index name:** {_options.IndexName}");

        try
        {
            var chunkCount = _index.GetChunkCount();
            var fileCount = _index.GetSourceFileCount();
            var lastIndexed = _index.GetLastIndexedTime();

            sb.AppendLine($"- **Indexed chunks:** {chunkCount}");
            sb.AppendLine($"- **Indexed files:** {fileCount}");
            sb.AppendLine($"- **Last indexed:** {lastIndexed?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "never"}");

            if (chunkCount == 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Warning:** The index is empty. Possible causes:");
                sb.AppendLine("  1. The server was started without `--watch` or `--build-index`");
                sb.AppendLine("  2. The source path contains no `.md` files");
                sb.AppendLine("  3. Indexing failed silently (check server stderr for errors)");
                sb.AppendLine("  4. The database path is different from what the search service is using");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"- **Error reading index:** {ex.Message}");
        }

        return sb.ToString();
    }
}
