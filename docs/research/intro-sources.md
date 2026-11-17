# Intro and credits timestamp sources for Seaglass

Researched 2026-11-17 from primary sources only (OpenAPI specs, plugin source code, GitHub repos, NOTICE files). No API was called. Anything marked **[UNVERIFIED]** could not be confirmed from a primary source.

Goal: when the user's Jellyfin server has no media segments (no admin rights, so no server plugin), the client looks up skip times directly from a public service, per episode, only when the server has none.

## Summary table

| | TheIntroDB | SkipMe.db | AniSkip |
|---|---|---|---|
| Base URL | `https://api.theintrodb.org/v4` | `https://db.skipme.workers.dev` (only seen in a NOTICE file, no docs) | `https://api.aniskip.com` |
| Public API docs | Yes, OpenAPI at `https://theintrodb.org/openapi.yaml` | No | Yes, OpenAPI at `https://api.aniskip.com/openapi.json`, reference at `/docs` |
| Lookup | `GET /media` | `POST /shows`, `POST /movies` (batch, shape from plugin source only) | `GET /v2/skip-times/{malId}/{episode}` |
| IDs | TVDB (best), TMDB, IMDb | TMDB, TVDB, IMDb, AniList | MAL only |
| Key needed | No (optional Bearer key, higher limits) | None seen in the client | No |
| Types | intro, recap, credits, preview | intro, recap, credits, preview (commercial mentioned in the repo blurb) | op, ed, mixed-op, mixed-ed, recap |
| Third-party client allowed | Not stated anywhere I could read **[UNVERIFIED]**; designed for third-party clients (API keys "intended for third-party clients", public npm package) | **No**, data terms restrict use to the Jellyfin plugin | Not stated; open public API, MIT code |
| Coverage | not published **[UNVERIFIED]** | not published **[UNVERIFIED]** | anime only |

## 1. TheIntroDB

Sources: `https://theintrodb.org/openapi.yaml` (the root `https://api.theintrodb.org` redirects there), github.com/TheIntroDB (plugin, npm package, mpv script, org repo list).

