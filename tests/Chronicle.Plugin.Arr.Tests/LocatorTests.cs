using System.Net;
using System.Text;
using System.Text.Json;
using Chronicle.Plugins;

namespace Chronicle.Plugin.Arr.Tests;

/// <summary>Answers each request from a table keyed by path and records every request that was made.</summary>
internal sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
}

internal sealed class AsyncDelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
}

internal sealed class FakeArr : HttpMessageHandler
{
    public readonly Dictionary<string, (HttpStatusCode Status, string Body)> Routes = [];
    public readonly List<HttpRequestMessage> Requests = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var key = request.RequestUri!.Host + request.RequestUri.AbsolutePath;
        if (!Routes.TryGetValue(key, out var r)) r = (HttpStatusCode.NotFound, "{}");
        return Task.FromResult(new HttpResponseMessage(r.Status) { Content = new StringContent(r.Body, Encoding.UTF8, "application/json") });
    }
}

public class LocatorTests
{
    // Shapes taken from Radarr's /api/v3/movie, trimmed to what the plugin reads. Both films are real entries from one library.
    private const string RadarrMovies = """
    [
      { "title": "Heart of the Beast", "originalTitle": "Heart of the Beast", "year": 2026, "tmdbId": 1263337, "imdbId": "tt7526136",
        "path": "/movies/video3/Heart of the Beast (2026)", "alternateTitles": [],
        "movieFile": { "relativePath": "Heart of the Beast (2026).mkv", "path": "/movies/video3/Heart of the Beast (2026)/Heart of the Beast (2026).mkv" } },
      { "title": "The Beast", "originalTitle": "The Beast", "year": 2026, "tmdbId": 1185344, "imdbId": "tt1234567",
        "path": "/movies/video5/The Beast (2026)", "alternateTitles": [ { "sourceType": "tmdb", "title": "Beast: The President's Limousine" } ],
        "movieFile": { "relativePath": "The Beast (2026).mkv", "path": "/movies/video5/The Beast (2026)/The Beast (2026).mkv" } },
      { "title": "The Beast", "year": 2025, "tmdbId": 5550001, "path": "/movies/video2/The Beast (2025)", "alternateTitles": [],
        "movieFile": { "relativePath": "The Beast (2025).mkv" } }
    ]
    """;

    private const string SonarrSeries = """
    [ { "title": "Ted Lasso", "year": 2020, "tmdbId": 97546, "tvdbId": 383203, "imdbId": "tt10986410", "path": "/tv/Ted Lasso", "alternateTitles": [] },
      { "title": "Reacher", "year": 2022, "tmdbId": 108978, "imdbId": "tt9288030", "path": "/tv/Reacher", "alternateTitles": [ { "title": "Jack Reacher: The Series" } ] } ]
    """;

    private const string LidarrArtists = """
    [ { "artistName": "3 Doors Down", "foreignArtistId": "2386cd66-e923-4e8e-bf14-2eebe2e9b973", "path": "/music/3 Doors Down" } ]
    """;

    private const string ReadarrAuthors = """
    [ { "authorName": "Terry Pratchett", "foreignAuthorId": "1654", "path": "/books/Terry Pratchett" } ]
    """;

    // Shapes of Lidarr's /api/v1/album and Readarr's /api/v1/book, trimmed to what the plugin reads.
    private const string LidarrAlbums = """
    [ { "title": "Seventeen Days", "foreignAlbumId": "11111111-aaaa-bbbb-cccc-000000000001", "releaseDate": "2005-02-08T00:00:00Z", "artist": { "artistName": "3 Doors Down" } },
      { "title": "Greatest Hits", "foreignAlbumId": "11111111-aaaa-bbbb-cccc-000000000002", "releaseDate": "2012-01-01T00:00:00Z", "artist": { "artistName": "3 Doors Down" } },
      { "title": "Greatest Hits", "foreignAlbumId": "11111111-aaaa-bbbb-cccc-000000000003", "releaseDate": "2001-01-01T00:00:00Z", "artist": { "artistName": "Someone Else" } },
      { "title": "Orphan", "foreignAlbumId": "x" } ]
    """;

    private const string ReadarrBooks = """
    [ { "title": "The Martian", "releaseDate": "2014-02-11T00:00:00Z", "foreignBookId": "18457474", "author": { "authorName": "Andy Weir" },
        "editions": [ { "foreignEditionId": "111", "isbn13": "9780804139021", "monitored": false },
                      { "foreignEditionId": "18007564", "isbn13": "978-0-553-41802-6", "monitored": true } ] },
      { "title": "Artemis", "releaseDate": "2017-11-14T00:00:00Z", "foreignBookId": "33", "author": { "authorName": "Andy Weir" },
        "editions": [ { "foreignEditionId": "222", "isbn13": "123", "monitored": false } ] },
      { "title": "Artemis", "foreignBookId": "44", "author": { "authorName": "Somebody Else" }, "editions": [] },
      { "title": "No Author Here", "editions": [] } ]
    """;

