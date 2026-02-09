#!/usr/bin/env bash
#
# Downloads the all-MiniLM-L6-v2 ONNX embedding model required by MemoryExchange.
#
# Usage:
#   ./scripts/download-model.sh [output-path]
#
# If output-path is omitted, the model is placed in
# src/MemoryExchange.Local/Models/all-MiniLM-L6-v2.onnx relative to the repo root.

set -euo pipefail

MODEL_URL="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx"
MODEL_FILENAME="all-MiniLM-L6-v2.onnx"

# Resolve repository root (parent of the scripts directory)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

OUTPUT_PATH="${1:-$REPO_ROOT/src/MemoryExchange.Local/Models/$MODEL_FILENAME}"
OUTPUT_DIR="$(dirname "$OUTPUT_PATH")"

if [ -f "$OUTPUT_PATH" ]; then
    echo "Model already exists at: $OUTPUT_PATH"
    echo "Delete it first if you want to re-download."
    exit 0
fi

mkdir -p "$OUTPUT_DIR"

echo "Downloading $MODEL_FILENAME (~90 MB) from HuggingFace..."
echo "URL: $MODEL_URL"
echo "Destination: $OUTPUT_PATH"
echo ""

if command -v curl &> /dev/null; then
    curl -L --progress-bar -o "$OUTPUT_PATH" "$MODEL_URL"
elif command -v wget &> /dev/null; then
    wget --show-progress -O "$OUTPUT_PATH" "$MODEL_URL"
else
    echo "Error: Neither curl nor wget found. Please install one of them."
    exit 1
fi

SIZE=$(du -h "$OUTPUT_PATH" | cut -f1)
echo ""
echo "Download complete! ($SIZE)"
echo "Model saved to: $OUTPUT_PATH"
