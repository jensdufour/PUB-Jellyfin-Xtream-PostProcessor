using Jellyfin.Plugin.XtreamPostProcessor.Services;
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
    private readonly ITaskManager _taskManager;
    private readonly ILogger<EnrichXtreamTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnrichXtreamTask"/> class.
    /// </summary>
    public EnrichXtreamTask(
        LibraryAuditService auditService,
        AuditReportWriter reportWriter,
        ITaskManager taskManager,
        ILogger<EnrichXtreamTask> logger)
    {
        _auditService = auditService;
        _reportWriter = reportWriter;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Audit Xtream Metadata Enrichment";

    /// <inheritdoc />
    public string Key => "XtreamPostProcessorEnrich";

    /// <inheritdoc />
    public string Description => "Plans exact-TMDB enrichment after an Xtream synchronization.";

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

        var report = await _auditService.AuditEnrichmentAsync(cancellationToken).ConfigureAwait(false);
        await _reportWriter.WriteEnrichmentAsync(report, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Xtream enrichment audit: sync={SyncIdentity} success={SyncSuccess} scanned={ScannedCount} candidates={CandidateCount} invalidProviderIds={InvalidCount}",
            report.SyncResult?.Identity,
            report.SyncResult?.Success,
            report.ScannedItemCount,
            report.Candidates.Count,
            report.Candidates.Count(candidate => candidate.InvalidProviderId));
        progress.Report(100);

        if (report.SyncResult?.Success == true)
        {
            _taskManager.QueueIfNotRunning<NormalizeXtreamTask>();
        }
    }
}
