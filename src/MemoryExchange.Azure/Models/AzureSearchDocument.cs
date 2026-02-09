using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using MemoryExchange.Core.Models;

namespace MemoryExchange.Azure.Models;

/// <summary>
/// Azure AI Search document DTO with field attributes.
/// Maps to/from the provider-agnostic <see cref="MemoryChunk"/>.
/// </summary>
internal class AzureSearchDocument
{
    [SimpleField(IsKey = true, IsFilterable = true)]
    public string Id { get; set; } = string.Empty;

    [SearchableField(AnalyzerName = LexicalAnalyzerName.Values.EnLucene)]
    public string Content { get; set; } = string.Empty;

    [SimpleField(IsFilterable = true)]
    public string SourceFile { get; set; } = string.Empty;

    [SearchableField]
    public string HeadingPath { get; set; } = string.Empty;

    [SimpleField(IsFilterable = true, IsFacetable = true)]
    public string Domain { get; set; } = string.Empty;

    [SearchableField]
    public IList<string> Tags { get; set; } = new List<string>();

    [SimpleField]
    public IList<string> RelatedFiles { get; set; } = new List<string>();

    [SimpleField(IsFilterable = true)]
    public bool IsInstruction { get; set; }

    [VectorSearchField(VectorSearchDimensions = 1536, VectorSearchProfileName = "default-vector-profile")]
    public float[]? Embedding { get; set; }

    [SimpleField]
    public DateTimeOffset LastUpdated { get; set; }

    [SimpleField(IsFilterable = true)]
    public int ChunkIndex { get; set; }

    /// <summary>
    /// Converts a provider-agnostic <see cref="MemoryChunk"/> to an Azure search document.
    /// </summary>
    public static AzureSearchDocument FromChunk(MemoryChunk chunk) => new()
    {
        Id = chunk.Id,
        Content = chunk.Content,
        SourceFile = chunk.SourceFile,
        HeadingPath = chunk.HeadingPath,
        Domain = chunk.Domain,
        Tags = chunk.Tags,
        RelatedFiles = chunk.RelatedFiles,
        IsInstruction = chunk.IsInstruction,
        Embedding = chunk.Embedding,
        LastUpdated = chunk.LastUpdated,
        ChunkIndex = chunk.ChunkIndex
    };

    /// <summary>
    /// Converts this Azure search document back to a provider-agnostic <see cref="MemoryChunk"/>.
    /// </summary>
    public MemoryChunk ToChunk() => new()
    {
        Id = Id,
        Content = Content,
        SourceFile = SourceFile,
        HeadingPath = HeadingPath,
        Domain = Domain,
        Tags = Tags,
        RelatedFiles = RelatedFiles,
        IsInstruction = IsInstruction,
        Embedding = Embedding,
        LastUpdated = LastUpdated,
        ChunkIndex = ChunkIndex
    };
}
