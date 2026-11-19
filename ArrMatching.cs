using System.Text.RegularExpressions;

namespace Chronicle.Plugin.Arr;

/// <summary>
/// How a file name or title is matched to something an arr application manages. The rule is the opposite of fuzzy: a title must
/// be EQUAL once case and punctuation are ignored, and anything ambiguous is a miss. A wrong answer here would override
/// Chronicle's own matching, while a miss costs nothing because Chronicle simply carries on as it did before this plugin.
/// </summary>
internal static partial class ArrMatching
{
    [GeneratedRegex(@"[\(\[]\d{4}[\)\]]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYear();

    /// <summary>Lower-case letters and digits only, with a trailing "(YYYY)" removed: "Alien: Romulus (2024)" and "alien romulus" are the same.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var stripped = TrailingYear().Replace(text, "");
        var chars = stripped.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    /// <summary>The item whose file has exactly this name, or null when none or several do (several: the year decides, else a miss).</summary>
    public static ArrItem? FindByFileName(IReadOnlyList<ArrItem> items, string? fileName, int? year)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = fileName.Split('/', '\\').Last();
        var hits = items.Where(i => i.FileNames.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        return Single(hits, year);
    }

    /// <summary>The item with exactly this title (or original or alternate title), and this year when both sides have one.</summary>
    public static ArrItem? FindByTitle(IReadOnlyList<ArrItem> items, string? title, int? year)
    {
        var target = Normalize(title);
        if (target.Length == 0) return null;
        var hits = items.Where(i => i.AllTitles().Any(t => Normalize(t) == target)).ToList();
        if (year.HasValue)
            hits = hits.Where(i => !i.Year.HasValue || i.Year == year).ToList();
        return Single(hits, year);
    }

    /// <summary>An album or book: its parent (artist or author) must equal the given one, and its title the given title, by the same
    /// rule. A year, when both sides have one, must agree; a release year is only a hint (editions differ), so it separates equal
    /// titles but never rejects the only candidate.</summary>
    public static ArrItem? FindChild(IReadOnlyList<ArrItem> items, string? title, string? parentName, int? year)
    {
        var target = Normalize(title);
        var parent = Normalize(parentName);
        if (target.Length == 0 || parent.Length == 0) return null;   // an album or book is only identified within its artist or author
        var hits = items.Where(i => Normalize(i.ParentName) == parent && i.AllTitles().Any(t => Normalize(t) == target)).ToList();
        return Single(hits, year);
    }

    private static ArrItem? Single(List<ArrItem> hits, int? year)
    {
        if (hits.Count == 1) return hits[0];
        if (hits.Count > 1 && year.HasValue)
        {
            var sameYear = hits.Where(i => i.Year == year).ToList();
            if (sameYear.Count == 1) return sameYear[0];
        }
        return null;
    }
}
