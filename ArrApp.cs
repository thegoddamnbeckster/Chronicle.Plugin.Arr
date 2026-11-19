using Chronicle.Plugins;

namespace Chronicle.Plugin.Arr;

/// <summary>The four applications this plugin can read from.</summary>
internal enum ArrApp { Radarr, Sonarr, Lidarr, Readarr }

/// <summary>Where one application lives and the key that opens it.</summary>
internal sealed record ArrEndpoint(ArrApp App, Uri BaseUri, string ApiKey);

/// <summary>What is fixed about each application: its settings keys, API version, the list to read and the media family it answers for.</summary>
internal static class ArrApps
{
    public static readonly ArrApp[] All = [ArrApp.Radarr, ArrApp.Sonarr, ArrApp.Lidarr, ArrApp.Readarr];

    public static string Label(ArrApp app) => app.ToString();

    public static string UrlKey(ArrApp app) => app.ToString().ToLowerInvariant() + "_url";
    public static string ApiKeyKey(ArrApp app) => app.ToString().ToLowerInvariant() + "_api_key";

    /// <summary>Radarr and Sonarr are on API v3; Lidarr and Readarr are on v1.</summary>
    public static string ApiPrefix(ArrApp app) => app is ArrApp.Radarr or ArrApp.Sonarr ? "/api/v3" : "/api/v1";

    /// <summary>The call that returns everything the application manages: movies, series, artists or authors.</summary>
    public static string ListPath(ArrApp app) => ApiPrefix(app) + app switch
    {
        ArrApp.Radarr => "/movie",
        ArrApp.Sonarr => "/series",
        ArrApp.Lidarr => "/artist",
        _ => "/author",
    };

    /// <summary>The second list, for the applications whose library has a level below the first: Lidarr's albums and Readarr's books.
    /// Null for Radarr and Sonarr.</summary>
    public static string? SecondaryListPath(ArrApp app) => app switch
    {
        ArrApp.Lidarr => ApiPrefix(app) + "/album",
        ArrApp.Readarr => ApiPrefix(app) + "/book",
        _ => null,
    };

    /// <summary>The media type names a plugin like this declares for each application (as plugins do: "music", "books"). Chronicle
    /// maps them to its own types; these are the plugin's words, not Chronicle's.</summary>
    public static string[] MediaTypes(ArrApp app) => app switch
    {
        ArrApp.Lidarr => ["music"],
        ArrApp.Readarr => ["books", "audiobooks"],
        _ => [],
    };

    /// <summary>A cheap call that needs a valid key, used to prove the address and key work.</summary>
    public static string StatusPath(ArrApp app) => ApiPrefix(app) + "/system/status";

    public static LocatorFamily Family(ArrApp app) => app switch
    {
        ArrApp.Radarr => LocatorFamily.Movie,
        ArrApp.Sonarr => LocatorFamily.Tv,
        ArrApp.Lidarr => LocatorFamily.Music,
        _ => LocatorFamily.Book,
    };
}
