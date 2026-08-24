using Jellyfin.Plugin.XtreamPostProcessor.Planning;
using Jellyfin.Plugin.XtreamPostProcessor.Services;
using Jellyfin.Plugin.XtreamPostProcessor.Configuration;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public sealed class IndexingSafetyTests
{
    [Fact]
    public void ChangedSourceRootsIgnoreFoldersWithoutStrmFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"xtream-{Guid.NewGuid():N}");
        var emptyRoot = Path.Combine(root, "Movies", "EN - - Example (2026) [tmdbid-42]");
        var mediaRoot = Path.Combine(root, "Movies", "EN - Example (2026) [tmdbid-42]");
        Directory.CreateDirectory(emptyRoot);
        Directory.CreateDirectory(mediaRoot);
        File.WriteAllText(Path.Combine(emptyRoot, "Example.nfo"), "metadata only");
        File.WriteAllText(Path.Combine(mediaRoot, "Example.strm"), "https://example.invalid/stream");

        try
        {
            var changed = LibraryAuditService.ChangedSourceRoots(
                new PluginConfiguration { XtreamRoot = root },
                DateTimeOffset.UtcNow.AddMinutes(-1));

            Assert.Equal(Path.GetFullPath(mediaRoot), Assert.Single(changed));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DoesNotCollapseDistinctRootsByTmdbIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "xtream");
        var changed = Path.Combine(root, "Movies", "Provider A Example [tmdbid-42]");
        var indexed = Item(
            Path.Combine(root, "Movies", "Provider B Example [tmdbid-42]", "Example.strm"),
            "42");

        Assert.Equal(
            Path.GetFullPath(changed),
            Assert.Single(LibraryAuditService.PendingChangedRoots([changed], [indexed])));
    }

    [Fact]
    public void RetainsUnrepresentedChangedRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "xtream");
        var changed = Path.Combine(root, "Series", "New [tmdbid-99]");

        Assert.Equal(
            Path.GetFullPath(changed),
            Assert.Single(LibraryAuditService.PendingChangedRoots([changed], [])));
    }

    private static LibraryItemSnapshot Item(string path, string tmdbId) => new(
        Guid.NewGuid().ToString("D"),
        "MediaBrowser.Controller.Entities.Movies.Movie",
        "Example",
        null,
        path,
        null,
        tmdbId,
        DateTime.UtcNow,
        false);
}