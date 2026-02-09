using Azure;
using Azure.AI.OpenAI;
using MemoryExchange.Azure.Configuration;
using MemoryExchange.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;

namespace MemoryExchange.Azure.Services;

/// <summary>
/// Generates embeddings using Azure OpenAI's embedding models.
/// Implements <see cref="IEmbeddingService"/>.
/// </summary>
public class AzureEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _client;
    private readonly ILogger<AzureEmbeddingService> _logger;
    private readonly int _dimensions;

    public AzureEmbeddingService(IOptions<AzureOpenAIOptions> options, ILogger<AzureEmbeddingService> logger)
    {
        _logger = logger;
        var config = options.Value;
        _dimensions = config.EmbeddingDimensions;

        var azureClient = new AzureOpenAIClient(
            new Uri(config.Endpoint),
            new AzureKeyCredential(config.ApiKey));
        _client = azureClient.GetEmbeddingClient(config.EmbeddingDeployment);
    }

    /// <inheritdoc />
    public async Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts)
    {
        if (texts.Count == 0)
            return new List<float[]>();

        _logger.LogInformation("Generating embeddings for {Count} chunks...", texts.Count);

        var results = new List<float[]>();
        var embeddingOptions = new EmbeddingGenerationOptions
        {
            Dimensions = _dimensions
        };

        // Process in batches of 16 (Azure OpenAI batch limit for embeddings)
        const int batchSize = 16;
        for (int i = 0; i < texts.Count; i += batchSize)
        {
            var batch = texts.Skip(i).Take(batchSize).ToList();
            _logger.LogDebug("Processing embedding batch {Start}-{End} of {Total}",
                i + 1, Math.Min(i + batchSize, texts.Count), texts.Count);

            var response = await _client.GenerateEmbeddingsAsync(batch, embeddingOptions);

            foreach (var embedding in response.Value)
            {
                results.Add(embedding.ToFloats().ToArray());
            }
        }

        _logger.LogInformation("Generated {Count} embeddings successfully", results.Count);
        return results;
    }

    /// <inheritdoc />
    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        var results = await GenerateEmbeddingsAsync(new List<string> { text });
        return results[0];
    }
}