    private static readonly Dictionary<string, string> AllApps = new()
    {
        ["radarr_url"] = "http://radarr.lan:7878", ["radarr_api_key"] = "radarr-key",
        ["sonarr_url"] = "http://sonarr.lan:8989", ["sonarr_api_key"] = "sonarr-key",
        ["lidarr_url"] = "http://lidarr.lan:8686", ["lidarr_api_key"] = "lidarr-key",
        ["readarr_url"] = "http://readarr.lan:8787", ["readarr_api_key"] = "readarr-key",
    };

    private static (ArrMediaLocator Locator, FakeArr Fake, Func<DateTime> Clock, Action<TimeSpan> Advance) Make(IReadOnlyDictionary<string, string>? settings = null)
    {
        var fake = new FakeArr();
        fake.Routes["radarr.lan/api/v3/movie"] = (HttpStatusCode.OK, RadarrMovies);
        fake.Routes["sonarr.lan/api/v3/series"] = (HttpStatusCode.OK, SonarrSeries);
        fake.Routes["lidarr.lan/api/v1/artist"] = (HttpStatusCode.OK, LidarrArtists);
        fake.Routes["readarr.lan/api/v1/author"] = (HttpStatusCode.OK, ReadarrAuthors);
        fake.Routes["lidarr.lan/api/v1/album"] = (HttpStatusCode.OK, LidarrAlbums);
        fake.Routes["readarr.lan/api/v1/book"] = (HttpStatusCode.OK, ReadarrBooks);
        foreach (var host in new[] { "radarr.lan/api/v3", "sonarr.lan/api/v3", "lidarr.lan/api/v1", "readarr.lan/api/v1" })
            fake.Routes[host + "/system/status"] = (HttpStatusCode.OK, "{\"version\":\"1\"}");

        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var clock = () => now;
        var locator = new ArrMediaLocator(new ArrClient("1.0.0", new HttpClient(fake), (_, _) => Task.CompletedTask), clock);
        locator.Configure(settings ?? AllApps);
        return (locator, fake, clock, span => now += span);
    }

