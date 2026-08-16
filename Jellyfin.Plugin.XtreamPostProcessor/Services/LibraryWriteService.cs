using Jellyfin.Plugin.XtreamPostProcessor.Planning;
using Jellyfin.Plugin.XtreamPostProcessor.Normalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

/// <summary>
/// Applies planned metadata changes through Jellyfin's supported library interfaces.
/// </summary>
public sealed class LibraryWriteService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryWriteService"/> class.
    /// </summary>
    public LibraryWriteService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    internal async Task<bool> ApplyEnrichmentAsync(
        EnrichmentPlanItem plan,
        string fallbackLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.InvalidProviderId)
        {
            throw new InvalidOperationException($"Invalid TMDB provider ID for {plan.Item.Id}");
        }

        var item = GetLiveItem(plan.Item);
        if (!string.IsNullOrWhiteSpace(item.Overview)
            && !item.Name.Contains("[tmdbid-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var results = item switch
        {
            Movie => await SearchAsync<Movie, MovieInfo>(new MovieInfo(), plan, null, cancellationToken).ConfigureAwait(false),
            Series => await SearchAsync<Series, SeriesInfo>(new SeriesInfo(), plan, null, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported item type {item.GetType().FullName}")
        };
        var matches = results
            .Where(result => string.Equals(
                ProviderId(result.ProviderIds, "Tmdb"),
                plan.Item.TmdbId,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one exact TMDB {plan.Item.TmdbId} result, got {matches.Length}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        item.ProviderIds = matches[0].ProviderIds;
        await _providerManager.RefreshFullItem(
            item,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ReplaceAllImages = false,
                SearchResult = matches[0],
                RemoveOldMetadata = true
            },
            CancellationToken.None).ConfigureAwait(false);

        item = GetLiveItem(plan.Item);
        if (string.IsNullOrWhiteSpace(item.Overview) && !string.IsNullOrWhiteSpace(fallbackLanguage))
        {
            var fallbackResults = item switch
            {
                Movie => await SearchAsync<Movie, MovieInfo>(new MovieInfo(), plan, fallbackLanguage, CancellationToken.None).ConfigureAwait(false),
                Series => await SearchAsync<Series, SeriesInfo>(new SeriesInfo(), plan, fallbackLanguage, CancellationToken.None).ConfigureAwait(false),
                _ => []
            };
            var fallbackMatches = fallbackResults
                .Where(result => string.Equals(
                    ProviderId(result.ProviderIds, "Tmdb"),
                    plan.Item.TmdbId,
                    StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(result.Overview))
                .ToArray();
            if (fallbackMatches.Length == 1)
            {
                item.Overview = fallbackMatches[0].Overview;
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            }
        }

        item = GetLiveItem(plan.Item);
        return !string.IsNullOrWhiteSpace(item.Overview)
            && !item.Name.Contains("[tmdbid-", StringComparison.OrdinalIgnoreCase);
    }

    internal async Task<bool> ApplyTitleAsync(
        NormalizationPlanItem plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = GetLiveItem(plan.Item);
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
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
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