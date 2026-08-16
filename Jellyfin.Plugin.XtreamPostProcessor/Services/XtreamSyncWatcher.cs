using Jellyfin.Plugin.XtreamPostProcessor.Sync;
using Jellyfin.Plugin.XtreamPostProcessor.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

internal sealed class XtreamSyncWatcher : IHostedService, IDisposable
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ITaskManager _taskManager;
    private readonly SyncHistoryReader _historyReader;
    private readonly ILogger<XtreamSyncWatcher> _logger;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _debounce;
    private string? _lastQueuedIdentity;

    public XtreamSyncWatcher(
        IApplicationPaths applicationPaths,
        ITaskManager taskManager,
        SyncHistoryReader historyReader,
        ILogger<XtreamSyncWatcher> logger)
    {
        _applicationPaths = applicationPaths;
        _taskManager = taskManager;
        _historyReader = historyReader;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration?.Enabled != true)
        {
            return;
        }

        var path = ResolveDataPath(configuration.SyncHistoryRelativePath);
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (directory is null || !Directory.Exists(directory))
        {
            _logger.LogWarning("Xtream sync-history directory is unavailable: {Directory}", directory);
            return;
        }

        _lifetime = new CancellationTokenSource();
        if (File.Exists(path))
        {
            _lastQueuedIdentity = (await _historyReader.ReadLatestAsync(path, cancellationToken).ConfigureAwait(false))?.Identity;
        }

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnRenamed;
        _logger.LogInformation("Watching Xtream sync history at {Path}", path);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime?.Cancel();
        _debounce?.Cancel();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _lifetime?.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs) => Schedule(eventArgs.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs eventArgs) => Schedule(eventArgs.FullPath);

    private void Schedule(string path)
    {
        var lifetime = _lifetime;
        var configuration = Plugin.Instance?.Configuration;
        if (lifetime is null || lifetime.IsCancellationRequested || configuration is null)
        {
            return;
        }

        var debounce = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var previous = Interlocked.Exchange(ref _debounce, debounce);
        previous?.Cancel();
        previous?.Dispose();
        _ = ProcessAfterDelayAsync(path, configuration.WatchDebounceSeconds, debounce.Token);
    }

    private async Task ProcessAfterDelayAsync(string path, int debounceSeconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, debounceSeconds)), cancellationToken).ConfigureAwait(false);
            var result = await _historyReader.ReadLatestAsync(path, cancellationToken).ConfigureAwait(false);
            if (result?.Success != true || string.Equals(result.Identity, _lastQueuedIdentity, StringComparison.Ordinal))
            {
                return;
            }

            _lastQueuedIdentity = result.Identity;
            _logger.LogInformation("Queuing Xtream enrichment audit for sync {SyncIdentity}", result.Identity);
            _taskManager.QueueIfNotRunning<EnrichXtreamTask>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to process Xtream sync-history change");
        }
    }

    private string ResolveDataPath(string configuredPath) => Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.Combine(_applicationPaths.DataPath, configuredPath);
}
