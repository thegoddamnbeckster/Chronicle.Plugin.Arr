using System.Text.Json;

namespace Chronicle.Plugin.Arr;

/// <summary>
/// One thing an arr application manages, reduced to what identification needs. Parsed once when the list is fetched so a lookup
/// is a pass over memory, not a fresh request.
/// </summary>
internal sealed record ArrItem(
    ArrApp App,
    string Title,
    string? OriginalTitle,
    int? Year,
    IReadOnlyList<string> AlternateTitles,
    string? Path,
    /// <summary>Base names (with extension) of the files the application holds for this item. Only Radarr reports them.</summary>
    IReadOnlyList<string> FileNames,
    int? TmdbId,
    string? ImdbId,
    /// <summary>Lidarr's MusicBrainz id, or Readarr's Goodreads id.</summary>
    string? ForeignId)
{
    /// <summary>Every title this item is known by.</summary>
    public IEnumerable<string> AllTitles()
    {
        yield return Title;
        if (!string.IsNullOrWhiteSpace(OriginalTitle)) yield return OriginalTitle!;
        foreach (var alt in AlternateTitles) yield return alt;
    }
}

/// <summary>Turns each application's list response into <see cref="ArrItem"/>s. Anything unexpected is skipped, never thrown.</summary>
internal static class ArrParser
{
    public static List<ArrItem> ParseList(ArrApp app, JsonDocument doc)
    {
        var items = new List<ArrItem>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var item = app switch
            {
                ArrApp.Radarr => ParseMovie(e),
                ArrApp.Sonarr => ParseSeries(e),
                ArrApp.Lidarr => ParseArtist(e),
                _ => ParseAuthor(e),
            };
            if (item is not null) items.Add(item);
        }
        return items;
    }

    private static ArrItem? ParseMovie(JsonElement e)
    {
        var title = Str(e, "title");
        if (title is null) return null;
        var files = new List<string>();
        if (e.TryGetProperty("movieFile", out var mf) && mf.ValueKind == JsonValueKind.Object)
        {
            AddBaseName(files, Str(mf, "relativePath"));
            AddBaseName(files, Str(mf, "path"));
        }
        return new ArrItem(ArrApp.Radarr, title, Str(e, "originalTitle"), Int(e, "year"), AltTitles(e), Str(e, "path"),
            files, Int(e, "tmdbId"), Str(e, "imdbId"), null);
    }

    private static ArrItem? ParseSeries(JsonElement e)
    {
        var title = Str(e, "title");
        if (title is null) return null;
        return new ArrItem(ArrApp.Sonarr, title, Str(e, "originalTitle"), Int(e, "year"), AltTitles(e), Str(e, "path"),
            [], Int(e, "tmdbId"), Str(e, "imdbId"), null);
    }

    private static ArrItem? ParseArtist(JsonElement e)
    {
        var name = Str(e, "artistName");
        if (name is null) return null;
        return new ArrItem(ArrApp.Lidarr, name, null, null, [], Str(e, "path"), [], null, null, Str(e, "foreignArtistId"));
    }

    private static ArrItem? ParseAuthor(JsonElement e)
    {
        var name = Str(e, "authorName");
        if (name is null) return null;
        return new ArrItem(ArrApp.Readarr, name, null, null, [], Str(e, "path"), [], null, null, Str(e, "foreignAuthorId"));
    }

    private static IReadOnlyList<string> AltTitles(JsonElement e)
    {
        var list = new List<string>();
        if (e.TryGetProperty("alternateTitles", out var alts) && alts.ValueKind == JsonValueKind.Array)
            foreach (var a in alts.EnumerateArray())
                if (a.ValueKind == JsonValueKind.Object && Str(a, "title") is { } t) list.Add(t);
        return list;
    }

    private static void AddBaseName(List<string> files, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        // Paths come from another machine, so split on both separators instead of trusting this OS's.
        var name = path.Split('/', '\\').Last();
        if (name.Length > 0 && !files.Contains(name, StringComparer.OrdinalIgnoreCase)) files.Add(name);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0 ? n : null;
}
