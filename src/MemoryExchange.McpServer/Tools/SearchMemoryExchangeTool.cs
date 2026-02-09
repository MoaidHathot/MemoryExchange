using System.ComponentModel;
using MemoryExchange.Core.Search;
using ModelContextProtocol.Server;

namespace MemoryExchange.McpServer.Tools;

/// <summary>
/// MCP tool that exposes memory exchange search to GitHub Copilot and other MCP clients.
/// </summary>
[McpServerToolType]
public sealed class SearchMemoryExchangeTool
{
    private readonly SearchOrchestrator _orchestrator;

    public SearchMemoryExchangeTool(SearchOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    /// <summary>
    /// Searches the memory exchange for relevant architectural knowledge, patterns,
    /// code conventions, and project context.
    /// </summary>
    [McpServerTool(
        Name = "search_memory_bank",
        ReadOnly = true,
        Idempotent = true)]
    [Description(
        "Search the project's memory bank for architectural knowledge, code patterns, " +
        "conventions, and domain-specific context. Use this tool whenever you need to " +
        "understand how the codebase works, find established patterns, or look up " +
        "project-specific decisions and guidelines. The memory bank contains documentation " +
        "about architecture, data access patterns, deployment, testing strategies, and more.")]
    public async Task<string> SearchMemoryExchange(
        [Description("Search query describing what you want to know about the project. " +
                     "Be specific - e.g., 'how does FASTER KV caching work' or " +
                     "'what is the resource provider architecture'")] 
        string query,
        [Description("Optional: the file path the user is currently editing. " +
                     "Enables domain-aware boosting to prioritize relevant context. " +
                     "Example: 'src/ResourceProvider/Controllers/PolicyController.cs'")] 
        string? currentFilePath = null,
        [Description("Number of results to return (1-10). Default is 5.")] 
        int topK = 5)
    {
        // Clamp topK to reasonable bounds
        topK = Math.Clamp(topK, 1, 10);

        return await _orchestrator.SearchAsync(query, currentFilePath, topK);
    }
}
