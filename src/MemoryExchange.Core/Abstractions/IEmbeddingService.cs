namespace MemoryExchange.Core.Abstractions;

/// <summary>
/// Generates vector embeddings from text content.
/// Implemented by provider-specific services (e.g., Azure OpenAI, ONNX local model).
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates embeddings for a batch of text inputs.
    /// </summary>
    /// <param name="texts">List of text content to embed.</param>
    /// <returns>List of embedding vectors in the same order as inputs.</returns>
    Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts);

    /// <summary>
    /// Generates an embedding for a single text input.
    /// </summary>
    /// <param name="text">The text content to embed.</param>
    /// <returns>The embedding vector.</returns>
    Task<float[]> GenerateEmbeddingAsync(string text);
}
