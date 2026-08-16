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
}
