using Jellyfin.Plugin.XtreamPostProcessor.Configuration;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public sealed class PluginConfigurationTests
{
    [Fact]
    public void DefaultsAreSafeForShadowInstallation()
    {
        var configuration = new PluginConfiguration();

        Assert.True(configuration.Enabled);
        Assert.True(configuration.AuditOnly);
        Assert.Equal("xtream-library/sync_history.json", configuration.SyncHistoryRelativePath);
        Assert.Equal("xtream-post-processor/enrichment-state.json", configuration.StateRelativePath);
        Assert.Equal(6, configuration.EnrichmentWorkers);
    }
}
