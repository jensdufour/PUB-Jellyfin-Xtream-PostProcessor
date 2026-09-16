using System.Text.Json;
using Jellyfin.Plugin.XtreamPostProcessor.Sync;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public sealed class SyncHistoryReaderTests
{
    [Fact]
    public async Task SelectsLatestResultByTimestampRatherThanArrayOrder()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new object[]
            {
                new { StartTime = "2026-08-16T02:30:00Z", EndTime = "2026-08-16T02:30:21Z", Success = true },
                new { StartTime = "2026-08-15T20:49:44Z", EndTime = "2026-08-15T20:50:04Z", Success = false }
            }));

            var result = await new SyncHistoryReader().ReadLatestAsync(path, CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.Equal(DateTimeOffset.Parse("2026-08-16T02:30:21Z"), result.EndTime);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EmptyHistoryReturnsNull()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[]");
            Assert.Null(await new SyncHistoryReader().ReadLatestAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedSyncReusesScanOnlyAfterLastChangedOrUnknownRun(bool missingCounters)
    {
        var path = Path.GetTempFileName();
        try
        {
            var previous = new XtreamSyncResult
            {
                StartTime = DateTimeOffset.Parse("2026-08-16T01:00:00Z"), EndTime = DateTimeOffset.Parse("2026-08-16T01:30:00Z"),
                Success = true, MoviesCreated = missingCounters ? null : 1
            };
            var latest = new XtreamSyncResult
            {
                StartTime = previous.EndTime.AddHours(1), EndTime = previous.EndTime.AddHours(1).AddMinutes(1), Success = true,
                MoviesCreated = 0, MoviesUpdated = 0, EpisodesCreated = 0, EpisodesUpdated = 0,
                SeriesCreated = 0, SeasonsCreated = 0, FilesDeleted = 0, SeriesDeleted = 0, SeasonsDeleted = 0, Errors = 0
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { latest, previous }));
            var result = await new SyncHistoryReader().ReadLatestAsync(path, CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(previous.EndTime, result.RequiredScanAfter);
            var scan = new MediaBrowser.Model.Tasks.TaskResult
            {
                Status = MediaBrowser.Model.Tasks.TaskCompletionStatus.Completed, StartTimeUtc = previous.EndTime.UtcDateTime, EndTimeUtc = previous.EndTime.UtcDateTime.AddMinutes(1)
            };
            Assert.True(Services.LibraryAuditService.IsReady(result, scan, false));
            scan.EndTimeUtc = previous.StartTime.UtcDateTime;
            Assert.False(Services.LibraryAuditService.IsReady(result, scan, false));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { latest }));
            result = await new SyncHistoryReader().ReadLatestAsync(path, CancellationToken.None);
            Assert.Equal(latest.StartTime, result!.RequiredScanAfter);
        }
        finally { File.Delete(path); }
    }
}
