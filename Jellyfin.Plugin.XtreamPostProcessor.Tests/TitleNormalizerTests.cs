using Jellyfin.Plugin.XtreamPostProcessor.Normalization;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public sealed class TitleNormalizerTests
{
    [Theory]
    [InlineData("384", "Doug")]
    [InlineData("222023", "Jury Duty")]
    [InlineData("100963", "The Big Show Show")]
    [InlineData("12908", "Flikken")]
    public void UsesExactProviderTitle(string identifier, string title)
    {
        Assert.Equal(title, TitleNormalizer.ExactResult(identifier, [Result(identifier, title)])!.Name);
    }

    [Fact]
    public void RejectsUnrelatedBlankAndInvalidResults()
    {
        Assert.Null(TitleNormalizer.ExactResult("42", [Result("43", "Wrong")]));
        Assert.Null(TitleNormalizer.ExactResult("42", [Result("42", " ")]));
        Assert.Null(TitleNormalizer.ExactResult("0", [Result("0", "Unknown")]));
    }

    [Fact]
    public void RejectsAmbiguousResults()
    {
        Assert.Throws<InvalidOperationException>(() => TitleNormalizer.ExactResult("42", [Result("42", "First"), Result("42", "Second")]));
    }

    [Fact]
    public void PreservesProviderPunctuationAndLegitimateYears()
    {
        Assert.Equal("1984 (Director's Cut)", TitleNormalizer.ExactResult("42", [Result("42", "1984 (Director's Cut)")])!.Name);
    }

    private static RemoteSearchResult Result(string identifier, string title) => new()
    {
        Name = title,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = identifier }
    };
}
