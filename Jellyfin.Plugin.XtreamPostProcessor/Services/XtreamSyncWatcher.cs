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
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Interlocked.Exchange(ref _pending, 1);
                    _logger.LogError(exception, "Could not queue Xtream canonical metadata; will retry on the next completion event");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _taskManager.TaskCompleted -= OnTaskCompleted;
        }
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
        if (key is "XtreamLibrarySync" or "RefreshLibrary" or "MergeMoviesTask" or "MergeEpisodesTask" or "XtreamPostProcessorNormalize")
            _signals.Writer.TryWrite(true);
    }
}
