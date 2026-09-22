using System.Reflection;
using Jellyfin.Plugin.XtreamPostProcessor.Planning;
using Jellyfin.Plugin.XtreamPostProcessor.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.XtreamPostProcessor.Tests;

public class InterfaceStub : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (_, _) => throw new NotImplementedException();
    protected override object? Invoke(MethodInfo? method, object?[]? arguments) => Handler(method!, arguments);
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = Create<T, InterfaceStub>();
        ((InterfaceStub)(object)proxy).Handler = handler;
        return proxy;
    }
}

public sealed class LibraryWriteServiceTests
{
    [Theory]
    [InlineData("EN - Doug (1991) (US)", "Doug", "384")]
    [InlineData("AMZ - Jury Duty (2023) (US)", "Jury Duty", "222023")]
    [InlineData("The Big Show Show (2020) (US)", "The Big Show Show", "100963")]
    public async Task WritesCanonicalTitleAndChildLabelsWithoutChangingIdentity(string source, string canonical, string identifier)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = source, Path = "/media/Series/" + source, Overview = "Keep synopsis" };
        series.ProviderIds["Tmdb"] = identifier;
        var season = new Season { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = source, Name = "Season 2" };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = source, Name = "Keep episode title", IndexNumber = 1 };
        var writes = new List<Guid>();
        var savedNfoTitles = new List<string>();
        var library = InterfaceStub.Create<ILibraryManager>((method, arguments) => method.Name switch
        {
            "GetItemById" => series,
            "GetLibraryOptions" => new LibraryOptions(),
            "GetItemList" => new List<BaseItem> { season, episode },
            "UpdateItemAsync" => Record(writes, ((BaseItem)arguments![0]!).Id),
            _ => throw new NotImplementedException(method.Name)
        });
        var provider = InterfaceStub.Create<IProviderManager>((method, arguments) => method.Name switch
        {
            "GetMetadataSavers" => Array.Empty<IMetadataSaver>(),
            "SaveMetadataAsync" => Record(savedNfoTitles, ((BaseItem)arguments![0]!).Name),
            _ => throw new NotImplementedException(method.Name)
        });
        var service = new LibraryWriteService(library, provider);
        var snapshot = Snapshot(series);
        var plan = new NormalizationPlanItem(snapshot, source, new(canonical, "exact-tmdb"), true);
        Assert.True(await service.ApplyTitleAsync(plan, CancellationToken.None));
        Assert.Equal(canonical, series.Name);
        Assert.Equal(canonical, season.SeriesName);
        Assert.Equal(canonical, episode.SeriesName);
        Assert.Equal("Season 2", season.Name);
        Assert.Equal("Keep episode title", episode.Name);
        Assert.Equal(snapshot.Path, series.Path);
        Assert.Equal("Keep synopsis", series.Overview);
        Assert.Equal(identifier, series.ProviderIds["Tmdb"]);
        Assert.Single(savedNfoTitles);
        Assert.Equal(3, writes.Count);
        Assert.False(await service.ApplyTitleAsync(plan, CancellationToken.None));
        Assert.Single(savedNfoTitles);
        Assert.Equal(3, writes.Count);
    }

    [Fact]
    public async Task RepairsOwnedAlternateChildLabels()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Canonical", Path = "/media/Series/Canonical" };
        series.ProviderIds["Tmdb"] = "42";
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeriesName = "Provider", Name = "Episode" };
        var writes = new List<Guid>();
        var library = InterfaceStub.Create<ILibraryManager>((method, arguments) => method.Name switch
        {
            "GetItemById" => series,
            "GetItemList" => ((InternalItemsQuery)arguments![0]!).IncludeOwnedItems
                ? new List<BaseItem> { episode }
                : new List<BaseItem>(),
            "UpdateItemAsync" => Record(writes, ((BaseItem)arguments![0]!).Id),
            _ => throw new NotImplementedException(method.Name)
        });
        var service = new LibraryWriteService(library,
            InterfaceStub.Create<IProviderManager>((method, _) => throw new InvalidOperationException("Unexpected provider call: " + method.Name)));

        Assert.True(await service.ApplyChildLabelsAsync(Snapshot(series), CancellationToken.None));
        Assert.Equal("Canonical", episode.SeriesName);
        Assert.Equal([episode.Id], writes);
    }

    [Fact]
    public async Task ResolvesExactLocalizedTitleWithoutWriting()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Provider", Path = "/media/Series/Provider" };
        series.ProviderIds["Tmdb"] = "42";
        var library = InterfaceStub.Create<ILibraryManager>((_, _) => series);
        var provider = InterfaceStub.Create<IProviderManager>((method, arguments) =>
        {
            Assert.Equal("GetRemoteSearchResults", method.Name);
            var query = Assert.IsType<RemoteSearchQuery<SeriesInfo>>(arguments![0]);
            Assert.Equal("TheMovieDb", query.SearchProviderName);
            Assert.Equal("fr", query.SearchInfo.MetadataLanguage);
            Assert.Equal("BE", query.SearchInfo.MetadataCountryCode);
            Assert.Equal("42", query.SearchInfo.ProviderIds["Tmdb"]);
            return Task.FromResult<IEnumerable<RemoteSearchResult>>([new() { Name = "Titre", ProviderIds = new() { ["Tmdb"] = "42" } }]);
        });
        var service = new LibraryWriteService(library, provider);
        var item = Snapshot(series) with { MetadataLanguage = "fr", MetadataCountryCode = "BE" };
        var resolved = await service.ResolveTitleAsync(new(item, "Provider", new("", "pending"), false), CancellationToken.None);
        Assert.Equal("Titre", resolved.Decision.Title);
        Assert.Equal("Provider", series.Name);
    }

    [Fact]
    public async Task RespectsLocksAndConcurrentTitleChanges()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Provider", Path = "/media/Series/Provider" };
        series.ProviderIds["Tmdb"] = "42";
        var service = new LibraryWriteService(InterfaceStub.Create<ILibraryManager>((_, _) => series),
            InterfaceStub.Create<IProviderManager>((method, _) => throw new InvalidOperationException("Unexpected write: " + method.Name)));
        var plan = new NormalizationPlanItem(Snapshot(series), "Provider", new("Canonical", "exact-tmdb"), true);
        series.LockedFields = [MetadataField.Name];
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyTitleAsync(plan, CancellationToken.None));
        series.LockedFields = [];
        series.Name = "New user edit";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyTitleAsync(plan, CancellationToken.None));
        Assert.Equal("New user edit", series.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectsSilentNfoFailureAndRetries(bool repositoryFails)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xtream-nfo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var nfo = Path.Combine(directory, "tvshow.nfo");
            await File.WriteAllTextAsync(nfo, "<tvshow><title>Provider</title></tvshow>");
            var series = new Series { Id = Guid.NewGuid(), Name = "Provider", Path = directory };
            series.ProviderIds["Tmdb"] = "42";
            var fail = true;
            var writes = 0;
            var library = InterfaceStub.Create<ILibraryManager>((method, _) => method.Name switch
            {
                "GetItemById" => series,
                "GetLibraryOptions" => new LibraryOptions(),
                "GetItemList" => new List<BaseItem>(),
                "UpdateItemAsync" => SaveRepository(),
                _ => throw new NotImplementedException(method.Name)
            });
            Task SaveRepository()
            {
                if (repositoryFails && fail) throw new IOException("Simulated repository failure");
                writes++;
                return Task.CompletedTask;
            }
            var saver = InterfaceStub.Create<IMetadataFileSaver>((method, _) => method.Name == "GetSavePath" ? nfo : throw new NotImplementedException(method.Name));
            var provider = InterfaceStub.Create<IProviderManager>((method, _) => method.Name switch
            {
                "GetMetadataSavers" => new IMetadataSaver[] { saver },
                "SaveMetadataAsync" => fail && !repositoryFails ? Task.CompletedTask : File.WriteAllTextAsync(nfo, "<tvshow><title>Canonical</title></tvshow>"),
                _ => throw new NotImplementedException(method.Name)
            });
            var service = new LibraryWriteService(library, provider);
            var plan = new NormalizationPlanItem(Snapshot(series), "Provider", new("Canonical", "exact-tmdb"), true);
            await Assert.ThrowsAnyAsync<Exception>(() => service.ApplyTitleAsync(plan, CancellationToken.None));
            Assert.Equal("Provider", series.Name);
            Assert.Equal(0, writes);
            fail = false;
            Assert.True(await service.ApplyTitleAsync(plan, CancellationToken.None));
            Assert.Equal("Canonical", series.Name);
            Assert.Equal(1, writes);
            Assert.Contains("<title>Canonical</title>", await File.ReadAllTextAsync(nfo));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static LibraryItemSnapshot Snapshot(Series series) => new(series.Id.ToString(), typeof(Series).FullName!, series.Name,
        series.OriginalTitle, series.Path, series.Overview, series.ProviderIds["Tmdb"], DateTime.UtcNow, true);

    private static Task Record<T>(List<T> target, T value)
    {
        target.Add(value);
        return Task.CompletedTask;
    }
}