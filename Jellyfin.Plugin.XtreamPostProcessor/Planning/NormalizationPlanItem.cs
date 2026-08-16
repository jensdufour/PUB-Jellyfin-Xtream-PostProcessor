using Jellyfin.Plugin.XtreamPostProcessor.Normalization;

namespace Jellyfin.Plugin.XtreamPostProcessor.Planning;

internal sealed record NormalizationPlanItem(
    LibraryItemSnapshot Item,
    string SourceName,
    TitleDecision Decision,
    bool NeedsItemUpdate);
