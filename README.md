# MemoryExchange

A .NET tool that makes markdown knowledge files (such as Cline-style "Memory Banks") searchable by GitHub Copilot (or any MCP-compatible client) via the **Model Context Protocol (MCP)**.

It pre-processes markdown files into semantically chunked, vector-embedded documents and serves them through an MCP server with tools for searching, retrieving files, and diagnosing index health.

## Features

- **Fully local by default** — SQLite + ONNX (`all-MiniLM-L6-v2`) embeddings, no cloud dependencies
- **Azure provider** available — Azure AI Search + Azure OpenAI for cloud-scale deployments
- **Hybrid search** — FTS5 BM25 keyword search + cosine vector similarity, merged via Reciprocal Rank Fusion (RRF)
- **Domain-aware boosting** — chunks matching the user's current file domain are boosted (1.3x)
- **Instruction boosting** — `.instructions.md` files get additional priority (1.2x)
- **Heading-aware chunking** — markdown is split by headings with breadcrumb context; code blocks are kept atomic
- **Zero-install via `dotnet dnx`** — run directly with `dotnet dnx --yes MemoryExchange`
- **Also installable as a .NET global tool** — `dotnet tool install -g MemoryExchange`

## Quick Start

### Configure your MCP client

Add the server to your MCP client configuration (e.g. VS Code `mcp.json`, Cline, etc.):

**Option A: Via `dotnet dnx` (recommended — no install required, .NET 10+)**

```json
{
  "servers": {
    "memory-exchange": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "dnx",
        "--yes",
        "MemoryExchange",
        "--source-path", "/path/to/your/memory-exchange",
        "--provider", "local",
        "--build-index"
      ]
    }
  }
}
```

**Option B: As a .NET global tool**

```bash
dotnet tool install -g MemoryExchange
```

```json
{
  "servers": {
    "memory-exchange": {
      "type": "stdio",
      "command": "memory-exchange",
      "args": [
        "--source-path", "/path/to/your/memory-exchange",
        "--provider", "local",
        "--build-index"
      ]
    }
  }
}
```

**Option C: Environment variables**

```json
{
  "servers": {
    "memory-exchange": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "dnx",
        "--yes",
        "MemoryExchange",
        "--build-index"
      ],
      "env": {
        "MEMORYEXCHANGE_SOURCEPATH": "/path/to/your/memory-exchange",
        "MEMORYEXCHANGE_PROVIDER": "local"
      }
    }
  }
}
```

**Option D: From source**

```json
{
  "servers": {
    "memory-exchange": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project", "/path/to/MemoryExchange/src/MemoryExchange.McpServer",
        "--",
        "--source-path", "/path/to/your/memory-exchange",
        "--provider", "local",
        "--build-index"
      ]
    }
  }
}
```

> With `--build-index`, the server indexes your source directory on startup (incremental — only changed files are re-processed) and then starts accepting MCP connections. No separate indexer step required.
>
> Alternatively, use `--watch` instead of `--build-index` to keep the index updated automatically while the server runs. The watcher builds the index on startup and then monitors for file changes (creates, edits, deletes, renames), triggering incremental re-indexing as needed.

### Stand-alone Indexer

You can also index separately using the CLI indexer:

```bash
dotnet run --project src/MemoryExchange.Indexer -- \
  --source /path/to/your/memory-exchange \
  --provider local \
  --database-path /path/to/memoryexchange.db
```

| Flag | Description |
|------|-------------|
| `--source`, `-s` | **(required)** Path to the source directory containing markdown files |
| `--provider`, `-p` | `local` (default) or `azure` |
| `--database-path` | SQLite database file path (defaults to `memory_exchange.db` in the source directory) |
| `--model-path` | Custom ONNX model file path (local provider) |
| `--index-name` | Logical index name (default: `memory-exchange`) |
| `--force`, `-f` | Force full re-index, ignoring cached state |
| `--exclude` | Glob patterns to exclude from indexing (can be specified multiple times) |
| `--watch`, `-w` | After indexing, watch for file changes and re-index incrementally (runs until Ctrl+C) |

## Configuration

Configuration is loaded with the following precedence (highest wins):

1. `appsettings.json`
2. Environment variables (`MEMORYEXCHANGE_` prefix)
3. CLI arguments

### Environment Variables

| Variable | Maps to |
|----------|---------|
| `MEMORYEXCHANGE_SOURCEPATH` | Source directory path |
| `MEMORYEXCHANGE_PROVIDER` | `local` or `azure` |
| `MEMORYEXCHANGE_INDEXNAME` | Search index name |
| `MEMORYEXCHANGE_DATABASEPATH` | SQLite database path (defaults to `memory_exchange.db` in the source directory) |
| `MEMORYEXCHANGE_MODELPATH` | ONNX model file path |
| `MEMORYEXCHANGE_BUILDINDEX` | Set to `true` to build index on startup |
| `MEMORYEXCHANGE_WATCH` | Set to `true` to enable file watching mode |
| `MEMORYEXCHANGE_AZURE_SEARCH_ENDPOINT` | Azure AI Search endpoint |
| `MEMORYEXCHANGE_AZURE_SEARCH_APIKEY` | Azure AI Search API key |
| `MEMORYEXCHANGE_AZURE_OPENAI_ENDPOINT` | Azure OpenAI endpoint |
| `MEMORYEXCHANGE_AZURE_OPENAI_APIKEY` | Azure OpenAI API key |
| `MEMORYEXCHANGE_AZURE_OPENAI_DEPLOYMENT` | Azure OpenAI embedding deployment name |

