# Serializd-style TV journal inside FluentFin MPV: research (2026-11-14)

Research only, no code. The plan changed from "integrate with Serializd" to "build its features ourselves" using TMDB and similar sources plus the Jellyfin watch history.

**Why not integrate (for the record).** Serializd has no official API, developer programme or OAuth. Community tools ([serializd-py][spy], [unserializd (Go)][go], [Jellyfin scrobbler plugin][jfp], [Jellyscribe][jsc], [trakt-serializd-sync][tss]) call the private backend the website uses (`https://serializd.onrender.com/api`, `X-Requested-With: serializd_vercel`). Logging in means sending the user's email and password to get a bearer token, and the plugins keep the password, encrypted, so they can log in again ([UserConfig.cs][jfcfg]). Every one of them warns it "may break". The site's terms and privacy pages refused automated fetches (403), so the ToS position on automated access is **unverified**. If we ever want to push data there, a one-way exporter of our own diary could come later. It is not recommended.

## 1. Feature teardown, and what fits a single-user desktop client

Sources: [serializd.com][home], [App Store listing and version notes][ios], [Gizmodo][giz], competitor pages ([SIMKL][simkl], [Moviebase][mb]), and the request fields used by the unofficial clients ([diary request][jfdiary], [client docs][spydoc], [Go client][go]).

| Serializd feature | How it works for the user | Fit here |
|---|---|---|
| Log episode / season / show watched | Mark individual episodes, whole seasons or a whole show. Marking every episode does **not** auto-mark the season. | **Yes, automatic** from Jellyfin playback. |
| Statuses | Profile lists: Watching, Watched, Watchlist, Paused, Dropped. | **Yes.** Watching and Completed are derived from progress; Paused and Dropped are set by hand. |
| Diary | Dated entry per log: backdate, rating, review text, spoiler flag, rewatch flag, tags, like. "Quick logging" by press-and-hold on a poster (v1.911). | **Yes, core.** |
| Ratings | On episodes, seasons and shows. Stored as an integer 1-10 per the unofficial clients, so 5 stars in half-star steps (official display not confirmed). | **Yes.** |
| Reviews | Text on show, season or episode, with a spoiler flag. Others can like and discuss them. | **Yes, private.** The social layer does not apply. |
| Watchlist | "Want to watch", with shuffle. | **Yes**, including shows not in the library (from TMDB). |
| Lists | Custom lists, "Top" lists, and browsing other members' lists. | Personal lists **yes**; others' lists **no**. |
| Stats | Total watch time, completed shows, genre breakdown, favourite episodes, most-watched actors and directors, stats card, "fix stats". | **Yes**, computed locally. |
| Year in review | "Serializd Rewatch 2025" and "Best of 2025". | **Yes**, a local year page. |
| Profile | Favourite shows, custom posters, badges, Patreon badges. | Favourites and poster choice **yes**; badges are a later bit of fun. |
| New-episode alerts, widgets | Notifications for upcoming episodes and release dates. | **Partly.** An "Airing soon" list, with an optional Windows toast later. |
| Discovery | Trending, top shows/seasons/episodes, recommendations, JustWatch "where to watch". | **Partly**: TMDB trending and recommendations. Community rankings no; where-to-watch is not needed for an own library. |
| Social | Follow, friends' activity, likes, comments and discussions, leaderboards, Emmy voting. | **No.** It could only work later if the data lived on the server and several Jellyfin users shared a feed. |
| Import / export | Imports from TV Time and Trakt (site FAQ). SIMKL (a competitor) says there is **no export**, and Trakt imports lose watch dates ([trakt-to-serializd][t2s]). | Import **yes**. Export of our own data **yes** (JSON/CSV), which Serializd lacks. |
| Scope | TV only, metadata from TMDB, iOS/Android/web. | Movies would be cheap to add with the same path (open question). |

## 2. Data sources

