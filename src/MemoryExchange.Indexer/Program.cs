using System.CommandLine;
using System.CommandLine.Parsing;
using MemoryExchange.Azure;
using MemoryExchange.Core.Abstractions;
using MemoryExchange.Core.Configuration;
using MemoryExchange.Indexing;
using MemoryExchange.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var sourceOption = new Option<string>("--source", "-s")
{
    Description = "Path to the source directory containing markdown files",
    Required = true
};

var forceOption = new Option<bool>("--force", "-f")
{
    Description = "Force a full rebuild of the index, ignoring cached state",
    DefaultValueFactory = _ => false
};

var providerOption = new Option<string>("--provider", "-p")
{
    Description = "Search/embedding provider to use: 'local' (SQLite + ONNX) or 'azure' (Azure AI Search + OpenAI)",
    DefaultValueFactory = _ => "local"
};

var databasePathOption = new Option<string?>("--database-path")
{
    Description = "Path to the SQLite database file (local provider only)"
};

var modelPathOption = new Option<string?>("--model-path")
{
    Description = "Path to the ONNX embedding model file (local provider only)"
};

var indexNameOption = new Option<string?>("--index-name")
{
    Description = "Logical name of the search index"
};

var rootCommand = new RootCommand("Memory Exchange Indexer — indexes markdown files for search");
rootCommand.Options.Add(sourceOption);
rootCommand.Options.Add(forceOption);
rootCommand.Options.Add(providerOption);
rootCommand.Options.Add(databasePathOption);
rootCommand.Options.Add(modelPathOption);
rootCommand.Options.Add(indexNameOption);

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var source = parseResult.GetValue(sourceOption)!;
    var force = parseResult.GetValue(forceOption);
    var provider = parseResult.GetValue(providerOption)!;
    var databasePath = parseResult.GetValue(databasePathOption);
    var modelPath = parseResult.GetValue(modelPathOption);
    var indexName = parseResult.GetValue(indexNameOption);
    await RunIndexerAsync(source, force, provider, databasePath, modelPath, indexName);
});

return await rootCommand.Parse(args).InvokeAsync();

static async Task RunIndexerAsync(string sourcePath, bool forceRebuild, string providerArg,
    string? databasePath, string? modelPath, string? indexName)
{
    // Validate source path
    if (!Directory.Exists(sourcePath))
    {
        Console.Error.WriteLine($"Error: Source directory '{sourcePath}' does not exist.");
        return;
    }

    sourcePath = Path.GetFullPath(sourcePath);

    // Build host for DI and configuration
    // Sources (lowest to highest priority):
    //   1. appsettings.json
    //   2. User secrets
    //   3. Environment variables (MEMORYEXCHANGE_ prefix)
    //   4. CLI args (highest priority)
    var builder = Host.CreateApplicationBuilder();
    builder.Configuration.AddJsonFile("appsettings.json", optional: true);
    builder.Configuration.AddUserSecrets<Program>(optional: true);
    builder.Configuration.AddInMemoryCollection(
        Program.MapEnvironmentVariables());

    // Apply CLI overrides
    var cliOverrides = new Dictionary<string, string?>();
    if (databasePath is not null)
        cliOverrides["MemoryExchange:Local:DatabasePath"] = databasePath;
    if (modelPath is not null)
        cliOverrides["MemoryExchange:Local:ModelPath"] = modelPath;
    if (indexName is not null)
        cliOverrides["MemoryExchange:IndexName"] = indexName;
    if (cliOverrides.Count > 0)
        builder.Configuration.AddInMemoryCollection(cliOverrides);

    builder.Services.Configure<MemoryExchangeOptions>(
        builder.Configuration.GetSection(MemoryExchangeOptions.SectionName));

    // Determine provider: CLI arg overrides config
    var providerType = providerArg.Equals("azure", StringComparison.OrdinalIgnoreCase)
        ? ProviderType.Azure
        : ProviderType.Local;

    // Check config if CLI didn't explicitly set it
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
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Indexer");
    var options = host.Services.GetRequiredService<IOptions<MemoryExchangeOptions>>().Value;

    logger.LogInformation("Using provider: {Provider}", providerType);

    var pipeline = host.Services.GetRequiredService<IndexingPipeline>();
    await pipeline.RunAsync(sourcePath, forceRebuild, options.IndexName);
}

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
