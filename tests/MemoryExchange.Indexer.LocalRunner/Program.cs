using MemoryExchange.Azure;
using MemoryExchange.Core.Configuration;
using MemoryExchange.Indexing;
using MemoryExchange.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ---------------------------------------------------------------
// Local Runner for the Memory Exchange Indexer
// Uses the provider abstraction layer (Local or Azure).
//
// Usage:
//   dotnet run --project tests/MemoryExchange.Indexer.LocalRunner -- <source-path> [options]
//
// Options:
//   --force, -f              Force a full rebuild of the index
//   --provider, -p           Provider: 'local' (default) or 'azure'
//   --database-path          SQLite database path (local provider only)
//   --model-path             ONNX model path (local provider only)
//   --index-name             Logical name of the search index
// ---------------------------------------------------------------

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("Usage: dotnet run -- <source-path> [--force] [--provider local|azure]");
    Console.WriteLine();
    Console.WriteLine("  <source-path>    Path to the source directory containing markdown files");
    Console.WriteLine("  --force          Force a full rebuild of the index, ignoring cached state");
    Console.WriteLine("  --provider       Search/embedding provider: 'local' (default) or 'azure'");
    Console.WriteLine("  --database-path  SQLite database path (local provider only)");
    Console.WriteLine("  --model-path     ONNX model path (local provider only)");
    Console.WriteLine("  --index-name     Logical name of the search index");
    return;
}

var sourcePath = Path.GetFullPath(args[0]);
var forceRebuild = args.Any(a => a is "--force" or "-f");

// Parse named arguments
string? ParseNamedArg(params string[] names)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        foreach (var name in names)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
    }
    return null;
}

var providerArg = ParseNamedArg("--provider", "-p") ?? "local";
var databasePath = ParseNamedArg("--database-path");
var modelPath = ParseNamedArg("--model-path");
var indexName = ParseNamedArg("--index-name");

if (!Directory.Exists(sourcePath))
{
    Console.Error.WriteLine($"Error: Source directory '{sourcePath}' does not exist.");
    return;
}

// Build host for DI and configuration
// Sources (lowest to highest priority):
//   1. appsettings.json
//   2. Environment variables (MEMORYEXCHANGE_ prefix, flat mapping)
//   3. CLI args (highest priority)
var builder = Host.CreateApplicationBuilder();
builder.Configuration.AddJsonFile("appsettings.json", optional: false);
builder.Configuration.AddInMemoryCollection(
    ConfigHelper.MapEnvironmentVariables());

// Apply CLI overrides
var cliOverrides = new Dictionary<string, string?>();
if (databasePath is not null)
    cliOverrides["MemoryExchange:Local:DatabasePath"] = databasePath;
if (modelPath is not null)
    cliOverrides["MemoryExchange:Local:ModelPath"] = modelPath;
if (indexName is not null)
    cliOverrides["MemoryExchange:IndexName"] = indexName;
if (!providerArg.Equals("local", StringComparison.OrdinalIgnoreCase))
    cliOverrides["MemoryExchange:Provider"] = providerArg;
if (cliOverrides.Count > 0)
    builder.Configuration.AddInMemoryCollection(cliOverrides);

builder.Services.Configure<MemoryExchangeOptions>(
    builder.Configuration.GetSection(MemoryExchangeOptions.SectionName));

// Determine provider: CLI arg overrides config
var providerType = providerArg.Equals("azure", StringComparison.OrdinalIgnoreCase)
    ? ProviderType.Azure
    : ProviderType.Local;

var configProvider = builder.Configuration.GetValue<string>("MemoryExchange:Provider");
if (providerArg.Equals("local", StringComparison.OrdinalIgnoreCase) &&
    configProvider?.Equals("azure", StringComparison.OrdinalIgnoreCase) == true)
{
    providerType = ProviderType.Azure;
}

// Register provider services
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

builder.Services.AddSingleton<FileScanner>();
builder.Services.AddSingleton<IndexingPipeline>();

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LocalRunner");
var options = host.Services.GetRequiredService<IOptions<MemoryExchangeOptions>>().Value;

logger.LogInformation("Using provider: {Provider}", providerType);
logger.LogInformation("Source path: {Path}", sourcePath);
logger.LogInformation("Force rebuild: {Force}", forceRebuild);

var pipeline = host.Services.GetRequiredService<IndexingPipeline>();
await pipeline.RunAsync(sourcePath, forceRebuild, options.IndexName);

internal static class ConfigHelper
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
}
