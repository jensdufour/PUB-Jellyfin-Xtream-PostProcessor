using Jellyfin.Plugin.XtreamPostProcessor.Services;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public sealed class CrossProcessFileLockTests
{
    [Fact]
    public void PreventsASecondOwner()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "processor.lock");
        try
        {
            using var first = CrossProcessFileLock.TryAcquire(path);
            using var second = CrossProcessFileLock.TryAcquire(path);

            Assert.NotNull(first);
            Assert.Null(second);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WaitsUntilCurrentOwnerReleases()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "processor.lock");
        try
        {
            var first = CrossProcessFileLock.TryAcquire(path);
            Assert.NotNull(first);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var waiting = CrossProcessFileLock.AcquireAsync(path, cancellation.Token);
            await Task.Delay(100, cancellation.Token);
            Assert.False(waiting.IsCompleted);

            first.Dispose();
            using var second = await waiting;
            Assert.NotNull(second);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}