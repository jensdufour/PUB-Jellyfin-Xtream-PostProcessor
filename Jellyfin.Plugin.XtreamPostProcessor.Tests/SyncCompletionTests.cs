using Jellyfin.Plugin.XtreamPostProcessor.Configuration;
using Jellyfin.Plugin.XtreamPostProcessor.Services;
using Jellyfin.Plugin.XtreamPostProcessor.State;
using Jellyfin.Plugin.XtreamPostProcessor.Sync;
using Jellyfin.Plugin.XtreamPostProcessor.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public sealed class SyncCompletionTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task NativeFlowAwaitsEachStageAndStopsOnFailure(int failedStage)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xtream-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "flow.json");
            var called = new List<string>();
            var results = new Dictionary<string, TaskResult>();
            var workers = NativeLibraryFlow.TaskKeys.Select(key => InterfaceStub.Create<IScheduledTaskWorker>((method, _) => method.Name switch
            {
                "get_ScheduledTask" => InterfaceStub.Create<IScheduledTask>((_, _) => key),
                "get_State" => TaskState.Idle,
                "get_Triggers" => Array.Empty<TaskTriggerInfo>(),
                "get_LastExecutionResult" => results.GetValueOrDefault(key),
                _ => throw new NotImplementedException(method.Name)
            })).ToArray();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Execute(IScheduledTaskWorker worker)
            {
                var key = worker.ScheduledTask.Key;
                var started = DateTime.UtcNow;
                called.Add(key);
                entered.TrySetResult();
                await release.Task;
                results[key] = new TaskResult { StartTimeUtc = started, EndTimeUtc = DateTime.UtcNow,
                    Status = called.Count - 1 == failedStage ? TaskCompletionStatus.Failed : TaskCompletionStatus.Completed };
            }
            var manager = InterfaceStub.Create<ITaskManager>((method, arguments) => method.Name switch
            {
                "get_ScheduledTasks" => workers,
                "Execute" => Execute((IScheduledTaskWorker)arguments![0]!),
                _ => throw new NotImplementedException(method.Name)
            });
            var flow = new NativeLibraryFlow(manager, path, NullLogger.Instance);
            var running = flow.RunAsync("cycle-one", _ => Task.CompletedTask, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(called);
            Assert.False(running.IsCompleted);
            release.SetResult();
            if (failedStage < 0) await running;
            else await Assert.ThrowsAsync<InvalidOperationException>(() => running);
            Assert.Equal(NativeLibraryFlow.TaskKeys.Take(failedStage < 0 ? 4 : failedStage + 1), called);
            var checkpoint = System.Text.Json.JsonSerializer.Deserialize<NativeLibraryFlow.Checkpoint>(await File.ReadAllTextAsync(path))!;
            Assert.Equal(failedStage < 0 ? "completed" : "failed", checkpoint.Status);
            await new NativeLibraryFlow(manager, path, NullLogger.Instance).RunAsync("cycle-one", _ => Task.CompletedTask, CancellationToken.None);
            Assert.Equal(failedStage < 0 ? 4 : failedStage + 1, called.Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("running-success")]
    [InlineData("running-stale")]
    [InlineData("running-failed")]
    [InlineData("running-cancelled")]
    [InlineData("running-aborted")]
    [InlineData("busy")]
    [InlineData("timer")]
    [InlineData("new-cycle")]
    [InlineData("changed-sync")]
    [InlineData("changed-timer")]
    [InlineData("cancelled")]
    public async Task NativeFlowRecoveryAndSafetyGates(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xtream-flow-guards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "flow.json");
            var requested = DateTime.UtcNow.AddMinutes(-2);
            var recovering = scenario.StartsWith("running-", StringComparison.Ordinal);
            var state = new NativeLibraryFlow.Checkpoint
            {
                Cycle = "cycle-one", NextStage = recovering ? 1 : 0,
                Status = recovering ? "running" : scenario == "new-cycle" ? "completed" : "ready", RequestedUtc = requested
            };
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(state));
            var results = new Dictionary<string, TaskResult>
            {
                ["MergeMoviesTask"] = new TaskResult
                {
                    Status = scenario switch { "running-failed" => TaskCompletionStatus.Failed, "running-cancelled" => TaskCompletionStatus.Cancelled,
                        "running-aborted" => TaskCompletionStatus.Aborted, _ => TaskCompletionStatus.Completed },
                    StartTimeUtc = scenario == "running-stale" ? requested.AddMinutes(-10) : requested.AddSeconds(1),
                    EndTimeUtc = requested.AddMinutes(1)
                }
            };
            var calls = new List<string>();
            var workers = NativeLibraryFlow.TaskKeys.Select(key => InterfaceStub.Create<IScheduledTaskWorker>((method, _) => method.Name switch
            {
                "get_ScheduledTask" => InterfaceStub.Create<IScheduledTask>((_, _) => key),
                "get_State" => scenario == "busy" && key == "MergeEpisodesTask" ? TaskState.Running : TaskState.Idle,
                "get_Triggers" => scenario == "timer" || (scenario == "changed-timer" && calls.Count > 0) ? new[] { new TaskTriggerInfo() } : Array.Empty<TaskTriggerInfo>(),
                "get_LastExecutionResult" => results.GetValueOrDefault(key),
                _ => throw new NotImplementedException(method.Name)
            })).ToArray();
            object Execute(IScheduledTaskWorker worker)
            {
                var key = worker.ScheduledTask.Key;
                calls.Add(key);
                results[key] = new TaskResult { Status = TaskCompletionStatus.Completed, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow };
                return Task.CompletedTask;
            }
            var manager = InterfaceStub.Create<ITaskManager>((method, arguments) => method.Name switch
            {
                "get_ScheduledTasks" => workers,
                "Execute" => Execute((IScheduledTaskWorker)arguments![0]!),
                _ => throw new NotImplementedException(method.Name)
            });
            using var cancellation = new CancellationTokenSource();
            if (scenario == "cancelled") cancellation.Cancel();
            Task Ensure(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (scenario == "changed-sync" && calls.Count > 0) throw new InvalidOperationException("New sync superseded the current flow");
                return Task.CompletedTask;
            }
            var operation = new NativeLibraryFlow(manager, path, NullLogger.Instance).RunAsync(
                scenario == "new-cycle" ? "cycle-two" : "cycle-one", Ensure, cancellation.Token);
            if (scenario is "running-success" or "new-cycle") await operation;
            else if (scenario == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            else await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            Assert.Equal(scenario switch { "running-success" => 2, "new-cycle" => 4, "changed-sync" or "changed-timer" => 1, _ => 0 }, calls.Count);
            if (scenario == "running-success") Assert.Equal(NativeLibraryFlow.TaskKeys.Skip(2), calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CoalescesEventsWaitsForIndexingAndRecoversAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xtream-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var ended = DateTimeOffset.UtcNow;
            var historyPath = Path.Combine(directory, "sync.json");
            await File.WriteAllTextAsync(historyPath, System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new XtreamSyncResult { StartTime = ended.AddMinutes(-1), EndTime = ended, Success = true }
            }));
            var configuration = new PluginConfiguration { SyncHistoryRelativePath = historyPath };
            var paths = InterfaceStub.Create<IApplicationPaths>((_, _) => directory);
            var busy = true;
            var library = InterfaceStub.Create<ILibraryManager>((method, _) => method.Name == "get_IsScanRunning" ? busy : throw new NotImplementedException(method.Name));
            var queued = 0;
            var workers = new List<IScheduledTaskWorker>();
            var manager = InterfaceStub.Create<ITaskManager>((method, _) => method.Name switch
            {
                "get_ScheduledTasks" => workers,
                "QueueIfNotRunning" => Queue(),
                _ => throw new InvalidOperationException("Unexpected scheduler mutation: " + method.Name)
            });
            object? Queue() { queued++; return null; }
            var scanResult = new TaskResult { Status = TaskCompletionStatus.Completed, StartTimeUtc = ended.UtcDateTime.AddMinutes(-3), EndTimeUtc = ended.UtcDateTime.AddMinutes(-2) };
            var scanTask = InterfaceStub.Create<IScheduledTask>((method, _) => method.Name == "get_Key" ? "RefreshLibrary" : throw new NotImplementedException());
            var scanWorker = Worker(scanTask, () => busy ? TaskState.Running : TaskState.Idle, () => scanResult);
            workers.Add(scanWorker);
            using var audit = new LibraryAuditService(library, paths, new SyncHistoryReader(), new EnrichmentStateReader(),
                InterfaceStub.Create<IServerConfigurationManager>((_, _) => throw new NotImplementedException()), manager);
            var normalizer = new NormalizeXtreamTask(audit, new AuditReportWriter(paths),
                new LibraryWriteService(library, InterfaceStub.Create<IProviderManager>((_, _) => throw new NotImplementedException())),
                new EnrichmentStateReader(), NullLogger<NormalizeXtreamTask>.Instance);
            var normalizing = false;
            var normalizationWorker = Worker(normalizer, () => normalizing ? TaskState.Running : TaskState.Idle, () => null);
            workers.Add(normalizationWorker);
            using var watcher = new XtreamSyncWatcher(manager, audit, NullLogger<XtreamSyncWatcher>.Instance);
            await watcher.ProcessPendingAsync(configuration, CancellationToken.None);
            Assert.Equal(0, queued);
            busy = false;
            await watcher.ProcessPendingAsync(configuration, CancellationToken.None);
            Assert.Equal(0, queued);
            scanResult.StartTimeUtc = ended.UtcDateTime;
            scanResult.EndTimeUtc = ended.UtcDateTime.AddSeconds(1);
            var completion = new TaskCompletionEventArgs(scanWorker, scanResult);
            watcher.OnTaskCompleted(null, completion);
            watcher.OnTaskCompleted(null, completion);
            await watcher.ProcessPendingAsync(configuration, CancellationToken.None);
            await watcher.ProcessPendingAsync(configuration, CancellationToken.None);
            Assert.Equal(1, queued);
            watcher.OnTaskCompleted(null, new TaskCompletionEventArgs(normalizationWorker, scanResult));
            await watcher.ProcessPendingAsync(configuration, CancellationToken.None);
            Assert.Equal(1, queued);
            normalizing = true;
            watcher.OnTaskCompleted(null, completion);
            await watcher.ProcessPendingAsync(configuration, CancellationToken.None);
            Assert.Equal(1, queued);
            normalizing = false;
            watcher.OnTaskCompleted(null, new TaskCompletionEventArgs(normalizationWorker, scanResult));
            await watcher.ProcessPendingAsync(configuration, CancellationToken.None);
            Assert.Equal(2, queued);
            using var restarted = new XtreamSyncWatcher(manager, audit, NullLogger<XtreamSyncWatcher>.Instance);
            await restarted.ProcessPendingAsync(configuration, CancellationToken.None);
            Assert.Equal(3, queued);
            configuration.RunLibraryFlow = true;
            restarted.OnTaskCompleted(null, completion);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ProcessPendingAsync(configuration, CancellationToken.None));
            configuration.AuditOnly = false;
            configuration.WriteBatchSize = 5;
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ProcessPendingAsync(configuration, CancellationToken.None));
            configuration.WriteBatchSize = 0;
            configuration.WriteItemId = Guid.NewGuid().ToString();
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ProcessPendingAsync(configuration, CancellationToken.None));
            configuration.WriteItemId = string.Empty;
            var missingMerge = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ProcessPendingAsync(configuration, CancellationToken.None));
            Assert.Contains("Merge Versions 12.0.1", missingMerge.Message);
            Assert.Equal(3, queued);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TaskAuditsWithoutCheckpointAndRetriesFailedWrites(bool auditOnly)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xtream-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Series", "Provider"));
        try
        {
            var history = Path.Combine(directory, "sync.json");
            var ended = DateTimeOffset.UtcNow.AddMinutes(-1);
            await File.WriteAllTextAsync(history, System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new XtreamSyncResult { StartTime = ended.AddMinutes(-1), EndTime = ended, Success = true }
            }));
            var paths = InterfaceStub.Create<IApplicationPaths>((_, _) => directory);
            var plugin = new Plugin(paths, InterfaceStub.Create<IXmlSerializer>((method, _) =>
                method.Name.StartsWith("Deserialize", StringComparison.Ordinal) ? new PluginConfiguration() : null));
            var configuration = plugin.Configuration;
            configuration.XtreamRoot = directory;
            configuration.SyncHistoryRelativePath = history;
            configuration.AuditOnly = auditOnly;
            var series = new Series { Id = Guid.NewGuid(), Name = "Provider", Path = Path.Combine(directory, "Series", "Provider") };
            series.ProviderIds["Tmdb"] = "42";
            var failSave = !auditOnly;
            var lookups = 0;
            var library = InterfaceStub.Create<ILibraryManager>((method, arguments) => method.Name switch
            {
                "get_IsScanRunning" => false,
                "GetItemById" => series,
                "GetLibraryOptions" => new LibraryOptions { PreferredMetadataLanguage = "nl", MetadataCountryCode = "BE" },
                "GetItemList" => ((InternalItemsQuery)arguments![0]!).IncludeItemTypes.Contains(BaseItemKind.Series) ? new List<BaseItem> { series } : new List<BaseItem>(),
                "UpdateItemAsync" => Task.CompletedTask,
                _ => throw new NotImplementedException(method.Name)
            });
            var provider = InterfaceStub.Create<IProviderManager>((method, _) => method.Name switch
            {
                "GetRemoteSearchResults" => Lookup(),
                "GetMetadataSavers" => Array.Empty<IMetadataSaver>(),
                "SaveMetadataAsync" => failSave ? throw new IOException("Simulated save failure") : Task.CompletedTask,
                _ => throw new NotImplementedException(method.Name)
            });
            Task<IEnumerable<RemoteSearchResult>> Lookup()
            {
                lookups++;
                return Task.FromResult<IEnumerable<RemoteSearchResult>>([new() { Name = "Canonical", ProviderIds = new() { ["Tmdb"] = "42" } }]);
            }
            var scan = InterfaceStub.Create<IScheduledTask>((_, _) => "RefreshLibrary");
            var manager = InterfaceStub.Create<ITaskManager>((_, _) => new[]
            {
                Worker(scan, () => TaskState.Idle, () => new TaskResult { Status = TaskCompletionStatus.Completed, StartTimeUtc = ended.UtcDateTime, EndTimeUtc = DateTime.UtcNow })
            });
            var stateReader = new EnrichmentStateReader();
            using var audit = new LibraryAuditService(library, paths, new SyncHistoryReader(), stateReader,
                InterfaceStub.Create<IServerConfigurationManager>((_, _) => new ServerConfiguration()), manager);
            var task = new NormalizeXtreamTask(audit, new AuditReportWriter(paths), new LibraryWriteService(library, provider),
                stateReader, NullLogger<NormalizeXtreamTask>.Instance);
            var statePath = Path.Combine(directory, "xtream-post-processor", "title-state.json");
            if (auditOnly)
            {
                await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
                Assert.Equal("Provider", series.Name);
                Assert.False(File.Exists(statePath));
                Assert.Equal(1, lookups);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new Progress<double>(), CancellationToken.None));
                var state = await stateReader.ReadAsync(statePath, CancellationToken.None);
                Assert.Equal("failed", Assert.Single(state.Items).Value.Status);
                Assert.Equal("Provider", series.Name);
                failSave = false;
                await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
                state = await stateReader.ReadAsync(statePath, CancellationToken.None);
                Assert.Equal("canonical", Assert.Single(state.Items).Value.Status);
                Assert.Equal("Canonical", series.Name);
                await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
                Assert.Equal(2, lookups);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static IScheduledTaskWorker Worker(IScheduledTask task, Func<TaskState> state, Func<TaskResult?> result) =>
        InterfaceStub.Create<IScheduledTaskWorker>((method, _) => method.Name switch
        {
            "get_ScheduledTask" => task,
            "get_State" => state(),
            "get_LastExecutionResult" => result(),
            _ => throw new NotImplementedException(method.Name)
        });
}