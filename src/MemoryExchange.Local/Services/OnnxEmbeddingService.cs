using MemoryExchange.Core.Abstractions;
using MemoryExchange.Local.Configuration;
using MemoryExchange.Local.Tokenization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MemoryExchange.Local.Services;

/// <summary>
/// Generates 384-dimensional embeddings using a local all-MiniLM-L6-v2 ONNX model.
/// Thread-safe: the InferenceSession and tokenizer are created once and reused.
/// </summary>
public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    /// <summary>
    /// Output embedding dimension for all-MiniLM-L6-v2.
    /// </summary>
    public const int EmbeddingDimensions = 384;

    /// <summary>
    /// Maximum token sequence length for the model.
    /// </summary>
    private const int MaxSequenceLength = 256;

    private readonly Lazy<InferenceSession> _session;
    private readonly Lazy<WordPieceTokenizer> _tokenizer;
    private readonly LocalProviderOptions _options;
    private readonly ILogger<OnnxEmbeddingService> _logger;

    public OnnxEmbeddingService(IOptions<LocalProviderOptions> options, ILogger<OnnxEmbeddingService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _session = new Lazy<InferenceSession>(CreateSession, isThreadSafe: true);
        _tokenizer = new Lazy<WordPieceTokenizer>(() => new WordPieceTokenizer(), isThreadSafe: true);
    }

    /// <inheritdoc />
    public Task<float[]> GenerateEmbeddingAsync(string text)
    {
        var embedding = GenerateEmbedding(text);
        return Task.FromResult(embedding);
    }

    /// <inheritdoc />
    public Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts)
    {
        var results = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            results.Add(GenerateEmbedding(text));
        }
        return Task.FromResult(results);
    }

    private float[] GenerateEmbedding(string text)
    {
        var session = _session.Value;
        var tokenizer = _tokenizer.Value;

        // Tokenize using our WordPiece tokenizer
        var (inputIds, attentionMask, tokenTypeIds) = tokenizer.Encode(text, MaxSequenceLength);

        // Find the actual (non-padded) sequence length for the attention mask
        var seqLength = 0;
        for (int i = 0; i < attentionMask.Length; i++)
        {
            if (attentionMask[i] == 1) seqLength = i + 1;
        }

        // Create input tensors with shape [1, MaxSequenceLength]
        var dims = new ReadOnlySpan<int>([1, MaxSequenceLength]);
        var inputIdsTensor = new DenseTensor<long>(inputIds.AsMemory(), dims);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask.AsMemory(), dims);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds.AsMemory(), dims);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        // Run inference
        using var results = session.Run(inputs);

        // The model outputs token embeddings with shape [1, MaxSequenceLength, 384]
        // We need to mean-pool over the token dimension (ignoring padding)
        var tokenEmbeddings = results.First().AsTensor<float>();
        return MeanPool(tokenEmbeddings, attentionMask, MaxSequenceLength);
    }

    /// <summary>
    /// Mean-pools token embeddings using the attention mask (ignores padding tokens).
    /// Result is L2-normalized.
    /// </summary>
    private static float[] MeanPool(Tensor<float> tokenEmbeddings, long[] attentionMask, int tokenCount)
    {
        var embedding = new float[EmbeddingDimensions];
        var validTokens = 0;

        for (int t = 0; t < tokenCount; t++)
        {
            if (attentionMask[t] == 0) continue;
            validTokens++;
            for (int d = 0; d < EmbeddingDimensions; d++)
            {
                embedding[d] += tokenEmbeddings[0, t, d];
            }
        }

        if (validTokens > 0)
        {
            for (int d = 0; d < EmbeddingDimensions; d++)
            {
                embedding[d] /= validTokens;
            }
        }

        // L2 normalize
        var norm = 0.0f;
        for (int d = 0; d < EmbeddingDimensions; d++)
        {
            norm += embedding[d] * embedding[d];
        }
        norm = MathF.Sqrt(norm);

        if (norm > 0)
        {
            for (int d = 0; d < EmbeddingDimensions; d++)
            {
                embedding[d] /= norm;
            }
        }

        return embedding;
    }

    private InferenceSession CreateSession()
    {
        var modelPath = ResolveModelPath();
        _logger.LogInformation("Loading ONNX embedding model from {ModelPath}", modelPath);

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
        };
        sessionOptions.AppendExecutionProvider_CPU();

        return new InferenceSession(modelPath, sessionOptions);
    }

    private string ResolveModelPath()
    {
        // If explicitly configured, use that path
        if (!string.IsNullOrEmpty(_options.ModelPath) && File.Exists(_options.ModelPath))
        {
            return _options.ModelPath;
        }

        // Look for the bundled model next to the assembly
        var assemblyDir = Path.GetDirectoryName(typeof(OnnxEmbeddingService).Assembly.Location)!;
        var bundledPath = Path.Combine(assemblyDir, "Models", "all-MiniLM-L6-v2.onnx");
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        // Fallback: check current directory
        var localPath = Path.Combine(Directory.GetCurrentDirectory(), "Models", "all-MiniLM-L6-v2.onnx");
        if (File.Exists(localPath))
        {
            return localPath;
        }

        throw new FileNotFoundException(
            "Could not find the ONNX embedding model. Place 'all-MiniLM-L6-v2.onnx' in the Models directory " +
            "or set the ModelPath in configuration.",
            "all-MiniLM-L6-v2.onnx");
    }

    public void Dispose()
    {
        if (_session.IsValueCreated)
        {
            _session.Value.Dispose();
        }
    }
}
