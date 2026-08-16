using Jellyfin.Plugin.XtreamPostProcessor.Services;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="NormalizeXtreamTask"/> class.
    /// </summary>
    public NormalizeXtreamTask(
        LibraryAuditService auditService,
        AuditReportWriter reportWriter,
        LibraryWriteService writeService,
        ILogger<NormalizeXtreamTask> logger)
    {
        _auditService = auditService;
        _reportWriter = reportWriter;
        _writeService = writeService;
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
        if (writeEnabled)
        {
            var expectedSyncIdentity = report.SyncResult!.Identity;
            var processingLock = await CrossProcessFileLock.AcquireAsync(
                _auditService.ResolveProcessingLockPath(),
                cancellationToken).ConfigureAwait(false);
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

                var updates = report.Candidates.Where(candidate => candidate.NeedsItemUpdate).ToArray();
                var processedCount = 0;
                foreach (var candidate in updates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (await _writeService.ApplyTitleAsync(candidate, cancellationToken).ConfigureAwait(false))
                        {
                            appliedCount++;
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        failureCount++;
                        _logger.LogError(exception, "Failed to normalize Xtream item {ItemId}", candidate.Item.Id);
                    }

                    progress.Report(100d * ++processedCount / updates.Length);
                }
            }
            finally
            {
                processingLock.Dispose();
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
