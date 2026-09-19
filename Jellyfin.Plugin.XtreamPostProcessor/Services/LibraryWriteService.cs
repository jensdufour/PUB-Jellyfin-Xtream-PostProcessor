using Jellyfin.Plugin.XtreamPostProcessor.Planning;
using Jellyfin.Plugin.XtreamPostProcessor.Normalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Jellyfin.Data.Enums;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

/// <summary>
/// Applies planned metadata changes through Jellyfin's supported library interfaces.
/// </summary>
public sealed class LibraryWriteService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryWriteService"/> class.
    /// </summary>
    public LibraryWriteService(
        ILibraryManager libraryManager,
        IProviderManager providerManager)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
    }

    internal async Task<EnrichmentWriteResult> ApplyEnrichmentAsync(
        EnrichmentPlanItem plan,
        string fallbackLanguages,
        CancellationToken cancellationToken,
        Func<Task>? beforeWrite = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.InvalidProviderId)
        {
            throw new InvalidOperationException($"Invalid TMDB provider ID for {plan.Item.Id}");
        }

        var item = GetLiveItem(plan.Item);
        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            return new EnrichmentWriteResult("enriched", true, false);
        }

        if (item.IsLocked || item.LockedFields.Contains(MetadataField.Overview))
        {
            return new EnrichmentWriteResult("overview-locked", false, false, "Overview is locked in Jellyfin");
        }

        var languages = ParseFallbackLanguages(fallbackLanguages);
        if (languages.Count == 0)
        {
            throw new InvalidOperationException("At least one fallback metadata language is required");
        }

        var exactMatchSeen = false;
        var exactMatchMissing = false;
        foreach (var language in languages)
        {
            var results = item switch
            {
                Movie => await SearchAsync<Movie, MovieInfo>(new MovieInfo(), plan, language, cancellationToken).ConfigureAwait(false),
                Series => await SearchAsync<Series, SeriesInfo>(new SeriesInfo(), plan, language, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported item type {item.GetType().FullName}")
            };
            cancellationToken.ThrowIfCancellationRequested();
            var matches = results
                .Where(result => string.Equals(
                    ProviderId(result.ProviderIds, "Tmdb"),
                    plan.Item.TmdbId,
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Expected at most one exact TMDB {plan.Item.TmdbId} result, got {matches.Length}");
            }

            if (matches.Length == 0)
            {
                exactMatchMissing = true;
                continue;
            }

            exactMatchSeen = true;
            if (string.IsNullOrWhiteSpace(matches[0].Overview))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (beforeWrite is not null) await beforeWrite().ConfigureAwait(false);
            item = GetLiveItem(plan.Item);
            if (!string.IsNullOrWhiteSpace(item.Overview))
            {
                return new EnrichmentWriteResult("enriched", true, false);
            }

            if (item.IsLocked || item.LockedFields.Contains(MetadataField.Overview))
            {
                return new EnrichmentWriteResult("overview-locked", false, false, "Overview was locked during lookup");
            }

            item.Overview = matches[0].Overview;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, CancellationToken.None).ConfigureAwait(false);
            return new EnrichmentWriteResult("enriched", true, false);
        }

        if (exactMatchSeen && !exactMatchMissing)
        {
            return new EnrichmentWriteResult(
                "no-overview-available",
                false,
                true,
                $"TMDB {plan.Item.TmdbId} has no overview in: {string.Join(", ", languages)}");
        }

        if (exactMatchSeen)
        {
            throw new InvalidOperationException(
                $"TMDB {plan.Item.TmdbId} returned inconsistent exact results across languages");
        }

        return new EnrichmentWriteResult(
            "provider-id-unavailable",
            false,
            false,
            $"TMDB {plan.Item.TmdbId} has no exact {item.GetType().Name} result");
    }

    internal async Task<bool> ApplyTitleAsync(
        NormalizationPlanItem plan,
        CancellationToken cancellationToken,
        Func<Task>? beforeWrite = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = GetLiveItem(plan.Item);
        if (item.IsLocked || item.LockedFields.Contains(MetadataField.Name))
        {
            throw new InvalidOperationException($"Title was locked during processing for {plan.Item.Id}");
        }

        if (plan.Decision.Source != "exact-tmdb" || string.IsNullOrWhiteSpace(plan.Decision.Title))
        {
            return false;
        }

        if (!string.Equals(item.Name, plan.Item.Name, StringComparison.Ordinal)
            && !string.Equals(item.Name, plan.Decision.Title, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Title changed during lookup for {plan.Item.Id}");
        }

        var nfoPaths = _providerManager.GetMetadataSavers(item, _libraryManager.GetLibraryOptions(item))
            .OfType<IMetadataFileSaver>().Select(saver => saver.GetSavePath(item))
            .Where(path => string.Equals(System.IO.Path.GetExtension(path), ".nfo", StringComparison.OrdinalIgnoreCase))
            .Distinct().ToArray();
        var nfoNeedsUpdate = false;
        foreach (var path in nfoPaths)
        {
            if (!File.Exists(path) || await ReadNfoTitleAsync(path, cancellationToken).ConfigureAwait(false) != plan.Decision.Title)
                nfoNeedsUpdate = true;
        }
        if (beforeWrite is not null) await beforeWrite().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        item = GetLiveItem(plan.Item);
        if (item.IsLocked || item.LockedFields.Contains(MetadataField.Name)
            || (item.Name != plan.Item.Name && item.Name != plan.Decision.Title))
            throw new InvalidOperationException($"Title or lock changed before saving {plan.Item.Id}");
        var oldName = item.Name;
        var oldOverview = item.Overview;
        var fillOverview = !string.IsNullOrWhiteSpace(plan.MissingOverview) && string.IsNullOrWhiteSpace(item.Overview)
            && !item.LockedFields.Contains(MetadataField.Overview);
        var changed = item.Name != plan.Decision.Title || fillOverview || nfoNeedsUpdate;
        if (changed)
        {
            item.Name = plan.Decision.Title;
            if (fillOverview) item.Overview = plan.MissingOverview;
            try
            {
                await _providerManager.SaveMetadataAsync(item, ItemUpdateType.MetadataEdit).ConfigureAwait(false);
                foreach (var path in nfoPaths)
                {
                    if (await ReadNfoTitleAsync(path, CancellationToken.None).ConfigureAwait(false) != item.Name)
                        throw new InvalidOperationException($"NFO title was not saved for {plan.Item.Id}");
                }
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataImport, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                item.Name = oldName;
                item.Overview = oldOverview;
                throw;
            }
        }

        return await ApplyChildLabelsAsync(plan.Item with { Name = item.Name }, cancellationToken, beforeWrite).ConfigureAwait(false) || changed;
    }

    internal async Task<bool> ApplyChildLabelsAsync(LibraryItemSnapshot snapshot, CancellationToken cancellationToken,
        Func<Task>? beforeWrite = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = GetLiveItem(snapshot);
        if (item.IsLocked || item.LockedFields.Contains(MetadataField.Name) || item.Name != snapshot.Name)
            throw new InvalidOperationException($"Title or lock changed before child-label check for {snapshot.Id}");
        var changed = false;
        if (item is Series series)
        {
            var query = new InternalItemsQuery
            {
                Parent = series, IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode],
                Recursive = true, GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false
            };
            typeof(InternalItemsQuery).GetProperty("IncludeAlternateVersions")?.SetValue(query, true);
            var children = _libraryManager.GetItemList(query);
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var oldSeriesName = child switch { Season currentSeason => currentSeason.SeriesName, Episode currentEpisode => currentEpisode.SeriesName, _ => null };
                var belongsToSeries = child switch { Season currentSeason => currentSeason.SeriesId == series.Id, Episode currentEpisode => currentEpisode.SeriesId == series.Id, _ => false };
                if (!belongsToSeries || oldSeriesName == series.Name) continue;
                if (beforeWrite is not null) await beforeWrite().ConfigureAwait(false);
                var currentParent = GetLiveItem(snapshot);
                if (currentParent.Name != snapshot.Name || currentParent.IsLocked || currentParent.LockedFields.Contains(MetadataField.Name))
                    throw new InvalidOperationException($"Series changed before child-label save for {snapshot.Id}");
                if (child.IsLocked || child.LockedFields.Contains(MetadataField.Name)) continue;
                if (child is Season season)
                    season.SeriesName = series.Name;
                else if (child is Episode episode)
                    episode.SeriesName = series.Name;
                try
                {
                    await _libraryManager.UpdateItemAsync(child, child.GetParent(), ItemUpdateType.MetadataImport, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    if (child is Season failedSeason) failedSeason.SeriesName = oldSeriesName!;
                    if (child is Episode failedEpisode) failedEpisode.SeriesName = oldSeriesName!;
                    throw;
                }
                changed = true;
            }
        }
        return changed;
    }

    internal async Task<NormalizationPlanItem> ResolveTitleAsync(NormalizationPlanItem plan, CancellationToken cancellationToken)
    {
        if (!TitleNormalizer.IsValidTmdbId(plan.Item.TmdbId)) throw new InvalidOperationException("A valid existing TMDb ID is required");
        var item = GetLiveItem(plan.Item);
        var lookup = new EnrichmentPlanItem(plan.Item, string.Empty, false);
        var results = item switch
        {
            Movie => await SearchAsync<Movie, MovieInfo>(new MovieInfo(), lookup, plan.Item.MetadataLanguage, cancellationToken).ConfigureAwait(false),
            Series => await SearchAsync<Series, SeriesInfo>(new SeriesInfo(), lookup, plan.Item.MetadataLanguage, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported item type {item.GetType().FullName}")
        };
        cancellationToken.ThrowIfCancellationRequested();
        var match = TitleNormalizer.ExactResult(plan.Item.TmdbId!, results);
        var missingOverview = plan.Item.FillMissingOverview && string.IsNullOrWhiteSpace(item.Overview)
            && !item.LockedFields.Contains(MetadataField.Overview) ? match?.Overview : null;
        return match is null
            ? plan with { Decision = new(plan.Item.Name, "provider-unavailable"), NeedsItemUpdate = false }
            : plan with { Decision = new(match.Name!, "exact-tmdb"), NeedsItemUpdate = match.Name != item.Name || !string.IsNullOrWhiteSpace(missingOverview), MissingOverview = missingOverview };
    }

    private async Task<IReadOnlyList<RemoteSearchResult>> SearchAsync<TItem, TLookup>(
        TLookup searchInfo,
        EnrichmentPlanItem plan,
        string? metadataLanguage,
        CancellationToken cancellationToken)
        where TItem : BaseItem, new()
        where TLookup : ItemLookupInfo
    {
        searchInfo.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tmdb"] = plan.Item.TmdbId!
        };
        searchInfo.IsAutomated = false;
        searchInfo.MetadataLanguage = metadataLanguage;
        searchInfo.MetadataCountryCode = plan.Item.MetadataCountryCode;
        var results = await _providerManager.GetRemoteSearchResults<TItem, TLookup>(
            new RemoteSearchQuery<TLookup>
            {
                ItemId = Guid.Parse(plan.Item.Id),
                SearchProviderName = "TheMovieDb",
                SearchInfo = searchInfo,
                IncludeDisabledProviders = false
            },
            cancellationToken).ConfigureAwait(false);
        return results.ToArray();
    }

    private static async Task<string?> ReadNfoTitleAsync(string path, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        return document.Root?.Element("title")?.Value;
    }

    internal static IReadOnlyList<string> ParseFallbackLanguages(string value) => value
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal static string LookupPolicy(string value) =>
        string.Join(",", ParseFallbackLanguages(value).Select(language => language.ToLowerInvariant()));

    private BaseItem GetLiveItem(LibraryItemSnapshot snapshot)
    {
        var item = _libraryManager.GetItemById(Guid.Parse(snapshot.Id))
            ?? throw new InvalidOperationException($"Item {snapshot.Id} no longer exists");
        if (!string.Equals(item.Path, snapshot.Path, StringComparison.Ordinal)
            || !string.Equals(ProviderId(item.ProviderIds, "Tmdb"), snapshot.TmdbId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Item identity changed for {snapshot.Id}");
        }

        return item;
    }

    private static string? ProviderId(IReadOnlyDictionary<string, string> providerIds, string provider) =>
        providerIds.FirstOrDefault(pair => string.Equals(pair.Key, provider, StringComparison.OrdinalIgnoreCase)).Value;
}

internal sealed record EnrichmentWriteResult(
    string Status,
    bool Succeeded,
    bool Terminal,
    string? Reason = null);