**Jellyfin already gives us the mapping, so no searching is needed.**
- Series and movies carry `ProviderIds` with `Tmdb`, `Tvdb` and `Imdb` when requested with `Fields=ProviderIds`. The [Jellyfin Serializd plugin][jfscrob] maps an episode as series `Tmdb` id + `ParentIndexNumber` + `IndexNumber`, and skips the episode if the series has no Tmdb id.
- TMDB's [`/find`][find] converts a `tvdb_id` or `imdb_id` (shows *and* episodes) to TMDB ids as a fallback.
- Per-user data ([UserItemDataDto][uid]): `Played`, `PlayCount`, `LastPlayedDate` (the last play only, not a history), `PlaybackPositionTicks`, `PlayedPercentage`, `IsFavorite`, `Likes`, and `Rating` (0-10, nullable).
- Writes:
  - `POST/DELETE /UserPlayedItems/{id}`, with an optional `datePlayed` ([Playstate API][ps]).
  - `/UserFavoriteItems/{id}`.
  - `POST /UserItems/{id}/Rating`, which takes only the `likes` boolean ([UserLibrary API][ul]).
  - A numeric rating through `POST /UserItems/{id}/UserData`. This route is reported by forum users, not official docs, so it **must be checked on the user's server version** ([forum][fc]).
- The server marks an item played past "Max resume %", reportedly 90% by default ([SDK][maxpct]).
- The **Playback Reporting** server plugin keeps true per-play history (`PlaybackActivity` with `DateCreated`, `UserId`, item name and type). FluentFin already calls its `/user_usage_stats/*` endpoints (`ui\FluentFin.Plugins\Playback Reporting\PlaybackReportingHelper.cs`).

| Source | Key / cost | Limits | Terms that matter | Use for |
|---|---|---|---|---|
| **TMDB** (primary) | Free account. API key (query parameter) or Read Access Token (bearer); both give the same access ([auth][tmdbauth]). | About 40 req/s; no daily cap; honour 429 ([rate][tmdbrate]). | Non-commercial use only. Must show the TMDB logo and the notice "...uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB". **No caching longer than 6 months.** No AI training ([terms][tmdbtos]). | Full episode lists including episodes missing from the library, season and episode counts, `next_episode_to_air`, genres, networks, cast, trending, recommendations. Images from `image.tmdb.org/t/p/{size}` (posters w92-w780, backdrops w300/w780/w1280, stills w92/w185/w300, plus original; read sizes from `/configuration` ([sizes][sizes])). `append_to_response` combines up to 20 sub-requests ([tv][tmdbtv]). |
| **TVmaze** (secondary) | Free, **no key** ([api][tvmaze]). | 20 calls per 10 s per IP; 429 means retry. Responses cached for 60 min. | CC BY-SA 4.0: credit and link TVmaze; ShareAlike applies if the data is redistributed. | Air schedules for "Airing soon"; lookup by `thetvdb` or `imdb` id. |
| **TheTVDB v4** | Project key (licensed or user-supported model) plus each user's **subscriber PIN**, about $11.99/yr per the FAQ ([api][tvdb], [FAQ][tvdbfaq]). | n/a | Attribution with a link required. | **Skip.** Jellyfin already holds TVDB ids, and TMDB `/find` maps them. |
| **Trakt** | OAuth (device flow). New API apps reportedly **need VIP since mid-2026** (second-hand source: [Traktivity][traktvip]). | 1 authenticated write/s; 1,000 GETs per 5 min ([mirror][traktrl]). Freemium list caps changed in 2025-26 ([AlternativeTo][traktfree]). | n/a | Import only, from a JSON export the user already has, or the API if they own an app. No live sync. |
| **OMDb** | Key sent by email; free tier 1,000 requests/day ([keys][omdbkey]). | n/a | CC BY-NC 4.0 ([site][omdb]). | Optional IMDb-side ratings. Low value. |

## 3. Design sketch

**Where the data lives.**
- **Main store: a local SQLite file** at `%LOCALAPPDATA%\FluentFinMpv\journal.db`, written by the C# UI.
  - Tables: diary entries, ratings (shows, seasons, episodes), reviews, statuses, lists and watchlist.
  - It also holds a TMDB cache that is refreshed and pruned before the 6-month limit.
  - It is private, works offline, and can hold everything a diary needs.
- **Mirror to Jellyfin** (`Played`, `IsFavorite`, `Rating` = half-stars × 2) so the TV and phone clients see the same state. Jellyfin's `Played` stays the source of truth for "watched".
- **A Jellyfin server plugin** would capture plays from every client, but it costs the most: server deployment, and plugin APIs that change between Jellyfin versions. A **pull "catch-up" sync** gets most of that benefit without a plugin: on start, find episodes whose `LastPlayedDate` is newer than the last sync and log them, using Playback Reporting history for exact dates when the plugin is present.

