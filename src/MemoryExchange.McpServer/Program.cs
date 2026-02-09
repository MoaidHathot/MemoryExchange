using MemoryExchange.Azure;
using MemoryExchange.Core.Abstractions;
using MemoryExchange.Core.Chunking;
using MemoryExchange.Core.Configuration;
using MemoryExchange.Core.Search;
using MemoryExchange.Indexing;
using MemoryExchange.Local;
using MemoryExchange.McpServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// Configuration sources (lowest to highest priority):
//   1. appsettings.json
//   2. User secrets
//   3. Environment variables (MEMORYEXCHANGE_ prefix)
//   4. CLI args (--source-path, --database-path, --provider, --build-index, etc.)
builder.Configuration.AddJsonFile("appsettings.json", optional: true);
builder.Configuration.AddUserSecrets<Program>(optional: true);

// Environment variables: flat names with MEMORYEXCHANGE_ prefix
// e.g. MEMORYEXCHANGE_SOURCEPATH -> MemoryExchange:SourcePath
//      MEMORYEXCHANGE_DATABASEPATH -> MemoryExchange:Local:DatabasePath
//      MEMORYEXCHANGE_PROVIDER -> MemoryExchange:Provider
//      MEMORYEXCHANGE_INDEXNAME -> MemoryExchange:IndexName
//      MEMORYEXCHANGE_MODELPATH -> MemoryExchange:Local:ModelPath
builder.Configuration.AddInMemoryCollection(
    MapEnvironmentVariables());

// CLI args: --source-path, --database-path, --provider, --index-name, --model-path, --build-index
// These override everything else.
builder.Configuration.AddInMemoryCollection(
    MapCommandLineArgs(args));

// Redirect all logging to stderr (stdout is reserved for MCP protocol)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(consoleOptions =>
{
    consoleOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Default database path to {SourcePath}/memory_exchange.db when not explicitly configured
var configuredSourcePath = builder.Configuration.GetValue<string>("MemoryExchange:SourcePath");
var configuredDatabasePath = builder.Configuration.GetValue<string>("MemoryExchange:Local:DatabasePath");

if (string.IsNullOrWhiteSpace(configuredDatabasePath) && !string.IsNullOrWhiteSpace(configuredSourcePath))
{
    builder.Configuration.AddInMemoryCollection(
    [
        new KeyValuePair<string, string?>("MemoryExchange:Local:DatabasePath",
            Path.Combine(configuredSourcePath, "memory_exchange.db"))
    ]);
}

// Bind core configuration
builder.Services.Configure<MemoryExchangeOptions>(
    builder.Configuration.GetSection(MemoryExchangeOptions.SectionName));

// Determine provider from configuration (default: Local)
var providerType = builder.Configuration.GetValue<string>("MemoryExchange:Provider")
    ?.Equals("azure", StringComparison.OrdinalIgnoreCase) == true
    ? ProviderType.Azure
    : ProviderType.Local;

// Register provider-specific services
switch (providerType)
{
    case ProviderType.Azure:
        builder.Services.AddAzureMemoryExchange(builder.Configuration);
        break;
    case ProviderType.Local:
    default:
        builder.Services.AddLocalMemoryExchange(builder.Configuration);
        break;
}

// Register indexing services (needed for --build-index and --watch)
builder.Services.AddSingleton<FileScanner>();
builder.Services.AddSingleton<IndexingPipeline>();

// Register file watcher service when --watch is enabled
var watchMode = builder.Configuration.GetValue<bool>("MemoryExchange:Watch");
if (watchMode)
{
    builder.Services.AddHostedService<FileWatcherService>();
}

// Register SearchOrchestrator with optional DomainRoutingMap
builder.Services.AddSingleton(sp =>
{
    var searchService = sp.GetRequiredService<ISearchService>();
    var embeddingService = sp.GetRequiredService<IEmbeddingService>();
    var logger = sp.GetRequiredService<ILogger<SearchOrchestrator>>();
    var options = sp.GetRequiredService<IOptions<MemoryExchangeOptions>>().Value;

    DomainRoutingMap? routingMap = null;

    if (!string.IsNullOrWhiteSpace(options.SourcePath))
    {
        var managementFile = Path.Combine(options.SourcePath, "MemoryExchangeManagement.md");
        if (File.Exists(managementFile))
        {
            try
            {
                var content = File.ReadAllText(managementFile);
                routingMap = DomainRoutingMap.Parse(content);
                logger.LogInformation("Loaded domain routing map with {Count} domains from {Path}",
                    routingMap.Domains.Count, managementFile);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse MemoryExchangeManagement.md — domain routing disabled");
            }
        }
        else
        {
            logger.LogDebug("MemoryExchangeManagement.md not found at {Path} — domain routing disabled", managementFile);
        }
    }
    else
    {
        logger.LogDebug("No SourcePath configured — domain routing disabled");
    }

    return new SearchOrchestrator(searchService, embeddingService, logger, routingMap, options.SourcePath);
});

// Register MCP server with stdio transport and auto-discover tools
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "memory-exchange",
            Version = "1.0.0"
        };
        options.ServerInstructions =
            "This server provides access to the project's memory exchange — a collection of " +
            "architectural knowledge, code patterns, conventions, and domain-specific context. " +
            "ALWAYS search the memory exchange when answering questions about the codebase, " +
            "implementing new features, or making architectural decisions. " +
            "The memory exchange contains critical information about established patterns " +
            "that should be followed consistently.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

