using System.Globalization;
using Jellyfin.Plugin.XtreamPostProcessor.Normalization;
using Jellyfin.Plugin.XtreamPostProcessor.State;

namespace Jellyfin.Plugin.XtreamPostProcessor.Planning;

internal static partial class CandidatePlanner
{
    public static IReadOnlyList<EnrichmentPlanItem> PlanEnrichment(
        IEnumerable<LibraryItemSnapshot> items,
        EnrichmentState state,
        bool retryFailed,
        string lookupPolicy)
    {
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.TmdbId))
            .Where(item => string.IsNullOrWhiteSpace(item.Overview))
            .Select(item =>
            {
                var fingerprint = $"{item.TypeName}|tmdb:{item.TmdbId}|{item.Path}";
                var invalidProviderId = !int.TryParse(
                    item.TmdbId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var providerId)
                    || providerId <= 0
                    || !string.Equals(
                        item.TmdbId,
                        providerId.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal);
                return new EnrichmentPlanItem(item, fingerprint, invalidProviderId);
            })
            .Where(candidate => ShouldProcess(candidate, state, retryFailed, lookupPolicy))
            .OrderBy(candidate => state.Items.ContainsKey(candidate.Item.Id))
            .ThenBy(candidate => state.Items.TryGetValue(candidate.Item.Id, out var previous)
                ? previous.AttemptedUtc ?? DateTimeOffset.MinValue
                : DateTimeOffset.MinValue)
            .ThenByDescending(candidate => candidate.Item.DateCreated)
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
        bool retryFailed,
        string lookupPolicy)
    {
        if (!state.Items.TryGetValue(candidate.Item.Id, out var previous)
            || !string.Equals(previous.Fingerprint, candidate.Fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        return (previous.Status == "no-overview-available"
                && !string.Equals(previous.LookupPolicy, lookupPolicy, StringComparison.Ordinal))
            || (retryFailed && previous.Status is "failed" or "refreshed-no-details" or "provider-id-unavailable" or "overview-locked");
    }

    private static string SourceName(LibraryItemSnapshot item)
    {
        var path = item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return (item.IsSeries
            ? Path.GetFileName(path)
            : Path.GetFileName(Path.GetDirectoryName(path))) ?? string.Empty;
    }
}
