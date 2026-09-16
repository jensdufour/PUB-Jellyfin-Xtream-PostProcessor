using System.Globalization;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.XtreamPostProcessor.Normalization;

internal static class TitleNormalizer
{
    internal static bool IsValidTmdbId(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var identifier)
        && identifier > 0 && identifier.ToString(CultureInfo.InvariantCulture) == value;

    internal static RemoteSearchResult? ExactResult(string tmdbId, IEnumerable<RemoteSearchResult> results)
    {
        if (!IsValidTmdbId(tmdbId)) return null;
        var matches = results.Where(result => result.ProviderIds.Any(pair =>
            string.Equals(pair.Key, "Tmdb", StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.Value, tmdbId, StringComparison.Ordinal))).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException($"Ambiguous exact TMDb result for {tmdbId}");
        return matches.Length == 1 && !string.IsNullOrWhiteSpace(matches[0].Name) ? matches[0] : null;
    }
}
