namespace Jellyfin.Plugin.XtreamPostProcessor.Planning;

internal sealed record EnrichmentPlanItem(
    LibraryItemSnapshot Item,
    string Fingerprint,
    bool InvalidProviderId);
