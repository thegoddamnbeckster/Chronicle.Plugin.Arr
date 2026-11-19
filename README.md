# Chronicle.Plugin.Arr

A reference plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle). It asks **Radarr, Sonarr, Lidarr and
Readarr** which film, show, artist or author a file or title really is, so Chronicle can identify it by id instead of guessing
from its name.

It is **read-only**. Chronicle only ever reads from these applications; it never adds, changes or deletes anything in them, and
the plugin has no code that could (every request is a plain `GET`).

## Why

Chronicle matches a file by its name. Names collide: "The Beast (2026)" and "Heart of the Beast (2026)" are two different films
and a name match can confuse them. Radarr already knows exactly which film each file is (it downloaded and named it), so Chronicle
asks Radarr first and uses the TMDB id it reports.

| Application | Chronicle asks | It answers with | Used when |
|---|---|---|---|
| Radarr | "which film is `The Beast (2026).mkv`?" | TMDB and IMDb ids | Kodi scrapes a movie |
| Sonarr | "which show is this?" | TMDB and IMDb ids | Kodi scrapes a TV show |
| Lidarr | "which artist is this?" / "which album by this artist?" | MusicBrainz artist id / release-group id | Chronicle identifies a music artist or album |
| Readarr | "which author is this?" / "which book by this author?" | Goodreads author id; for a book its edition's Goodreads id and ISBN-13 | Chronicle identifies an author or a book |

For music and books, Chronicle asks before it searches a provider by name. When the answer is an id the provider uses itself (the
MusicBrainz id for the MusicBrainz plugin) it fetches that item directly; every id the application gave is also kept on the item as a
hint other providers read (Open Library reads the Goodreads id of a book). Tracks are not asked about: Chronicle derives them from
their album.

## Setup

1. Install the plugin in Chronicle (**Settings, Plugins**).
2. Open its settings. For each application you run, enter its **address** as Chronicle's server reaches it (for example
   `http://10.0.0.5:7878`) and its **API key** (in the application under **Settings, General**). Leave the rest blank.
3. Use the plugin's health check to confirm each address and key work.

Plain `http://` and `https://` addresses are accepted (not ones with a user name or password in them). No redirects are followed.

## How a match is made

A match is made on **equality**, never similarity.

- **Radarr, by file:** the exact file name Radarr holds (any folder). If two films hold the same file name, the year decides, else
  it is a miss.
- **By title:** the title, original title or an alternate title must equal the searched one once case and punctuation are ignored
  and a trailing "(year)" is dropped; the year must agree when both sides have one. Two equal titles with no year to separate
  them is a miss.

A miss, an application that is down, a wrong key or a slow answer all do the same thing: Chronicle matches by name as it always
did. Each application's whole list is read once and kept for five minutes, so scanning a large library does not hammer it.

## Requirements and behaviour

- **Needs Chronicle 0.20.76 or later.** The plugin implements `IMediaLocator`, an interface added in 0.20.75 and extended for music and books in 0.20.76; an older
  Chronicle does not have it and cannot load this plugin. It works on its own: nothing else needs to be installed.
- **A rejected API key** is reported once and then nothing more is sent to that application until you save the settings again.
- **Rate limits:** a 429 is retried twice honouring `Retry-After` (each wait capped at 4 seconds), then Chronicle is told to
  pause. A list already read is used instead when there is one.
- **Every request** identifies itself as `Chronicle-Arr/<version>` and sends the API key in a header, never in the address, and no
  part of it is written to a log or an error.
- Not a metadata source: it adds no fields, images or credits, so there is nothing to add to the Metadata Assignment lists.
- Design, terms and checklist record: `docs/plugins/PLUGIN_ARR.md` in the Chronicle repository.

## Development

```
dotnet test tests/Chronicle.Plugin.Arr.Tests
```

Requires the [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) repo beside this one (`..\Chronicle`).

MIT licensed.