**What happens automatically.** The bridge already gives the UI what it needs (`docs\BRIDGE.md` §5):
- `item`: series name, season and episode numbers, runtime.
- `position`.
- `ended`: `eof`, `stopped`, `error` or `replaced`.
- `reported`: `sent`, `failed` or `none` for each `playSessionId`.

The rules:
1. On `reported: sent`, re-read the item's UserData. If `Played` flipped or `PlayCount` rose, write one diary entry (date = now, rewatch if `PlayCount` > 1). The dedupe key is `playSessionId`.
2. On `failed`: if the item ended with `eof` or the last position was 90% or more, log the entry locally and queue the `/UserPlayedItems` call for later.
3. Ignore plays shorter than a few minutes.

**What the user does.**
- An optional, non-blocking "Rate S02E05?" flyout with half-stars and a "Write review" link; dismissing it does nothing. Setting: off / every episode / finales only.
- Editing diary entries: date, rewatch flag, tags, spoiler flag, delete.
- Setting statuses, building lists, adding to the watchlist from TMDB search. For titles not in the library, a "Request" button through FluentFin's existing Jellyseerr plugin.

**Screens.**
- **Show page additions:** status chip; your show, season and episode ratings; an "X of Y episodes" bar counted against TMDB, so episodes missing from the library count too; "Log" / "Log season" buttons; review box.
- **Diary:** grouped by month, each entry with still, S/E, date, stars and rewatch icon; filter by year or show.
- **Stats / Year in review:** hours watched, episodes, shows completed, genres, top-rated, a monthly chart.
- **Watchlist & Lists.**
- **Airing soon:** shows you are watching that have a next episode.
- **Settings:** TMDB token, imports and exports, TMDB/TVmaze attribution on the About page.

**Imports.**
- **Jellyfin backfill:** one entry per played episode at `LastPlayedDate`, flagged as "approximate date"; exact dates from Playback Reporting history when the plugin is installed.
- **Trakt:** from a JSON export.
- **TV Time:** only from a GDPR CSV saved before the service shut down on 15 Jul 2026 (`tracking-prod-records-v2.csv` per a single source) ([MacRumors][tvt], [Moviebase][tvtexp]).
- **Serializd:** community exporters exist ([GitHub search][ghs]).
- Every import shows a mapping-review screen listing the titles it could not match.
- **Export:** JSON plus a diary CSV.

**Increment plan (rough dev-agent days).**

| # | Increment | Effort |
|---|---|---|
| 0 | Spike: does the user's server accept a numeric rating on `/UserItems/{id}/UserData`? How many series lack a Tmdb id? | 0.5 d |
| 1 | **MVP journal:** SQLite schema; auto-log on finish (rules above, with unit tests for dedupe, threshold, `replaced` and `failed`); Jellyfin backfill; Diary page with edit and delete; half-star episode and show rating mirrored to Jellyfin; JSON/CSV export. Uses Jellyfin metadata only, no TMDB. | 2-3 d |
| 2 | TMDB layer: token entry, cached client with 429 backoff, attribution; full episode progress, statuses, season ratings, reviews. | 2-3 d |
| 3 | Stats and Year in review. | 2 d |
| 4 | Watchlist, lists, Airing soon (TVmaze or TMDB), Jellyseerr request hook. | 2-3 d |
| 5 | Trakt and TV Time imports with the mapping-review screen. | 2 d |
| 6 | Later: rating flyout, Windows toasts, movies (Letterboxd-style), catch-up sync or server plugin. | 3-6 d |

About 10-14 dev days reaches "Serializd minus the social layer". The MVP (0+1) is useful on its own.

## 4. What the user provides, and open questions

**Provides:**
- A free TMDB account and its **API Read Access Token**, which they create themselves at themoviedb.org/settings/api and paste into Settings. It is stored DPAPI-encrypted and never put in argv, the environment or logs (the same rule BRIDGE.md applies to Jellyfin tokens).
- No passwords for any service, and no TVDB subscription.
- Optionally, any saved Trakt or TV Time export files.

