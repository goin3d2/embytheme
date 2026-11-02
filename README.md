# EmbyTheme

An Emby Server plugin that downloads **TV theme songs** and saves them as `theme.mp3` in each series folder, so Emby
plays them in the background while you browse.

- Looks themes up on **Plex's TV theme server** (`tvthemes.plexapp.com/{tvdb}.mp3`), by TVDB id.
- Series that only have a **TMDB id** get their TVDB id looked up through TMDB first (optional, needs a TMDB key).
- Works through big libraries a slice at a time (**1,000 lookups per run** by default, every 6 hours), new series first.
- Remembers every miss, so a show Plex doesn't have is only asked about again after 30 days.
- Checks each library folder for **write access** before downloading anything for it, so read-only mounts are
  reported once instead of failing per series.
- A settings page with counts by status, a searchable series list, and per-series **Fetch**, **Set URL** (save any
  MP3 link as the theme) and **Skip**.

No external tools (no ffmpeg, yt-dlp or Deno). Never overwrites a theme you already have.

Targets **Emby Server 4.10+** (.NET 8).

## Install

1. Download `EmbyTheme.dll` from [Releases](https://github.com/goin3d2/embytheme/releases), or build it (below).
2. Copy it into Emby's `plugins` folder (`/config/plugins` in the Docker image) and restart Emby.
3. Open **Settings > Plugins > EmbyTheme**. Pick libraries (or leave all unticked for every TV library), paste a
   TMDB key if you have one, and save.
4. Run **Scheduled Tasks > EmbyTheme > Download TV theme songs**, or wait for its 6-hour schedule.

Emby only plays theme songs in clients that support them, and some clients have a setting to turn them on.

## How a run works

1. Lists the series in the chosen libraries, newest added first.
2. Skips series that already have a theme: anything Emby already lists, a `theme.*` audio file in the series folder,
   or a `theme-music/` folder with audio in it.
3. Picks the series that are due: never checked first, then misses and failures whose retry time has passed.
4. For each one, until the per-run limit:
   - Probes the library folder for write access (once per folder per run).
   - Uses the series' TVDB id, or looks one up from its TMDB id (once, then cached).
   - Downloads `{tvdb}.mp3` from Plex, checks it's really an MP3 (size and header), and writes it as `theme.mp3`
     through a temp file.
   - Queues a light refresh of the series (no metadata or image downloads) so Emby picks the theme up without a
     library scan.
5. Saves per-series results to `theme-state.json` in the plugin's data folder and logs a one-line summary.

## Statuses

| Status | Meaning | Looked at again |
|---|---|---|
| Saved | Theme downloaded and saved by the plugin | Never (use **Fetch** or reset) |
| Already had one | A theme was there before the plugin looked | Never |
| Not found | Plex has no theme for the series' TVDB id | After *Look again for missing themes* (30 days) |
| No usable id | No TVDB id, and none could be found from TMDB | After 30 days |
| Failed | Network error, unreadable download, or the folder isn't writable | After *Retry failures* (12 hours), doubling on repeats up to 7 days |
| Skipped | You told it to leave this series alone | Never (until you unskip) |

The **Look again** and **Retry failed now** buttons on the settings page reset those groups so the next run tries
them straight away.

## Settings

| Setting | Default | |
|---|---|---|
| Libraries | all TV libraries | |
| Lookups per run | 1000 | Outside requests (Plex or TMDB) per run. Series that already have a theme don't count. |
| Pause between requests | 250 ms | |
| Look again for missing themes after | 30 days | |
| Retry failures after | 12 hours | |
| Refresh each series after saving | on | Off means new themes show up after your next library scan. |
| TMDB API key | empty | v3 key or v4 read access token. |

## API

All admin-only, under `/emby/EmbyTheme/`:

```
GET  Settings | POST Settings
GET  Status                                   counts, last run, libraries
GET  Series?Status=NotFound&Search=&StartIndex=0&Limit=100
POST Reset?Statuses=NotFound,NoId
POST Series/{id}/Fetch
POST Series/{id}/Url?Url=https://.../theme.mp3
POST Series/{id}/Skip?Skip=true|false
```

## Build from source

```bash
dotnet publish src/EmbyTheme -c Release -o out      # out/EmbyTheme.dll
dotnet run --project tests/EmbyTheme.Tests           # tests for the parts that don't need Emby
```

By default it compiles against Emby's NuGet package `mediabrowser.server.core`. To compile against your own server's
assemblies instead:

```bash
docker cp <emby-container>:/system ./emby-lib
dotnet publish src/EmbyTheme -c Release -o out -p:EmbyLibDir=$PWD/emby-lib
```

Pushing a `v*` tag builds the DLL on GitHub Actions and attaches it to a release.

## Layout

```
src/EmbyTheme/
  Plugin.cs                    plugin entry, settings.json, state store, page registration
  PluginOptions.cs             settings, with clamping
  Core/ThemeRules.cs           retry rules, theme detection, MP3 check, write probe, safe write (no Emby types)
  Core/ThemeSources.cs         Plex theme server and TMDB clients (no Emby types)
  Core/ThemeState.cs           per-series state and theme-state.json (no Emby types)
  Services/ThemeService.cs     Emby side: series queries, saving, refresh
  Tasks/DownloadThemesTask.cs  the scheduled task
  Api/EmbyThemeApi.cs          admin endpoints for the settings page
  Configuration/configPage.*   settings page
tests/EmbyTheme.Tests/         console test runner for Core/
```

## Ideas for later

- More sources for the shows Plex doesn't have (TelevisionTunes title search, ThemerrDB).
- An opt-in fallback that cuts the opening from Emby's intro markers, for local libraries.
