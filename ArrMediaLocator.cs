using System.Net;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Chronicle.Plugin.Arr;

/// <summary>
/// Chronicle media locator for Radarr, Sonarr, Lidarr and Readarr. One plugin serves all four: each application is optional and is
/// switched on by giving its address and API key.
///
/// Reference data only. Chronicle asks "which film is this file?" and the application that downloaded and named it answers with
/// ids, so a file called "The Beast (2026).mkv" is identified as TMDB 1185344 instead of guessed from its name. Nothing is ever
/// written to the applications (see <see cref="ArrClient"/>), and a lookup that finds nothing, or an application that is down,
/// just leaves Chronicle to match by name as it always has.
///
///   Radarr  movies   by the exact file name Radarr holds, else by exact title and year; gives TMDB and IMDb ids
///   Sonarr  TV       by exact title and year; gives TMDB and IMDb ids
///   Lidarr  music    an artist by exact name (MusicBrainz artist id); an album by exact title within its artist (MusicBrainz
///                    release-group id). Tracks are not asked about: Chronicle derives them from their album
///   Readarr books    an author by exact name (Goodreads author id); a book by exact title within its author (Goodreads edition
///                    id and ISBN-13, which Open Library resolves)
///
/// Each application's whole list is read once and kept for five minutes, so a library scan of thousands of files costs a handful
/// of requests, not thousands. A key an application rejects is latched: nothing more is sent to it until the settings change.
/// </summary>
public sealed class ArrMediaLocator : IMediaLocator, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ArrMediaLocator>();

    /// <summary>How long a fetched list is trusted before it is read again.</summary>
    internal static readonly TimeSpan CacheLife = TimeSpan.FromMinutes(5);

    public string PluginId => ArrClient.PluginId;
    public string Name => "Arr Apps";

    /// <summary>Read from the assembly, which takes it from the project file: the manifest is checked against it by a test.</summary>
    public string Version => VersionOf(typeof(ArrMediaLocator).Assembly);

    internal static string VersionOf(System.Reflection.Assembly assembly) =>
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)
            ?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    private readonly ArrClient _client;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private Dictionary<ArrApp, ArrEndpoint> _endpoints = [];
    // Keyed by application and list: false is the first list (movies, series, artists, authors), true the second (albums, books).
    private readonly Dictionary<(ArrApp App, bool Second), (DateTime At, List<ArrItem> Items)> _cache = [];
    private readonly HashSet<ArrApp> _rejected = [];
    private readonly Dictionary<(ArrApp App, bool Second), TaskCompletionSource<FetchResult>> _inflight = [];

    /// <summary>Used by Chronicle, which creates plugins with no arguments.</summary>
    public ArrMediaLocator() : this(new ArrClient(VersionOf(typeof(ArrMediaLocator).Assembly)), () => DateTime.UtcNow) { }

    internal ArrMediaLocator(ArrClient client, Func<DateTime> now)
    {
        _client = client;
        _now = now;
    }

    public PluginSettingsSchema GetSettingsSchema() => ArrSettings.Schema();

    /// <summary>Idempotent: replaces the applications, empties the cache and clears every rejected-key latch.</summary>
    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        var endpoints = ArrSettings.From(settings);
        lock (_lock)
        {
            _endpoints = endpoints;
            _cache.Clear();
            _rejected.Clear();
        }
        Log.Information("{PluginId} configured for: {Apps}", PluginId, endpoints.Count == 0 ? "(none)" : string.Join(", ", endpoints.Keys));
    }

    public bool Handles(LocatorFamily family)
    {
        lock (_lock)
            return _endpoints.Keys.Any(a => ArrApps.Family(a) == family && !_rejected.Contains(a));
    }

    public async Task<LocatedMedia?> LocateAsync(LocatorFamily family, LocatorQuery query, CancellationToken ct = default)
    {
        ArrEndpoint? endpoint;
        lock (_lock)
            endpoint = _endpoints.Values.FirstOrDefault(e => ArrApps.Family(e.App) == family && !_rejected.Contains(e.App));
        if (endpoint is null || string.IsNullOrWhiteSpace(query.Title)) return null;

        var items = await GetItemsAsync(endpoint, false, ct);
        if (items.Count == 0) return null;

        string reason;
        ArrItem? match = null;
        if (endpoint.App == ArrApp.Radarr)
            match = ArrMatching.FindByFileName(items, query.FileName, query.Year);
        if (match is not null)
            reason = $"{ArrApps.Label(endpoint.App)} holds the file {query.FileName} as {Describe(match)}";
        else
        {
            match = ArrMatching.FindByTitle(items, query.Title, query.Year);
            reason = $"{ArrApps.Label(endpoint.App)} has the title as {(match is null ? "" : Describe(match))}";
        }
        if (match is null) return null;

        var ids = IdsFor(match);
        if (ids.Count == 0) return null;   // nothing Chronicle could use to be more certain than it already is
        return new LocatedMedia(match.Title, match.Year, ids, match.Path, reason);
    }

    /// <summary>Ids in the forms Chronicle stores them, best identifier first.</summary>
    internal static Dictionary<string, string> IdsFor(ArrItem item)
    {
        var ids = new Dictionary<string, string>();
        switch (item.App)
        {
            case ArrApp.Radarr:
                if (item.TmdbId is { } m) ids["tmdb"] = $"movie:{m}";
                if (!string.IsNullOrWhiteSpace(item.ImdbId)) ids["imdb"] = item.ImdbId!;
                break;
            case ArrApp.Sonarr:
                if (item.TmdbId is { } t) ids["tmdb"] = $"tv:{t}";
                if (!string.IsNullOrWhiteSpace(item.ImdbId)) ids["imdb"] = item.ImdbId!;
                break;
            case ArrApp.Lidarr:
                // An album's foreignAlbumId is the MusicBrainz release-group id; an artist's is the artist id.
                if (!string.IsNullOrWhiteSpace(item.ForeignId)) ids["musicbrainz"] = $"{(item.IsChild ? "release-group" : "artist")}:{item.ForeignId}";
                break;
            default:
                // Readarr: an author's id is Goodreads' author id; a book carries its edition's Goodreads id and ISBN-13, which Open
                // Library resolves. ("isbn13" is the source name Open Library's own metadata uses.)
                if (!string.IsNullOrWhiteSpace(item.ForeignId)) ids["goodreads"] = item.ForeignId!;
                if (!string.IsNullOrWhiteSpace(item.Isbn13)) ids["isbn13"] = item.Isbn13!;
                break;
        }
        return ids;
    }

    /// <summary>The media types of the applications configured here, so Chronicle asks about those library items only.</summary>
    public IReadOnlyList<string> SupportedMediaTypes
    {
        get
        {
            lock (_lock)
                return [.. _endpoints.Keys.Where(a => !_rejected.Contains(a)).SelectMany(ArrApps.MediaTypes).Distinct()];
        }
    }

    /// <summary>
    /// Identifies one library item. Lidarr: an artist (level 0) or an album within its artist (level 1). Readarr: an author (level 0)
    /// or a book within its author (level 1). Anything deeper (a track, a chapter) is not asked about: Chronicle derives it from its
    /// parent. A miss, or an application that is not configured, is null.
    /// </summary>
    public async Task<LocatedMedia?> LocateItemAsync(LocatorQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Title) || query.HierarchyLevel > 1) return null;

        var type = MediaTypeNameOf(query.MediaTypeName);
        ArrEndpoint? endpoint;
        lock (_lock)
            endpoint = _endpoints.Values.FirstOrDefault(e => !_rejected.Contains(e.App) && ArrApps.MediaTypes(e.App).Contains(type));
        if (endpoint is null) return null;

        var child = query.HierarchyLevel == 1;
        var items = await GetItemsAsync(endpoint, child, ct);
        if (items.Count == 0) return null;

        var app = ArrApps.Label(endpoint.App);
        ArrItem? match;
        if (child)
        {
            match = ArrMatching.FindChild(items, query.Title, query.ParentName, query.Year);
            if (match is null) return null;
        }
        else
        {
            match = ArrMatching.FindByTitle(items, query.Title, null);
            if (match is null) return null;
        }

        var ids = IdsFor(match);
        if (ids.Count == 0) return null;
        var what = child ? $"{match.ParentName}: {Describe(match)}" : Describe(match);
        return new LocatedMedia(match.Title, match.Year, ids, match.Path, $"{app} has {(child ? "the " + (endpoint.App == ArrApp.Lidarr ? "album" : "book") : "the " + (endpoint.App == ArrApp.Lidarr ? "artist" : "author"))} {what}");
    }

    private static string MediaTypeNameOf(string? name) => (name ?? "").Trim().ToLowerInvariant();

    private static string Describe(ArrItem item) => item.Year.HasValue ? $"{item.Title} ({item.Year})" : item.Title;

    /// <summary>
    /// The application's whole list, from the cache when it is fresh. The read is NOT tied to the caller: Chronicle gives a lookup
    /// four seconds, and a large library can take longer to read the first time. The read runs on its own (bounded by the client's
    /// timeout) and fills the cache even when the caller has given up, so the next lookup finds it. Callers that arrive while it
    /// runs share it. A rejected key latches the application and yields nothing; a failed read yields what was kept before; a
    /// rate limit with nothing kept is thrown for the host.
    /// </summary>
    private async Task<List<ArrItem>> GetItemsAsync(ArrEndpoint endpoint, bool second, CancellationToken ct)
    {
        var key = (endpoint.App, second);
        if (TryGetFresh(key, out var fresh)) return fresh;

        // Registered BEFORE it starts, so a read that finishes at once can never be cleaned up ahead of being registered.
        TaskCompletionSource<FetchResult> shared;
        var owner = false;
        lock (_lock)
        {
            if (!_inflight.TryGetValue(key, out shared!))
            {
                shared = new TaskCompletionSource<FetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inflight[key] = shared;
                owner = true;
            }
        }
        if (owner) _ = RunFetchAsync(endpoint, second, shared);

        // The caller may stop waiting (its own deadline); the read carries on and is cached.
        var result = await shared.Task.WaitAsync(ct);
        if (result.RateLimited) throw new HttpRequestException($"{ArrApps.Label(endpoint.App)} is rate limiting requests (HTTP 429).", null, HttpStatusCode.TooManyRequests);
        return result.Items;
    }

    private sealed record FetchResult(List<ArrItem> Items, bool RateLimited);

    private async Task RunFetchAsync(ArrEndpoint endpoint, bool second, TaskCompletionSource<FetchResult> shared)
    {
        FetchResult result;
        try { result = await FetchAsync(endpoint, second); }
        catch (Exception ex)
        {
            // FetchAsync reports every failure in its result; this is the last line so the shared read always completes.
            Log.Error(ex, "{PluginId}: reading {App} failed unexpectedly", PluginId, ArrApps.Label(endpoint.App));
            result = new FetchResult([], false);
        }
        lock (_lock)
            _inflight.Remove((endpoint.App, second));
        shared.SetResult(result);
    }

    /// <summary>Reads one application's list and stores it. Never throws: every outcome is in the result, so nothing is left
    /// unobserved when the caller that started it has already gone.</summary>
    private async Task<FetchResult> FetchAsync(ArrEndpoint endpoint, bool second)
    {
        var key = (endpoint.App, second);
        {
            if (TryGetFresh(key, out var fresh)) return new FetchResult(fresh, false);
            try
            {
                var path = second ? ArrApps.SecondaryListPath(endpoint.App) : ArrApps.ListPath(endpoint.App);
                if (path is null) return new FetchResult([], false);
                using var doc = await _client.GetJsonAsync(endpoint, path, CancellationToken.None);
                if (doc is not null)
                {
                    var items = ArrParser.ParseList(endpoint.App, doc, second);
                    lock (_lock)
                        _cache[key] = (_now(), items);
                    Log.Information("{PluginId}: {App} has {Count} {What}", PluginId, ArrApps.Label(endpoint.App), items.Count, second ? "album(s) or book(s)" : "item(s)");
                    return new FetchResult(items, false);
                }
            }
            catch (PluginAuthException ex)
            {
                Latch(endpoint.App, ex.Message);
                return new FetchResult([], false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (TryGetStale(key, out var stale))
                {
                    Log.Warning("{PluginId}: {App} is rate limiting; using the list read earlier", PluginId, ArrApps.Label(endpoint.App));
                    return new FetchResult(stale, false);
                }
                return new FetchResult([], true);
            }
            catch (Exception ex)
            {
                // Anything unforeseen is a degraded answer, not a crash: Chronicle carries on matching by name.
                Log.Warning(ex, "{PluginId}: {App} could not be read", PluginId, ArrApps.Label(endpoint.App));
            }
            return new FetchResult(TryGetStale(key, out var old) ? old : [], false);
        }
    }

    private void Latch(ArrApp app, string message)
    {
        lock (_lock)
            _rejected.Add(app);
        Log.Warning("{PluginId}: {Message} Nothing more is sent to {App} until the settings are saved again.", PluginId, message, ArrApps.Label(app));
    }

    private bool TryGetFresh((ArrApp App, bool Second) key, out List<ArrItem> items)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var entry) && _now() - entry.At < CacheLife)
            {
                items = entry.Items;
                return true;
            }
        }
        items = [];
        return false;
    }

    private bool TryGetStale((ArrApp App, bool Second) key, out List<ArrItem> items)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                items = entry.Items;
                return true;
            }
        }
        items = [];
        return false;
    }

    /// <summary>One read-only call per configured application. False when none is configured or one does not answer; throws
    /// <see cref="PluginAuthException"/> naming the application whose key was rejected, so the owner is told what to fix.</summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        List<ArrEndpoint> endpoints;
        lock (_lock)
            endpoints = [.. _endpoints.Values];
        if (endpoints.Count == 0)
        {
            Log.Warning("{PluginId} health check: no application is configured", PluginId);
            return false;
        }

        var allOk = true;
        foreach (var endpoint in endpoints)
        {
            var app = ArrApps.Label(endpoint.App);
            bool rejected;
            lock (_lock)
                rejected = _rejected.Contains(endpoint.App);
            if (rejected)
                throw new PluginAuthException(PluginId,
                    $"{app} rejected the API key. Copy the key again from {app}'s Settings, General page into this plugin's {app} API key box, then save.");

            try
            {
                using var doc = await _client.GetJsonAsync(endpoint, ArrApps.StatusPath(endpoint.App), ct);
                if (doc is null)
                {
                    Log.Warning("{PluginId} health check: {App} did not answer at this address", PluginId, app);
                    allOk = false;
                }
            }
            catch (PluginAuthException ex)
            {
                Latch(endpoint.App, ex.Message);
                throw;
            }
        }
        return allOk;
    }

    public void Dispose() => _client.Dispose();
}
