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