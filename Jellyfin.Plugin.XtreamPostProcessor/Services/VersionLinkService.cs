using Jellyfin.Data.Enums;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

internal sealed class VersionLinkService(ILibraryManager library, IItemRepository repository,
    IItemPersistenceService persistence, AuditReportWriter reports)
{
    internal sealed record VersionItem(Guid Id, string Path, Guid? Primary, Guid Owner, bool Episode,
        int? Season, int? Number, int? EndNumber, Guid Series, Dictionary<string, string> Providers,
        string[] LocalPaths, Guid[] LinkedIds);

    internal sealed record Repair(Guid Child, Guid PreviousPrimary, Guid Primary, bool AddLink, bool RemoveSelfLink = false);
    internal sealed record Baseline(string Cycle, string MediaHash, Dictionary<Guid, Guid> Groups);

    internal static IReadOnlyList<Repair> Plan(IReadOnlyDictionary<Guid, VersionItem> items)
    {
        var repairs = new List<Repair>();
        var paths = items.Values.ToDictionary(item => item.Path, item => item.Id, StringComparer.Ordinal);
        var incoming = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var parent in items.Values)
        {
            foreach (var child in parent.LinkedIds.Concat(parent.LocalPaths.Where(paths.ContainsKey).Select(path => paths[path])).Distinct())
            {
                if (child == parent.Id)
                {
                    if (parent.Primary.HasValue || parent.LocalPaths.Contains(parent.Path))
                        throw new InvalidDataException($"Unsafe self-linked local or alternate version {parent.Id}");
                    continue;
                }
                if (!items.ContainsKey(child)) throw new InvalidDataException($"Version {parent.Id} links outside the indexed media scope: {child}");
                if (!incoming.TryGetValue(child, out var parents)) incoming[child] = parents = [];
                parents.Add(parent.Id);
            }
            if (parent.LocalPaths.Any(path => !paths.ContainsKey(path)))
                throw new InvalidDataException($"Version {parent.Id} has an unindexed local source");
        }
        foreach (var child in items.Values)
        {
            if (child.Primary is not { } previous) continue;
            var seen = new HashSet<Guid> { child.Id };
            var primary = previous;
            while (true)
            {
                if (!seen.Add(primary) || !items.TryGetValue(primary, out var parent))
                    throw new InvalidDataException($"Missing or cyclic primary for {child.Id}");
                if (parent.Primary is not { } next) break;
                primary = next;
            }
            var root = items[primary];
            incoming.TryGetValue(child.Id, out var parents);
            var direct = parents?.Contains(previous) == true;
            if (direct) continue;
            if (parents is not null && parents.Any(parent => parent != primary))
                throw new InvalidDataException($"Conflicting version parents for {child.Id}");
            if (child.Owner != Guid.Empty && (child.Owner != primary || !root.LocalPaths.Contains(child.Path)))
                throw new InvalidDataException($"Owned local version {child.Id} lacks a verified owner link");
            if (!SameIdentity(child, root))
                throw new InvalidDataException($"Cannot prove the media identity for version {child.Id} and primary {primary}");
            repairs.Add(new Repair(child.Id, previous, primary, parents?.Contains(primary) != true));
        }
        var corrected = repairs.ToDictionary(change => change.Child, change => change.Primary);
        foreach (var (child, parents) in incoming)
        {
            var expected = corrected.TryGetValue(child, out var repaired) ? repaired : items[child].Primary;
            if (!expected.HasValue || !parents.Contains(expected.Value)
                || parents.Any(parent => Root(parent, items) != Root(child, items)))
                throw new InvalidDataException($"Nonreciprocal or conflicting outgoing version link for {child}");
        }
        repairs.AddRange(items.Values.Where(item => item.LinkedIds.Contains(item.Id))
            .Select(item => new Repair(item.Id, item.Id, item.Id, false, true)));
        return repairs;
    }

    internal static bool SameIdentity(VersionItem child, VersionItem parent)
    {
        if (child.Episode != parent.Episode) return false;
        if (!child.Episode)
            return child.Providers.TryGetValue("Tmdb", out var movieId) && !string.IsNullOrWhiteSpace(movieId)
                && parent.Providers.GetValueOrDefault("Tmdb") == movieId;
        if (child.Season != parent.Season || child.Number != parent.Number || child.EndNumber != parent.EndNumber
            || child.Number is null || child.Season is null) return false;
        var common = new[] { "Tvdb", "Tmdb", "Imdb" }.Where(provider =>
            child.Providers.TryGetValue(provider, out var value) && !string.IsNullOrWhiteSpace(value)
            && parent.Providers.TryGetValue(provider, out var other) && !string.IsNullOrWhiteSpace(other)).ToArray();
        return common.Length > 0
            ? common.All(provider => child.Providers[provider] == parent.Providers[provider])
            : child.Series != Guid.Empty && child.Series == parent.Series;
    }

    internal async Task CheckAsync(string mediaRoot, string cycle, string baselinePath, bool repair,
        Func<CancellationToken, Task> ensureReady, CancellationToken token)
    {
        await ensureReady(token).ConfigureAwait(false);
        var items = Read(mediaRoot, token);
        var plan = Plan(items);
        await ReportAsync(repair ? "planned" : "checking", items.Count, plan, token).ConfigureAwait(false);
        if (!repair && plan.Count > 0)
            throw new InvalidDataException($"Post-merge integrity failed: {plan.Count} nonreciprocal or chained version relationships");
        foreach (var change in plan)
        {
            await ensureReady(token).ConfigureAwait(false);
            var child = repository.RetrieveItem(change.Child) as Video ?? throw new InvalidDataException("Version disappeared before repair");
            var primary = repository.RetrieveItem(change.Primary) as Video ?? throw new InvalidDataException("Primary disappeared before repair");
            if (change.RemoveSelfLink)
            {
                if (child.PrimaryVersionId.HasValue || child.Path != items[child.Id].Path || child.IsLocked || child.LockedFields.Length > 0
                    || !child.LocalAlternateVersions.SequenceEqual(items[child.Id].LocalPaths)
                    || !child.LinkedAlternateVersions.Select(link => link.ItemId!.Value).SequenceEqual(items[child.Id].LinkedIds))
                    throw new InvalidDataException($"Self-linked primary changed before repair: {child.Id}");
                child.LinkedAlternateVersions = child.LinkedAlternateVersions.Where(link => link.ItemId != child.Id).ToArray();
                persistence.SaveItems([child], CancellationToken.None);
                library.RegisterItem(repository.RetrieveItem(child.Id));
                foreach (var alternate in child.LinkedAlternateVersions)
                    library.RegisterItem(repository.RetrieveItem(alternate.ItemId!.Value));
                continue;
            }
            if (child.PrimaryVersionId != change.PreviousPrimary || child.OwnerId != items[child.Id].Owner
                || child.Path != items[child.Id].Path || primary.Path != items[primary.Id].Path
                || primary.PrimaryVersionId.HasValue || !SameIdentity(Snapshot(child), Snapshot(primary))
                || child.IsLocked || primary.IsLocked || child.LockedFields.Length > 0 || primary.LockedFields.Length > 0)
                throw new InvalidDataException($"Version identity, links or locks changed before repairing {change.Child}");
            if (change.PreviousPrimary != change.Primary)
            {
                child.SetPrimaryVersionId(change.Primary);
                persistence.SaveItems([child], CancellationToken.None);
                library.RegisterItem(repository.RetrieveItem(child.Id));
            }
            if (change.AddLink)
            {
                await ensureReady(token).ConfigureAwait(false);
                library.UpsertLinkedChild(primary.Id, child.Id, LinkedChildType.LinkedAlternateVersion);
                library.RegisterItem(repository.RetrieveItem(primary.Id));
            }
        }
        await ensureReady(token).ConfigureAwait(false);
        var after = repair && plan.Count > 0 ? Read(mediaRoot, token) : items;
        if (MediaHash(after) != MediaHash(items) || Plan(after).Count > 0
            || items.Values.Any(item => after[item.Id].Owner != item.Owner))
            throw new InvalidDataException("Version relationships did not pass persisted readback");
        if (repair)
            await reports.WriteAsync(Path.GetFileName(baselinePath), Capture(cycle, after), token).ConfigureAwait(false);
        else
        {
            var baseline = JsonSerializer.Deserialize<Baseline>(await File.ReadAllTextAsync(baselinePath, token).ConfigureAwait(false))
                ?? throw new InvalidDataException("Missing pre-merge source baseline");
            VerifyPreserved(cycle, baseline, after);
        }
        await ReportAsync(repair ? "preserved" : "verified", after.Count, plan, token).ConfigureAwait(false);
    }

    internal static Baseline Capture(string cycle, IReadOnlyDictionary<Guid, VersionItem> items)
    {
        var groups = new Dictionary<Guid, Guid>();
        foreach (var item in items.Values.Where(item => item.Primary.HasValue))
        {
            var root = Root(item.Id, items);
            groups[item.Id] = root;
            groups[root] = root;
        }
        return new(cycle, MediaHash(items), groups);
    }

    internal static void VerifyPreserved(string cycle, Baseline before, IReadOnlyDictionary<Guid, VersionItem> after)
    {
        if (before.Cycle != cycle || before.MediaHash != MediaHash(after))
            throw new InvalidDataException("Post-merge source IDs or paths changed, or baseline belongs to a different cycle");
        foreach (var group in before.Groups.GroupBy(pair => pair.Value))
            if (group.Select(pair => Root(pair.Key, after)).Distinct().Skip(1).Any())
                throw new InvalidDataException($"Post-merge version group lost a source: {group.Key}");
    }

    private static Guid Root(Guid id, IReadOnlyDictionary<Guid, VersionItem> items)
    {
        var seen = new HashSet<Guid>();
        while (true)
        {
            if (!seen.Add(id) || !items.TryGetValue(id, out var item))
                throw new InvalidDataException("Missing or cyclic version group");
            if (item.Primary is not { } parent) return id;
            id = parent;
        }
    }

    private static string MediaHash(IReadOnlyDictionary<Guid, VersionItem> items)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in items.Values.OrderBy(item => item.Id))
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { item.Id, item.Path })));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private Task ReportAsync(string status, int count, IReadOnlyList<Repair> plan, CancellationToken token) =>
        reports.WriteAsync("last-version-integrity.json", new { generatedUtc = DateTimeOffset.UtcNow, status, mediaCount = count, relationships = plan }, token);

    private Dictionary<Guid, VersionItem> Read(string mediaRoot, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(mediaRoot) || !Directory.Exists(mediaRoot))
            throw new InvalidDataException("Version checks require an existing absolute media root");
        var prefix = Path.GetFullPath(mediaRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var idsQuery = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode], Recursive = true, IsVirtualItem = false,
            GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false
        };
        IncludeAlternates(idsQuery);
        var ids = repository.GetItemIdsList(idsQuery);
        var items = new Dictionary<Guid, VersionItem>();
        foreach (var item in ReadSnapshot(ids, batch =>
        {
            token.ThrowIfCancellationRequested();
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode], Recursive = true, IsVirtualItem = false,
                GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false,
                DtoOptions = new DtoOptions(false) { Fields = [ItemFields.ProviderIds, ItemFields.Settings], EnableImages = false, EnableUserData = false },
                ItemIds = batch
            };
            IncludeAlternates(query);
            return repository.GetItemList(query).OfType<Video>().ToArray();
        }, item => item.Id))
            if (!string.IsNullOrWhiteSpace(item.Path) && Path.GetFullPath(item.Path).StartsWith(prefix, comparison))
                items.Add(item.Id, Snapshot(item));
        if (items.Count == 0) throw new InvalidDataException("No indexed media under the configured root; integrity cannot be established");
        return items;
    }

    internal static IEnumerable<T> ReadSnapshot<T>(IReadOnlyList<Guid> ids,
        Func<Guid[], IReadOnlyList<T>> readBatch, Func<T, Guid> getId)
    {
        foreach (var batch in ids.Chunk(500))
        {
            var page = readBatch(batch);
            if (page.Count != batch.Length || !page.Select(getId).ToHashSet().SetEquals(batch))
                throw new InvalidDataException("Indexed media changed while reading version relationships");
            foreach (var item in page) yield return item;
        }
    }

    internal static void IncludeAlternates(InternalItemsQuery query)
    {
        query.IncludeOwnedItems = true;
        var property = typeof(InternalItemsQuery).GetProperty("IncludeAlternateVersions");
        if (property?.PropertyType != typeof(bool) || !property.CanWrite)
            throw new NotSupportedException("Version integrity checks require Jellyfin 12.1 IncludeAlternateVersions support");
        property.SetValue(query, true);
    }

    private static VersionItem Snapshot(Video item) => new(item.Id, item.Path, item.PrimaryVersionId, item.OwnerId,
        item is Episode, (item as Episode)?.ParentIndexNumber, (item as Episode)?.IndexNumber, (item as Episode)?.IndexNumberEnd,
        (item as Episode)?.SeriesId ?? Guid.Empty, new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
        item.LocalAlternateVersions.ToArray(), item.LinkedAlternateVersions.Where(link => link.ItemId.HasValue).Select(link => link.ItemId!.Value).ToArray());
}