### Endpoint
- `GET https://api.theintrodb.org/v4/media`. `/v2` and `/v3` are frozen, use `/v4`.
- Identity is TVDB. Priority `tvdb_id`, then `tmdb_id`, then `imdb_id`. TMDB and IMDb are resolved server side (slower, and TMDB/IMDb requests are read in that provider's numbering, which the plugin notes is less accurate for TV episodes).
- Query parameters:
  - `tvdb_id` (int), `tmdb_id` (int), `imdb_id` (`tt` plus 7 or 8 digits). Send one.
  - `season` (0 to 9999, year-style seasons are valid), `episode` (0 to 10000). Both or neither. Both present means TV, neither means movie.
  - `mapping`: episode order, one of `default`, `official`, `dvd`, `alternate`, `absolute`, `regional` (Jellyfin spellings accepted). Jellyfin series have a display order, so send it when the series uses a non-default order with a TVDB id. The official plugin skips series with a non-default order and no TVDB id.
  - `tvdb_episode_id` (int64): pins the exact episode, takes priority over season/episode. Jellyfin usually has the episode TVDB id, so send it when present.
  - `duration_ms` (int64): picks the matching release version (within 5 minutes, else falls back to the most-submitted version). Send the real runtime of the file being played.
  - `episode_name` / `episode_title`: optional title hint. Not needed, do not send (leaks a title for no benefit).
  - `list_versions`, `merge_unknown`, `details` (adds `confidence`, `submission_count`, `group_duration_ms`).
- Headers: `Accept: application/json`, `User-Agent: seaglass/<version>`. Optional `Authorization: Bearer <API_KEY>`.

### Response (from the spec, 200)
```json
{
  "tvdb_id": 71663, "type": "tv", "season": 1, "episode": 1,
  "intro":   [{ "start_ms": null, "end_ms": 23000 }],
  "recap":   [{ "start_ms": 25000, "end_ms": 134000 }],
  "credits": [{ "start_ms": 5801777, "end_ms": 6371111 }, { "start_ms": 6408000, "end_ms": null }],
  "preview": [{ "start_ms": 1680000, "end_ms": 1740000 }]
}
```
- Times in milliseconds. A segment type with no data is omitted entirely. Each type can hold several segments.
- `start_ms: null` (or 0) means from the start. `end_ms: null` means to the end of the media. For credits and preview start is required. A `0,0` entry means "confirmed no segment": discard it (the official mpv script does).
- Errors: JSON `{ "error": "...", "code": "..." }`. 404 codes: `no_submissions`, `episode_not_found`, `media_not_found`. 400 for bad params (including "Invalid season. Must be between 0 and 1000" on older deployments). 422 for unparseable params. 429 with `retry_after`.

### Segment types
`intro`, `recap`, `credits`, `preview`. No opening, ending or mixed types.

### Auth
Optional for lookups. Without a key it works. A key is a user key obtained by signing in at theintrodb.org (the plugin page says "signed-in key"); the exact signup path **[UNVERIFIED]**. With a key the user's own pending submissions are included and weighted 10x. Never ship a shared application key for submissions (the npm docs say so). For read-only lookups no key is the right default; offer an optional key field in settings.

### Rate limits (spec)
- `GET /media`: 30 per 10 s anonymous per IP, 120 per 10 s with a key.
- Daily usage: 5,000 `GET /media` per IP anonymous, 100,000 per account with a key.
- Per media target (same id plus season plus episode): 2,000 per day for all callers.
- Response headers: `X-UsageLimit-Limit/Remaining/Reset`, and on 429 `X-RateLimit-*`. 429 body codes: `usage_limit_exceeded`, `specific_media_usage_limit_exceeded`.
- The official plugin paces itself at 25 per 10 s anonymous (about 400 ms spacing) and 100 per 10 s with a key, and backs off on 429 using `X-UsageLimit-Reset`, `X-RateLimit-Reset`, then `Retry-After`, default 300 s.
- Seaglass only looks up the episode being played, so volume is tiny. Do not bulk scan a library.

### Terms and licence
- The OpenAPI document has no terms, licence or contact fields.
- `https://theintrodb.org/terms` exists but the fetch returned only the tagline (page is probably script-rendered), so the terms on third-party clients, caching, attribution and data licence are **[UNVERIFIED]**. Read that page in a normal browser before shipping, and ask in the project Discord if it is unclear.
- Signals that third-party clients are welcome: the spec says API keys are "intended method for third-party clients"; the org publishes MIT and GPL clients for Jellyfin, Emby, Kodi, mpv, IINA, Stremio, Plex and a general browser extension; the org's `theintrodb-assets` repo (logos) is CC-BY-SA-4.0 (attribution for the logo only, not a statement about data).
- Caching: not stated **[UNVERIFIED]**. The service itself imposes a 2,000/day per-episode cap, and the official plugin refetches after 30 days by default and keeps a not-found cache, so a local cache with a similar lifetime is clearly the expected pattern. Cache the 404 result too (short, for example one day) to avoid hammering.
- Attribution: not stated **[UNVERIFIED]**. Cheap safe choice: credit "TheIntroDB" with a link in an About or Sources screen.

### Reliability and coverage
- Coverage numbers are not published on any page I could read **[UNVERIFIED]**. It is a young (2026), community-submitted database; expect gaps on obscure shows. Not anime-only.
- Segments are community averaged, with confidence available via `details=true`.

### Existing clients (references)
| Project | Licence | Use |
|---|---|---|
| TheIntroDB/jellyfin-plugin | GPL-3.0-or-later | Server plugin, best reference for the exact request (`TheIntroDB/Api/TheIntroDbClient.cs`) and 429 handling |
| TheIntroDB/mpv-script | MIT | Client side, plain GET with curl, segment mapping rules (`scripts/theintrodb.lua`) |
| TheIntroDB/theintrodb-npm | MIT | Typed client |
| TheIntroDB/go-episode-remapper | MIT | Remaps episodes between TVDB, TMDB and IMDb orders |
| TheIntroDB/kodi-addon, iina-package, emby-plugin, universal-extension | GPL-3.0 | Other players |

Seaglass is GPLv3 combination code, so reading the GPL plugin for protocol behaviour is fine; copying the MIT script's mapping logic is also fine with its notice kept.

### What leaves the PC
Confirmed from the spec and both reference clients: one HTTPS GET containing the show id (TVDB/TMDB/IMDb), season, episode, optionally `mapping`, `tvdb_episode_id` and `duration_ms`, plus the User-Agent. The server also sees the user's IP. The Bearer key is sent only if the user pasted one. No Jellyfin server URL, token, file name or title needs to leave (leave `episode_name` out).

## 2. SkipMe.db

Sources: github.com/intro-skipper/skipme.db-plugin (README, NOTICE, source), github.com/intro-skipper/segment-editor-mobile (NOTICE).

### What exists
- It is the crowd-sourced backend for the Intro Skipper project's Jellyfin plugin. There are no public API docs and no OpenAPI. The base URL is not in the repo code (injected at build from a CI secret `API_URL`). The only place it is written down is the segment-editor-mobile NOTICE: `https://db.skipme.workers.dev`. The root of that host just says "SkipMe.db" (no docs).
- Plugin repo licence GPL-3.0-only (code only).

### Endpoints (from plugin source only, not documented, could change without notice)
- `POST {base}/shows` and `POST {base}/movies`, body is a JSON array (up to 550 items, 100 MB). Show item fields: `tvdb_series_id`, `tmdb_id`, `imdb_series_id`, `anilist_id`. Response array in the same order, each show has `segments[]` with `tvdb_id`, `tvdb_series_id`, `tvdb_season_id`, `tmdb_id`, `anilist_id`, `season`, `episode`, `segment` (intro, credits, preview, recap), `start_ms`, `end_ms`, `duration_ms`, `submissions`.
- Share endpoints `POST /submit/season`, `POST /submit/collection`.
- It is a series-level batch design meant for syncing whole libraries into a local database, not an episode lookup. No auth header was visible in the client; whether any is configured elsewhere is **[UNVERIFIED]**.
- Rate limits: no numbers published. Plugin spaces batches 500 ms apart and treats 429, or a 500 whose body mentions usage limit, rate limit or quota, as "usage limit reached". Plugin sync is limited to one run per 8 hours.

### Terms (NOTICE, primary source)
- The cached data is "crowd-sourced community data", not GPL, under separate terms: it may be used **only to supply skip-segment information in Jellyfin through the plugin**. Other uses (competing services, automated harvesting, bulk redistribution) need written consent from the SkipMe.db maintainers. Data must not be presented as owned by anyone but the contributor community. NOTICE must be kept unmodified in copies.
- Conclusion: a separate desktop client querying the service directly is not covered by the permitted use. Needs written permission from the maintainers first. The NOTICE text was summarized by the fetch tool, so re-read it before acting.

### Coverage and reliability
Not published **[UNVERIFIED]**. Not anime-only (TMDB/TVDB/IMDb plus AniList ids). Cloudflare Workers hosted.

### Related, do not confuse
- `skipdb.tv` (SkipDB, `api.skipdb.tv`) is a different service: open read API keyed by `imdb_id`, 120 requests per minute, data ODbL 1.0 with reciprocity, AGPL code, about 123k segments on 65k episodes (from its own page), types intro/recap/outro/preview. Terms page `/terms` unread. Worth a later look as an IMDb-keyed fallback, but the ODbL reciprocity clause needs checking against Seaglass's cache before use.
- `introdb.app` is yet another service (its terms page says attribution appreciated, not required). Not TheIntroDB.

## 3. AniSkip

Sources: `https://api.aniskip.com/openapi.json` (spec v2.5.1), github.com/aniskip (aniskip-api MIT, aniskip-extension MIT, aniskip-cli GPL-3.0, aniskip-jellyfin MIT, common MIT).

### Endpoint
`GET https://api.aniskip.com/v2/skip-times/{animeId}/{episodeNumber}?types=op&types=ed&types=mixed-op&types=mixed-ed&types=recap&episodeLength={seconds}`
- `animeId` is the **MAL id**. `episodeNumber` is a number (min 0 in v2) in that MAL entry's own numbering (restarts at 1 for each season/cour entry on MAL).
- `types` is required, repeated parameter (some clients send `types[]`; the spec says array of the enum). `episodeLength` is required, seconds, up to 3 decimals. Per the spec description, 0 means "return all lengths". Send the real runtime in seconds so the matching submission is chosen.
- v1 (`/v1/skip-times/{anime_id}/{episode_number}?types=op&types=ed`, snake_case, op/ed only) still exists; use v2.
- Relation rules: `GET /v2/relation-rules/{animeId}` returns `rules[]` with `from {start,end?}` and `to {start,end?,malId}`: remaps an episode range of one MAL entry onto another MAL entry (for shows MAL splits into several entries). Fetch it when absolute-numbered episodes do not hit.

### Response (v2, from the spec schema, no example in the docs)
```json
{
  "statusCode": 200, "message": "...", "found": true,
  "results": [
    { "interval": { "startTime": 12.5, "endTime": 101.9 },
      "skipType": "op", "skipId": "<uuid>", "episodeLength": 1420.04 }
  ]
}
```
Field names and types are from the spec; values above are illustrative only. Times are seconds. Not found is `found: false` (some clients report HTTP 404); handle both. Spec documents only success responses.

### Segment types
`op`, `ed`, `mixed-op`, `mixed-ed`, `recap`. Map to Jellyfin kinds: op and mixed-op to Intro, ed and mixed-ed to Outro/Credits, recap to Recap. There is no preview type.

### Auth, limits, terms
- No key, no account, no security scheme in the spec. Reading is open.
- Rate limits for reads: none stated in spec or code excerpts I read **[UNVERIFIED]**. The only throttle guards in the repo are for creating skip times and voting. Behave anyway: one request per episode played, cache.
- Terms of use and data licence: none stated. The API repo and extension are MIT (code). The API README says a weekly anonymised data dump exists (`sanitize-db-dump` repo, licence not listed **[UNVERIFIED]**), which suggests the data is meant to be open. No caching or attribution rule found. Safe course: cache modestly, credit AniSkip in an About screen.
- Reliability: community run, single host. Anime only. Coverage is strong for popular seasonal anime, thin for old or niche titles **[UNVERIFIED, general knowledge not from a primary source]**.

### Existing clients (references)
| Project | Licence | Use |
|---|---|---|
| aniskip/aniskip-extension | MIT | Browser extension, MAL id as primary key |
| aniskip/aniskip-cli (fork of synacktraa/ani-skip) | GPL-3.0 | mpv Lua, gets op/ed and writes mpv options |
| aniskip/aniskip-jellyfin | MIT | "Automated skipping of segments within a Jellyfin player"; repo has only a README, 1 commit, no code seen; contents unread |
| intro-skipper issue #200 "Anime OP/ED via AniSkip API" | n/a | Open feature request, status unknown |

### Mapping Jellyfin IDs to MAL
Jellyfin gives series provider ids: `Tvdb`, `Tmdb`, `Imdb`, plus `AniDB` and `AniList` when the anime plugins are installed. There is no MAL id unless the MAL plugin is used, so mapping is needed.

**Option A, ARM (arm.haglund.dev), by BeeeQueue**
- Open hosted API, no auth, no rate limit or terms stated (spec: "no rate limits, terms of use, or authentication are listed") **[a hosted service with no stated limits; be gentle]**. Code AGPL-3.0 (service code only). Can be self-hosted with Docker (`ghcr.io/beeequeue/arm-server`).
- Data comes from Fribb/anime-lists, refreshed every 24 hours, plus manual rules.
- `GET /api/v2/ids?source=anilist&id=<n>&include=myanimelist` (sources: anidb, anilist, kitsu, myanimelist, anime-planet, livechart, ...). Result has `myanimelist`, `anidb`, `anilist`, `thetvdb`, `themoviedb`, `imdb`, `thetvdb-season`, `themoviedb-season`.
- TVDB, TMDB and IMDb have their own endpoints: `GET /api/v2/thetvdb?id=<n>`, `/api/v2/themoviedb?id=<n>`, `/api/v2/imdb?id=<tt...>`, each returning an **array** (one show can map to several MAL entries, one per season). Pick the entry whose `thetvdb-season` (or `themoviedb-season`) equals the episode's season. Nothing returned: `null`, not 404.
- Batch: `POST /api/v2/ids` accepts up to 100 queries.
- The ARM response gives the season but **no episode offset**. If a MAL entry covers a later part of a TVDB season, use AniSkip's relation rules or the Anime-Lists offsets (below).

**Option B, ship the data (Fribb/anime-lists, fully offline, no extra request leaves the PC)**
- Download `anime-list-full.json` (merged, keyed by AniDB id; fields `mal_id`, `anilist_id`, `anidb_id`, `tvdb_id`, `themoviedb_id`, `imdb_id`, `season.tvdb/tmdb`) once, cache it, refresh weekly. Built from anime-offline-database (ODbL 1.0, attribution notice required: "Contains information from anime-offline-database, which is made available here under the Open Database License (ODbL)") and Anime-Lists/anime-lists.
- Fribb/anime-lists itself shows no licence on its README; Anime-Lists/anime-lists shows none either **[UNVERIFIED]**. Check the repos' LICENSE files before bundling a copy; downloading at runtime from the repo is lower risk than redistributing.
- Anime-Lists/anime-lists XML carries `anidbid`, `tvdbid`, `defaulttvdbseason`, `episodeoffset`, `tmdbid`/`tmdbtv`, `imdbid`: use `episodeoffset` to convert the TVDB episode number to the AniDB/MAL entry's numbering.

**Recommended mapping order:** AniList id (if Jellyfin has it) via ARM or the Fribb file; else AniDB id; else TVDB id plus season via the Fribb file; then episode number adjusted by offset or relation rules. If no MAL id is found, do not query AniSkip.

### What leaves the PC
- To AniSkip: one HTTPS GET with the MAL id, episode number, episode length in seconds, plus IP and User-Agent. Nothing else needed. Confirmed against the spec (all required params are those).
- To ARM (if used): one GET with a public show id. With Option B nothing extra leaves the machine at lookup time (only the periodic data download).

## 4. Recommendation

1. **Implement TheIntroDB first.** Documented OpenAPI, no key needed, TVDB-keyed (Jellyfin has TVDB ids for most shows, and both TMDB/IMDb work as fallback), covers movies and shows, returns intro/recap/credits/preview, and Jellyfin's four media segment kinds map directly. Official plugin and mpv script are readable references.
2. **Add AniSkip second, only for anime** (series with an AniList/AniDB id or matched in the anime list). Use the Fribb list downloaded and cached locally for mapping (privacy and no dependency on a hosted mapper), with ARM as an optional fallback. Needs episode-number translation, so budget the extra work.
3. **Do not use SkipMe.db** unless the maintainers give written permission. Its data terms restrict use to the Jellyfin plugin, the API has no public docs and the URL is not published in code.
4. Optional later: skipdb.tv as an IMDb-keyed fallback after reading its terms and the ODbL reciprocity clause.

### Implementation sketch
- Order per episode, only when the server returns no media segments: local cache, TheIntroDB, AniSkip (anime only). Stop at the first source that returns something, so most episodes send one request.
- Trigger at play start (or when the episode page opens), never as a library scan.
- Send only: public show id, season, episode, `duration_ms` (TheIntroDB) or length in seconds (AniSkip). No titles, file names, server URL or Jellyfin token. Fixed User-Agent `seaglass/<version>` with no user details.
- Cache per (source, id, season, episode, duration bucket): hits for 30 days (matches the official plugin), misses and 404 for about 1 day, 429 stores the reset time and pauses that source. Honour `X-UsageLimit-Reset` and `Retry-After`. Timeout around 5 s so playback never waits on a lookup; fetch in the background and apply when it arrives.
- Normalize into one internal segment list: kind (intro, recap, credits, preview), start, end in ms. Null start is 0, null end is the media end, discard 0,0, clamp to media duration, sort by start. Remember where each segment came from (for a small "source: TheIntroDB" label).
- User controls in settings: sources on or off, optional TheIntroDB key (never required), clear cache. Default for first run: on, but state plainly what is sent.
- Add an About/Sources entry crediting TheIntroDB, AniSkip, and (if used) anime-offline-database under ODbL.

### Open items to close before shipping
1. Read `https://theintrodb.org/terms` in a normal browser (third-party clients, caching, attribution). Not readable from here.
2. Confirm how a user obtains a TheIntroDB key (sign-in on the site).
3. Confirm coverage numbers for TheIntroDB (none published in what I read).
4. Make one example AniSkip call with a known MAL id to confirm the live response shape (not done, per the no-calls instruction).
5. Check LICENSE files of Fribb/anime-lists and Anime-Lists/anime-lists before bundling data.
6. Ask AniSkip maintainers (Discord linked from github.com/aniskip/aniskip-extension) about read rate limits and any usage expectations.

## Source list
- https://theintrodb.org/openapi.yaml, https://theintrodb.org/terms (unreadable), https://github.com/TheIntroDB (org), https://github.com/TheIntroDB/jellyfin-plugin (`TheIntroDB/Api/TheIntroDbClient.cs`, `MediaResponse.cs`, `SegmentTimestamp.cs`), https://github.com/TheIntroDB/mpv-script (`scripts/theintrodb.lua`), https://theintrodb.github.io/theintrodb-npm/
- https://github.com/intro-skipper/skipme.db-plugin (README, NOTICE, `Services/SkipMeApiClient.cs`, `Services/ShareSubmissionService.cs`, `Models/*`), https://github.com/intro-skipper/segment-editor-mobile (NOTICE)
- https://api.aniskip.com/openapi.json, https://github.com/aniskip (aniskip-api, aniskip-extension, aniskip-cli, aniskip-jellyfin, common)
- https://github.com/BeeeQueue/arm-server (and `docs/openapi.yaml`), https://github.com/Fribb/anime-lists, https://github.com/Anime-Lists/anime-lists, https://github.com/manami-project/anime-offline-database
- https://skipdb.tv and https://skipdb.tv/docs (separate service)

## TheIntroDB terms, read 2026-11-17 (https://theintrodb.org/docs/terms, "Last updated: 2026-09-20")
Read by the coordinator in a browser. The research pass could not load the page; the real address is `/docs/terms`, not `/terms`.
- **4.1 Licence:** a "limited, non-exclusive, non-transferable, revocable license to use timestamp data retrieved through the API solely for client-side, non-commercial, end-user applications". The calls must come from the end user's device. Seaglass, a free GPL desktop client calling from the user's PC, fits.
- **4.2 Attribution:** requested, not required by default; TheIntroDB may require it with 14 days' notice. If given, it must name "TheIntroDB" with a link to https://theintrodb.org where a user meets it (settings, about page, or next to the feature). **Seaglass gives it** in Settings next to the toggle.
- **4.3 Restrictions:**
  - No scraping or systematic collection. **Fetch only the episode being played, never a whole season or library ahead.**
  - No storing responses to rebuild the database. "Temporary client-side caching for performance, such as caching a single episode's timestamps for the duration of a session, is permitted for personal use." **Cache in memory for the session only; never persist TheIntroDB data to disk.** The "personal library caching" permission names media server software, so don't rely on it for a client.
  - No derivative database, no redistribution, no server-side proxy or aggregation, no monetising, no commercial use.
- **6.2 Rate limits:** per IP; respect them, with no circumvention. Honour 429 and Retry-After, and back off.
- **6.4:** submitting timestamps with the user's own API key is encouraged but optional (a later feature at most).
- **9.2:** a piracy policy (legal access to the media expected), which is a note for the README.
