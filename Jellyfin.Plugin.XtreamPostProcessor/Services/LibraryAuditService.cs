using Jellyfin.Data.Enums;
using Jellyfin.Plugin.XtreamPostProcessor.Configuration;
using Jellyfin.Plugin.XtreamPostProcessor.Normalization;
using Jellyfin.Plugin.XtreamPostProcessor.Planning;
using Jellyfin.Plugin.XtreamPostProcessor.State;
using Jellyfin.Plugin.XtreamPostProcessor.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

/// <summary>
/// Builds processing plans from Jellyfin's supported library interface.
/// </summary>
public sealed class LibraryAuditService : IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly SyncHistoryReader _syncHistoryReader;
    private readonly EnrichmentStateReader _stateReader;
    private readonly IServerConfigurationManager _serverConfiguration;
    private readonly ITaskManager _taskManager;
    internal SemaphoreSlim ProcessingGate { get; } = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryAuditService"/> class.
    /// </summary>
    public LibraryAuditService(
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths,
        SyncHistoryReader syncHistoryReader,
        EnrichmentStateReader stateReader,
        IServerConfigurationManager serverConfiguration,
        ITaskManager taskManager)
    {
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _syncHistoryReader = syncHistoryReader;
        _stateReader = stateReader;
        _serverConfiguration = serverConfiguration;
        _taskManager = taskManager;
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
        var state = await _stateReader.ReadAsync(ResolveOwnedStatePath("xtream-post-processor/title-state.json"), cancellationToken).ConfigureAwait(false);
        var candidates = CandidatePlanner.PlanNormalization(items, state, configuration.RetryFailed);
        return new NormalizationAuditReport(sync, items.Count, candidates);
    }

    internal async Task WaitForIndexingAsync(
        PluginConfiguration configuration,
        XtreamSyncResult expectedSync,
        CancellationToken cancellationToken)
    {
        await EnsureExpectedSyncAsync(configuration, expectedSync, cancellationToken).ConfigureAwait(false);
        if (!CanProcess(expectedSync)) throw new InvalidOperationException("Processing deferred until sync and indexing have completed successfully");
    }

    internal bool CanProcess(XtreamSyncResult? sync)
    {
        var tasks = _taskManager.ScheduledTasks.ToArray();
        var scan = tasks.FirstOrDefault(task => task.ScheduledTask.Key == "RefreshLibrary")?.LastExecutionResult;
        var busy = _libraryManager.IsScanRunning || tasks.Any(task =>
            task.ScheduledTask.Key is "RefreshLibrary" or "XtreamLibrarySync" or "MergeMoviesTask" or "MergeEpisodesTask"
            && task.State != TaskState.Idle);
        return IsReady(sync, scan, busy);
    }

    internal async Task EnsureCanWriteAsync(
        PluginConfiguration expectedConfiguration,
        XtreamSyncResult expectedSync,
        CancellationToken cancellationToken)
    {
        await WaitForIndexingAsync(expectedConfiguration, expectedSync, cancellationToken).ConfigureAwait(false);
        var current = Plugin.Instance?.Configuration;
        if (!ReferenceEquals(current, expectedConfiguration) || current?.Enabled != true || current.AuditOnly)
            throw new OperationCanceledException("Post-processing configuration changed; start a new task with the saved settings", cancellationToken);
    }

    internal static bool IsReady(XtreamSyncResult? sync, TaskResult? scan, bool busy) =>
        !busy && sync?.Success == true && sync.EndTime != default
        && scan?.Status == TaskCompletionStatus.Completed
        && scan.StartTimeUtc >= (sync.RequiredScanAfter ?? sync.EndTime).UtcDateTime
        && scan.EndTimeUtc >= scan.StartTimeUtc;

    /// <inheritdoc />
    public void Dispose() => ProcessingGate.Dispose();

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

    internal async Task<XtreamSyncResult?> ReadLatestSyncAsync(
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
                item.DateLastRefreshed,
                FirstConfigured(item.PreferredMetadataLanguage, _libraryManager.GetLibraryOptions(item).PreferredMetadataLanguage, _serverConfiguration.Configuration.PreferredMetadataLanguage),
                FirstConfigured(item.PreferredMetadataCountryCode, _libraryManager.GetLibraryOptions(item).MetadataCountryCode, _serverConfiguration.Configuration.MetadataCountryCode),
                item.IsLocked || item.LockedFields.Contains(MediaBrowser.Model.Entities.MetadataField.Name),
                configuration.FillMissingOverview,
                item is Series series ? series.DateLastMediaAdded : null))
            .ToArray();
    }

    internal static string? FirstConfigured(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

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

    private static IReadOnlyList<(string Path, string Prefix)> CanonicalRoots(PluginConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.XtreamRoot) || !Path.IsPathFullyQualified(configuration.XtreamRoot))
            throw new InvalidOperationException("Configure an absolute Xtream media root before processing");
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

    private static PluginConfiguration Configuration() =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
