using Jellyfin.Plugin.XtreamPostProcessor.Services;
using Jellyfin.Plugin.XtreamPostProcessor.Planning;
using Jellyfin.Plugin.XtreamPostProcessor.State;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tasks;

/// <summary>
/// Audits Xtream items that require title normalization.
/// </summary>
public sealed class NormalizeXtreamTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly LibraryAuditService _auditService;
    private readonly AuditReportWriter _reportWriter;
    private readonly LibraryWriteService _writeService;
    private readonly ILogger<NormalizeXtreamTask> _logger;
    private readonly EnrichmentStateReader _stateReader;

    /// <summary>
    /// Initializes a new instance of the <see cref="NormalizeXtreamTask"/> class.
    /// </summary>
    public NormalizeXtreamTask(
        LibraryAuditService auditService,
        AuditReportWriter reportWriter,
        LibraryWriteService writeService,
        EnrichmentStateReader stateReader,
        ILogger<NormalizeXtreamTask> logger)
    {
        _auditService = auditService;
        _reportWriter = reportWriter;
        _writeService = writeService;
        _stateReader = stateReader;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Process Xtream Title Normalization";

    /// <inheritdoc />
    public string Key => "XtreamPostProcessorNormalize";

    /// <inheritdoc />
    public string Description => "Audits or applies display-title and NFO normalization for Xtream items.";

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

        var report = await _auditService.AuditNormalizationAsync(cancellationToken).ConfigureAwait(false);
        var writeEnabled = !configuration.AuditOnly && report.SyncResult?.Success == true;
        var appliedCount = 0;
        var failureCount = 0;
        if (report.SyncResult?.Success == true)
        {
            var expectedSyncIdentity = report.SyncResult!.Identity;
            await _auditService.ProcessingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _auditService.WaitForIndexingAsync(configuration, report.SyncResult!, cancellationToken).ConfigureAwait(false);
                report = await _auditService.AuditNormalizationAsync(cancellationToken).ConfigureAwait(false);
                if (report.SyncResult?.Success != true)
                {
                    throw new InvalidOperationException("Latest Xtream synchronization is not successful");
                }

                if (!string.Equals(report.SyncResult.Identity, expectedSyncIdentity, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Latest Xtream synchronization changed after indexing stabilized");
                }

                if (configuration.WriteBatchSize < 0) throw new InvalidOperationException("Write batch size cannot be negative");
                IEnumerable<NormalizationPlanItem> updates = report.Candidates;
                if (!string.IsNullOrWhiteSpace(configuration.WriteItemId))
                {
                    var selected = Guid.Parse(configuration.WriteItemId);
                    updates = updates.Where(candidate => Guid.Parse(candidate.Item.Id) == selected);
                }
                if (configuration.WriteBatchSize > 0) updates = updates.Take(configuration.WriteBatchSize);
                var candidates = updates.ToArray();
                var resolved = new List<NormalizationPlanItem>();
                var statePath = _auditService.ResolveOwnedStatePath("xtream-post-processor/title-state.json");
                var state = await _stateReader.ReadAsync(statePath, cancellationToken).ConfigureAwait(false);
                var processedCount = 0;
                try
                {
                    foreach (var candidate in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await _auditService.WaitForIndexingAsync(configuration, report.SyncResult!, cancellationToken).ConfigureAwait(false);
                        var fingerprint = CandidatePlanner.NormalizationFingerprint(candidate.Item);
                        var status = "provider-unavailable";
                        try
                        {
                            var decision = await _writeService.ResolveTitleAsync(candidate, cancellationToken).ConfigureAwait(false);
                            resolved.Add(decision);
                            if (writeEnabled && decision.Decision.Source == "exact-tmdb")
                            {
                                if (await _writeService.ApplyTitleAsync(decision, cancellationToken,
                                    () => _auditService.EnsureCanWriteAsync(configuration, report.SyncResult!, cancellationToken)).ConfigureAwait(false)) appliedCount++;
                                fingerprint = CandidatePlanner.NormalizationFingerprint(candidate.Item with
                                {
                                    Name = decision.Decision.Title,
                                    Overview = string.IsNullOrWhiteSpace(candidate.Item.Overview) ? decision.MissingOverview : candidate.Item.Overview
                                });
                                status = "canonical";
                            }
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            failureCount++;
                            status = "failed";
                            _logger.LogError(exception, "Failed to normalize Xtream item {ItemId}", candidate.Item.Id);
                        }
                        if (writeEnabled)
                        {
                            state.Items[candidate.Item.Id] = new EnrichmentStateItem
                            {
                                Fingerprint = fingerprint, Status = status, AttemptedUtc = DateTimeOffset.UtcNow
                            };
                            if ((processedCount + 1) % 64 == 0)
                                await _stateReader.WriteAsync(statePath, state, CancellationToken.None).ConfigureAwait(false);
                        }
                        progress.Report(100d * ++processedCount / candidates.Length);
                    }
                }
                finally
                {
                    if (writeEnabled)
                    {
                        state.UpdatedUtc = DateTimeOffset.UtcNow;
                        await _stateReader.WriteAsync(statePath, state, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                report = report with { Candidates = resolved };
            }
            finally
            {
                _auditService.ProcessingGate.Release();
            }
        }

        await _reportWriter.WriteNormalizationAsync(
            report,
            writeEnabled,
            appliedCount,
            failureCount,
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Xtream normalization audit: sync={SyncIdentity} success={SyncSuccess} scanned={ScannedCount} candidates={CandidateCount} itemUpdates={UpdateCount} applied={AppliedCount} failed={FailureCount}",
            report.SyncResult?.Identity,
            report.SyncResult?.Success,
            report.ScannedItemCount,
            report.Candidates.Count,
            report.Candidates.Count(candidate => candidate.NeedsItemUpdate),
            appliedCount,
            failureCount);
        progress.Report(100);

        if (failureCount > 0)
        {
            throw new InvalidOperationException($"Failed to normalize {failureCount} Xtream items");
        }
    }
}
