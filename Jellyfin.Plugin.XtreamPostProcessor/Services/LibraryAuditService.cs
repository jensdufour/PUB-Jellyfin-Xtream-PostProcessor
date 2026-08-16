using Jellyfin.Data.Enums;
using Jellyfin.Plugin.XtreamPostProcessor.Configuration;
using Jellyfin.Plugin.XtreamPostProcessor.Planning;
using Jellyfin.Plugin.XtreamPostProcessor.State;
using Jellyfin.Plugin.XtreamPostProcessor.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

/// <summary>
/// Builds shadow-mode plans from Jellyfin's supported library interface.
/// </summary>
public sealed class LibraryAuditService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly SyncHistoryReader _syncHistoryReader;
    private readonly EnrichmentStateReader _stateReader;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryAuditService"/> class.
    /// </summary>
    public LibraryAuditService(
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths,
        SyncHistoryReader syncHistoryReader,
        EnrichmentStateReader stateReader)
    {
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _syncHistoryReader = syncHistoryReader;
        _stateReader = stateReader;
    }

    internal async Task<EnrichmentAuditReport> AuditEnrichmentAsync(CancellationToken cancellationToken)
    {
        var configuration = Configuration();
        var sync = await ReadLatestSyncAsync(configuration, cancellationToken).ConfigureAwait(false);
        var items = ReadItems(configuration);
        var state = await _stateReader.ReadAsync(
            ResolveDataPath(configuration.LegacyStateRelativePath),
            cancellationToken).ConfigureAwait(false);
        var candidates = CandidatePlanner.PlanEnrichment(items, state, configuration.RetryFailed);
        return new EnrichmentAuditReport(sync, items.Count, candidates);
    }

    internal async Task<NormalizationAuditReport> AuditNormalizationAsync(CancellationToken cancellationToken)
    {
        var configuration = Configuration();
        var sync = await ReadLatestSyncAsync(configuration, cancellationToken).ConfigureAwait(false);
        var items = ReadItems(configuration);
        var candidates = CandidatePlanner.PlanNormalization(items);
        return new NormalizationAuditReport(sync, items.Count, candidates);
    }

    internal string ResolveDataPath(string configuredPath) => Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.Combine(_applicationPaths.DataPath, configuredPath);

    private async Task<XtreamSyncResult?> ReadLatestSyncAsync(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var path = ResolveDataPath(configuration.SyncHistoryRelativePath);
        return File.Exists(path)
            ? await _syncHistoryReader.ReadLatestAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private IReadOnlyList<LibraryItemSnapshot> ReadItems(PluginConfiguration configuration)
    {
        var root = Path.GetFullPath(configuration.XtreamRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false,
            EnableTotalRecordCount = false,
            GroupByPresentationUniqueKey = false
        });

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Where(item => Path.GetFullPath(item.Path).StartsWith(root, comparison))
            .Select(item => new LibraryItemSnapshot(
                item.Id.ToString("D").ToUpperInvariant(),
                item.GetType().FullName ?? item.GetType().Name,
                item.Name,
                item.OriginalTitle,
                item.Path,
                item.Overview,
                item.ProviderIds.TryGetValue("Tmdb", out var tmdbId) ? tmdbId : null,
                item.DateCreated,
                item is Series))
            .ToArray();
    }

    private static PluginConfiguration Configuration() =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
