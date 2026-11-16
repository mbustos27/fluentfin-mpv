# TV journal (Serializd-style), as built

The MVP of the journal in `docs/research/journal-research.md` (increment 1 there). It is a diary of the episodes you
watched, built on your Jellyfin history, with half-star ratings and export. Serializd has no API, so this is our own,
and it is private to this PC. There is no TMDB yet; everything uses Jellyfin metadata.

## Decisions (made 2026-11-14, "make all recommended decisions")

| # | Decision | As built |
|---|---|---|
| 1 | TV first, movies later | Only `Episode` items are logged and synced. The rating panel shows on episode, season and series items (the panel is on the episode and series pages only). |
| 2 | Plays from other clients: catch-up sync on start, no server plugin. Exact dates from Playback Reporting only if present. | `JournalSync`. It runs when the main pane is built after a sign-in, and from the Diary's Sync button. Playback Reporting is asked once per sync; a 401, 403 or 404 means "not available" and is logged at Debug only. |
| 3 | Auto-log when an episode finishes; entries editable and deletable | `JournalAutoLogger` + `AutoLogRules`, below. |
| 4 | Rating prompt off by default (off / every episode / finales only); the flyout comes later | The setting is stored in `journal.db` (`rating_prompt`, default `Off`). It is not in the UI until the flyout exists. |
| 5 | Half-stars stored 1-10. Played and Favourite mirrored to Jellyfin. The numeric rating mirrored only behind a setting that defaults off. | **Played:** a mark-played the journal owes (after a failed Stopped report) is sent with its date. **Favourite:** FluentFin's heart button already writes Jellyfin's `IsFavorite`, and the journal keeps no favourite of its own. **Rating:** "Send my star ratings to Jellyfin" (off) sends `POST /UserItems/{id}/UserData {Rating: 1-10}`; a cleared rating is not sent. Not verified against the user's server. |
| 6 | Separate journals per server + user | Every row is keyed by `profiles(server_id, user_id)`. The current journal comes from `SessionInfo` (`SessionInfoJournalSession`). A server with no id is keyed by its address. |
| 7 | No TMDB yet; a clean seam for it | `IJournalServer` is Jellyfin only. TMDB goes beside it, see "Next". |
| 8 | Local SQLite `journal.db` in the app's data folder | `KnownFolders.ApplicationData\journal.db` (so `--data-dir` moves it too). Microsoft.Data.Sqlite 10.0.14. |

## Where the code is

- `ui/FluentFin.Core/Journal/` holds the logic, all tested in `ui/FluentFin.Core.Tests/Journal/`:
  - `JournalStore`: SQLite, schema and migrations.
  - `PlaySessionTracker`: pure, over the bridge events.
  - `AutoLogRules`: pure.
  - `JournalAutoLogger`, `JournalSync`, `JournalService`: the facade for the pages.
  - `JournalExport`, `StarRating`, `PlaybackReportingQuery`.
  - `DiaryViewModel` and `DiaryItems`, `JournalRatingViewModel`.
  - `IJournalServer` and `IJournalSession`: the seams.
- `ui/FluentFin.Core/Services/JellyfinClient_Journal.cs`: FluentFin's own client implements `IJournalServer` (a new partial file).
- `ui/FluentFin.Plugins/Journal/` holds the UI, as an `IPlugin`:
  - `JournalPlugin` registers the services and the page, adds the "Diary" pane entry, and starts things after sign-in.
  - `Views/DiaryPage.xaml`.
  - `Controls/HalfStarRating`, `Controls/JournalRatingPanel`.
  - App wiring is tested in `ui/FluentFin.Tests/JournalPluginTests.cs`.

## Storage: `journal.db`, schema 2

`PRAGMA user_version` is the schema version. `JournalStore.Migrations` lists the steps; each one runs in a transaction
together with its `user_version`. Never edit a step that has shipped: add one.

