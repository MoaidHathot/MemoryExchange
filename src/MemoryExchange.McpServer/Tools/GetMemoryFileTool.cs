using System.ComponentModel;
using MemoryExchange.Core.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MemoryExchange.McpServer.Tools;

/// <summary>
/// MCP tool that retrieves the full content of a specific memory file by relative path.
/// </summary>
[McpServerToolType]
public sealed class GetMemoryFileTool
{
    private readonly string _sourcePath;

    public GetMemoryFileTool(IOptions<MemoryExchangeOptions> options)
    {
        _sourcePath = options.Value.SourcePath;
    }

    /// <summary>
    /// Retrieves the full content of a specific markdown file from the memory exchange.
    /// </summary>
    [McpServerTool(
        Name = "get_memory_file",
        ReadOnly = true,
        Idempotent = true)]
    [Description(
        "Retrieve the full content of a specific markdown file from the memory exchange. " +
        "Use this tool when you found a relevant chunk via search and need the complete " +
        "file context, or when you already know which memory file contains the information " +
        "you need. Provide the relative file path as shown in search results.")]
    public async Task<string> GetMemoryFile(
        [Description("Relative path to the markdown file within the memory exchange " +
                     "source directory (e.g., 'architecture/database.md' or " +
                     "'rp/deployment-guide.md'). Use paths as returned by search_memory_bank results.")]
        string filePath)
    {
        if (string.IsNullOrWhiteSpace(_sourcePath))
        {
            return "Error: source-path is not configured. Cannot retrieve memory files.";
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: filePath is required.";
        }

        // Normalize and resolve the full path
        var normalizedPath = filePath.Replace('/', Path.DirectorySeparatorChar)
                                     .Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_sourcePath, normalizedPath));
        var sourceRoot = Path.GetFullPath(_sourcePath);

        // Prevent directory traversal — resolved path must stay within the source directory
        if (!fullPath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return "Error: the specified path is outside the memory exchange source directory.";
        }

        if (!File.Exists(fullPath))
        {
            return $"Error: file not found: {filePath}";
        }

        var content = await File.ReadAllTextAsync(fullPath);
        return content;
    }
}
