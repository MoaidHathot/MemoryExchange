using MemoryExchange.Core.Models;

namespace MemoryExchange.Core.Abstractions;

/// <summary>
/// A single search result returned by an <see cref="ISearchService"/> implementation.
/// Contains the matched chunk and its provider-native relevance score.
/// </summary>
/// <param name="Chunk">The matched memory chunk.</param>
/// <param name="Score">Provider-native relevance score (higher is better).</param>
public record SearchHit(MemoryChunk Chunk, double Score);
