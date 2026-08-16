using System.Text.Json;
using Jellyfin.Plugin.XtreamPostProcessor.Planning;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

/// <summary>
/// Persists shadow audit reports under Jellyfin's data directory.
/// </summary>
public sealed class AuditReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IApplicationPaths _applicationPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditReportWriter"/> class.
    /// </summary>
    public AuditReportWriter(IApplicationPaths applicationPaths)
    {
        _applicationPaths = applicationPaths;
    }

    internal Task WriteEnrichmentAsync(
        EnrichmentAuditReport report,
        bool writeEnabled,
        int completedCount,
        int failureCount,
        int terminalCount,
        int retryableCount,
        CancellationToken cancellationToken) => WriteAsync(
            "last-enrichment-audit.json",
            new
            {
                generatedUtc = DateTimeOffset.UtcNow,
                syncIdentity = report.SyncResult?.Identity,
                syncSuccess = report.SyncResult?.Success,
                scannedItemCount = report.ScannedItemCount,
                candidateCount = report.Candidates.Count,
                invalidProviderIdCount = report.Candidates.Count(candidate => candidate.InvalidProviderId),
                writeEnabled,
                completedCount,
                failureCount,
                terminalCount,
                retryableCount,
                candidates = report.Candidates.Select(candidate => new
                {
                    id = candidate.Item.Id,
                    path = candidate.Item.Path,
                    tmdbId = candidate.Item.TmdbId,
                    fingerprint = candidate.Fingerprint,
                    invalidProviderId = candidate.InvalidProviderId
                })
            },
            cancellationToken);

    internal Task WriteNormalizationAsync(
        NormalizationAuditReport report,
        bool writeEnabled,
        int appliedCount,
        int failureCount,
        CancellationToken cancellationToken) => WriteAsync(
            "last-normalization-audit.json",
            new
            {
                generatedUtc = DateTimeOffset.UtcNow,
                syncIdentity = report.SyncResult?.Identity,
                syncSuccess = report.SyncResult?.Success,
                scannedItemCount = report.ScannedItemCount,
                candidateCount = report.Candidates.Count,
                itemUpdateCount = report.Candidates.Count(candidate => candidate.NeedsItemUpdate),
                writeEnabled,
                appliedCount,
                failureCount,
                candidates = report.Candidates.Select(candidate => new
                {
                    id = candidate.Item.Id,
                    path = candidate.Item.Path,
                    sourceName = candidate.SourceName,
                    currentTitle = candidate.Item.Name,
                    desiredTitle = candidate.Decision.Title,
                    titleSource = candidate.Decision.Source,
                    needsItemUpdate = candidate.NeedsItemUpdate
                })
            },
            cancellationToken);

    private async Task WriteAsync(string fileName, object value, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_applicationPaths.DataPath, "xtream-post-processor");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, fileName);
        var temporary = destination + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, destination, true);
    }
}
