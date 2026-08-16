using Jellyfin.Plugin.XtreamPostProcessor.State;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public sealed class EnrichmentStateReaderTests
{
    [Fact]
    public async Task ReadsLegacyPythonStateShape()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "schemaVersion": 1,
                  "items": {
                    "ABC": {
                      "fingerprint": "Series|tmdb:42|/data/example",
                      "status": "failed"
                    }
                  }
                }
                """);

            var state = await new EnrichmentStateReader().ReadAsync(path, CancellationToken.None);

            Assert.Equal("failed", state.Items["ABC"].Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingStateReturnsEmptyState()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var state = await new EnrichmentStateReader().ReadAsync(path, CancellationToken.None);
        Assert.Empty(state.Items);
    }
}
