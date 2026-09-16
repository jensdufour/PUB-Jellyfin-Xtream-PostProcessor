namespace Jellyfin.Plugin.XtreamPostProcessor.Planning;

internal sealed record LibraryItemSnapshot(
    string Id,
    string TypeName,
    string Name,
    string? OriginalTitle,
    string Path,
    string? Overview,
    string? TmdbId,
    DateTime DateCreated,
    bool IsSeries,
    DateTime DateLastRefreshed = default,
    string? MetadataLanguage = null,
    string? MetadataCountryCode = null,
    bool TitleLocked = false,
    bool FillMissingOverview = false,
    DateTime? DateLastMediaAdded = null);
