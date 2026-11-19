using Chronicle.Plugins.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Chronicle.Plugin.Arr;

/// <summary>
/// The plugin's settings, declared once (<see cref="Schema"/>) and read once (<see cref="From"/>). A value is validated when it is
/// read: a bad one means "this application is not used" with a logged reason, never a crash and never something sent on the wire.
/// </summary>
internal static class ArrSettings
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(ArrSettings));

    private static int DefaultPort(ArrApp app) => app switch
    {
        ArrApp.Radarr => 7878, ArrApp.Sonarr => 8989, ArrApp.Lidarr => 8686, _ => 8787,
    };

    public static PluginSettingsSchema Schema()
    {
        var settings = new List<SettingDefinition>
        {
            new()
            {
                Key = "info", Label = "Read this first", Type = SettingType.Notice,
                Description = "Fill in only the applications you run and leave the rest blank. Chronicle reads from them to learn exactly which " +
                              "film, show, artist or author a file is, and never changes anything in them. Work down the list: for each " +
                              "application enter its address, then its API key.",
            },
        };
        foreach (var app in ArrApps.All)
        {
            var name = ArrApps.Label(app);
            var port = DefaultPort(app);
            settings.Add(new SettingDefinition
            {
                Key = ArrApps.UrlKey(app), Label = $"{name} address", Type = SettingType.Url, DefaultValue = "",
                Description = $"Where {name} is reached from the computer running Chronicle, for example http://10.0.0.5:{port} or " +
                              $"http://{name.ToLowerInvariant()}.lan:{port}. Copy it from the address bar you use to open {name}, up to the port, " +
                              $"and leave off anything after it. Do not put a user name or password in it, and do not enter an address that only works " +
                              $"from your phone or another network. Leave blank if you do not use {name}.",
            });
            settings.Add(new SettingDefinition
            {
                Key = ArrApps.ApiKeyKey(app), Label = $"{name} API key", Type = SettingType.Password, DefaultValue = "",
                Description = $"In {name}, open Settings, then General; the API Key is under Security. It is a string of 32 letters and numbers. " +
                              $"Paste only that, not your {name} password. Leave blank if you do not use {name}.",
            });
        }
        return new PluginSettingsSchema { Settings = settings };
    }

    /// <summary>Reads each application's address and key. An application with neither is simply off; one with only half, or a
    /// value that cannot be used, is off with a logged reason (never the key).</summary>
    public static Dictionary<ArrApp, ArrEndpoint> From(IReadOnlyDictionary<string, string> settings)
    {
        var endpoints = new Dictionary<ArrApp, ArrEndpoint>();
        foreach (var app in ArrApps.All)
        {
            settings.TryGetValue(ArrApps.UrlKey(app), out var url);
            settings.TryGetValue(ArrApps.ApiKeyKey(app), out var key);
            url = url?.Trim();
            key = key?.Trim();
            if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(key)) continue;

            var uri = ArrClient.ParseBaseUri(url);
            if (uri is null)
            {
                Log.Warning("{App} is not used: its address must be an http or https address without a user name or password in it", ArrApps.Label(app));
                continue;
            }
            if (string.IsNullOrEmpty(key))
            {
                Log.Warning("{App} is not used: it has an address but no API key", ArrApps.Label(app));
                continue;
            }
            // A key goes into a request header, so a line break or other control character in it (a bad paste) would be an
            // attempt to add headers. Refuse it rather than send it.
            if (key.Any(char.IsControl) || key.Any(char.IsWhiteSpace))
            {
                Log.Warning("{App} is not used: its API key contains spaces or line breaks; paste only the key", ArrApps.Label(app));
                continue;
            }
            endpoints[app] = new ArrEndpoint(app, uri, key);
        }
        return endpoints;
    }
}