### MCP Server CLI Args

`--source-path`, `--provider`, `--index-name`, `--database-path`, `--model-path`, `--build-index`, `--watch`, `--exclude`, `--azure-search-endpoint`, `--azure-search-key`, `--azure-openai-endpoint`, `--azure-openai-key`, `--azure-openai-deployment`

> **`--build-index`** — When present, the MCP server runs incremental indexing on startup before accepting connections. This eliminates the need to run the Indexer CLI separately. Requires `--source-path` to be set.
>
> **`--watch`** — Monitors the source directory for markdown file changes and triggers incremental re-indexing automatically. Implies `--build-index` on startup (builds the index if it doesn't exist, then watches for changes). When `--watch` is active, a separate `--build-index` flag is redundant. Requires `--source-path` to be set.
>
> **`--exclude`** — Optional glob patterns to exclude files/directories from indexing. Can be specified multiple times (e.g., `--exclude "**/archive/**" --exclude "**/drafts/**"`). The `personal/` directory is always excluded regardless of this setting.
>
> **`--database-path`** — If omitted, defaults to `memory_exchange.db` inside the `--source-path` directory.

## MCP Tools

The server exposes the following tools:

### `search_memory_bank`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `query` | `string` | yes | What to search for (e.g. "how does caching work", "deployment architecture") |
| `currentFilePath` | `string` | no | File the user is editing — enables domain-aware boosting |
| `topK` | `int` | no | Number of results, 1-10 (default: 5) |

### `get_memory_file`

Retrieves the full content of a specific markdown file from the memory exchange. Useful when a search result references a file and the agent needs the complete context.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | `string` | yes | Relative path to the markdown file (e.g. "architecture/database.md") |

### `get_index_status`

Returns diagnostic information about the memory exchange index. Use this to verify the index is populated before searching, or to diagnose why `search_memory_bank` returns no results.

No parameters. Returns the source path, provider, index name, chunk count, file count, and last indexed time. When the index is empty, it includes actionable troubleshooting hints.

## Startup Diagnostics

When the MCP server starts, it prints diagnostic information to stderr including the configured provider, source path, index name, watch/build-index mode, and the number of markdown files found in the source directory. This output is visible in the MCP client's server logs and is useful for confirming the server is configured correctly.

## Architecture

```
src/
  MemoryExchange.Core/          Core abstractions, chunking, search orchestrator
  MemoryExchange.Azure/         Azure AI Search + Azure OpenAI provider
  MemoryExchange.Local/         SQLite + ONNX provider (default)
  MemoryExchange.Indexing/      Shared indexing pipeline (FileScanner + IndexingPipeline)
  MemoryExchange.Indexer/       CLI indexer tool
  MemoryExchange.McpServer/     MCP stdio server (supports --build-index)
tests/
  MemoryExchange.Indexer.LocalRunner/   Development test harness
```

### Provider Abstractions

- `IEmbeddingService` — generate vector embeddings from text
- `ISearchService` — read-side: hybrid search (keyword + vector)
- `ISearchIndex` — write-side: upsert/delete chunks

### Search Pipeline

1. Query is embedded via `IEmbeddingService`
2. `ISearchService` runs FTS5 BM25 + vector cosine similarity, merged via RRF (k=60)
3. `SearchOrchestrator` applies domain boosting (1.3x) and instruction boosting (1.2x)
4. Results are over-fetched at 2x, reranked, and trimmed to `topK`

### Local Provider Details

- **Model:** `all-MiniLM-L6-v2` (384-dim, L2-normalized)
- **Tokenizer:** Custom `WordPieceTokenizer` with embedded 30k vocab
- **Storage:** SQLite with WAL mode, FTS5 virtual table, embeddings as BLOBs
- **Vector search:** Pure C# cosine similarity (dot product on normalized vectors)

## Building from Source

Requires .NET 9.0 SDK.

### 1. Download the ONNX model

The published NuGet package bundles the model, but when building from source you need to download it manually. Use the included script to fetch the `all-MiniLM-L6-v2` embedding model (~90 MB) from HuggingFace:

```powershell
# Windows (PowerShell)
.\scripts\download-model.ps1
```

```bash
# Linux / macOS
./scripts/download-model.sh
```

This places the model in `src/MemoryExchange.Local/Models/all-MiniLM-L6-v2.onnx`. You can pass a custom output path as an argument if needed.

### 2. Build

```bash
dotnet build MemoryExchange.slnx
```

## License

[MIT](LICENSE) - Copyright (c) 2026 Moaid Hathot