// --build-index: run indexing pipeline before starting the MCP server
// Skipped when --watch is active (watch handles initial indexing itself)
var buildIndex = builder.Configuration.GetValue<bool>("MemoryExchange:BuildIndex");

// Startup diagnostics — log configuration so operators can verify args were parsed correctly
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("McpServer.Startup");
    var options = host.Services.GetRequiredService<IOptions<MemoryExchangeOptions>>().Value;
    logger.LogInformation("MemoryExchange MCP Server starting...");
    logger.LogInformation("  Provider:    {Provider}", providerType);
    logger.LogInformation("  Source path: {SourcePath}", string.IsNullOrWhiteSpace(options.SourcePath) ? "(not set)" : Path.GetFullPath(options.SourcePath));
    logger.LogInformation("  Index name:  {IndexName}", options.IndexName);
    logger.LogInformation("  Watch mode:  {Watch}", watchMode);
    logger.LogInformation("  Build index: {BuildIndex}", buildIndex);

    if (providerType == ProviderType.Local)
    {
        var localOptions = host.Services.GetRequiredService<IOptions<MemoryExchange.Local.Configuration.LocalProviderOptions>>().Value;
        logger.LogInformation("  Database:    {DatabasePath}", localOptions.DatabasePath);
    }

    if (!string.IsNullOrWhiteSpace(options.SourcePath))
    {
        var fullSourcePath = Path.GetFullPath(options.SourcePath);
        if (Directory.Exists(fullSourcePath))
        {
            var mdFiles = Directory.GetFiles(fullSourcePath, "*.md", SearchOption.AllDirectories);
            logger.LogInformation("  Markdown files found: {Count}", mdFiles.Length);
        }
        else
        {
            logger.LogWarning("  Source directory does NOT exist: {Path}", fullSourcePath);
        }
    }

    if (options.ExcludePatterns.Count > 0)
    {
        logger.LogInformation("  Exclude patterns: {Patterns}", string.Join(", ", options.ExcludePatterns));
    }
}

// --build-index without --watch: run indexing before starting MCP server
if (buildIndex && !watchMode)
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("McpServer");
    var options = host.Services.GetRequiredService<IOptions<MemoryExchangeOptions>>().Value;

    if (string.IsNullOrWhiteSpace(options.SourcePath))
    {
        logger.LogError("--build-index requires --source-path to be set");
    }
    else
    {
        var sourcePath = Path.GetFullPath(options.SourcePath);
        if (!Directory.Exists(sourcePath))
        {
            logger.LogError("Source directory '{Path}' does not exist", sourcePath);
        }
        else
        {
            logger.LogInformation("Build-index mode: indexing memory exchange at {Path} before starting MCP server...", sourcePath);
            var pipeline = host.Services.GetRequiredService<IndexingPipeline>();
            await pipeline.RunAsync(sourcePath, forceRebuild: true, options.IndexName);
            logger.LogInformation("Build-index complete. Starting MCP server...");
        }
    }
}

await host.RunAsync();

