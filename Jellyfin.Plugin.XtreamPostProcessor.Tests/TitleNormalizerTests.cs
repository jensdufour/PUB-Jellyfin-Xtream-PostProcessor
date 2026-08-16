using Jellyfin.Plugin.XtreamPostProcessor.Normalization;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public sealed class TitleNormalizerTests
{
    [Theory]
    [InlineData("NL - Example (2024) [tmdbid-42]", "Example")]
    [InlineData("#BE-NL| Example [tvdbid-42]", "Example")]
    [InlineData("4K-EN - Example (US) (2024) [tmdbid-42]", "Example")]
    public void NormalizesProviderDisplayTitles(string source, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.NormalizeDisplayTitle(source));
    }

    [Fact]
    public void KeepsCurrentJellyfinTitleWhenAlreadyCurated()
    {
        var result = TitleNormalizer.DesiredItemTitle(
            "The Curated Title",
            "NL - Provider Title [tmdbid-42]",
            "Provider Title",
            isSeries: false);

        Assert.Equal(new TitleDecision("The Curated Title", "current-jellyfin-title"), result);
    }

    [Fact]
    public void AppliesKnownSeriesAlias()
    {
        var result = TitleNormalizer.DesiredItemTitle(
            "NL - Flikken Gent",
            "Flikken Gent (1999) (BE) [tmdbid-12908]",
            null,
            isSeries: true);

        Assert.Equal(new TitleDecision("Flikken", "known-series-alias"), result);
    }

    [Fact]
    public void HasNoFuturamaSpecificRule()
    {
        var result = TitleNormalizer.DesiredItemTitle(
            "EN - Futurama",
            "EN - Futurama [tmdbid-615]",
            "Futurama",
            isSeries: true);

        Assert.Equal("Futurama", result.Title);
        Assert.NotEqual("known-series-alias", result.Source);
    }
}
