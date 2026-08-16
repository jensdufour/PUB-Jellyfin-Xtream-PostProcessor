using Jellyfin.Plugin.XtreamPostProcessor.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

/// <summary>
/// Preserves normalization requests that arrive while normalization is running.
/// </summary>
public sealed class DeferredTaskScheduler
{
    private readonly object _gate = new();
    private readonly ITaskManager _taskManager;
    private bool _normalizationPending;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeferredTaskScheduler"/> class.
    /// </summary>
    public DeferredTaskScheduler(ITaskManager taskManager)
    {
        _taskManager = taskManager;
        _taskManager.TaskExecuting += (_, eventArgs) => OnTaskExecuting(eventArgs.Argument);
        _taskManager.TaskCompleted += OnTaskCompleted;
    }

    internal void QueueNormalization()
    {
        lock (_gate)
        {
            _normalizationPending = true;
        }

        QueueIfIdle();
    }

    private void OnTaskExecuting(IScheduledTaskWorker task)
    {
        if (task.ScheduledTask is not NormalizeXtreamTask)
        {
            return;
        }

        lock (_gate)
        {
            _normalizationPending = false;
        }
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs eventArgs)
    {
        if (eventArgs.Task.ScheduledTask is NormalizeXtreamTask)
        {
            QueueIfIdle();
        }
    }

    private void QueueIfIdle()
    {
        bool pending;
        lock (_gate)
        {
            pending = _normalizationPending;
        }

        var worker = _taskManager.ScheduledTasks.First(task => task.ScheduledTask is NormalizeXtreamTask);
        if (pending && worker.State == TaskState.Idle)
        {
            _taskManager.QueueIfNotRunning<NormalizeXtreamTask>();
        }
    }
}