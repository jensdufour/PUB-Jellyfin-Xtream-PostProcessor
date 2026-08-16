using Jellyfin.Plugin.XtreamPostProcessor.Sync;

namespace Jellyfin.Plugin.XtreamPostProcessor.Planning;

internal sealed record EnrichmentAuditReport(
    XtreamSyncResult? SyncResult,
    int ScannedItemCount,
    IReadOnlyList<EnrichmentPlanItem> Candidates);

internal sealed record NormalizationAuditReport(
    XtreamSyncResult? SyncResult,
    int ScannedItemCount,
    IReadOnlyList<NormalizationPlanItem> Candidates);
