using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.XtreamPostProcessor.Normalization;

internal static partial class TitleNormalizer
{
    private static readonly string[] Prefixes =
    [
        "BE-NL  -", "BE-NL -", "BE-NL|", "BE-FR -", "BE-FR|", "NL  -",
        "NL -", "NL-", "NL :", "NF -", "AMZ -", "AMZ-", "D+ -", "PRMT -",
        "A+ -", "DSC+ -", "P+ -", "PCOK -", "CR -", "SHWT -", "MAX -"
    ];

    private static readonly Dictionary<string, string> SeriesAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Kopen Zonder Kijken België (2019) (BE) [tmdbid-122158]"] = "Blind Gekocht",
        ["Blind Gekocht (2019) (BE) [tmdbid-122158]"] = "Blind Gekocht",
        ["Blind Gekocht (2019) [tmdbid-122158]"] = "Blind Gekocht",
        ["Flikken Gent (1999) (BE) [tmdbid-12908]"] = "Flikken",
        ["Flikken (1999) [tmdbid-12908]"] = "Flikken",
        ["Moedermaffia [tmdbid-209373]"] = "Moedermaffia",
        ["Moedermaffia (2022) [tmdbid-209373]"] = "Moedermaffia",
        ["Casa di Beau [tmdbid-230855]"] = "Casa di Beau",
        ["Casa Di Beau Winterspecial (2025) [tmdbid-230855]"] = "Casa di Beau",
        ["Casa di Beau (2023) [tmdbid-230855]"] = "Casa di Beau",
        ["Buck Rogers in the 25th Century (1979) (US) [tmdbid-2443]"] = "Buck Rogers in the 25th Century",
        ["Buck Rogers in the 25th Century (1979) [tmdbid-2443]"] = "Buck Rogers in the 25th Century",
        ["Border Patrol USA (2016) (US) [tmdbid-74818]"] = "Border Security: America's Front Line",
        ["Border Security - America's Front Line (2016) [tmdbid-74818]"] = "Border Security: America's Front Line",
        ["SAS - Rogue Heroes (2022) (GB) [tmdbid-93870]"] = "SAS Rogue Heroes",
        ["SAS Rogue Heroes (2022) (GB) [tmdbid-93870]"] = "SAS Rogue Heroes",
        ["SAS Rogue Heroes (2022) [tmdbid-93870]"] = "SAS Rogue Heroes"
    };

    public static bool HasProviderPrefix(string value)
    {
        var clean = value.TrimStart('#').TrimStart();
        return Prefixes.Any(prefix => clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || DisplayPrefixRegex().IsMatch(clean);
    }

    public static string StripProviderPrefixes(string value)
    {
        var clean = HasProviderPrefix(value) ? value.TrimStart('#').TrimStart() : value;
        while (true)
        {
            var prefix = Prefixes.FirstOrDefault(candidate =>
                clean.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (prefix is null)
            {
                break;
            }

            clean = clean[prefix.Length..].TrimStart();
        }

        return DisplayPrefixRegex().Replace(clean, string.Empty).Trim();
    }

    public static string NormalizeName(string value)
    {
        var clean = ZeroIdSuffixRegex().Replace(StripProviderPrefixes(value), string.Empty);
        return TrimTitle(MultipleSpacesRegex().Replace(clean, " "), " _-.");
    }

    public static string NormalizeDisplayTitle(string value)
    {
        var clean = MetadataIdSuffixRegex().Replace(StripProviderPrefixes(value), string.Empty);
        while (true)
        {
            var previous = clean;
            clean = CountrySuffixRegex().Replace(clean, string.Empty);
            clean = YearSuffixRegex().Replace(clean, string.Empty);
            if (clean == previous)
            {
                break;
            }
        }

        return TrimTitle(MultipleSpacesRegex().Replace(clean, " "), " _-");
    }

    public static TitleDecision DesiredItemTitle(
        string currentName,
        string sourceName,
        string? originalTitle,
        bool isSeries)
    {
        if (!HasProviderPrefix(currentName) && !ZeroIdSuffixRegex().IsMatch(currentName))
        {
            return new TitleDecision(MultipleSpacesRegex().Replace(currentName, " ").Trim(), "current-jellyfin-title");
        }

        if (isSeries && SeriesAliases.TryGetValue(NormalizeName(sourceName), out var alias))
        {
            return new TitleDecision(alias, "known-series-alias");
        }

        var clean = NormalizeDisplayTitle(sourceName);
        var original = MultipleSpacesRegex().Replace(originalTitle ?? string.Empty, " ").Trim();
        if (clean.Length > 0 && original.Length > 0 && Similarity(Identity(clean), Identity(original)) >= 0.9)
        {
            return new TitleDecision(original, "matching-original-title");
        }

        return clean.Length > 0
            ? new TitleDecision(clean, "normalized-source")
            : new TitleDecision(original.Length > 0 ? original : currentName.Trim(), "original-title-fallback");
    }

    private static string Identity(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return MultipleSpacesRegex().Replace(builder.ToString(), " ").Trim();
    }

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                current[rightIndex] = left[leftIndex - 1] == right[rightIndex - 1]
                    ? previous[rightIndex - 1] + 1
                    : Math.Max(previous[rightIndex], current[rightIndex - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return 2.0 * previous[right.Length] / (left.Length + right.Length);
    }

    private static string TrimTitle(string value, string characters) => value.Trim(characters.ToCharArray());

    [GeneratedRegex(@"\s*\[tmdbid-0\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ZeroIdSuffixRegex();

    [GeneratedRegex(@"\s*\[(?:tmdb|tvdb)id-\d+\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MetadataIdSuffixRegex();

    [GeneratedRegex(@"\s*\([A-Z]{2,3}\)\s*$")]
    private static partial Regex CountrySuffixRegex();

    [GeneratedRegex(@"\s*\((?:19|20)\d{2}\)\s*$")]
    private static partial Regex YearSuffixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"^(?:(?:4k-osn\+|4k-en|vp-nl|nick|osn\+|mrvl|unv|dwa|en|vp|sc)\s*[-|:]\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayPrefixRegex();
}