// Needed for user secrets + static helper methods
public partial class Program
{
    /// <summary>
    /// Maps flat MEMORYEXCHANGE_ environment variables to .NET configuration keys.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, string?>> MapEnvironmentVariables()
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MEMORYEXCHANGE_SOURCEPATH"] = "MemoryExchange:SourcePath",
            ["MEMORYEXCHANGE_PROVIDER"] = "MemoryExchange:Provider",
            ["MEMORYEXCHANGE_INDEXNAME"] = "MemoryExchange:IndexName",
            ["MEMORYEXCHANGE_DATABASEPATH"] = "MemoryExchange:Local:DatabasePath",
            ["MEMORYEXCHANGE_MODELPATH"] = "MemoryExchange:Local:ModelPath",
            ["MEMORYEXCHANGE_AZURE_SEARCH_ENDPOINT"] = "MemoryExchange:AzureSearch:Endpoint",
            ["MEMORYEXCHANGE_AZURE_SEARCH_APIKEY"] = "MemoryExchange:AzureSearch:ApiKey",
            ["MEMORYEXCHANGE_AZURE_OPENAI_ENDPOINT"] = "MemoryExchange:AzureOpenAI:Endpoint",
            ["MEMORYEXCHANGE_AZURE_OPENAI_APIKEY"] = "MemoryExchange:AzureOpenAI:ApiKey",
            ["MEMORYEXCHANGE_AZURE_OPENAI_DEPLOYMENT"] = "MemoryExchange:AzureOpenAI:EmbeddingDeployment",
            ["MEMORYEXCHANGE_BUILDINDEX"] = "MemoryExchange:BuildIndex",
            ["MEMORYEXCHANGE_WATCH"] = "MemoryExchange:Watch",
        };

        foreach (var (envVar, configKey) in mappings)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(value))
            {
                yield return new KeyValuePair<string, string?>(configKey, value);
            }
        }
    }

    /// <summary>
    /// Parses CLI args (--key value pairs, boolean flags, and list args) and maps them to .NET configuration keys.
    /// Supports: --source-path, --database-path, --provider, --index-name, --model-path,
    ///           --azure-search-endpoint, --azure-search-key,
    ///           --azure-openai-endpoint, --azure-openai-key, --azure-openai-deployment,
    ///           --build-index (boolean flag), --watch (boolean flag),
    ///           --exclude (list arg, can appear multiple times)
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, string?>> MapCommandLineArgs(string[] args)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--source-path"] = "MemoryExchange:SourcePath",
            ["--provider"] = "MemoryExchange:Provider",
            ["--index-name"] = "MemoryExchange:IndexName",
            ["--database-path"] = "MemoryExchange:Local:DatabasePath",
            ["--model-path"] = "MemoryExchange:Local:ModelPath",
            ["--azure-search-endpoint"] = "MemoryExchange:AzureSearch:Endpoint",
            ["--azure-search-key"] = "MemoryExchange:AzureSearch:ApiKey",
            ["--azure-openai-endpoint"] = "MemoryExchange:AzureOpenAI:Endpoint",
            ["--azure-openai-key"] = "MemoryExchange:AzureOpenAI:ApiKey",
            ["--azure-openai-deployment"] = "MemoryExchange:AzureOpenAI:EmbeddingDeployment",
        };

        // Boolean flags (presence = true)
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--build-index"] = "MemoryExchange:BuildIndex",
            ["--watch"] = "MemoryExchange:Watch",
        };

        // List arguments (can appear multiple times, mapped to indexed config keys)
        var listArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--exclude"] = "MemoryExchange:ExcludePatterns",
        };
        var listCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            if (mappings.TryGetValue(args[i], out var configKey) && i + 1 < args.Length)
            {
                yield return new KeyValuePair<string, string?>(configKey, args[i + 1]);
                i++; // skip the value
            }
            else if (flags.TryGetValue(args[i], out var flagKey))
            {
                yield return new KeyValuePair<string, string?>(flagKey, "true");
            }
            else if (listArgs.TryGetValue(args[i], out var listKey) && i + 1 < args.Length)
            {
                listCounters.TryGetValue(args[i], out var count);
                yield return new KeyValuePair<string, string?>($"{listKey}:{count}", args[i + 1]);
                listCounters[args[i]] = count + 1;
                i++; // skip the value
            }
        }
    }
}