| Table | What |
|---|---|
| `profiles` | One row per journal: `server_id` (lower case) and `user_id` (N format), plus display names. |
| `entries` | One watch of one episode. <ul><li>Ids and names of the episode, season and series; S/E numbers; runtime.</li><li>Image tags: the episode still, and the series poster as fallback.</li><li>`watched_at` (fixed-width UTC text, or NULL for "date unknown").</li><li>`date_source`: `session` / `playback_reporting` / `last_played` / `manual` / `unknown`; `last_played` and `unknown` are "approximate".</li><li>`source`: `auto` / `backfill` / `catchup` / `manual`.</li><li>`rewatch`, `review`, `spoiler`, `play_session_id`.</li><li>`on_server`: 0 while a mark-played is owed.</li><li>`deleted_at`: deletes are tombstones.</li><li>`logged_at` (schema 2): the date the play was logged with. Editing or clearing `watched_at` leaves it, and every "already in the journal?" check uses it, so an edited date never makes a sync add the play again.</li></ul> A unique index on (`profile_id`, `play_session_id`) backs the auto-log dedupe. |
| `ratings` | Per item (series, season or episode), 1-10. The diary shows the episode's rating; there is no per-watch rating yet. |
| `statuses` | Placeholder for Watching / Completed / Paused / Dropped / Watchlist (increment 2). |
| `sync_state` | Per journal: last sync start, backfill time, whether Playback Reporting was readable, the last error. |
| `pending_actions` | Writes owed to Jellyfin (`mark_played` with its date and entry). Dropped after 20 failed attempts. |
| `settings` | `auto_log` (1), `rating_prompt` (Off), `mirror_rating` (0); shared by every journal on this PC. |

- The file is checked at open with `PRAGMA quick_check`, which reads every page: the header alone says nothing about the
  pages behind it. A damaged file, or one that is not a database, is kept as `journal.db.corrupt-<time>` and a new one
  starts.
- Damage found later (a query fails with SQLITE_CORRUPT/NOTADB) closes the file and shows "damaged" on the Diary. The next
  use checks it again, keeps it aside and starts a new journal.
- Every SQLite failure reaches the pages as `JournalUnavailableException`, never a raw `SqliteException`.
- A file with a newer schema is left untouched, and the journal is off: no entries, no rating panel, an error on the Diary.
- The app always runs on.
- The check and the insert are one transaction: for the auto-logger (`AddPlayedSession`) and for each sync page
  (`AddSyncedEntries`). A sync and the auto-logger can't both add the same play.

## Auto-log (decision 3)

`JournalAutoLogger` subscribes to `IMpvBackend.Events` and only listens. It sends nothing to the backend and never
reports playback: the backend stays the one reporter. The event handler only records and queues; the work runs one item
at a time on the thread pool. `PlaySessionTracker` follows each play session through these events (docs/BRIDGE.md §5):

| Event | What the tracker does |
|---|---|
| `state` | A session opens at its first active state. The "before" user data is read then. |
| `item` | Keeps the type, series, S/E and runtime. |
| `position` | Watched time is the sum of steps of 10 s or less; seeks don't count. Positions after the item's `ended` are ignored. |
| `ended` | Keeps the reason and the time. |
| `reported` | Closes the session. |
| `hello` | A new backend process: everything is forgotten. |

On `reported` (`AutoLogRules`):

1. **`sent`**: the server took the Stopped report, so its user data is read again. The play is logged when `Played` is
   true now and either:
   - `Played` flipped since the session opened, or
   - `PlayCount` rose **and** the session ended at the end (`eof`, or the last position at 90 % or more).

   Jellyfin raises `PlayCount` when a play *starts* and resets `Played` for videos (per the server's
   `SessionManager.OnPlaybackStart`, from memory of its source; to verify, below). So a risen count alone would log an
   abandoned rewatch. The session's own end decides instead when:
   - the "before" read failed,
   - the server does not answer, or
   - FluentFin has since switched to another server or user. The entry then goes to the journal the session was played
     in, and that server is not asked.
2. **`failed`**: logged locally when the session ended at the end. The entry shows "not on server yet" and a
   `mark_played` with that date is queued. Queued writes are sent after the next `sent` and after every sync; a server
   that already has the play is not told twice.
3. **Short plays are ignored**: less than min(3 min, half the runtime) actually watched. The exception is a session
   that resumed past the middle, i.e. finishing an earlier play.
4. `none`, unknown outcomes, movies, and items whose type nobody knows: nothing.

Each logged entry gets:
- **Date:** the session's `ended` time (`date_source = session`).
- **Rewatch:** when the episode is already in the diary, or Jellyfin had it as played before.
- **Dedupe**, deleted entries included:
  - by play session id;
  - a track or quality change restarts the stream on a new play session (the old one gets `failed`). An auto-logged
    entry of the item dated at or after this session opened is the same watch, so a change at 95 % is not logged twice
    or as a rewatch.
  - a synced entry of the item dated no earlier than 10 minutes before this session opened: a sync logged this play
    from the server first.

