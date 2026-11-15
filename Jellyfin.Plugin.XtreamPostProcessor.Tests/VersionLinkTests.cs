using Jellyfin.Plugin.XtreamPostProcessor.Services;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public sealed class VersionLinkTests
{
    [Fact]
    public void OptionalSanitizedProductionGraphReconcilesWithoutSourceLoss()
    {
        var path = Environment.GetEnvironmentVariable("XTREAM_VERSION_FIXTURE");
        if (string.IsNullOrEmpty(path)) return;
        using var file = File.OpenRead(path);
        using var archive = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        var items = System.Text.Json.JsonSerializer.Deserialize<VersionLinkService.VersionItem[]>(archive)!.ToDictionary(item => item.Id);
        var plan = VersionLinkService.Plan(items);
        Assert.Equal(127, plan.Count);
        Assert.Equal(117, plan.Count(change => change.AddLink));
        Assert.Equal(5, plan.Count(change => change.RemoveSelfLink));
        var before = VersionLinkService.Capture("fixture", items);
        foreach (var change in plan)
        {
            if (change.ClearPrimary)
            {
                items[change.Child] = items[change.Child] with { Primary = null };
                continue;
            }
            if (change.RemoveSelfLink)
            {
                items[change.Child] = items[change.Child] with { LinkedIds = items[change.Child].LinkedIds.Where(id => id != change.Child).ToArray() };
                continue;
            }
            items[change.Child] = items[change.Child] with { Primary = change.Primary };
            if (change.AddLink) items[change.Primary] = items[change.Primary] with { LinkedIds = [.. items[change.Primary].LinkedIds, change.Child] };
        }
        Assert.Empty(VersionLinkService.Plan(items));
        VersionLinkService.VerifyPreserved("fixture", before, items);
        Assert.Equal(2984, items.Values.Count(item => item.Owner != Guid.Empty));
    }

    private static VersionLinkService.VersionItem Item(Guid id, Guid? primary = null) =>
        new(id, "/media/" + id + ".strm", primary, Guid.Empty, true, 1, 2, null, Guid.NewGuid(),
            new() { ["Tvdb"] = "189328" }, [], []);

    [Fact]
    public void SnapshotBatchesReadEveryIdOrFailClosed()
    {
        var ids = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray();
        var batchSizes = new List<int>();
        var actual = VersionLinkService.ReadSnapshot(ids, batch =>
        {
            batchSizes.Add(batch.Length);
            return batch.Reverse().ToArray();
        }, id => id).ToArray();

        Assert.Equal([500, 500, 1], batchSizes);
        Assert.Equal(ids.Order(), actual.Order());
        Assert.Throws<InvalidDataException>(() => VersionLinkService.ReadSnapshot(
            ids, batch => batch.Skip(1).ToArray(), id => id).ToArray());
    }

    [Fact]
    public void VersionQueriesIncludeLinkedAndOwnedAlternates()
    {
        var query = new InternalItemsQuery();
        var property = typeof(InternalItemsQuery).GetProperty("IncludeAlternateVersions");

        if (property is null)
            Assert.Throws<NotSupportedException>(() => VersionLinkService.IncludeAlternates(query));
        else
            VersionLinkService.IncludeAlternates(query);

        Assert.True(query.IncludeOwnedItems);
        if (property is not null) Assert.True((bool)property.GetValue(query)!);
    }

    [Fact]
    public void RestoresMissingLinkedEditionAndAcceptsPersistedReadback()
    {
        var primary = Item(Guid.NewGuid());
        var child = Item(Guid.NewGuid(), primary.Id);
        var items = new[] { primary, child }.ToDictionary(item => item.Id);
        var repair = Assert.Single(VersionLinkService.Plan(items));
        Assert.Equal(primary.Id, repair.Primary);
        Assert.True(repair.AddLink);
        items[primary.Id] = primary with { LinkedIds = [child.Id] };
        Assert.Empty(VersionLinkService.Plan(items));
    }

    [Fact]
    public void RepairsReciprocalPrimaryCycleFromVerifiedLocalOwner()
    {
        var series = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var ownedId = Guid.NewGuid();
        var root = Item(rootId, ownedId) with { Series = series, Providers = new() };
        var owned = Item(ownedId, rootId) with { Owner = rootId, Series = series, Providers = new() };
        root = root with { LocalPaths = [owned.Path] };
        var items = new[] { root, owned }.ToDictionary(item => item.Id);

        var repair = Assert.Single(VersionLinkService.Plan(items));

        Assert.Equal(rootId, repair.Child);
        Assert.Equal(ownedId, repair.PreviousPrimary);
        Assert.Equal(rootId, repair.Primary);
        Assert.True(repair.ClearPrimary);
        items[rootId] = root with { Primary = null };
        Assert.Empty(VersionLinkService.Plan(items));
        Assert.Equal(rootId, items[ownedId].Owner);
        Assert.Equal([owned.Path], items[rootId].LocalPaths);
    }

    [Fact]
    public void ClearsCatalogVerifiedMissingPrimaryAndPreservesLocalSources()
    {
        var series = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var root = Item(rootId, Guid.NewGuid()) with { Series = series, Providers = new(), PrimaryTargetMissing = true };
        var child = Item(childId, rootId) with { Owner = rootId, Series = series, Providers = new() };
        root = root with { LocalPaths = [child.Path] };
        var items = new[] { root, child }.ToDictionary(item => item.Id);

        var repair = Assert.Single(VersionLinkService.Plan(items));

        Assert.Equal(rootId, repair.Child);
        Assert.True(repair.ClearMissingPrimary);
        items[rootId] = root with { Primary = null };
        Assert.Empty(VersionLinkService.Plan(items));
        Assert.Equal(root.LocalPaths, items[rootId].LocalPaths);
        Assert.Equal(rootId, items[childId].Owner);
    }

    [Fact]
    public void ClearsMissingPrimaryWhenOwnerIsAlsoAbsentWithoutChangingOwnerId()
    {
        var owner = Guid.NewGuid();
        var orphan = Item(Guid.NewGuid(), Guid.NewGuid()) with
        {
            Owner = owner,
            PrimaryTargetMissing = true,
            OwnerTargetMissing = true
        };
        var items = new[] { orphan }.ToDictionary(item => item.Id);

        var repair = Assert.Single(VersionLinkService.Plan(items));

        Assert.True(repair.ClearMissingPrimary);
        items[orphan.Id] = orphan with { Primary = null };
        Assert.Empty(VersionLinkService.Plan(items));
        Assert.Equal(owner, items[orphan.Id].Owner);
    }

    [Fact]
    public void RehomesMissingPrimaryToVerifiedOwnerRoot()
    {
        var series = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var missingPrimary = Guid.NewGuid();
        var root = Item(rootId) with { Series = series, Providers = new() };
        var child = Item(childId, missingPrimary) with { Owner = rootId, Series = series, Providers = new() };
        root = root with { LocalPaths = [child.Path], LinkedIds = [child.Id] };
        child = child with { PrimaryTargetMissing = true };
        var items = new[] { root, child }.ToDictionary(item => item.Id);

        var repair = Assert.Single(VersionLinkService.Plan(items));

        Assert.Equal(childId, repair.Child);
        Assert.Equal(missingPrimary, repair.PreviousPrimary);
        Assert.Equal(rootId, repair.Primary);
        Assert.False(repair.AddLink);
        items[childId] = child with { Primary = rootId };
        Assert.Empty(VersionLinkService.Plan(items));
        Assert.Equal(rootId, items[childId].Owner);
        Assert.Equal([child.Path], items[rootId].LocalPaths);
        Assert.Equal([childId], items[rootId].LinkedIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconcilesExistingPrimaryToVerifiedOwnerRoot(bool identitiesMatch)
    {
        var series = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var linkedRootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var owner = Item(ownerId) with { Series = series, Providers = new() { ["Tmdb"] = "42" } };
        var linkedRoot = Item(linkedRootId) with
        {
            Series = series,
            Providers = new() { ["Tmdb"] = identitiesMatch ? "42" : "43" },
            LinkedIds = [childId]
        };
        var child = Item(childId, linkedRootId) with
        {
            Owner = ownerId,
            Series = series,
            Providers = new() { ["Tmdb"] = "42" }
        };
        owner = owner with { LocalPaths = [child.Path], LinkedIds = [child.Id] };
        var items = new[] { owner, linkedRoot, child }.ToDictionary(item => item.Id);

        if (!identitiesMatch)
        {
            Assert.Throws<InvalidDataException>(() => VersionLinkService.Plan(items));
            return;
        }

        var repair = Assert.Single(VersionLinkService.Plan(items));
        Assert.Equal(linkedRootId, repair.Child);
        Assert.Equal(ownerId, repair.Primary);
        Assert.True(repair.AddLink);
        items[linkedRootId] = linkedRoot with { Primary = ownerId };
        items[ownerId] = owner with { LinkedIds = [childId, linkedRootId] };
        Assert.Empty(VersionLinkService.Plan(items));
        Assert.Equal(ownerId, items[childId].Owner);
        Assert.Equal(linkedRootId, items[childId].Primary);
        Assert.Equal([child.Path], items[ownerId].LocalPaths);
        Assert.Equal([childId], linkedRoot.LinkedIds);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void RejectsUnverifiedMissingPrimaryTarget(bool targetMissing, bool ownerMissing)
    {
        var orphan = Item(Guid.NewGuid(), Guid.NewGuid()) with
        {
            Owner = Guid.NewGuid(),
            PrimaryTargetMissing = targetMissing,
            OwnerTargetMissing = ownerMissing
        };

        Assert.Throws<InvalidDataException>(() => VersionLinkService.Plan(new[] { orphan }.ToDictionary(item => item.Id)));
    }

    [Theory]
    [InlineData("missing-owner")]
    [InlineData("missing-local-path")]
    [InlineData("identity-conflict")]
    public void RejectsReciprocalPrimaryCycleWithoutCompleteOwnerProof(string scenario)
    {
        var series = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var ownedId = Guid.NewGuid();
        var root = Item(rootId, ownedId) with
        {
            Series = series,
            Providers = new(),
            LocalPaths = scenario == "missing-local-path" ? [] : ["/media/owned.strm"]
        };
        var owned = Item(ownedId, rootId) with
        {
            Owner = scenario == "missing-owner" ? Guid.Empty : rootId,
            Series = scenario == "identity-conflict" ? Guid.NewGuid() : series,
            Number = scenario == "identity-conflict" ? 3 : 2,
            Providers = new()
        };

        Assert.Throws<InvalidDataException>(() => VersionLinkService.Plan(new[] { root, owned }.ToDictionary(item => item.Id)));
    }

    [Fact]
    public void AcceptsEpisodeIdentityFromMatchingParentSeriesIds()
    {
        var primary = Item(Guid.NewGuid()) with
        {
            Providers = new(),
            Series = Guid.NewGuid(),
            SeriesProviders = new() { ["Tvdb"] = "series-42" }
        };
        var child = Item(Guid.NewGuid(), primary.Id) with
        {
            Providers = new() { ["Tvdb"] = "episode-99" },
            Series = Guid.NewGuid(),
            SeriesProviders = new() { ["Tvdb"] = "series-42" }
        };

        var repair = Assert.Single(VersionLinkService.Plan(new[] { primary, child }.ToDictionary(item => item.Id)));

        Assert.Equal(primary.Id, repair.Primary);
    }

    [Theory]
    [InlineData("episode-id-conflict")]
    [InlineData("series-id-conflict")]
    [InlineData("episode-number-conflict")]
    [InlineData("end-number-conflict")]
    [InlineData("unsupported-provider")]
    public void RejectsParentSeriesFallbackWhenEpisodeEvidenceConflicts(string scenario)
    {
        var primary = Item(Guid.NewGuid()) with
        {
            Providers = scenario switch
            {
                "episode-id-conflict" => new() { ["Tvdb"] = "episode-1" },
                "unsupported-provider" => new() { ["Custom"] = "same" },
                _ => new()
            },
            Series = Guid.NewGuid(),
            Number = 2,
            EndNumber = 3,
            SeriesProviders = new() { ["Tvdb"] = "series-42" }
        };
        var child = Item(Guid.NewGuid(), primary.Id) with
        {
            Providers = scenario switch
            {
                "episode-id-conflict" => new() { ["Tvdb"] = "episode-2" },
                "unsupported-provider" => new() { ["Custom"] = "same" },
                _ => new()
            },
            Series = Guid.NewGuid(),
            Number = scenario == "episode-number-conflict" ? 4 : 2,
            EndNumber = scenario == "end-number-conflict" ? 4 : 3,
            SeriesProviders = scenario switch
            {
                "series-id-conflict" => new() { ["Tvdb"] = "series-43" },
                "unsupported-provider" => new() { ["Tmdb"] = "series-42" },
                _ => new() { ["Tvdb"] = "series-42" }
            }
        };

        Assert.Throws<InvalidDataException>(() => VersionLinkService.Plan(new[] { primary, child }.ToDictionary(item => item.Id)));
    }

    [Fact]
    public void ResolvesUnambiguousChainWithoutChangingLocalOwnership()
    {
        var root = Item(Guid.NewGuid());
        var former = Item(Guid.NewGuid(), root.Id);
        var child = Item(Guid.NewGuid(), former.Id) with { Owner = root.Id };
        var local = Item(Guid.NewGuid(), root.Id) with { Owner = root.Id };
        root = root with { LinkedIds = [former.Id], LocalPaths = [local.Path, child.Path] };
        var items = new[] { root, former, child, local }.ToDictionary(item => item.Id);
        var repair = Assert.Single(VersionLinkService.Plan(items));
        Assert.Equal(child.Id, repair.Child);
        Assert.Equal(root.Id, repair.Primary);
        Assert.False(repair.AddLink);
        Assert.Equal(root.Id, items[local.Id].Owner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconcilesLinkedRootWithVerifiedLocalOwnerWithoutLosingSources(bool pointerAlreadySaved)
    {
        var owner = Item(Guid.NewGuid());
        var local = Item(Guid.NewGuid(), owner.Id) with { Owner = owner.Id };
        var linked = Item(Guid.NewGuid(), pointerAlreadySaved ? owner.Id : null) with { LinkedIds = [local.Id] };
        var otherLocal = Item(Guid.NewGuid(), linked.Id) with { Owner = linked.Id };
        owner = owner with { LocalPaths = [local.Path] };
        linked = linked with { LocalPaths = [otherLocal.Path] };
        var items = new[] { owner, local, linked, otherLocal }.ToDictionary(item => item.Id);
        var before = VersionLinkService.Capture("cycle", items);

        var repair = Assert.Single(VersionLinkService.Plan(items));

        Assert.Equal(linked.Id, repair.Child);
        Assert.Equal(linked.Primary, repair.PreviousPrimary);
        Assert.Equal(owner.Id, repair.Primary);
        Assert.True(repair.AddLink);
        items[linked.Id] = linked with { Primary = repair.Primary };
        items[owner.Id] = owner with { LinkedIds = [linked.Id] };
        Assert.Empty(VersionLinkService.Plan(items));
        VersionLinkService.VerifyPreserved("cycle", before, items);
        Assert.Equal(owner.Id, items[local.Id].Owner);
        Assert.Equal(linked.Id, items[otherLocal.Id].Owner);
        Assert.Equal([local.Path], items[owner.Id].LocalPaths);
        Assert.Equal([otherLocal.Path], items[linked.Id].LocalPaths);
        Assert.Equal([local.Id], items[linked.Id].LinkedIds);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing")]
    [InlineData("identity")]
    [InlineData("conflict")]
    [InlineData("local-gap")]
    [InlineData("stale-outgoing")]
    [InlineData("multiple-incoming")]
    [InlineData("owned-outgoing-identity")]
    [InlineData("owned-outgoing-number")]
    [InlineData("owned-outgoing-owner")]
    public void RejectsUnsafeRelationships(string scenario)
    {
        var primary = Item(Guid.NewGuid());
        var child = Item(Guid.NewGuid(), primary.Id);
        var other = Item(Guid.NewGuid());
        if (scenario == "cycle") primary = primary with { Primary = child.Id };
        if (scenario == "missing") child = child with { Primary = Guid.NewGuid() };
        if (scenario == "identity") child = child with { Providers = new() { ["Tvdb"] = "999" } };
        if (scenario == "conflict") other = other with { LinkedIds = [child.Id] };
        if (scenario == "local-gap") child = child with { Owner = primary.Id };
        if (scenario == "stale-outgoing") { primary = primary with { LinkedIds = [child.Id] }; child = child with { Primary = null }; }
        if (scenario == "multiple-incoming") { primary = primary with { LinkedIds = [child.Id] }; other = other with { LinkedIds = [child.Id] }; }
        if (scenario.StartsWith("owned-outgoing-", StringComparison.Ordinal))
        {
            child = child with { Owner = primary.Id };
            primary = primary with { LocalPaths = [child.Path] };
            other = other with { LinkedIds = [child.Id] };
            if (scenario == "owned-outgoing-identity") other = other with { Providers = new() { ["Tvdb"] = "999" } };
            if (scenario == "owned-outgoing-number") other = other with { Number = 3 };
            if (scenario == "owned-outgoing-owner") other = other with { Owner = Guid.NewGuid() };
        }
        var items = new[] { primary, child, other }.ToDictionary(item => item.Id);
        Assert.Throws<InvalidDataException>(() => VersionLinkService.Plan(items));
    }

    [Fact]
    public void PostMergeAllowsNewPrimaryButRejectsLostSourcesAndSplits()
    {
        var primary = Item(Guid.NewGuid());
        var child = Item(Guid.NewGuid(), primary.Id);
        primary = primary with { LinkedIds = [child.Id] };
        var before = new[] { primary, child }.ToDictionary(item => item.Id);
        var baseline = VersionLinkService.Capture("cycle", before);
        var after = new[] { primary with { Primary = child.Id, LinkedIds = [] }, child with { Primary = null, LinkedIds = [primary.Id] } }.ToDictionary(item => item.Id);
        Assert.Empty(VersionLinkService.Plan(after));
        VersionLinkService.VerifyPreserved("cycle", baseline, after);
        Assert.Throws<InvalidDataException>(() => VersionLinkService.VerifyPreserved("other-cycle", baseline, after));
        after[primary.Id] = after[primary.Id] with { Path = "/changed.strm" };
        Assert.Throws<InvalidDataException>(() => VersionLinkService.VerifyPreserved("cycle", baseline, after));
        after[primary.Id] = primary with { LinkedIds = [] };
        after[child.Id] = child with { Primary = null };
        Assert.Empty(VersionLinkService.Plan(after));
        Assert.Throws<InvalidDataException>(() => VersionLinkService.VerifyPreserved("cycle", baseline, after));
        after.Remove(child.Id);
        Assert.Throws<InvalidDataException>(() => VersionLinkService.VerifyPreserved("cycle", baseline, after));
    }
}