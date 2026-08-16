using Jellyfin.Plugin.XtreamPostProcessor.Planning;
using Jellyfin.Plugin.XtreamPostProcessor.Normalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

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
        CancellationToken cancellationToken)
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = GetLiveItem(plan.Item);
        if (item.IsLocked || item.LockedFields.Contains(MetadataField.Name))
        {
            return false;
        }

        if (!TitleNormalizer.HasProviderPrefix(plan.SourceName)
            && !plan.SourceName.Contains("[tmdbid-0]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var decision = TitleNormalizer.DesiredItemTitle(
            item.Name,
            plan.SourceName,
            item.OriginalTitle,
            item is Series);

        if (string.Equals(item.Name, decision.Title, StringComparison.Ordinal))
        {
            return false;
        }

        item.Name = decision.Title;
        await _providerManager.SaveMetadataAsync(item, ItemUpdateType.MetadataEdit).ConfigureAwait(false);
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, CancellationToken.None).ConfigureAwait(false);
        return true;
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
        searchInfo.MetadataCountryCode = metadataLanguage is null ? null : "BE";
        var results = await _providerManager.GetRemoteSearchResults<TItem, TLookup>(
            new RemoteSearchQuery<TLookup>
            {
                ItemId = Guid.Parse(plan.Item.Id),
                SearchInfo = searchInfo,
                IncludeDisabledProviders = false
            },
            cancellationToken).ConfigureAwait(false);
        return results.ToArray();
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