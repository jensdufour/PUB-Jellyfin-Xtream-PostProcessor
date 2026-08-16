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
            Assert.Equal("failed", state.Items["abc"].Status);
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

    [Fact]
    public async Task WritesStateAtomicallyAndReadsItBack()
    {
      var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
      var path = Path.Combine(directory, "enrichment-state.json");
      try
      {
        var state = new EnrichmentState
        {
          UpdatedUtc = DateTimeOffset.UtcNow,
          Items = new Dictionary<string, EnrichmentStateItem>(StringComparer.OrdinalIgnoreCase)
          {
            ["ABC"] = new()
            {
              Fingerprint = "Series|tmdb:42|/data/example",
              Status = "enriched",
              AttemptedUtc = DateTimeOffset.UtcNow
            }
          }
        };
        var reader = new EnrichmentStateReader();

        await reader.WriteAsync(path, state, CancellationToken.None);
        var restored = await reader.ReadAsync(path, CancellationToken.None);

        Assert.Equal(1, restored.SchemaVersion);
        Assert.Equal("enriched", restored.Items["ABC"].Status);
        Assert.False(File.Exists(path + ".tmp"));
      }
      finally
      {
        Directory.Delete(directory, true);
      }
    }
}
