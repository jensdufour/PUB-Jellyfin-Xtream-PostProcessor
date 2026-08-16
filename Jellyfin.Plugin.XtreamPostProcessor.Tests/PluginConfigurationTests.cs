using System.Xml.Serialization;
using Jellyfin.Plugin.XtreamPostProcessor.Configuration;
using Jellyfin.Plugin.XtreamPostProcessor.Services;

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
        Assert.Equal(100, configuration.MaxUnindexedChangedRoots);
        Assert.Equal("nl", configuration.FallbackLanguage);
        Assert.Equal("nl,en,sv,da,cs", configuration.FallbackLanguages);
        Assert.Equal(0, configuration.WriteBatchSize);
        Assert.Equal(string.Empty, configuration.WriteItemId);
        Assert.Equal(
            ["nl", "en", "sv", "da", "cs"],
            LibraryWriteService.ParseFallbackLanguages(configuration.FallbackLanguages));
    }

    [Fact]
    public void VersionTwoConfigurationReceivesSafeNewDefaults()
    {
        const string xml = """
            <PluginConfiguration>
              <Enabled>true</Enabled>
              <AuditOnly>true</AuditOnly>
              <FallbackLanguage>nl</FallbackLanguage>
            </PluginConfiguration>
            """;
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        var configuration = Assert.IsType<PluginConfiguration>(
            serializer.Deserialize(new StringReader(xml)));

        Assert.Equal("nl", configuration.FallbackLanguage);
        Assert.Equal("nl,en,sv,da,cs", configuration.FallbackLanguages);
        Assert.Equal(0, configuration.WriteBatchSize);
    }

    [Fact]
    public void VersionTwoCustomLanguageIsPreservedFirst()
    {
        const string xml = """
            <PluginConfiguration>
              <FallbackLanguage>fr</FallbackLanguage>
            </PluginConfiguration>
            """;
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        var configuration = Assert.IsType<PluginConfiguration>(
            serializer.Deserialize(new StringReader(xml)));

        Assert.Equal("fr,nl,en,sv,da,cs", configuration.FallbackLanguages);
        Assert.Equal("fr,nl,en,sv,da,cs", LibraryWriteService.LookupPolicy(configuration.FallbackLanguages));
    }
}