Robustness:
- Server answers count only while FluentFin is still signed in as the journal's user when they arrive.
- A server that throws, such as HttpClient's own timeout (a `TaskCanceledException` nobody asked for), counts as "did not
  answer". `JellyfinClient_Journal` catches it too.

The rules for the crash case and for other players:
- A backend that crashes never sends `reported`. Its play is picked up by the next catch-up, if the server counted it
  (FluentFin's own crash Stopped report, docs/BRIDGE.md §6).
- Plays in FluentFin's other players (not mpv) are not auto-logged; the catch-up gets them too.

## Sync (decision 2)

Each sync asks `GET /Items` for played episodes, `SortBy=DatePlayed` descending, 200 per page, with the user data.

- **First sync of a journal (backfill):** one entry per played episode.
  - Date: `LastPlayedDate` (`last_played`, "approximate": Jellyfin keeps only the last play). No date at all gives
    "Date unknown".
  - Never marked rewatch.
- **Later syncs (catch-up):**
  - Reads down to the last sync start minus a day of overlap, for clock differences.
  - Skips an episode that already has an entry (deleted ones too) logged at or after **the date this sync would write**,
    minus 12 h: the auto-logged play, or one synced before. That date is the Playback Reporting date when there is one, so
    marking a series played elsewhere does not duplicate plays the plugin dated.
  - A new play of an episode already in the diary is a rewatch.
- **Playback Reporting (when readable):**
  - Request: `POST /user_usage_stats/submit_custom_query`, with
    `SELECT DateCreated, ItemId, PlayDuration FROM PlaybackActivity WHERE <user> AND ItemType='Episode'`.
  - The episode's latest play gives the exact date: its start plus its duration (`playback_reporting`).
  - The plugin stores the server's local time with no zone. Its source (read 2026-11-15) does
    `PlaybackInfo(date: DateTime.Now)` and writes a Local `DateTime` as `yyyy-MM-dd HH:mm:ss.FFFFFFF`.
  - So times are read as this PC's local time (a home server is usually in the same zone).
  - The sync then compares them with Jellyfin's own UTC `LastPlayedDate` of the same plays. Jellyfin sets it when the play
    starts, and the plugin records it then too.
  - When the median difference over at least 3 plays (within 14 h) is 30 minutes or more, the server runs in another
    zone: every plugin date is moved by that, to the quarter hour. A zone with different daylight-saving dates can still
    be an hour off for part of the year.
- **Failure:** a failed page, or a server that throws, keeps the last sync time, so the next sync covers the same ground.
- **Account switch:** every answer is checked against who is signed in once it arrives. After a switch the sync stops, writes
  nothing more and does not save its state; pages read before the switch stay. The queued writes stop the same way.
- **Threads:** the sync runs on the thread pool whoever starts it (the Sync button), a page (200 episodes) per transaction.
  Rating clicks and settings switches are written off the UI thread too.
- **Concurrency:** one sync at a time.

## UI

- **Diary** (main pane, after Libraries):
  - Header: whose journal ("alice on Home"); Sync; Export (CSV diary, JSON everything); Settings flyout (auto-log on/off,
    "Send my star ratings to Jellyfin", off).
  - Year and show filters, an entry count, and the sync status (where dates come from).
  - Month groups, newest first; "Date unknown" last.
  - Each entry: day and weekday, the episode still (or the series poster), series, `S02 E05 · Title`, the date ("about"
    when approximate, with where it came from on hover), review excerpt, rewatch icon, "not on server yet" icon, stars.
  - Click an entry to open its episode page. "…" offers Edit (date and time, rating, rewatch, review, spoiler) and Delete.
  - Deleting does not unwatch in Jellyfin, and a sync does not bring the entry back.
- **"Your rating"** on the episode and series pages: half-stars, saved at once.
  - Pointer: hover previews, click sets, clicking the same value clears.
  - Keys: arrows move a half star, Home/Delete clear, End gives five stars.
  - Screen readers see a slider, 0-5 stars in steps of a half.
  - Hidden for movies, when nobody is signed in, and when the journal is off.

## Export

- **JSON** (`format: fluentfin-mpv-journal`, `version: 1`):
  - The journal (server and user ids and names).
  - Every entry (`watchedAt` UTC, `dateApproximate`, `dateSource`, `source`, `rewatch`, the episode's `rating` 1-10,
    `review`, `spoiler`, `onServer`).
  - Every rating (`kind`, `rating` 1-10, `stars`).
- **CSV:** RFC 4180, UTF-8 with BOM (for spreadsheets). Columns: `watched_at_utc`, `watched_date_local`, `date_approximate`,
  `date_source`, `series`, `season`, `episode`, `title`, `rating_stars`, `rating_10`, `rewatch`, `review`, `spoiler`,
  `source`, `on_server`, `item_id`, `series_id`. Free text starting with `= + - @` gets a leading `'` (formula injection).
- Deleted entries are not exported.

## Token and logs

- The journal's requests go through FluentFin's own client (the SDK puts the token in its header).
- The Playback Reporting request sends `Authorization: MediaBrowser Token="…"`, never `api_key` in the url.
- Log lines carry item ids, play session ids and HTTP status codes; never urls with tokens, bodies or exception text from
  the HTTP layer.
- Tested in `JellyfinClientJournalTests` (fake server on localhost, fake token).

## Not verified (needs the user's real server: the 0.5-day spike in the research)

1. Does Jellyfin raise `PlayCount` and reset `Played` at playback start? The rules are safe either way; this only
   decides how often the session's own end is what counts.
2. Does `POST /UserItems/{id}/UserData` with `Rating` stick on the user's server? That is why mirroring defaults off.
3. Playback Reporting against the user's server.
   - Settled from the plugin's source (master, read 2026-11-15):
     - the endpoint and its `colums` / `results` / `message` answer;
     - `RequiresElevation` (administrators only; a 403 is handled quietly);
     - `DateCreated` is server-local time with no zone (handled above).
   - Not tried yet: whether the user has the plugin and is an administrator, and the clock-shift check on real data.
4. How many of the user's series lack a TMDB id (matters for the TMDB layer).

## Visual check still to do

Not run: the user is using FluentFin, and the dev-harness's off-screen run had not landed when this was written.
The star glyphs were checked by drawing them (E734 outline, E735 full, E7C6 left half) with Segoe Fluent Icons.

With the dev-harness (`--data-dir` + `tools/ui-harness/fakeserver`), off-screen, one screenshot per item:
1. The Diary entry is in the main pane under Libraries, with the calendar icon.
2. On an empty journal: the empty text and "Not synced with Jellyfin yet". After Sync, entries are grouped by month and
   approximate dates read "about …".
3. Filters by year and show. The settings flyout. Edit (date/time pickers, stars, review) saves, and the rating shows in
   the list. Delete asks first.
4. Episode and series pages: "Your rating" sits after the runtime / community rating, readable on the dark backdrop, and
   hover previews half stars.
5. Export writes both files, and the CSV opens in a spreadsheet with accents intact.
6. Play an episode to the end with the mpv player against the fake server: an entry appears while the Diary is open.

## Shared files touched (for the merge)

- `ui/FluentFin.Core/FluentFin.Core.csproj`: one `PackageReference` (Microsoft.Data.Sqlite).
- `ui/FluentFin/Views/EpisodePage.xaml` and `SeriesPage.xaml`: one `xmlns:journal` and one `<journal:JournalRatingPanel>` each, in the info row.
- Nothing else. The plugin is found by App.xaml.cs's existing scan of the FluentFin.Plugins assembly. Keep that scan
  if App.xaml.cs changes (rename, startup).
- `wip/signout-ux`: the journal follows `SessionInfo`. Clearing it on sign-out is fine; a new sign-in rebuilds the pane
  and syncs that user's journal.
- `wip/player-controls`, `wip/startup-perf`: the journal only subscribes to `IMpvBackend.Events`, and it starts with the
  main pane, not at app start.

## Next

- **TMDB layer** (increment 2):
  - `ITmdbClient` beside `IJournalServer`, with the token DPAPI-encrypted and never logged.
  - A `tmdb_cache` table (schema 2), pruned before 6 months.
  - `Fields=ProviderIds` on the item reads, for the series' TMDB id.
  - Uses: full episode progress ("X of Y"), statuses, season ratings on the season page, reviews, TMDB attribution on
    the About page.
- **Stats and Year in review:** hours, episodes, shows completed, genres, top rated, a monthly chart.
- **Watchlist, lists, Airing soon** (TVmaze or TMDB), and the Jellyseerr request hook.
- **Imports** (Trakt JSON, TV Time CSV, Serializd exporters) with the mapping-review screen.
- **Rating flyout** after an episode (the setting is stored), "Log" / "Log season" buttons, manual entries.
- **Movies** (Letterboxd-style).
- Logging a crashed backend's last play directly, and auto-logging FluentFin's other players.
- Open question for the user: should `journal.db` join the Copy Guardian backup tiers?
