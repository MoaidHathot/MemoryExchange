namespace MemoryExchange.Local.Configuration;

/// <summary>
/// Configuration options for the local SQLite + ONNX provider.
/// Bind from "MemoryExchange:Local" configuration section.
/// </summary>
public class LocalProviderOptions
{
    public const string SectionName = "MemoryExchange:Local";

    /// <summary>
    /// Path to the SQLite database file. Defaults to "MemoryExchange.db" in the source directory.
    /// </summary>
    public string DatabasePath { get; set; } = "MemoryExchange.db";

    /// <summary>
    /// Path to the ONNX embedding model file. If null, uses the bundled all-MiniLM-L6-v2.onnx.
    /// </summary>
    public string? ModelPath { get; set; }
}
