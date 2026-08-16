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

        Assert.Empty(CandidatePlanner.PlanEnrichment([item], state, retryFailed: true, lookupPolicy: "nl"));

        state.Items["A"] = new EnrichmentStateItem { Fingerprint = fingerprint, Status = "failed" };
        Assert.Single(CandidatePlanner.PlanEnrichment([item], state, retryFailed: true, lookupPolicy: "nl"));
        Assert.Empty(CandidatePlanner.PlanEnrichment([item], state, retryFailed: false, lookupPolicy: "nl"));
    }

    [Fact]
    public void EnrichmentPlanIncludesInvalidProviderIdAsTerminalCandidate()
    {
        var plan = CandidatePlanner.PlanEnrichment(
            [Item("A", "Unknown [tmdbid-0]", overview: null, tmdbId: "0")],
            new EnrichmentState(),
            retryFailed: true,
            lookupPolicy: "nl");

        Assert.True(Assert.Single(plan).InvalidProviderId);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("00042")]
    [InlineData("2147483648")]
    public void EnrichmentPlanRejectsMalformedProviderIds(string providerId)
    {
        var plan = CandidatePlanner.PlanEnrichment(
            [Item("A", "Unknown", overview: null, tmdbId: providerId)],
            new EnrichmentState(),
            retryFailed: true,
            lookupPolicy: "nl");

        Assert.True(Assert.Single(plan).InvalidProviderId);
    }

    [Fact]
    public void EnrichmentPlanReopensNoOverviewWhenLookupPolicyChanges()
    {
        var item = Item("A", "Example", overview: null, tmdbId: "42");
        var fingerprint = $"{item.TypeName}|tmdb:42|{item.Path}";
        var state = new EnrichmentState
        {
            Items = new Dictionary<string, EnrichmentStateItem>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = new()
                {
                    Fingerprint = fingerprint,
                    Status = "no-overview-available",
                    LookupPolicy = "nl"
                }
            }
        };

        Assert.Empty(CandidatePlanner.PlanEnrichment([item], state, true, "nl"));
        Assert.Single(CandidatePlanner.PlanEnrichment([item], state, true, "nl,en"));
    }

    [Fact]
    public void EnrichmentPlanRetriesUnavailableProviderIdsOnlyWhenEnabled()
    {
        var item = Item("A", "Example", overview: null, tmdbId: "42");
        var fingerprint = $"{item.TypeName}|tmdb:42|{item.Path}";
        var state = new EnrichmentState
        {
            Items = new Dictionary<string, EnrichmentStateItem>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = new()
                {
                    Fingerprint = fingerprint,
                    Status = "provider-id-unavailable"
                }
            }
        };

        Assert.Single(CandidatePlanner.PlanEnrichment([item], state, true, "nl"));
        Assert.Empty(CandidatePlanner.PlanEnrichment([item], state, false, "nl"));
    }

    [Fact]
    public void EnrichmentPlanPrioritizesUnseenItemsBeforeRetries()
    {
        var retry = Item("retry", "New retry", overview: null, tmdbId: "42") with
        {
            DateCreated = DateTime.UtcNow
        };
        var unseen = Item("unseen", "Older unseen", overview: null, tmdbId: "43") with
        {
            DateCreated = DateTime.UtcNow.AddDays(-1)
        };
        var state = new EnrichmentState
        {
            Items = new Dictionary<string, EnrichmentStateItem>(StringComparer.OrdinalIgnoreCase)
            {
                ["retry"] = new()
                {
                    Fingerprint = $"{retry.TypeName}|tmdb:42|{retry.Path}",
                    Status = "provider-id-unavailable",
                    AttemptedUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
                }
            }
        };

        var plan = CandidatePlanner.PlanEnrichment([retry, unseen], state, true, "nl");

        Assert.Equal("unseen", plan[0].Item.Id);
        Assert.Equal("retry", plan[1].Item.Id);
    }

    [Fact]
    public void EnrichmentPlanIgnoresItemsWithAnOverview()
    {
        var item = Item("A", "NL - Example [tmdbid-42]", overview: "Present", tmdbId: "42");

        Assert.Empty(CandidatePlanner.PlanEnrichment([item], new EnrichmentState(), true, "nl"));
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
