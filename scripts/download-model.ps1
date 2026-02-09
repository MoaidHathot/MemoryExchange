<#
.SYNOPSIS
    Downloads the all-MiniLM-L6-v2 ONNX embedding model required by MemoryExchange.

.DESCRIPTION
    Downloads the model from HuggingFace and places it in the expected directory
    (src/MemoryExchange.Local/Models/all-MiniLM-L6-v2.onnx).

.PARAMETER OutputPath
    Override the output file path. Defaults to src/MemoryExchange.Local/Models/all-MiniLM-L6-v2.onnx
    relative to the repository root.
#>
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$ModelUrl = 'https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx'
$ModelFileName = 'all-MiniLM-L6-v2.onnx'

# Resolve repository root (parent of the scripts directory)
$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not $OutputPath) {
    $OutputPath = Join-Path $RepoRoot 'src' 'MemoryExchange.Local' 'Models' $ModelFileName
}

$OutputDir = Split-Path -Parent $OutputPath

if (Test-Path $OutputPath) {
    Write-Host "Model already exists at: $OutputPath"
    Write-Host "Delete it first if you want to re-download."
    exit 0
}

if (-not (Test-Path $OutputDir)) {
    Write-Host "Creating directory: $OutputDir"
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

Write-Host "Downloading $ModelFileName (~90 MB) from HuggingFace..."
Write-Host "URL: $ModelUrl"
Write-Host "Destination: $OutputPath"
Write-Host ""

try {
    $ProgressPreference = 'SilentlyContinue' # speeds up Invoke-WebRequest significantly
    Invoke-WebRequest -Uri $ModelUrl -OutFile $OutputPath -UseBasicParsing
    $ProgressPreference = 'Continue'
}
catch {
    Write-Error "Download failed: $_"
    if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }
    exit 1
}

$size = (Get-Item $OutputPath).Length / 1MB
Write-Host ""
Write-Host "Download complete! ($([math]::Round($size, 1)) MB)"
Write-Host "Model saved to: $OutputPath"