    // ── Radarr ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Radarr_FileName_IdentifiesTheExactFilm_NotTheLongerLookAlike()
    {
        var (locator, _, _, _) = Make();
        var found = await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026, "The Beast (2026).mkv"));

        Assert.NotNull(found);
        Assert.Equal("The Beast", found!.Title);
        Assert.Equal("movie:1185344", found.ExternalIds["tmdb"]);
        Assert.Equal("tt1234567", found.ExternalIds["imdb"]);
        Assert.Contains("holds the file", found.Reason);
    }

    [Fact]
    public async Task Radarr_TheOtherFilm_KeepsItsOwnId()
    {
        var (locator, _, _, _) = Make();
        var found = await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("Heart of the Beast", 2026, "Heart of the Beast (2026).mkv"));
        Assert.Equal("movie:1263337", found!.ExternalIds["tmdb"]);
    }

    [Fact]
    public async Task Radarr_NoFileName_FallsBackToExactTitleAndYear()
    {
        var (locator, _, _, _) = Make();
        var found = await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2025));
        Assert.Equal("movie:5550001", found!.ExternalIds["tmdb"]);   // the 2025 film, not the 2026 one
    }

    [Fact]
    public async Task Radarr_TitleOnlyHalfOfALongerTitle_IsNotAMatch()
    {
        var (locator, _, _, _) = Make();
        Assert.Null(await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("Beast", 2026)));
        Assert.Null(await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("Heart of the", 2026)));
    }

    [Fact]
    public async Task Radarr_AmbiguousTitleWithNoYear_IsAMiss()
    {
        var (locator, _, _, _) = Make();
        // Two films are called The Beast; with no year there is no honest answer.
        Assert.Null(await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast")));
    }

    [Fact]
    public async Task Radarr_PunctuationAndCaseAreIgnored_AndAlternateTitlesCount()
    {
        var (locator, _, _, _) = Make();
        var byAlt = await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("beast the presidents limousine", 2026));
        Assert.Equal("movie:1185344", byAlt!.ExternalIds["tmdb"]);
    }

    [Fact]
    public async Task Radarr_UnknownFile_IsAMiss()
    {
        var (locator, _, _, _) = Make();
        Assert.Null(await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("Nothing Here", 2020, "Nothing Here (2020).mkv")));
    }

    // ── The other three ─────────────────────────────────────────────────────

    [Fact]
    public async Task Sonarr_GivesTheTvFormOfTheTmdbId()
    {
        var (locator, _, _, _) = Make();
        var found = await locator.LocateAsync(LocatorFamily.Tv, new LocatorQuery("Ted Lasso", 2020));
        Assert.Equal("tv:97546", found!.ExternalIds["tmdb"]);
        Assert.Equal("tt10986410", found.ExternalIds["imdb"]);
    }

    [Fact]
    public async Task Sonarr_YearThatContradictsTheSeries_IsAMiss()
    {
        var (locator, _, _, _) = Make();
        Assert.Null(await locator.LocateAsync(LocatorFamily.Tv, new LocatorQuery("Ted Lasso", 1999)));
    }

    [Fact]
    public async Task Lidarr_GivesTheMusicBrainzArtistId()
    {
        var (locator, _, _, _) = Make();
        var found = await locator.LocateAsync(LocatorFamily.Music, new LocatorQuery("3 Doors Down"));
        Assert.Equal("artist:2386cd66-e923-4e8e-bf14-2eebe2e9b973", found!.ExternalIds["musicbrainz"]);
    }

    [Fact]
    public async Task Readarr_GivesTheGoodreadsAuthorId()
    {
        var (locator, _, _, _) = Make();
        var found = await locator.LocateAsync(LocatorFamily.Book, new LocatorQuery("Terry Pratchett"));
        Assert.Equal("1654", found!.ExternalIds["goodreads"]);
    }

    // ── Library items: artists, albums, authors, books (asked by Chronicle's enrichment) ──

    private static LocatorQuery Item(string title, string type, int level, string? parent = null, int? year = null) =>
        new(title, year) { MediaTypeName = type, HierarchyLevel = level, ParentName = parent };

    [Fact]
    public async Task Lidarr_ArtistItem_GivesTheMusicBrainzArtistId()
    {
        var (locator, _, _, _) = Make();
        var found = await locator.LocateItemAsync(Item("3 Doors Down", "music", 0));
        Assert.Equal("artist:2386cd66-e923-4e8e-bf14-2eebe2e9b973", found!.ExternalIds["musicbrainz"]);
    }

    [Fact]
    public async Task Lidarr_AlbumItem_GivesTheReleaseGroupId_WithinItsOwnArtist()
    {
        var (locator, _, _, _) = Make();
        var mine = await locator.LocateItemAsync(Item("Greatest Hits", "music", 1, "3 Doors Down"));
        var theirs = await locator.LocateItemAsync(Item("Greatest Hits", "music", 1, "Someone Else"));

        Assert.Equal("release-group:11111111-aaaa-bbbb-cccc-000000000002", mine!.ExternalIds["musicbrainz"]);
        Assert.Equal("release-group:11111111-aaaa-bbbb-cccc-000000000003", theirs!.ExternalIds["musicbrainz"]);   // same title, other artist
        Assert.Contains("3 Doors Down", mine.Reason);
    }

    [Fact]
    public async Task AnAlbumWithNoArtistNamed_IsAMiss_NeverGuessedFromTheTitleAlone()
    {
        var (locator, _, _, _) = Make();
        Assert.Null(await locator.LocateItemAsync(Item("Greatest Hits", "music", 1)));           // two artists have one
        Assert.Null(await locator.LocateItemAsync(Item("Seventeen Days", "music", 1, "  ")));
    }

    [Fact]
    public async Task AnAlbumInTheWrongArtist_IsAMiss()
    {
        var (locator, _, _, _) = Make();
        Assert.Null(await locator.LocateItemAsync(Item("Seventeen Days", "music", 1, "Someone Else")));
    }

    [Fact]
    public async Task Tracks_AreNotAskedAbout()
    {
        var (locator, fake, _, _) = Make();
        Assert.Null(await locator.LocateItemAsync(Item("Duck and Run", "music", 2, "Seventeen Days")));
        Assert.Empty(fake.Requests);   // not even a request
    }

    [Fact]
    public async Task Readarr_BookItem_GivesTheMonitoredEditionsGoodreadsIdAndIsbn13()
    {
        var (locator, _, _, _) = Make();
        var found = await locator.LocateItemAsync(Item("The Martian", "books", 1, "Andy Weir", 2014));

        Assert.Equal("18007564", found!.ExternalIds["goodreads"]);        // the monitored edition, not the first one listed
        Assert.Equal("9780553418026", found.ExternalIds["isbn13"]);        // hyphens removed
    }

    [Fact]
    public async Task Readarr_AnIsbnThatIsNotThirteenDigits_IsNotOffered()
    {
        var (locator, _, _, _) = Make();
        var found = await locator.LocateItemAsync(Item("Artemis", "books", 1, "Andy Weir"));
        Assert.Equal("222", found!.ExternalIds["goodreads"]);
        Assert.False(found.ExternalIds.ContainsKey("isbn13"));
    }

    [Fact]
    public async Task Readarr_SameTitleByAnotherAuthor_IsNotConfused_AndAudiobooksUseTheSameApplication()
    {
        var (locator, _, _, _) = Make();
        Assert.Null(await locator.LocateItemAsync(Item("Artemis", "books", 1, "Somebody Else")));   // that book has no edition id to give
        Assert.NotNull(await locator.LocateItemAsync(Item("The Martian", "audiobooks", 1, "Andy Weir")));
    }

    [Fact]
    public async Task Readarr_AuthorItem_GivesTheGoodreadsAuthorId()
    {
        var (locator, _, _, _) = Make();
        var found = await locator.LocateItemAsync(Item("Terry Pratchett", "books", 0));
        Assert.Equal("1654", found!.ExternalIds["goodreads"]);
    }

    [Fact]
    public async Task EntriesMissingAnArtistOrAuthor_AreSkipped()
    {
        var (locator, _, _, _) = Make();
        Assert.Null(await locator.LocateItemAsync(Item("Orphan", "music", 1, "3 Doors Down")));
        Assert.Null(await locator.LocateItemAsync(Item("No Author Here", "books", 1, "Anyone")));
    }

    [Fact]
    public void SupportedMediaTypes_FollowTheConfiguredApplications()
    {
        var (all, _, _, _) = Make();
        Assert.Equal(["music", "books", "audiobooks"], all.SupportedMediaTypes.ToArray());

        var (musicOnly, _, _, _) = Make(new Dictionary<string, string> { ["lidarr_url"] = "http://lidarr.lan:8686", ["lidarr_api_key"] = "k" });
        Assert.Equal(["music"], musicOnly.SupportedMediaTypes.ToArray());

        var (movieOnly, _, _, _) = Make(new Dictionary<string, string> { ["radarr_url"] = "http://radarr.lan:7878", ["radarr_api_key"] = "k" });
        Assert.Empty(movieOnly.SupportedMediaTypes);   // Radarr answers Kodi's scrapers, not library items
    }

    [Fact]
    public async Task AnItemOfAnUnservedType_IsAMiss()
    {
        var (locator, fake, _, _) = Make();
        Assert.Null(await locator.LocateItemAsync(Item("Heat", "movies", 0)));
        Assert.Null(await locator.LocateItemAsync(Item("Heat", null!, 0)));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task TheAlbumListAndTheArtistListAreCachedSeparately()
    {
        var (locator, fake, _, _) = Make();
        await locator.LocateItemAsync(Item("3 Doors Down", "music", 0));
        await locator.LocateItemAsync(Item("Seventeen Days", "music", 1, "3 Doors Down"));
        for (var i = 0; i < 10; i++)
        {
            await locator.LocateItemAsync(Item("3 Doors Down", "music", 0));
            await locator.LocateItemAsync(Item("Seventeen Days", "music", 1, "3 Doors Down"));
        }
        Assert.Equal(2, fake.Requests.Count);   // one read of each list
    }

    [Fact]
    public async Task ARejectedLidarrKey_StopsItemLookupsToo()
    {
        var (locator, fake, _, _) = Make();
        fake.Routes["lidarr.lan/api/v1/artist"] = (HttpStatusCode.Unauthorized, "{}");
        Assert.Null(await locator.LocateItemAsync(Item("3 Doors Down", "music", 0)));
        var sent = fake.Requests.Count;
        Assert.Null(await locator.LocateItemAsync(Item("Seventeen Days", "music", 1, "3 Doors Down")));
        Assert.Equal(sent, fake.Requests.Count);
        Assert.DoesNotContain("music", locator.SupportedMediaTypes);
    }

    // ── Configuration ───────────────────────────────────────────────────────

    [Fact]
    public void OnlyConfiguredApplicationsAreHandled()
    {
        var (locator, _, _, _) = Make(new Dictionary<string, string> { ["radarr_url"] = "http://radarr.lan:7878", ["radarr_api_key"] = "k" });
        Assert.True(locator.Handles(LocatorFamily.Movie));
        Assert.False(locator.Handles(LocatorFamily.Tv));
        Assert.False(locator.Handles(LocatorFamily.Music));
        Assert.False(locator.Handles(LocatorFamily.Book));
    }

    [Theory]
    [InlineData("ftp://radarr.lan/")]                 // not http(s)
    [InlineData("http://user:pass@radarr.lan:7878")]  // credentials in an address end up in logs
    [InlineData("not a url")]
    [InlineData("")]
    public void UnusableAddressesAreIgnored(string url)
    {
        var (locator, _, _, _) = Make(new Dictionary<string, string> { ["radarr_url"] = url, ["radarr_api_key"] = "k" });
        Assert.False(locator.Handles(LocatorFamily.Movie));
    }

    [Fact]
    public void AnAddressWithoutAKey_IsIgnored()
    {
        var (locator, _, _, _) = Make(new Dictionary<string, string> { ["radarr_url"] = "http://radarr.lan:7878" });
        Assert.False(locator.Handles(LocatorFamily.Movie));
    }

    [Fact]
    public void BaseAddress_GainsATrailingSlash_AndKeepsAnyPathPrefix()
    {
        Assert.Equal("http://nas.lan/radarr/", ArrClient.ParseBaseUri("http://nas.lan/radarr")!.ToString());
    }

    // ── Read-only, key handling, cache, failure ─────────────────────────────

    [Fact]
    public async Task EveryRequestIsAGet_AndCarriesTheKeyInAHeaderNotTheAddress()
    {
        var (locator, fake, _, _) = Make();
        await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026, "The Beast (2026).mkv"));
        await locator.LocateAsync(LocatorFamily.Tv, new LocatorQuery("Ted Lasso", 2020));
        await locator.HealthCheckAsync();

        Assert.NotEmpty(fake.Requests);
        Assert.All(fake.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Null(r.Content);
            Assert.DoesNotContain("key", r.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(r.Headers.TryGetValues("X-Api-Key", out var values) && values.Single().EndsWith("-key"));
        });
    }

    [Fact]
    public async Task TheListIsReadOncePerFiveMinutes_NotOncePerFile()
    {
        var (locator, fake, _, advance) = Make();
        for (var i = 0; i < 50; i++)
            await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026, "The Beast (2026).mkv"));
        Assert.Single(fake.Requests);

        advance(ArrMediaLocator.CacheLife + TimeSpan.FromSeconds(1));
        await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026, "The Beast (2026).mkv"));
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task AWrongKey_IsAMiss_NotAnException_AndIsLatched_SoNothingMoreIsSent()
    {
        var (locator, fake, _, _) = Make();
        fake.Routes["radarr.lan/api/v3/movie"] = (HttpStatusCode.Unauthorized, "{}");

        Assert.Null(await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026, "The Beast (2026).mkv")));
        var sent = fake.Requests.Count;
        Assert.False(locator.Handles(LocatorFamily.Movie));   // latched
        for (var i = 0; i < 20; i++)
            Assert.Null(await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026, "The Beast (2026).mkv")));
        Assert.Equal(sent, fake.Requests.Count);              // a rejected credential costs one call, not one per file
    }

    [Fact]
    public async Task AWrongKey_HealthThrowsAuthExceptionNamingTheApp_AndSavingSettingsClearsTheLatch()
    {
        var (locator, fake, _, _) = Make();
        fake.Routes["radarr.lan/api/v3/system/status"] = (HttpStatusCode.Unauthorized, "{}");

        var ex = await Assert.ThrowsAsync<PluginAuthException>(() => locator.HealthCheckAsync());
        Assert.Contains("Radarr", ex.Message);
        Assert.Contains("API key", ex.Message);
        Assert.Equal("chronicle.plugin.arr", ex.PluginId);
        Assert.False(locator.Handles(LocatorFamily.Movie));

        // The owner fixes the key and saves: Configure clears the latch and the app is asked again.
        fake.Routes["radarr.lan/api/v3/system/status"] = (HttpStatusCode.OK, "{}");
        locator.Configure(AllApps);
        Assert.True(locator.Handles(LocatorFamily.Movie));
        Assert.True(await locator.HealthCheckAsync());
    }

    [Fact]
    public async Task WhenTheApplicationGoesDown_TheLastListIsStillUsed()
    {
        var (locator, fake, _, advance) = Make();
        await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026));

        fake.Routes["radarr.lan/api/v3/movie"] = (HttpStatusCode.InternalServerError, "{}");
        advance(ArrMediaLocator.CacheLife + TimeSpan.FromMinutes(1));

        var found = await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026));
        Assert.Equal("movie:1185344", found!.ExternalIds["tmdb"]);
    }

    [Fact]
    public async Task GarbageInsteadOfJson_IsAMiss()
    {
        var (locator, fake, _, _) = Make();
        fake.Routes["radarr.lan/api/v3/movie"] = (HttpStatusCode.OK, "<html>not json</html>");
        Assert.Null(await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026)));
    }

    [Fact]
    public async Task EntriesWithMissingOrOddFields_AreSkipped_NotFatal()
    {
        var (locator, fake, _, _) = Make();
        fake.Routes["radarr.lan/api/v3/movie"] = (HttpStatusCode.OK,
            "[ 5, null, {}, { \"title\": 7 }, { \"title\": \"The Beast\", \"year\": 2026, \"tmdbId\": 1185344, \"movieFile\": 3 } ]");
        var found = await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026));
        Assert.Equal("movie:1185344", found!.ExternalIds["tmdb"]);
    }

    [Fact]
    public async Task AnItemWithNoUsableId_IsAMiss()
    {
        var (locator, fake, _, _) = Make();
        fake.Routes["radarr.lan/api/v3/movie"] = (HttpStatusCode.OK, "[ { \"title\": \"The Beast\", \"year\": 2026 } ]");
        Assert.Null(await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026)));
    }

    [Fact]
    public async Task HealthIsFalseWhenAnAppDoesNotAnswer_ButNotAnAuthProblem()
    {
        var (locator, fake, _, _) = Make();
        fake.Routes["sonarr.lan/api/v3/system/status"] = (HttpStatusCode.InternalServerError, "{}");
        Assert.False(await locator.HealthCheckAsync());
    }

    [Fact]
    public async Task HealthIsFalseWhenNothingIsConfigured_AndTrueWhenEveryConfiguredAppAnswers()
    {
        var (none, _, _, _) = Make(new Dictionary<string, string>());
        Assert.False(await none.HealthCheckAsync());

        var (all, _, _, _) = Make();
        Assert.True(await all.HealthCheckAsync());
    }

    // ── Schema and manifest ─────────────────────────────────────────────────

    [Fact]
    public void Schema_HasAnAddressAndAKeyForEachApplication_AndKeysAreMasked()
    {
        var schema = new ArrMediaLocator().GetSettingsSchema();
        foreach (var app in new[] { "radarr", "sonarr", "lidarr", "readarr" })
        {
            Assert.Contains(schema.Settings, s => s.Key == app + "_url" && s.Type == Chronicle.Plugins.Models.SettingType.Url);
            Assert.Contains(schema.Settings, s => s.Key == app + "_api_key" && s.Type == Chronicle.Plugins.Models.SettingType.Password);
        }
        Assert.DoesNotContain(schema.Settings, s => s.Required);   // every application is optional
    }

    // X10: every declared setting is read, has help text that says what to do, every secret is a password, every optional one has a default.
    [Fact]
    public void EverySettingIsReadByFrom_HasHelpText_AndEverySecretIsAPassword()
    {
        var schema = ArrSettings.Schema();
        var real = schema.Settings.Where(s => s.Type != Chronicle.Plugins.Models.SettingType.Notice).ToList();
        foreach (var def in real)
        {
            Assert.False(string.IsNullOrWhiteSpace(def.Description), def.Key);
            Assert.Contains("Leave blank", def.Description);
            Assert.NotNull(def.DefaultValue);
            if (def.Key.EndsWith("_api_key")) Assert.Equal(Chronicle.Plugins.Models.SettingType.Password, def.Type);
        }
        Assert.Contains(schema.Settings, s => s.Type == Chronicle.Plugins.Models.SettingType.Notice);

        // "Read by From": a key From ignored would be a dead setting. Half a pair is "not used", so give each key only its own
        // half and see nothing appear, then give both and see the application appear.
        foreach (var def in real)
        {
            var app = def.Key.Split('_')[0];
            var half = new Dictionary<string, string> { [def.Key] = def.Key.EndsWith("_url") ? "http://x.lan:1" : "abcdef0123456789abcdef0123456789" };
            var both = new Dictionary<string, string> { [app + "_url"] = "http://x.lan:1", [app + "_api_key"] = "abcdef0123456789abcdef0123456789" };
            Assert.Empty(ArrSettings.From(half));
            Assert.Single(ArrSettings.From(both));
        }
        Assert.Equal(8, real.Count);   // an address and a key for each of the four
    }

    [Theory]
    [InlineData("abc def")]
    [InlineData("abc\r\nX-Evil: 1")]
    [InlineData("abc\tdef")]
    public void AKeyWithSpacesOrLineBreaks_IsRefused_NeverSent(string key)
    {
        var endpoints = ArrSettings.From(new Dictionary<string, string> { ["radarr_url"] = "http://radarr.lan:7878", ["radarr_api_key"] = key });
        Assert.Empty(endpoints);
    }

    [Fact]
    public void SurroundingWhitespaceOnAddressAndKey_IsTrimmed()
    {
        var endpoints = ArrSettings.From(new Dictionary<string, string> { ["radarr_url"] = "  http://radarr.lan:7878  ", ["radarr_api_key"] = "  abc123  " });
        Assert.Equal("abc123", endpoints[ArrApp.Radarr].ApiKey);
    }

    [Fact]
    public void Configure_IsIdempotent()
    {
        var (locator, _, _, _) = Make();
        locator.Configure(AllApps);
        locator.Configure(AllApps);
        Assert.True(locator.Handles(LocatorFamily.Movie));
        locator.Configure(new Dictionary<string, string>());
        Assert.False(locator.Handles(LocatorFamily.Movie));
    }

    // ── K8, K9, K10, K7 ─────────────────────────────────────────────────────

    [Fact]
    public async Task EveryRequestSendsADescriptiveUserAgent_NamingChronicleAndTheVersion_AndNothingPersonal()
    {
        var (locator, fake, _, _) = Make();
        await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026));
        Assert.NotEmpty(fake.Requests);
        Assert.All(fake.Requests, r =>
        {
            var ua = r.Headers.UserAgent.ToString();
            Assert.StartsWith("Chronicle-Arr/", ua);
            Assert.DoesNotContain("@", ua);
        });
    }

    [Fact]
    public void TheClientTimeoutIsInsideTheHostsCallLimit()
    {
        Assert.True(ArrClient.RequestTimeout <= TimeSpan.FromSeconds(25));
        Assert.True(ArrClient.MaxRetryWait * ArrClient.MaxRetries < TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task ARateLimit_IsRetriedABoundedNumberOfTimes_ThenThrownAsTooManyRequests()
    {
        var waits = new List<TimeSpan>();
        var fake = new FakeArr();
        fake.Routes["radarr.lan/api/v3/movie"] = (HttpStatusCode.TooManyRequests, "{}");
        var client = new ArrClient("1.0.0", new HttpClient(fake), (w, _) => { waits.Add(w); return Task.CompletedTask; });
        var locator = new ArrMediaLocator(client, () => DateTime.UtcNow);
        locator.Configure(new Dictionary<string, string> { ["radarr_url"] = "http://radarr.lan:7878", ["radarr_api_key"] = "k" });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026)));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(ArrClient.MaxRetries, waits.Count);
        Assert.Equal(ArrClient.MaxRetries + 1, fake.Requests.Count);   // the first try plus the retries, no more
    }

    [Fact]
    public async Task ARateLimitThatLifts_IsRetriedAndSucceeds_HonouringRetryAfter_CappedAtTheMaximum()
    {
        var waits = new List<TimeSpan>();
        var calls = 0;
        var handler = new DelegateHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(600));   // far too long to honour in full
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RadarrMovies) };
        });
        var client = new ArrClient("1.0.0", new HttpClient(handler), (w, _) => { waits.Add(w); return Task.CompletedTask; });
        var locator = new ArrMediaLocator(client, () => DateTime.UtcNow);
        locator.Configure(new Dictionary<string, string> { ["radarr_url"] = "http://radarr.lan:7878", ["radarr_api_key"] = "k" });

        var found = await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026, "The Beast (2026).mkv"));

        Assert.Equal("movie:1185344", found!.ExternalIds["tmdb"]);
        Assert.Equal([ArrClient.MaxRetryWait], waits);
    }

    // Chronicle gives a lookup 4 seconds. A big library can take longer to read the first time: the read must finish in the
    // background and be cached, or the plugin would be cancelled on every lookup and never work.
    [Fact]
    public async Task ASlowFirstRead_ThatTheCallerGivesUpOn_StillFillsTheCache_SoTheNextLookupAnswers()
    {
        var release = new TaskCompletionSource();
        var reads = 0;
        var handler = new AsyncDelegateHandler(async _ =>
        {
            Interlocked.Increment(ref reads);
            await release.Task;   // the application is slow
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RadarrMovies) };
        });
        var locator = new ArrMediaLocator(new ArrClient("1.0.0", new HttpClient(handler), (_, _) => Task.CompletedTask), () => DateTime.UtcNow);
        locator.Configure(new Dictionary<string, string> { ["radarr_url"] = "http://radarr.lan:7878", ["radarr_api_key"] = "k" });

        using (var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026, "The Beast (2026).mkv"), impatient.Token));

        // A second caller while the read is still running shares it rather than starting another.
        var second = locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026, "The Beast (2026).mkv"));
        release.SetResult();
        var found = await second;

        Assert.Equal("movie:1185344", found!.ExternalIds["tmdb"]);
        Assert.Equal(1, reads);   // one read served both, and the first caller's cancellation did not waste it
        Assert.NotNull(await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026)));
        Assert.Equal(1, reads);   // now cached
    }

    [Fact]
    public async Task ARateLimitWithAListAlreadyRead_UsesTheOldList_InsteadOfFailing()
    {
        var (locator, fake, _, advance) = Make();
        await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026));
        fake.Routes["radarr.lan/api/v3/movie"] = (HttpStatusCode.TooManyRequests, "{}");
        advance(ArrMediaLocator.CacheLife + TimeSpan.FromMinutes(1));

        var found = await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026));
        Assert.Equal("movie:1185344", found!.ExternalIds["tmdb"]);
    }

    // K7: no part of a key reaches an exception or a message. The key is only ever in a header, so make each failure happen on
    // purpose and look for it in everything that is thrown.
    private const string SecretKey = "s3cr3tK3y-0123456789abcdef";

    [Fact]
    public async Task TheKeyNeverAppearsInAnExceptionOrAMessage_WhateverGoesWrong()
    {
        var failures = new List<string>();
        async Task Try(HttpStatusCode status, Func<ArrMediaLocator, Task> act)
        {
            var fake = new FakeArr();
            fake.Routes["radarr.lan/api/v3/movie"] = (status, "{}");
            fake.Routes["radarr.lan/api/v3/system/status"] = (status, "{}");
            var locator = new ArrMediaLocator(new ArrClient("1.0.0", new HttpClient(fake), (_, _) => Task.CompletedTask), () => DateTime.UtcNow);
            locator.Configure(new Dictionary<string, string> { ["radarr_url"] = "http://radarr.lan:7878", ["radarr_api_key"] = SecretKey });
            try { await act(locator); }
            catch (Exception ex) { failures.Add(ex.ToString()); }
        }

        await Try(HttpStatusCode.Unauthorized, l => l.HealthCheckAsync());
        await Try(HttpStatusCode.Forbidden, l => l.HealthCheckAsync());
        await Try(HttpStatusCode.TooManyRequests, l => l.LocateAsync(LocatorFamily.Movie, new LocatorQuery("x", 2000)));

        Assert.Equal(3, failures.Count);   // each really did throw, so this is looking at something
        Assert.All(failures, f =>
        {
            Assert.DoesNotContain(SecretKey, f);
            Assert.DoesNotContain(SecretKey[..6], f);   // not even its first characters
        });
    }

    [Fact]
    public async Task AConnectionFailure_IsAMiss_AndHealthIsFalse()
    {
        var handler = new DelegateHandler(_ => throw new HttpRequestException("No connection could be made because the target machine actively refused it."));
        var client = new ArrClient("1.0.0", new HttpClient(handler), (_, _) => Task.CompletedTask);
        var locator = new ArrMediaLocator(client, () => DateTime.UtcNow);
        locator.Configure(new Dictionary<string, string> { ["radarr_url"] = "http://radarr.lan:7878", ["radarr_api_key"] = SecretKey });

        Assert.Null(await locator.LocateAsync(LocatorFamily.Movie, new LocatorQuery("The Beast", 2026)));
        Assert.False(await locator.HealthCheckAsync());
    }

    // ── Contract ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LocatorFamily.Movie)]
    [InlineData(LocatorFamily.Tv)]
    [InlineData(LocatorFamily.Music)]
    [InlineData(LocatorFamily.Book)]
    public async Task LocateNeverThrows_ForAnUnconfiguredFamily_OrABlankTitle(LocatorFamily family)
    {
        var none = new ArrMediaLocator();
        none.Configure(new Dictionary<string, string>());
        Assert.False(none.Handles(family));
        Assert.Null(await none.LocateAsync(family, new LocatorQuery("Anything", 2000)));

        var (all, _, _, _) = Make();
        Assert.Null(await all.LocateAsync(family, new LocatorQuery("  ")));
    }

    [Fact]
    public void ManifestAgreesWithThePlugin()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")));
        var locator = new ArrMediaLocator();
        Assert.Equal(locator.PluginId, doc.RootElement.GetProperty("plugin_id").GetString());
        Assert.Equal(locator.Version, doc.RootElement.GetProperty("version").GetString());   // K1: the version comes from the project file, so the manifest must agree
        Assert.Matches("^chronicle\\.plugin\\.[a-z]+$", doc.RootElement.GetProperty("plugin_id").GetString());
        Assert.StartsWith("#", doc.RootElement.GetProperty("brandColorLight").GetString());
        Assert.StartsWith("#", doc.RootElement.GetProperty("brandColorDark").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("iconUrl").GetString()));
        Assert.Equal(0, doc.RootElement.GetProperty("settings").GetArrayLength());   // the schema is the one list of settings
        Assert.Equal(typeof(ArrMediaLocator).FullName, doc.RootElement.GetProperty("entry_type").GetString());
    }

    [Fact]
    public void ParseList_OfNonArray_IsEmpty()
    {
        using var doc = JsonDocument.Parse("{}");
        Assert.Empty(ArrParser.ParseList(ArrApp.Radarr, doc));
    }
}
