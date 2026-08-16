using Jellyfin.Plugin.XtreamPostProcessor.Planning;
using Jellyfin.Plugin.XtreamPostProcessor.State;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public sealed class CandidatePlannerTests
{
    [Fact]
    public void EnrichmentPlanHonorsLegacyStateAndRetryPolicy()
    {
        var item = Item("A", "NL - Example [tmdbid-42]", overview: null, tmdbId: "42");
        var fingerprint = $"{item.TypeName}|tmdb:42|{item.Path}";
        var state = new EnrichmentState
        {
            Items = new Dictionary<string, EnrichmentStateItem>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = new() { Fingerprint = fingerprint, Status = "enriched" }
            }
        };

        Assert.Empty(CandidatePlanner.PlanEnrichment([item], state, retryFailed: true));

        state.Items["A"] = new EnrichmentStateItem { Fingerprint = fingerprint, Status = "failed" };
        Assert.Single(CandidatePlanner.PlanEnrichment([item], state, retryFailed: true));
        Assert.Empty(CandidatePlanner.PlanEnrichment([item], state, retryFailed: false));
    }

    [Fact]
    public void EnrichmentPlanIncludesInvalidProviderIdAsTerminalCandidate()
    {
        var plan = CandidatePlanner.PlanEnrichment(
            [Item("A", "Unknown [tmdbid-0]", overview: null, tmdbId: "0")],
            new EnrichmentState(),
            retryFailed: true);

        Assert.True(Assert.Single(plan).InvalidProviderId);
    }

    [Fact]
    public void NormalizationPlanUsesSourceFolderAndPreservesCuratedTitle()
    {
        var item = Item("A", "Curated", overview: "Present", tmdbId: "42") with
        {
            Path = "/data/media/xtream/Series/NL - Example [tmdbid-42]",
            IsSeries = true
        };

        var plan = Assert.Single(CandidatePlanner.PlanNormalization([item]));

        Assert.Equal("NL - Example [tmdbid-42]", plan.SourceName);
        Assert.Equal("Curated", plan.Decision.Title);
        Assert.False(plan.NeedsItemUpdate);
    }

    [Fact]
    public void NormalizationPlanIgnoresOrdinarySourceNames()
    {
        var item = Item("A", "Futurama", overview: "Present", tmdbId: "615") with
        {
            Path = "/data/media/xtream/Series/EN Futurama [tmdbid-615]",
            IsSeries = true
        };

        Assert.Empty(CandidatePlanner.PlanNormalization([item]));
    }

    private static LibraryItemSnapshot Item(string id, string name, string? overview, string? tmdbId) =>
        new(
            id,
            "MediaBrowser.Controller.Entities.TV.Series",
            name,
            null,
            $"/data/media/xtream/Series/{name}",
            overview,
            tmdbId,
            DateTime.UtcNow,
            true);
}
