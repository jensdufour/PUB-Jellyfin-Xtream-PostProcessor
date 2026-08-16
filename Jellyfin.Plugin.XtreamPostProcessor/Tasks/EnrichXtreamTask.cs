using Jellyfin.Plugin.XtreamPostProcessor.Services;
using Jellyfin.Plugin.XtreamPostProcessor.State;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tasks;

/// <summary>
/// Audits Xtream items that require exact-TMDB enrichment.
/// </summary>
public sealed class EnrichXtreamTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly LibraryAuditService _auditService;
    private readonly AuditReportWriter _reportWriter;
    private readonly LibraryWriteService _writeService;
    private readonly EnrichmentStateReader _stateReader;
    private readonly DeferredTaskScheduler _deferredTasks;
    private readonly ILogger<EnrichXtreamTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnrichXtreamTask"/> class.
    /// </summary>
    public EnrichXtreamTask(
        LibraryAuditService auditService,
        AuditReportWriter reportWriter,
        LibraryWriteService writeService,
        EnrichmentStateReader stateReader,
        DeferredTaskScheduler deferredTasks,
        ILogger<EnrichXtreamTask> logger)
    {
        _auditService = auditService;
        _reportWriter = reportWriter;
        _writeService = writeService;
        _stateReader = stateReader;
        _deferredTasks = deferredTasks;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Process Xtream Metadata Enrichment";

    /// <inheritdoc />
    public string Key => "XtreamPostProcessorEnrich";

    /// <inheritdoc />
    public string Description => "Audits or applies exact-TMDB enrichment after an Xtream synchronization.";

    /// <inheritdoc />
    public string Category => "Xtream Post Processor";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration?.Enabled != true)
        {
            _logger.LogInformation("Xtream post-processing is disabled");
            return;
        }

        var report = await _auditService.AuditEnrichmentAsync(cancellationToken).ConfigureAwait(false);
        var writeEnabled = !configuration.AuditOnly && report.SyncResult?.Success == true;
        var completedCount = 0;
        var failureCount = 0;
        var terminalCount = 0;
        var retryableCount = 0;
        if (writeEnabled)
        {
            var expectedSyncIdentity = report.SyncResult!.Identity;
            var processingLock = await CrossProcessFileLock.AcquireAsync(
                _auditService.ResolveProcessingLockPath(),
                cancellationToken).ConfigureAwait(false);
            try
            {
                await _auditService.WaitForIndexingAsync(configuration, report.SyncResult!, cancellationToken).ConfigureAwait(false);
                report = await _auditService.AuditEnrichmentAsync(cancellationToken).ConfigureAwait(false);
                if (report.SyncResult?.Success != true)
                {
                    throw new InvalidOperationException("Latest Xtream synchronization is not successful");
                }

                if (!string.Equals(report.SyncResult.Identity, expectedSyncIdentity, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Latest Xtream synchronization changed after indexing stabilized");
                }

                var legacyStatePath = _auditService.ResolveDataPath(configuration.LegacyStateRelativePath);
                var statePath = _auditService.ResolveOwnedStatePath(configuration.StateRelativePath);
                var sourcePath = File.Exists(statePath) ? statePath : legacyStatePath;
                var state = await _stateReader.ReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                using var stateLock = new SemaphoreSlim(1, 1);
                var processedCount = 0;
                if (configuration.WriteBatchSize < 0)
                {
                    throw new InvalidOperationException("Write batch size cannot be negative");
                }

                IReadOnlyList<Planning.EnrichmentPlanItem> candidates = report.Candidates;
                if (!string.IsNullOrWhiteSpace(configuration.WriteItemId))
                {
                    if (!Guid.TryParse(configuration.WriteItemId, out var itemId))
                    {
                        throw new InvalidOperationException("Write item ID is not a valid GUID");
                    }

                    candidates = report.Candidates
                        .Where(candidate => Guid.Parse(candidate.Item.Id) == itemId)
                        .ToArray();
                    if (candidates.Count == 0)
                    {
                        _logger.LogInformation("Write item {ItemId} is no longer an enrichment candidate", itemId);
                    }
                }

                if (configuration.WriteBatchSize > 0)
                {
                    candidates = candidates.Take(configuration.WriteBatchSize).ToArray();
                }

                await Parallel.ForEachAsync(
                    candidates,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Math.Clamp(configuration.EnrichmentWorkers, 1, 16)
                    },
                    async (candidate, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        EnrichmentStateItem outcome;
                        if (candidate.InvalidProviderId)
                        {
                            Interlocked.Increment(ref terminalCount);
                            outcome = new EnrichmentStateItem
                            {
                                Fingerprint = candidate.Fingerprint,
                                Status = "invalid-provider-id",
                                AttemptedUtc = DateTimeOffset.UtcNow,
                                Terminal = true,
                                Reason = $"Invalid TMDB provider ID {candidate.Item.TmdbId}; exact lookup is impossible"
                            };
                        }
                        else
                        {
                            try
                            {
                                var result = await _writeService.ApplyEnrichmentAsync(
                                    candidate,
                                    configuration.FallbackLanguages,
                                    token).ConfigureAwait(false);
                                if (result.Succeeded)
                                {
                                    Interlocked.Increment(ref completedCount);
                                }
                                else if (result.Terminal)
                                {
                                    Interlocked.Increment(ref terminalCount);
                                }
                                else
                                {
                                    Interlocked.Increment(ref retryableCount);
                                }

                                outcome = new EnrichmentStateItem
                                {
                                    Fingerprint = candidate.Fingerprint,
                                    Status = result.Status,
                                    AttemptedUtc = DateTimeOffset.UtcNow,
                                    Terminal = result.Terminal,
                                    Reason = result.Reason,
                                    LookupPolicy = LibraryWriteService.LookupPolicy(configuration.FallbackLanguages)
                                };
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                Interlocked.Increment(ref failureCount);
                                var error = $"{exception.GetType().Name}: {exception.Message}";
                                _logger.LogError(exception, "Failed to enrich Xtream item {ItemId}", candidate.Item.Id);
                                outcome = new EnrichmentStateItem
                                {
                                    Fingerprint = candidate.Fingerprint,
                                    Status = "failed",
                                    AttemptedUtc = DateTimeOffset.UtcNow,
                                    Error = error
                                };
                            }
                        }

                        await stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        try
                        {
                            state.Items[candidate.Item.Id] = outcome;
                            state.UpdatedUtc = DateTimeOffset.UtcNow;
                            await _stateReader.WriteAsync(statePath, state, CancellationToken.None).ConfigureAwait(false);
                        }
                        finally
                        {
                            stateLock.Release();
                        }

                        progress.Report(100d * Interlocked.Increment(ref processedCount) / candidates.Count);
                    }).ConfigureAwait(false);
            }
            finally
            {
                processingLock.Dispose();
            }
        }

        await _reportWriter.WriteEnrichmentAsync(
            report,
            writeEnabled,
            completedCount,
            failureCount,
            terminalCount,
            retryableCount,
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Xtream enrichment audit: sync={SyncIdentity} success={SyncSuccess} scanned={ScannedCount} candidates={CandidateCount} completed={CompletedCount} failed={FailureCount} terminal={TerminalCount} retryable={RetryableCount}",
            report.SyncResult?.Identity,
            report.SyncResult?.Success,
            report.ScannedItemCount,
            report.Candidates.Count,
            completedCount,
            failureCount,
            terminalCount,
            retryableCount);
        progress.Report(100);

        if (failureCount > 0)
        {
            throw new InvalidOperationException($"Failed to enrich {failureCount} Xtream items");
        }

        if (report.SyncResult?.Success == true
            && string.IsNullOrWhiteSpace(configuration.WriteItemId)
            && configuration.WriteBatchSize == 0)
        {
            _deferredTasks.QueueNormalization();
        }
    }
}
