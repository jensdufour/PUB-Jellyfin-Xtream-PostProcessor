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
    private readonly ILogger<NormalizeXtreamTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NormalizeXtreamTask"/> class.
    /// </summary>
    public NormalizeXtreamTask(
        LibraryAuditService auditService,
        AuditReportWriter reportWriter,
        ILogger<NormalizeXtreamTask> logger)
    {
        _auditService = auditService;
        _reportWriter = reportWriter;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Audit Xtream Title Normalization";

    /// <inheritdoc />
    public string Key => "XtreamPostProcessorNormalize";

    /// <inheritdoc />
    public string Description => "Plans display-title and NFO normalization for Xtream items.";

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

        if (!configuration.AuditOnly)
        {
            throw new InvalidOperationException("Write mode is not available in this shadow release");
        }

        var report = await _auditService.AuditNormalizationAsync(cancellationToken).ConfigureAwait(false);
        await _reportWriter.WriteNormalizationAsync(report, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Xtream normalization audit: sync={SyncIdentity} success={SyncSuccess} scanned={ScannedCount} candidates={CandidateCount} itemUpdates={UpdateCount}",
            report.SyncResult?.Identity,
            report.SyncResult?.Success,
            report.ScannedItemCount,
            report.Candidates.Count,
            report.Candidates.Count(candidate => candidate.NeedsItemUpdate));
        progress.Report(100);
    }
}
