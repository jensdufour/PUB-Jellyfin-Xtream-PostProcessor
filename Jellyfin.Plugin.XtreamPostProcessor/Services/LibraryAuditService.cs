using System.Diagnostics;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.XtreamPostProcessor.Configuration;
using Jellyfin.Plugin.XtreamPostProcessor.Normalization;
using Jellyfin.Plugin.XtreamPostProcessor.Planning;
using Jellyfin.Plugin.XtreamPostProcessor.State;
using Jellyfin.Plugin.XtreamPostProcessor.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

/// <summary>
/// Builds processing plans from Jellyfin's supported library interface.
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
        var statePath = ResolveOwnedStatePath(configuration.StateRelativePath);
        if (!File.Exists(statePath))
        {
            statePath = ResolveDataPath(configuration.LegacyStateRelativePath);
        }

        var state = await _stateReader.ReadAsync(
            statePath,
            cancellationToken).ConfigureAwait(false);
        var lookupPolicy = LibraryWriteService.LookupPolicy(configuration.FallbackLanguages);
        var candidates = CandidatePlanner.PlanEnrichment(items, state, configuration.RetryFailed, lookupPolicy);
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

    internal async Task WaitForIndexingAsync(
        PluginConfiguration configuration,
        XtreamSyncResult expectedSync,
        CancellationToken cancellationToken)
    {
        if (configuration.IndexingStableSeconds < 0
            || configuration.IndexingTimeoutSeconds < 1
            || configuration.MaxUnindexedChangedRoots < 0)
        {
            throw new InvalidOperationException("Indexing limits must be non-negative and timeout must be positive");
        }

        var changedRoots = ChangedSourceRoots(configuration, expectedSync.StartTime);
        if (changedRoots.Count == 0)
        {
            await EnsureExpectedSyncAsync(configuration, expectedSync, cancellationToken).ConfigureAwait(false);
            return;
        }

        var stableWindow = TimeSpan.FromSeconds(configuration.IndexingStableSeconds);
        var timeout = TimeSpan.FromSeconds(configuration.IndexingTimeoutSeconds);
        var started = Stopwatch.GetTimestamp();
        var stableSince = started;
        var items = ReadItems(configuration);
        var previous = InventorySignature(items);
        while (true)
        {
            await EnsureExpectedSyncAsync(configuration, expectedSync, cancellationToken).ConfigureAwait(false);
            items = ReadItems(configuration);
            var current = InventorySignature(items);
            if (current != previous)
            {
                previous = current;
                stableSince = Stopwatch.GetTimestamp();
            }

            var pending = PendingChangedRoots(changedRoots, items);
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= timeout)
            {
                throw new TimeoutException(
                    $"Jellyfin indexing did not stabilize within {configuration.IndexingTimeoutSeconds} seconds; "
                    + $"pendingRoots={pending.Count} sample={string.Join(", ", pending.Take(5))}");
            }

            if (Stopwatch.GetElapsedTime(stableSince) >= stableWindow)
            {
                if (pending.Count > configuration.MaxUnindexedChangedRoots)
                {
                    throw new InvalidOperationException(
                        $"Refusing enrichment with {pending.Count} unindexed changed roots; "
                        + $"maximum={configuration.MaxUnindexedChangedRoots} sample={string.Join(", ", pending.Take(5))}");
                }

                if (pending.Count == 0)
                {
                    return;
                }
            }

            var remaining = timeout - elapsed;
            await Task.Delay(remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal string ResolveDataPath(string configuredPath) => Path.GetFullPath(Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.Combine(_applicationPaths.DataPath, configuredPath));

    internal string ResolveOwnedStatePath(string configuredPath)
    {
        var pluginDirectory = Path.GetFullPath(Path.Combine(_applicationPaths.DataPath, "xtream-post-processor"));
        var path = ResolveDataPath(configuredPath);
        var prefix = pluginDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison))
        {
            throw new InvalidOperationException(
                $"Plugin state must remain under {pluginDirectory}: {path}");
        }

        return path;
    }

    internal string ResolveProcessingLockPath() =>
        ResolveOwnedStatePath("xtream-post-processor/processor.lock");

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
        var roots = CanonicalRoots(configuration);
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
            .Where(item => roots.Any(root => Path.GetFullPath(item.Path).StartsWith(root.Prefix, comparison)))
            .Select(item => new LibraryItemSnapshot(
                item.Id.ToString("D").ToUpperInvariant(),
                item.GetType().FullName ?? item.GetType().Name,
                item.Name,
                item.OriginalTitle,
                item.Path,
                item.Overview,
                item.ProviderIds.TryGetValue("Tmdb", out var tmdbId) ? tmdbId : null,
                item.DateCreated,
                item is Series,
                item.DateLastRefreshed))
            .ToArray();
    }

    private async Task EnsureExpectedSyncAsync(
        PluginConfiguration configuration,
        XtreamSyncResult expectedSync,
        CancellationToken cancellationToken)
    {
        var latest = await ReadLatestSyncAsync(configuration, cancellationToken).ConfigureAwait(false);
        if (latest?.Success != true
            || !string.Equals(latest.Identity, expectedSync.Identity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Latest Xtream synchronization changed while waiting for indexing");
        }
    }

    private static IReadOnlyList<string> ChangedSourceRoots(
        PluginConfiguration configuration,
        DateTimeOffset syncStarted)
    {
        var cutoff = syncStarted.UtcDateTime.AddSeconds(-5);
        return CanonicalRoots(configuration)
            .SelectMany(root => Directory.EnumerateDirectories(root.Path, "*", SearchOption.TopDirectoryOnly))
            .Where(path =>
            {
                var directory = new DirectoryInfo(path);
                return directory.LastWriteTimeUtc >= cutoff || directory.CreationTimeUtc >= cutoff;
            })
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> PendingChangedRoots(
        IReadOnlyList<string> changedRoots,
        IReadOnlyList<LibraryItemSnapshot> items)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var indexedRoots = items
            .Select(item => Path.GetFullPath(item.IsSeries ? item.Path : Path.GetDirectoryName(item.Path)!))
            .ToHashSet(comparer);

        return changedRoots
            .Where(path => !indexedRoots.Contains(path))
            .Where(path => !indexedRoots.Contains(Path.Combine(
                Path.GetDirectoryName(path)!,
                TitleNormalizer.StripProviderPrefixes(Path.GetFileName(path)))))
            .Order(comparer)
            .ToArray();
    }

    private static IReadOnlyList<(string Path, string Prefix)> CanonicalRoots(PluginConfiguration configuration)
    {
        var configuredRoot = Path.GetFullPath(configuration.XtreamRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(
                configuredRoot,
                Path.GetPathRoot(configuredRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(configuredRoot))
        {
            throw new InvalidOperationException($"Invalid Xtream media root: {configuredRoot}");
        }

        var roots = new[] { "Movies", "Series" }
            .Select(name => Path.Combine(configuredRoot, name))
            .Where(Directory.Exists)
            .Select(path => (Path: path, Prefix: path + Path.DirectorySeparatorChar))
            .ToArray();
        if (roots.Length == 0)
        {
            throw new InvalidOperationException($"Xtream media root has no Movies or Series directory: {configuredRoot}");
        }

        return roots;
    }

    private static (int Count, DateTime Created, DateTime Refreshed) InventorySignature(
        IReadOnlyList<LibraryItemSnapshot> items) =>
        (
            items.Count,
            items.Count == 0 ? default : items.Max(item => item.DateCreated),
            items.Count == 0 ? default : items.Max(item => item.DateLastRefreshed));

    private static PluginConfiguration Configuration() =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
