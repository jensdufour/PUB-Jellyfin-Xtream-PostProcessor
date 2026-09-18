using System.Text.Json;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

internal sealed class NativeLibraryFlow(ITaskManager taskManager, string statePath, ILogger logger)
{
    internal static readonly string[] TaskKeys =
        ["XtreamPostProcessorNormalize", "MergeMoviesTask", "MergeEpisodesTask", "task-meilisearch-reindex-full"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal sealed record Checkpoint
    {
        public int SchemaVersion { get; init; } = 1;
        public string Cycle { get; init; } = string.Empty;
        public int NextStage { get; init; }
        public string Status { get; init; } = "ready";
        public DateTime RequestedUtc { get; init; }
        public string? Error { get; init; }
    }

    internal async Task RunAsync(string cycle, Func<CancellationToken, Task> ensureReady, CancellationToken cancellationToken)
    {
        var state = File.Exists(statePath)
            ? JsonSerializer.Deserialize<Checkpoint>(await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException("Empty library-flow checkpoint")
            : new Checkpoint { Cycle = cycle };
        if (state.SchemaVersion != 1 || state.NextStage < 0 || state.NextStage > TaskKeys.Length
            || state.Status is not ("ready" or "running" or "failed" or "completed"))
            throw new InvalidDataException("Invalid library-flow checkpoint");
        if (state.Cycle != cycle) state = new Checkpoint { Cycle = cycle };
        if (state.Status is "completed" or "failed") return;

        var workers = TaskKeys.Select(key => taskManager.ScheduledTasks.SingleOrDefault(worker => worker.ScheduledTask.Key == key)
            ?? throw new InvalidOperationException($"Library flow requires task {key}")).ToArray();
        if (workers.Any(worker => worker.Triggers.Any()))
            throw new InvalidOperationException("Remove independent title, merge and search task triggers before enabling library flow");

        while (state.NextStage < workers.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worker = workers[state.NextStage];
            try
            {
                await ensureReady(cancellationToken).ConfigureAwait(false);
                if (workers.Any(candidate => candidate.Triggers.Any()))
                    throw new InvalidOperationException("Independent task triggers changed during the library flow");
                if (workers.Any(candidate => candidate.State != TaskState.Idle))
                    throw new InvalidOperationException("A library-flow task is already active; no duplicate execution started");
                if (state.Status != "running")
                {
                    state = state with { Status = "running", RequestedUtc = DateTime.UtcNow, Error = null };
                    await SaveAsync(state).ConfigureAwait(false);
                    logger.LogInformation("Library flow {Cycle}: starting {TaskKey}", cycle, worker.ScheduledTask.Key);
                    await taskManager.Execute(worker, new TaskOptions()).ConfigureAwait(false);
                }
                var result = worker.LastExecutionResult;
                if (worker.State != TaskState.Idle || result?.Status != TaskCompletionStatus.Completed
                    || result.StartTimeUtc < state.RequestedUtc || result.EndTimeUtc < result.StartTimeUtc)
                    throw new InvalidOperationException($"Library flow stopped at {worker.ScheduledTask.Key}: no successful current-run result");
                await ensureReady(cancellationToken).ConfigureAwait(false);
                state = state with { NextStage = state.NextStage + 1, Status = "ready" };
                await SaveAsync(state).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                state = state with { Status = "failed", Error = exception.Message };
                await SaveAsync(state).ConfigureAwait(false);
                throw;
            }
        }
        await SaveAsync(state with { Status = "completed", Error = null }).ConfigureAwait(false);
        logger.LogInformation("Library flow {Cycle}: all native tasks completed", cycle);
    }

    private async Task SaveAsync(Checkpoint state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temporary = statePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions), CancellationToken.None).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, statePath, true);
    }
}