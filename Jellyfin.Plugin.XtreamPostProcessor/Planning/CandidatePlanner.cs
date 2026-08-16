using Jellyfin.Plugin.XtreamPostProcessor.Normalization;
using Jellyfin.Plugin.XtreamPostProcessor.State;

namespace Jellyfin.Plugin.XtreamPostProcessor.Planning;

internal static partial class CandidatePlanner
{
    public static IReadOnlyList<EnrichmentPlanItem> PlanEnrichment(
        IEnumerable<LibraryItemSnapshot> items,
        EnrichmentState state,
        bool retryFailed)
    {
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.TmdbId))
            .Where(item => string.IsNullOrWhiteSpace(item.Overview)
                || item.Name.Contains("[tmdbid-", StringComparison.OrdinalIgnoreCase))
            .Select(item =>
            {
                var fingerprint = $"{item.TypeName}|tmdb:{item.TmdbId}|{item.Path}";
                return new EnrichmentPlanItem(item, fingerprint, item.TmdbId is "0");
            })
            .Where(candidate => ShouldProcess(candidate, state, retryFailed))
            .OrderByDescending(candidate => candidate.Item.DateCreated)
            .ThenByDescending(candidate => candidate.Item.Name, StringComparer.Ordinal)
            .ThenByDescending(candidate => candidate.Item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<NormalizationPlanItem> PlanNormalization(
        IEnumerable<LibraryItemSnapshot> items)
    {
        var results = new List<NormalizationPlanItem>();
        foreach (var item in items)
        {
            var sourceName = SourceName(item);
            if (!TitleNormalizer.HasProviderPrefix(sourceName)
                && !sourceName.Contains("[tmdbid-0]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var decision = TitleNormalizer.DesiredItemTitle(
                item.Name,
                sourceName,
                item.OriginalTitle,
                item.IsSeries);
            results.Add(new NormalizationPlanItem(
                item,
                sourceName,
                decision,
                !string.Equals(decision.Title, item.Name, StringComparison.Ordinal)));
        }

        return results;
    }

    private static bool ShouldProcess(
        EnrichmentPlanItem candidate,
        EnrichmentState state,
        bool retryFailed)
    {
        if (!state.Items.TryGetValue(candidate.Item.Id, out var previous)
            || !string.Equals(previous.Fingerprint, candidate.Fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        return retryFailed && previous.Status is "failed" or "refreshed-no-details";
    }

    private static string SourceName(LibraryItemSnapshot item)
    {
        var path = item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return (item.IsSeries
            ? Path.GetFileName(path)
            : Path.GetFileName(Path.GetDirectoryName(path))) ?? string.Empty;
    }
}
