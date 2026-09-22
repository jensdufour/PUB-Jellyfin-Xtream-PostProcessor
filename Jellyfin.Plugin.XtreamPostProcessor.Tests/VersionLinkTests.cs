using Jellyfin.Plugin.XtreamPostProcessor.Services;

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
    [InlineData("cycle")]
    [InlineData("missing")]
    [InlineData("identity")]
    [InlineData("conflict")]
    [InlineData("local-gap")]
    [InlineData("stale-outgoing")]
    [InlineData("multiple-incoming")]
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