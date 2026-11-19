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
    /// <summary>Lidarr's MusicBrainz id (artist, or album as a release group), Readarr's Goodreads author id, or a Readarr book's
    /// Goodreads edition id.</summary>
    string? ForeignId)
{
    /// <summary>For an album, its artist's name; for a book, its author's. Null for artists, authors, films and shows.</summary>
    public string? ParentName { get; init; }

    /// <summary>A book's ISBN-13, when its edition has one.</summary>
    public string? Isbn13 { get; init; }

    /// <summary>True for an album or a book (the application's second list).</summary>
    public bool IsChild { get; init; }

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
    public static List<ArrItem> ParseList(ArrApp app, JsonDocument doc, bool secondary = false)
    {
        var items = new List<ArrItem>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var item = (app, secondary) switch
            {
                (ArrApp.Radarr, _) => ParseMovie(e),
                (ArrApp.Sonarr, _) => ParseSeries(e),
                (ArrApp.Lidarr, false) => ParseArtist(e),
                (ArrApp.Lidarr, true) => ParseAlbum(e),
                (_, false) => ParseAuthor(e),
                _ => ParseBook(e),
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

    /// <summary>A Lidarr album: its foreignAlbumId is the MusicBrainz release-group id; the artist is nested.</summary>
    private static ArrItem? ParseAlbum(JsonElement e)
    {
        var title = Str(e, "title");
        var artist = e.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "artistName") : null;
        if (title is null || artist is null) return null;
        return new ArrItem(ArrApp.Lidarr, title, null, YearOf(Str(e, "releaseDate")), [], null, [], null, null, Str(e, "foreignAlbumId"))
        { ParentName = artist, IsChild = true };
    }

    /// <summary>A Readarr book: the author is nested; the monitored edition (else the first) supplies the Goodreads edition id and the
    /// ISBN-13. The book's own foreignBookId is a Goodreads WORK id, which Open Library does not index; the edition id is the one it does.</summary>
    private static ArrItem? ParseBook(JsonElement e)
    {
        var title = Str(e, "title");
        var author = e.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "authorName") : null;
        if (title is null || author is null) return null;

        string? editionId = null, isbn = null;
        if (e.TryGetProperty("editions", out var eds) && eds.ValueKind == JsonValueKind.Array)
        {
            var list = eds.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList();
            var edition = list.FirstOrDefault(x => x.TryGetProperty("monitored", out var m) && m.ValueKind == JsonValueKind.True);
            if (edition.ValueKind != JsonValueKind.Object && list.Count > 0) edition = list[0];
            if (edition.ValueKind == JsonValueKind.Object)
            {
                editionId = Str(edition, "foreignEditionId");
                var digits = new string((Str(edition, "isbn13") ?? "").Where(char.IsDigit).ToArray());
                isbn = digits.Length == 13 ? digits : null;
            }
        }
        return new ArrItem(ArrApp.Readarr, title, null, YearOf(Str(e, "releaseDate")), [], null, [], null, null, editionId)
        { ParentName = author, Isbn13 = isbn, IsChild = true };
    }

    private static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) && y is > 1000 and < 3000 ? y : null;

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
