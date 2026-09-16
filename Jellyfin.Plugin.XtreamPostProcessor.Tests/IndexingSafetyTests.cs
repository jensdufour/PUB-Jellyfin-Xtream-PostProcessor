using Jellyfin.Plugin.XtreamPostProcessor.Services;
using Jellyfin.Plugin.XtreamPostProcessor.Sync;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public sealed class IndexingSafetyTests
{
    [Fact]
    public void BlankMetadataPreferencesFallBackToLibraryThenServer()
    {
        Assert.Equal("fr", LibraryAuditService.FirstConfigured("", "fr", "en"));
        Assert.Equal("en", LibraryAuditService.FirstConfigured(null, " ", "en"));
        Assert.Equal("nl", LibraryAuditService.FirstConfigured("nl", "fr", "en"));
    }

    [Fact]
    public void RequiresSuccessfulScanAfterSuccessfulSync()
    {
        var ended = DateTimeOffset.UtcNow;
        var sync = new XtreamSyncResult { Success = true, EndTime = ended };
        var scan = new TaskResult { Status = TaskCompletionStatus.Completed, StartTimeUtc = ended.UtcDateTime, EndTimeUtc = ended.UtcDateTime.AddSeconds(1) };
        Assert.True(LibraryAuditService.IsReady(sync, scan, false));
        Assert.False(LibraryAuditService.IsReady(sync, scan, true));
        Assert.False(LibraryAuditService.IsReady(null, scan, false));
        Assert.False(LibraryAuditService.IsReady(sync, null, false));
        Assert.False(LibraryAuditService.IsReady(new XtreamSyncResult { EndTime = ended }, scan, false));
        scan.StartTimeUtc = ended.UtcDateTime.AddSeconds(-1);
        Assert.False(LibraryAuditService.IsReady(sync, scan, false));
        scan.StartTimeUtc = ended.UtcDateTime;
        scan.EndTimeUtc = ended.UtcDateTime.AddSeconds(-1);
        Assert.False(LibraryAuditService.IsReady(sync, scan, false));
    }

    [Theory]
    [InlineData(TaskCompletionStatus.Cancelled)]
    [InlineData(TaskCompletionStatus.Failed)]
    [InlineData(TaskCompletionStatus.Aborted)]
    public void FailedOrCancelledScanNeverAuthorizesWrites(TaskCompletionStatus status)
    {
        var sync = new XtreamSyncResult { Success = true, EndTime = DateTimeOffset.UtcNow.AddMinutes(-1) };
        Assert.False(LibraryAuditService.IsReady(sync, new TaskResult { Status = status, EndTimeUtc = DateTime.UtcNow }, false));
    }
}