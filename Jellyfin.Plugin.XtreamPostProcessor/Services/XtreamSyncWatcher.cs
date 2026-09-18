using System.Threading.Channels;
using Jellyfin.Plugin.XtreamPostProcessor.Configuration;
using Jellyfin.Plugin.XtreamPostProcessor.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

internal sealed class XtreamSyncWatcher : BackgroundService
{
    private readonly ITaskManager _taskManager;
    private readonly LibraryAuditService _auditService;
    private readonly ILogger<XtreamSyncWatcher> _logger;
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private int _pending = 1;

    public XtreamSyncWatcher(
        ITaskManager taskManager,
        LibraryAuditService auditService,
        ILogger<XtreamSyncWatcher> logger)
    {
        _taskManager = taskManager;
        _auditService = auditService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is not null) plugin.ConfigurationChanged += OnConfigurationChanged;
        _taskManager.TaskCompleted += OnTaskCompleted;
        _signals.Writer.TryWrite(true);
        try
        {
            await foreach (var signal in _signals.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                var configuration = Plugin.Instance?.Configuration;
                if (configuration?.Enabled != true) continue;
                try
                {
                    await ProcessPendingAsync(configuration, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref _pending, 1);
                    _logger.LogError(exception, "Xtream processing stopped; inspect the task result and library-flow checkpoint before retrying");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (plugin is not null) plugin.ConfigurationChanged -= OnConfigurationChanged;
            _taskManager.TaskCompleted -= OnTaskCompleted;
        }
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration configuration)
    {
        Interlocked.Exchange(ref _pending, 1);
        _signals.Writer.TryWrite(true);
    }

    internal async Task ProcessPendingAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!configuration.Enabled || Interlocked.Exchange(ref _pending, 0) == 0) return;
        try
        {
            var sync = await _auditService.ReadLatestSyncAsync(configuration, cancellationToken).ConfigureAwait(false);
            var worker = _taskManager.ScheduledTasks.FirstOrDefault(task => task.ScheduledTask is NormalizeXtreamTask);
            if (!_auditService.CanProcess(sync) || worker?.State != TaskState.Idle)
            {
                Interlocked.Exchange(ref _pending, 1);
                return;
            }
            if (configuration.RunLibraryFlow)
            {
                if (configuration.AuditOnly || configuration.WriteBatchSize != 0 || !string.IsNullOrWhiteSpace(configuration.WriteItemId))
                    throw new InvalidOperationException("Library flow requires unrestricted metadata writes, not an audit or bounded item run");
                var merge = _taskManager.ScheduledTasks.SingleOrDefault(task => task.ScheduledTask.Key == "MergeMoviesTask");
                if (merge?.ScheduledTask.GetType().Assembly.GetName().Version is not { } version || version < new Version(12, 0, 1, 0))
                    throw new InvalidOperationException("Library flow requires Merge Versions 12.0.1 or newer with awaited task completion");
                var scan = _taskManager.ScheduledTasks.Single(task => task.ScheduledTask.Key == "RefreshLibrary").LastExecutionResult!;
                var cycle = $"{sync!.Identity}|{scan.StartTimeUtc:O}|{scan.EndTimeUtc:O}";
                var flow = new NativeLibraryFlow(_taskManager, _auditService.ResolveOwnedStatePath("xtream-post-processor/library-flow.json"), _logger);
                await flow.RunAsync(cycle, async token =>
                {
                    if (!configuration.RunLibraryFlow) throw new OperationCanceledException("Library flow disabled");
                    if (_taskManager.ScheduledTasks.Any(task => task.ScheduledTask.Key == "XtreamPostProcessorEnrich" && task.State != TaskState.Idle))
                        throw new InvalidOperationException("Legacy enrichment is active; library flow deferred");
                    await _auditService.EnsureCanWriteAsync(configuration, sync, token).ConfigureAwait(false);
                    var currentScan = _taskManager.ScheduledTasks.Single(task => task.ScheduledTask.Key == "RefreshLibrary").LastExecutionResult;
                    if (currentScan?.StartTimeUtc != scan.StartTimeUtc || currentScan.EndTimeUtc != scan.EndTimeUtc)
                        throw new InvalidOperationException("Library scan changed during the flow");
                }, cancellationToken).ConfigureAwait(false);
                return;
            }
            _taskManager.QueueIfNotRunning<NormalizeXtreamTask>();
            _logger.LogInformation("Queued canonical metadata processing for indexed sync {SyncIdentity}", sync!.Identity);
        }
        catch
        {
            Interlocked.Exchange(ref _pending, 1);
            throw;
        }
    }

    internal void OnTaskCompleted(object? sender, TaskCompletionEventArgs eventArgs)
    {
        var key = eventArgs.Task.ScheduledTask.Key;
        if (key is "XtreamLibrarySync" or "RefreshLibrary") Interlocked.Exchange(ref _pending, 1);
        if (key is "XtreamLibrarySync" or "RefreshLibrary" or "MergeMoviesTask" or "MergeEpisodesTask" or "XtreamPostProcessorNormalize" or "task-meilisearch-reindex-full")
            _signals.Writer.TryWrite(true);
    }
}
