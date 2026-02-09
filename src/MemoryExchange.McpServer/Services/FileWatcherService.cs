using MemoryExchange.Core.Configuration;
using MemoryExchange.Indexing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryExchange.McpServer.Services;

/// <summary>
/// Background service that watches the source directory for markdown file changes
/// and triggers incremental re-indexing. Implies --build-index on startup (builds
/// or updates the index before starting the watcher).
/// </summary>
public sealed class FileWatcherService : BackgroundService
{
    private readonly IndexingPipeline _pipeline;
    private readonly MemoryExchangeOptions _options;
    private readonly ILogger<FileWatcherService> _logger;

    /// <summary>
    /// Debounce window — changes within this period are batched into a single re-index.
    /// </summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);

    private FileSystemWatcher? _watcher;
    private readonly Channel _changeChannel = new();

    public FileWatcherService(
        IndexingPipeline pipeline,
        IOptions<MemoryExchangeOptions> options,
        ILogger<FileWatcherService> logger)
    {
        _pipeline = pipeline;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.SourcePath))
        {
            _logger.LogError("--watch requires --source-path to be set");
            return;
        }

        var sourcePath = Path.GetFullPath(_options.SourcePath);
        if (!Directory.Exists(sourcePath))
        {
            _logger.LogError("Source directory '{Path}' does not exist", sourcePath);
            return;
        }

        // Build/update the index on startup (--watch implies --build-index)
        _logger.LogInformation("Watch mode: building/updating index before starting file watcher...");
        try
        {
            await _pipeline.RunAsync(sourcePath, forceRebuild: false, _options.IndexName);
            _logger.LogInformation("Initial index build complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build initial index. File watcher will still start.");
        }

        // Start watching
        _watcher = new FileSystemWatcher(sourcePath)
        {
            Filter = "*.md",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileChanged;
        _watcher.Changed += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;

        _logger.LogInformation("File watcher started on {Path}", sourcePath);

        // Debounce loop: wait for changes, then re-index
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for a change signal
                await _changeChannel.WaitAsync(stoppingToken);

                // Debounce: keep waiting while more changes arrive
                while (true)
                {
                    var moreChanges = await _changeChannel.WaitWithTimeoutAsync(DebounceDelay, stoppingToken);
                    if (!moreChanges)
                        break; // No more changes within the debounce window
                }

                _logger.LogInformation("Changes detected — running incremental re-index...");

                try
                {
                    await _pipeline.RunAsync(sourcePath, forceRebuild: false, _options.IndexName);
                    _logger.LogInformation("Incremental re-index complete.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Incremental re-index failed");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _logger.LogDebug("File {ChangeType}: {Path}", e.ChangeType, e.Name);
        _changeChannel.Signal();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _logger.LogDebug("File renamed: {OldName} -> {Name}", e.OldName, e.Name);
        _changeChannel.Signal();
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Simple signaling primitive for debounce coordination.
    /// Uses a SemaphoreSlim to allow the debounce loop to wait for and drain signals.
    /// </summary>
    private sealed class Channel
    {
        private readonly SemaphoreSlim _semaphore = new(0);

        /// <summary>Signal that a change occurred.</summary>
        public void Signal()
        {
            // Only add a signal if there isn't one already pending
            if (_semaphore.CurrentCount == 0)
                _semaphore.Release();
        }

        /// <summary>Wait for a change signal.</summary>
        public async Task WaitAsync(CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
        }

        /// <summary>
        /// Wait for a change signal with a timeout. Returns true if a signal was received,
        /// false if the timeout elapsed.
        /// </summary>
        public async Task<bool> WaitWithTimeoutAsync(TimeSpan timeout, CancellationToken ct)
        {
            return await _semaphore.WaitAsync(timeout, ct);
        }
    }
}