**Open questions:**
1. TV only, or movies too?
2. Should plays from other Jellyfin clients (TV, phone) be logged? That decides between the catch-up sync and a server plugin.
3. Write a diary entry for every finished episode automatically, or only mark it watched and let you log by hand (the Serializd plugin offers both)?
4. Rating prompt: off, every episode, or finales only?
5. Half-stars (stored 1-10) acceptable? Should ratings and favourites be mirrored into Jellyfin?
6. Is Playback Reporting installed on the server? It is the only source of exact past dates.
7. Is there any Trakt, TV Time or Serializd history to import, and is there an existing Trakt API app?
8. Should `journal.db` be added to the Copy Guardian backup tiers?
9. Separate journals per Jellyfin user (for example a partner on the same PC)?

[spy]: https://github.com/Velocidensity/serializd-py
[spydoc]: https://velocidensity.github.io/serializd-py/client.html
[go]: https://pkg.go.dev/github.com/Skyth3r/unserializd
[jfp]: https://github.com/ElmarXCV/jellyfin-plugin-serializd-sync
[jfcfg]: https://raw.githubusercontent.com/ElmarXCV/jellyfin-plugin-serializd-sync/main/Jellyfin.Plugin.SerializdSync/Configuration/UserConfig.cs
[jfdiary]: https://raw.githubusercontent.com/ElmarXCV/jellyfin-plugin-serializd-sync/main/Jellyfin.Plugin.SerializdSync/API/Objects/DiaryEntryRequest.cs
[jfscrob]: https://raw.githubusercontent.com/ElmarXCV/jellyfin-plugin-serializd-sync/main/Jellyfin.Plugin.SerializdSync/Services/PlaybackScrobbler.cs
[jsc]: https://github.com/builtbyproxy/Jellyscribe
[tss]: https://github.com/VanillaChief/trakt-serializd-sync
[t2s]: https://github.com/Velocidensity/trakt-to-serializd
[ghs]: https://github.com/search?q=serializd&type=repositories
[home]: https://www.serializd.com
[ios]: https://apps.apple.com/us/app/serializd/id1581244120
[giz]: https://gizmodo.com/download/serializd
[simkl]: https://docs.simkl.org/how-to-use-simkl/faq/frequently-asked-questions/simkl-alternatives/simkl-vs-serializd
[mb]: https://moviebase.app/resources/moviebase-vs-serializd
[find]: https://developer.themoviedb.org/reference/find-by-id
[uid]: https://typescript-sdk.jellyfin.org/interfaces/generated-client.UserItemDataDto.html
[ps]: https://webysther.github.io/jellyfin-sdk-python.github.io/api_10_11/docs/PlaystateApi
[ul]: https://webysther.github.io/jellyfin-sdk-python.github.io/api_10_11/docs/UserLibraryApi
[fc]: https://community.firecore.com/t/sync-ratings-to-jellyfin/60409
[maxpct]: https://kotlin-sdk.jellyfin.org/dokka/jellyfin-model/org.jellyfin.sdk.model.api/-server-configuration/max-resume-pct.html
[tmdbauth]: https://developer.themoviedb.org/docs/authentication-application
[tmdbrate]: https://developer.themoviedb.org/docs/rate-limiting
[tmdbtos]: https://www.themoviedb.org/api-terms-of-use
[sizes]: https://community.kde.org/Macaw-Movies/TMDB-json-responses
[tmdbtv]: https://developer.themoviedb.org/reference/tv-series-details
[tvmaze]: https://www.tvmaze.com/api
[tvdb]: https://thetvdb.com/api-information
[tvdbfaq]: https://support.thetvdb.com/kb/faq.php?id=81
[traktvip]: https://wordpress.org/plugins/traktivity/
[traktrl]: https://apis.guru/apis/trakt.tv
[traktfree]: https://alternativeto.net/news/2025/2/trakt-tv-has-set-stricter-limits-for-free-users-and-raised-vip-subscription-prices-by-100-
[omdb]: https://www.omdbapi.com/
[omdbkey]: https://www.omdbapi.com/apikey.aspx
[tvt]: https://www.macrumors.com/2026/07/02/show-tracking-app-tv-time-shutting-down/
[tvtexp]: https://moviebase.app/resources/how-to-export-tv-time-data
