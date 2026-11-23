# Known issues at the integration baseline

Recorded at the baseline commit (2026-11-14), after seven review/fix rounds on each side.
The final pre-commit reviews found nothing of medium severity or above left open; everything
below is low severity, an accepted cost of the contract, or not verified yet. Each entry
says where it lives so it can be picked up later. Entries marked "fixed" say what is left.

## Fix queue (next fix release)
For 1.7.4 now: 1.7.3 took Made by Nixie's four P0s, 1.7.1's queue, the rule 11 sweep and the rename's code constants
(its section), so 1.7.0's queue below waits for 1.7.4, with what 1.7.3 left (just below).
1.6.1 took what it could of the queue it was given, and what is left of it stays below, after 1.7.0's own.

From 1.7.3 (its streams, their reviews and the integration, 2026-11-22; no Blocker, and M1's one Major was fixed before
the build):
- **BH-171-m5** (1.7.1's queue; not taken in 1.7.3): a library that changes every day (most big servers) still means a
  titles read about once a start. Read only what was added since the saved newest (`MinDateCreated`) instead.
- Stream I's review (Minors):
  - Played episodes that keep failing hold back a library change's read. `LibraryIndex.cs:365-373` picks the plays-only
    read before the check, and `CheckOnce` (`:450`) counts it as "read lately", so a change the check confirmed waits for
    the day's read, up to 24 h; 1.7.2 read it within the hour. The reviewer reproduced it. Fix: take the plays read only
    when no check is due.
  - The reading line's "about" total can be well off. The check counts each title once across all libraries, but the
    per-library total counts a shared title in each (`IndexPages.cs:303`, `IndexRead.cs:63`), so the line reads "of about
    34,006", then "of 37,173". A skipped mixed library inflates it too. Fix: say "titles" while the total is only "about".
  - A failed upgrade read isn't retried until a restart: `LibraryIndex.cs:502-505` clears the old-format flag when the
    whole read starts, not when it finishes, so 1.7.2's entries (no `LibraryIds`) stay until a restart past the hour or
    the day's read. (Since the 1.7.3 fix round the first start after the update reads at once whatever 1.7.2's note
    says, and Acclaimed comes from the old index meanwhile; a restart within the hour of a failed 1.7.3 read still waits.)
  - Later, not a fault: `LibraryIds` costs about 1.8 MB on 34,000 titles; writing it only for titles in two or more
    libraries would save that.
- Stream B's review (Minors):
  - `LoadState.cs:258` counts a `JsonException` (a captive portal or proxy page answering 200 with HTML) and Kiota's
    `InvalidOperationException` for a text/html content type as Nixie's own fault: the user sees "Nixie couldn't show this
    page… restart Nixie", though it's the connection. The reach is small, since `JellyfinClient` swallows most of these.
    Fix: classify them as the connection.
  - The 5xx copy rarely shows: `JellyfinClient` returns null on a 5xx and most pages swallow the error, so a 5xx there
    still gets the connection wording. Only a throw that reaches `LoadState.RunAsync` gets the server's or Nixie's words.
  - `tools\release\live-release-check.ps1:41` accepts 200 for an unknown API version silently. Print a warning when it
    happens.
- Stream M1's review (its Major and two Minors were fixed before the build: the 1.7.3 section):
  - `Find` collapses the whole index on every call (`SmartCollectionCatalog.cs:99`): about 170 ms cold per pin on 36,000
    entries, since `PinnedOnDemand` calls it once per pin. Build the collapsed titles once per batch.
- Stream M2's review (Minors):
  - `CollectionSettings.cs:207-210`: the "N removed" count can be logged twice, and the file written once per pin. Home's
    background call (`HomeViewModel.Nixie.cs:109`) and the Collections page's (`CollectionsViewModel.cs:468`) can run
    together on a first visit, and `gone++` counts a pin already gone. Nothing is lost. Fix: `SetFavorite` returns a
    bool, or one locked store call removes them all.
  - `NixiePresets.cs:214, 275-297`: the presets strip builds its decade, genre-mix and country collections itself,
    skipping M1's gate counter and copying `BuildAll`'s catch. Use `SmartCollectionCatalog.BuildKinds(own, now,
    {Decade, GenreMix, Country})`.
- Found at the integration:
  - `IndexOnABigServerTests.AFirstReadThatStopped_SaysSo_UnderTheRowsItMade` failed run alone at 1.7.2's `2406079b` too:
    a read's end doesn't wait for the pending show of its pages' rows, so "Nixie couldn't finish reading…" can show for a
    moment with no rows over it. The test now waits for the rows; awaiting the pending show before `IsReadStopped` in
    `CollectionsViewModel.ReadIndex` would close it in the page.
  - The README's Collections screenshot (`docs\screenshots\collections.webp`, its alt text "such as Short films") shows
    1.7.2's collections: retake it.
  - `ui\FluentFin.Core\ViewModels\CollectionViewModel.Nixie.cs:220` (Save as playlist refused with 403) says "Ask its
    administrator.": right to name the server's side, but the app says "the server's owner" elsewhere.
  - `tools\release\upgrade-test.ps1:105` downloads Seaglass 1.1.3 through the old repository name (GitHub redirects it).

From the 1.7.0 bug hunt (`docs\reports\Bug hunt 2026-11-22 (1.7.0).md`; no Blocker; its Major BH-170-M1 and q1, q4 were
fixed before the build):
- **BH-170-q2** `tools\live\live_trending_sources.ps1:55`: Trakt's live check builds its own `HttpWebRequest`, not the
  app's request (`OutsideSource.Request`: an empty body with `Content-Length: 0` and `Content-Type: application/json`,
  gzip only, redirects followed by hand). The app's own GET has never met real Trakt; give the Trakt and TMDb legs a
  `LiveTrending` test category through `OutsideHub`, as stream K's live check does.
- **BH-170-q3** `ui\FluentFin.Core\Outside\TrendingSources.cs:218-223`: `IsActive` is checked, then the list is written
  to the cache and held; a switch turned off between the two (Forget, on the UI thread) leaves that list on disk and held
  until its maximum age (Trakt a day, TMDb trending 7 days), shown again at once if the source is turned back on.
  PRIVACY.md says turning them off deletes their lists. A window of milliseconds, reasoned only; fix with a per-source
  generation that Forget bumps (or the write and Forget under one lock).
- **BH-170-q5** `ui\FluentFin.Core\Outside\OutsideLists.cs` (`Same`): compares only the counts of Wikidata's lists and
  of TMDb's keyword names, so a refresh with as many titles but other ones isn't published until a restart. Compare the
  items (or a hash of each cached list).

From the repository polish (2026-11-22, seen while taking 1.7.0's screenshots):
- Drive-in: Home's Up next board counts what it doesn't show. With Next Up empty or short but Continue Watching
  not, the board's link reads "All of Up next (3)" over fewer cards (`docs\screenshots\home-drive-in.webp` shows one).
  The count and the cards must come from the same list. Add a test with Next Up empty and Continue Watching full.
  (1.7.3's stream C takes it; it merges after this integration.)

From the 1.7 streams and their reviews (2026-11-21; each fixed what its review sent back before the merge):
- Drive-in: `ui\FluentFin\Themes\DriveIn\Controls\ItemStage.cs:104`: under 1400 px wide the item board's 20 px top margin
  puts its top row of lights under the 48 px top bar (seen at 1100x900). Found by stream U.
- Trakt: `ui\FluentFin.Core\Outside\OutsideSource.cs:347-353` puts Trakt's required `Content-Type: application/json` on
  an empty body, the only way .NET sends that header on a GET. The live check built its own requests (BH-170-q2 above),
  so that GET has not met real Trakt yet, and a proxy that refuses a GET with a body would refuse it. Found by stream T.
- Trakt's box office is asked again daily, though it changes once a week: Trakt's lists are kept at most a day
  (`ui\FluentFin.Core\Outside\TraktLists.cs`, `FreshUntil`, its Use Policy), so it is one request a day while Trakt is on.
  Found by stream T; not a fault, noted so nobody "fixes" it into a week-old list.
- Wikidata's lists have few Golden Globe, BAFTA and César winners (Wikidata's own gaps); the Awards row shows what it has.
  Found by stream K's live check.

From the 1.6.1 bug hunt (`docs\reports\Bug hunt 2026-11-21 (1.6.1).md`; no Blocker, no Major):
- **BH-161-q1** `ui\FluentFin.Core\Services\JellyfinClient_Search.cs:165-167`, `ui\FluentFin.Core\ViewModels\PersonItemsViewModel.cs:105`:
  "With <name>" asks for at most 200 episodes, so a person's own long-running show can fill the cap and their older
  guest roles disappear, with no hint that some were left out.
- **BH-161-q2** `ui\FluentFin.Core\InstallDeviceId.cs:156`: a `device-id.json` with a duplicate key throws; `Current`'s
  fallback then skips `ClaimWindow`, so two windows share one id again, and the file is never repaired (only a
  hand-edited or oddly broken file; every other bad file is handled).
- **BH-161-q3** `ui\FluentFin.Core\ViewModels\PersonItemsViewModel.cs:131`: once the cap is hit, every guest show reads
  "in N+ episodes", even shows whose episodes all arrived, not only the oldest.
- **BH-161-q4 (nit)** `backend\jellyfin_mpv_shim\skip_analyse.py:77`: the DLL search flags go in as `mode`, which
  Windows ignores; it works only because Python's default flags match.

Found by the 1.6.1 review (stream C, 2026-11-21):
- `ui\FluentFin.Core\InstallDeviceId.cs` (`ClaimWindow`): a window opened while the copy's last one is still closing (Restart
  to update reopening Nixie before the old process has ended, or a close and a quick start) finds the first slot still held
  and signs in as the copy's second window: a device of its own on the server, which a per-device allow list refuses. The
  next start is the first window again.

Found by the 1.5 reviews (2026-11-20); not fixed in 1.5, 1.5.1 or 1.6.1.
- **Home's row names are not in sentence case** (button review L6): "Continue Watching" and "Next Up" style names where the
  rest of the app says "Continue watching". Held back in 1.5 because the harness finds rows by name (`then-search.ps1`,
  `drive.ps1`'s Home step, the keys matrix): rename the rows and the scripts' names together. Left out of 1.5.1 and 1.6.1
  too: 61 names in 16 harness scripts and the keys matrix in four themes, more than a fix release's hour.

From the 1.5.1 bug hunt (`docs\reports\Bug hunt 2026-11-21 (1.5.1).md`):
- **BH-151-m4** `backend\jellyfin_mpv_shim\intro_sources.py:258` (`_save`): a 1.5.0 backend saving learned-intros.json
  drops 1.5.1's "moves" marks (a 1.5.0 portable copy beside 1.5.1, or a downgrade).
- **BH-151-m8 (the rest)** `backend\jellyfin_mpv_shim\player_reporting.py:432` (`send_timeline`): the sign-in ending
  mid-playback refuses the progress and Stopped reports. 1.6.1 says so in the player (Sign in again), but that
  playback's place and watched state are still lost: after Sign in again, the app could send the last position it saw
  (`RecentPlays` has it); a new server call, with its own live check.

Left out of 1.5.1, 1.6.1 and 1.7.0 (a feature):
- **Music in search, with playback** (BH-151-M2): songs and albums were taken out of search because nothing plays them.

Found at the 1.6 integration:
- `ui\FluentFin\Controls\UpdateSettingsPanel.xaml.cs` (Settings' Check now), then-update's full flow in Classic: after the
  release notes closed (Esc on "Open the release page"), the run's Check now started no check (no "[update] checked"
  line), so its checksum-mismatch step could not run. Not re-checked: 1.6.1 makes the notes start on Close (the part of
  this line it fixed), and the 1.6.0 bug hunt found Esc closes them from every stop. At 1.6.1's final pass the full
  flow passed in Classic (40/40) and in Drive-in (40/40); the Restart to update flow in Drive-in failed this step once
  under load (the notes closed, then Check now made no check for about two minutes; the next step's Check now worked)
  and passed it on its rerun (31/31). So it is intermittent, not gone: watch it in the next full-flow runs.

From the 1.6.1 streams (2026-11-21; each fixed what its review sent back before the merge):
- Skipping: `backend\jellyfin_mpv_shim\bridge_skip.py` (`refresh_rules`): a rule that took over the server's Intro or
  Outro becomes "Commercial" ("Skip extra") after any Add, Yes or Forget mid-episode (the server's segment it replaced
  is gone from the list), and forgetting such a rule mid-episode doesn't bring the server's intro back until the next
  item.
- Sign-in ended: `ui\FluentFin.Core\Admin\ServerPower.cs:54` and the library scan and delete notices have no words for
  an ended sign-in yet, and the played and favourite toggles spring back with nothing said.
- Two windows of one copy (each its own device id): unit tests only (the window slots); no harness run starts a second
  window.
- `ui\FluentFin\Views\CollectionsPage.xaml.cs:41-56`: the Libraries page's Collections tile sometimes opens the
  Collections page with the keyboard left on Back (one "could not take", no retry; Cinematic, and Drive-in at 1920 and
  1100).
- ShelfRow (`ItemsRepeater`): the keyboard on a tile jumps to the page's first focusable control when its row scrolls
  out of view (seen on the Collections page in Classic and Editorial).
- Save as playlist (also review A): another user's public playlist with the same name reads "Already in your
  playlists": the check for an earlier save goes by name.

## Hand checks, recorded
From 1.5, one line per check and release: pass, fail, or not run. They need a real desktop and real hands (off-screen
runs post keys and cannot move the real cursor or hear sound).
- **1.5.0**
  - Space before any click in the player, then move the mouse (the controls must come back): not run, for the owner.
  - Esc inside YouTube's player in the trailer theatre (the theatre must close): not run, for the owner.
  - One real trailer plays in the theatre, with sound: not run, for the owner.
  - Restart to update on a real install: not run on the owner's own install; the Sandbox upgrade test below covers it
    off the owner's account.
  - The Sandbox upgrade test (`upgrade-test-sandbox.ps1`): **passed on 2026-11-21, after the release** (1.5.0 shipped
    first, owner: "Publish now"; the first two tries failed on the test script and a stuck Sandbox). As a standard user
    at medium integrity:
    - leg 1: 1.4.1 installs, copies the Seaglass folder at its first start; 1.5.0 installs over it by the old flow,
      reopens nothing, keeps the data, leaves the Seaglass folder alone, and starts;
    - leg 2: Restart to update closes Nixie, the helper installs 1.5.90 quietly and opens Nixie once, without the
      switch, and the reopened Nixie logs the result (installer exit 0).

    Its three failures were the test's own, fixed on main: a start with no server logs nothing (the leg 1 log check
    is gone; leg 2's line proves the log), Windows PowerShell 5.1 threw on `@($checks)` so no results.json was written
    and the Sandbox didn't shut down, and the tester's exit code read empty. The test build is 1.5.0's files in an
    installer numbered 1.5.90, so the reopened app logs "None", not "Updated": the "Updated" notice is first seen for
    real on 1.5.0 to 1.5.1.
- **1.5.1**
  - The Sandbox upgrade test with its new leg 3 (1.5.0's own Restart to update to 1.5.1, the "Updated" line): **passed
    on 2026-11-21** (every check, as a standard user; `docs\ROADMAP.md`).
  - Sign in again on a real server after signing Nixie out there (Dashboard > Devices): not run, for the owner (the QA
    servers passed it off-screen: `then-signin.ps1` on 10.11.11 and 12.4.0).

- **1.6.0**
  - A skip rule on a real show with extras (Shift+S over a bumper, then the next episode offers it): not run, for the
    owner.
  - The Playback stats panel over real video (the numbers move, Copy has no token): not run, for the owner.
  - A quality change on the owner's server: never tested by us (the server isn't ours); not run, for the owner. The QA
    servers' quality run covers the code path.
  - The Sandbox upgrade test (1.5.1 to 1.6.0, its three legs): **passed on 2026-11-21, after the release**, on its
    second run (the first run's leg 3 failed on the test's own fault, fixed in `f71865a`; `docs\ROADMAP.md`).

- **1.6.1**
  - A skip rule snapping on a real show (Shift+S over a bumper, then the next episode's skip lands on the cut, with
    the package's own player and no separate mpv): not run, for the owner.
  - The device id kept across the owner's real upgrade (1.6.0 to 1.6.1: the saved sign-in still works, and the
    server's Devices page lists Nixie once, not twice): not run, for the owner.
  - Sign in again mid-playback on the owner's server: never tested by us (the server isn't ours, and ending a sign-in
    there is the owner's to do); not run, for the owner.

## Nixie 1.7.3: Made by Nixie, fewer and better
- **Merged** (2026-11-22, `wip/rel-1.7.3` from main's `fb5e2daf`): streams I (index), B (copy and rename), M1 (builders)
  and M2 (surfaces), with no textual conflicts; stream C (UI) merged after them (`eda146e5`): the close crash from the player page (since 1.6.1, `0xc000027b` after a NullReferenceException in `TitleBarControl.ApplyCaptionHeight`; Windows Error Reporting made the close 3 to 20 s), the Collections arrival focus, the reading line's space, Drive-in's Up next, the library art's crop. At the integration M2's stand-in for M1's API
  went, there is one list of the seven kinds no longer published (`SmartCollectionCatalog.OnDemandOnly`; M2's
  `SmartCollectionKinds.Retired` went), and the tests that crossed streams were brought up to date (below).
- **"Acclaimed, not seen yet" on a server with few critics' ratings and tags** (M1's review: a title needs a critics'
  rating, five or more tags, or an outside list to be acclaimed). Since the fix round, an index with none of these
  anywhere ranks by the rating alone; one title with them makes the rule apply, so Acclaimed can be short there. The
  census line says which way it was made, with the counts; the owner's server is the first real check.
- **Fixed before the release build, from the 1.7.3 bug hunt** (`docs\reports\Bug hunt 2026-11-23 (1.7.3).md`; two Majors in
  new code, and the owner asked for every Minor and nit too), each with a test that fails with its fix reverted:
  - BH-173-M1: after the update "Acclaimed, not seen yet" went from Home and Collections, possibly for an hour. 1.7.2's
    index keeps no critics' ratings or tag counts, so nothing was backed; and its read again waited out 1.7.2's hourly
    limit. An index saved before 1.7.3 (`IndexSnapshot.BackingKnown`), or one with nothing that can back a rating
    (`RatingBacking`, BH-173-m1), ranks Acclaimed by the rating alone, with the copies and outside lists as confidence;
    the old format wins over a note of a read an older version began (`Reads.Revision`), so the first start after the
    update reads again at once.
  - BH-173-M2: saved filters on "Highly rated" or "Short films" kept at most 20 titles and changed meaning ("Comedy +
    Short films": 100 titles, then 0), and the empty page said the titles had left the server. A saved filter's preset
    has every title of its rule (`BuildContext.Whole`, `SavedFilterPresets`); one saved before 1.7.3 (`SavedFilter.Revision`
    0) keeps 1.7.2's two rules; an empty saved filter says "No titles in <library> match this filter now".
  - BH-173-m1: on a server with no tags or critics' ratings Acclaimed never came (above), the census line now says the
    mode and the backing counts, and a Collections page with no Made by Nixie collection says so in its line, with what
    Nixie looks for (or that every kind is turned off).
  - BH-173-m2, q4: `CollectionsKindsOff` is no longer rewritten at start. Decades, genre mixes and countries turned off
    stay off in the library strip, with their switches back (Settings > Collections > Made by Nixie filters in
    libraries); directors, actors, studios and networks turned off stay off after a return to 1.7.2. The pins and saved
    filters 1.7.3 removed don't come back there (a CHANGELOG line says so).
  - BH-173-m3: two films in five of the fake server have no critics' rating (by id, so the fixture's names stay);
    `FAKESERVER_PLAIN=1` gives none, the throwaway's shape.
  - BH-173-m4: `HarnessPresetSource` lists 1.7.3's kinds of chip, and `then-160-colpage.ps1` ends on its last one.
  - BH-173-q1: Acclaimed's switch says "Titles with more behind their rating come first", not that few raters count less.
  - BH-173-q2: a 403 says the server doesn't let the account open it (its owner can change that), a 404 or 410 is the
    server's (an address it doesn't have: never "removed", the re-review), a 400 is Nixie's own; none says "Check this
    PC's connection".
  - BH-173-q3: an index entry's lists read `null` from a damaged file are empty (`InLibrary` and the load's `Once` threw).
- **The close crash, its second trigger** (the coordinator, from verify-package's NoMpv run of the first build: APPCRASH
  `0xc000027b` in `CoreMessagingXP.dll`, a stowed `E_UNEXPECTED` from `combase.dll`, 5.7 s to exit; stream C's review saw
  it on any close from the player page on a busy PC, 4 of 4): what ran in the window's teardown read XAML there, which
  answers `E_UNEXPECTED`, and the exception out of a dispatcher callback ended the app. Found with the first-chance trail
  kept alive through the close (it asked `Application.Current` for its logger, which the teardown no longer answers): the
  Drive-in player's dressing retry (`DriveInPlayer.DressBar`, a 250 ms timer for 10 s while mpv's own controls, or no
  mpv, leave its controls null), then the title bar's queued relayout once the player page gave the chrome back
  (`TitleBarControl.Relayout`). Fixed: `UiDispatch.Tick` (a tick that does nothing once `App.IsClosing` and never throws),
  the player page's `OnUnloaded` and property handlers, the video host's resync, the controls' heartbeat, the title bar's
  layout methods, `TrailerTheater.CaptionBack`, and every other timer tick of the app (a no-op once closing; the
  re-review found Drive-in Home's hero, `OutdoorScreen`, missed, and `Ui173Tests` now scans all 28 `Tick` handlers). Checked off-screen: no mpv 0.2 s exit 0 (3.2 to 4.1 s, `0xC000027B` before), and three closes at once
  (no mpv, mpv's controls playing, Nixie's controls playing) 0.2 to 0.5 s, exit 0. `drive.ps1`'s Step-Close and the
  self-test now check exit code 0, so verify-package's runs fail on a crash that ends within the wait.
- **The fix round's re-review** (one Major, five Minors; each fixed with a test that fails with its fix reverted):
  - BH-173-M2's other half: a library's Nixie filters (the strip) kept the 20-title cap while a saved filter didn't
    ("Under 90 minutes" + Comedy: 0 titles in the library, 80 once saved). The strip builds with `whole: true`.
  - Drive-in Home's hero timer had no closing check (above); `then-173-close-home.ps1` closes from Drive-in's Home just
    before the hero's next advance.
  - The note of the last read was stamped with 1.7.3's revision when a read began, so an upgrade read that failed or was
    closed part way held 1.7.2's index for the hour, across restarts: it is stamped when the read is in.
  - 1.7.2 rewrites the saved filters' file with the fields it knows, so a 1.7.3 filter's `Revision` went at a 1.7.2 save:
    a new filter's id says its revision too ("saved:r3-…"), which 1.7.2 keeps.
  - A 404 said "This isn't on the server any more" for any address the server lacks: the server's words now.
  - Privacy: the host's default Event Log provider sent Warning and Error lines to the Windows Application log, past the
    token masking (none was found there): the default providers are cleared (`NixieLogging`).
- **Left (seen in the fix round, not taken):**
  - `CompositionTarget.Rendering` handlers outside the player page (Home's heroes) aren't guarded at the close; none was
    seen to fire there.
  - The Collections page's arrival focus missed once under load (Drive-in, `then-160-collections`, 1 of 2 runs with three
    runs at once; the rerun passed): opened from the Libraries page's Collections tile, the first try logged "could not
    take the keyboard" and no retry was logged after it, so the keyboard stayed on Back. Stream C's retry
    (`CollectionsPage.TakeArrivalFocus`) gives up without a line when `NextArrivalTry` says None or the user is taken to
    have acted; say which in the log first.
- **Fixed in 1.7.3** (the owner on 1.7.2's Made by Nixie: "too many, too generic"; the four P0s of the local report
  `docs\reports\Useful Made by Nixie collections.md`):
  - P0-1: one census line per whole index, each kind's candidates, then past its floor, then past its cap (counts only).
  - P0-2: a title in two libraries counts in both (`IndexEntry.LibraryIds`, `InLibrary`: a library's presets and a saved
    filter's preset items). Copies count once (`CanonicalTitles`: TMDb, IMDb, TheTVDB, then name and year for titles
    with no ids), so two files of one film no longer make a franchise.
  - P0-3: Nixie publishes franchises, keywords, awards and the picks only. Decades, directors, actors, studios, networks,
    genre mixes and countries are made on demand (`Find`, `BuildKinds`). The presets strip keeps decades, genre mixes and
    countries; pins and saved filters of the other four are removed once at start (the owner's answer 2); Home's saved
    rows of the seven are never painted; their Settings switches went (the strip's three came back in the fix round, and
    `CollectionsKindsOff` keeps every name).
  - P0-4: "Acclaimed, not seen yet" (a score shrunk toward the mean by how much backs each rating; the best 20
    unwatched) and "Under 90 minutes" (unwatched films of 60 to 90 minutes, best first, 20). The ids stay, so settings,
    pins and Home's saved page carry over.
  - 1.7.2's review Minors: a trending failure no longer takes Made by Nixie with it (the page and Home), and the page's
    load failure is logged with its own cause.
- **Fixed in 1.7.3, from 1.7.1's queue** (that section): m3 (no people pass at all, the owner's answer 1, so a read asks
  the server for about half as much), q1 (the reading line's "about" total from the first page), M1's other half
  (`PlaysDue`: played episodes that didn't come are asked again, at most hourly), and a title in two libraries (P0-2).
  The saved index has `"Revision": 3`: an older one shows at once and is read again once, behind it.
- **The rule 11 sweep** (stream B and the integration): "the server", never "your server", in the app, the README,
  FEATURES.md, PRIVACY.md, the package README, the installer and the issue forms. Nothing says "Check that the server is
  running" or sends the user to the server's dashboard (the sign-in page's and Settings' Quick Connect messages, the bug
  form's server version); `ServerWordsTests` scans all of them. `LoadState.RunAsync` words a failure by whose it is:
  this PC's connection (HTTP errors, timeouts), the server (5xx, naming its owner) or Nixie.
- **The rename's constants:** `mbustos27/nixie` in the update check, Settings > About, the installer's links and the
  User-Agent. 1.7.2 and older update through GitHub's redirect (the live release check passed against it).
- **Fixed before the build, from the streams' reviews** (no Blocker; one Major), each with a test that fails with its fix
  reverted:
  - M1's Major: low-confidence titles led "Acclaimed, not seen yet". Artwork counted a whole point of confidence and
    nearly every rated title has a poster, so with no critics' ratings an obscure 9.8 with two tags outranked an 8.6 with
    24 (the reviewer's probe: all 20 places obscure). Artwork no longer counts, and Acclaimed takes only a title a
    critics' rating, five or more tags or an outside list backs.
  - M1's Minors: placeholder ids (TMDb or TheTVDB 0, an IMDb id that isn't "tt" and digits) merged different films; a
    show in two libraries, watched in one and half-watched in the other, was played with no episodes left (a regression
    from 1.7.2): a show is played only when every copy is, with the most episodes left of its copies.
  - M2's review: a test for a saved filter's preset in a title's second library (`CollectionSources`' `InLibrary`).
  - B's review: the bug form (where Report a problem leads) said "your server's address" and "Jellyfin's Dashboard shows
    it"; `ServerWordsTests` scans the issue forms and PRIVACY.md now.
- **Tests brought up to date at the integration** (each an expectation 1.7.3 made out of date; none hides a failure):
  - `IndexOnABigServerTests.AfterAFirstRead_EveryTileHoldsTheWholeIndexsCollection_InTheRowsThatStayed`: Picks' first
    tile is now Acclaimed, 20 titles early and after, so the tile that grows is the Keywords row's "Heists".
  - `AFirstReadThatStopped_SaysSo_UnderTheRowsItMade`: waits for the rows (a race since 1.7.1; queued above).
  - `SmartCollectionsTests.PresetLabels_AreShort`: the HighlyRated label is "Acclaimed" (and ShortFilms' is checked).
  - `ServerWordsTests`: M2's `CollectionSettings.cs` is no longer skipped.
  - With M1's backing rule: fixtures whose rated titles had no backing get ten tags or a critics' rating (their
    expectation, a Picks row or an Acclaimed collection, is unchanged), and the census test's HighlyRated counts are
    5/0/0 (five rated, none backed).
- **Left for 1.7.4:** the fix queue above (BH-171-m5, the reviews' Minors, the 5xx wording on most pages).
- **Stream C** (the Collections page's arrival focus, m4, Drive-in's Up next count and logo art, the slow exit) merges
  after this integration; its lines go here then.

## Nixie 1.7.2: the Collections page hotfix
- **Fixed** (`wip/172-fix`): on the owner's server 1.7.1's Collections page showed "Couldn't load your collections" at
  once, and Try again kept failing. The saved index, written by 1.7.0 (whose read kept repeats), listed one title twice;
  1.7.1 shows the saved index at once, `GenreMixBuilder` threw on the repeated id (`ToDictionary`), and the page's load
  awaited that build beside the server's sets, so the whole page failed. Home lost its Made by Nixie rows the same way.
  Now the saved index keeps each title once as it is read (logged with a count only), no builder throws on a repeated
  id (the genre mixes; a library's presets on a repeated library), a builder that throws leaves out only its own kind
  (logged once per kind: the kind and the exception's type), and a Made by Nixie that can't be made never fails the
  page: the server's collections and playlists show without it, as 1.7.0 showed them.
- **Copy:** the error card says "{App} couldn't get it from {server} just now. Check this PC's connection, then select
  Try again." (it said "Check that the server is running": the user may not be the server's admin), and the page's first
  section is "The server's collections". The app's other "your server" strings, Settings' "Your change didn't reach …
  Check that the server is running" and the sign-in page's "Can't reach …" are left for the 1.7.3 sweep, as is the
  README's "Your server's collections and playlists". Fixed in 1.7.3 (the sweep).
- **Verified:** `RepeatedTitleTests` (a format 2 and a format 1 saved index with a title twice: the page with the server's
  sets, playlists and Made by Nixie, Home's rows, the title once), each failing with its fix reverted (all three reverted:
  the page's error card, as on the owner's server); the Collections harness off-screen in Classic, 33 checks.
- **The saved file keeps its repeat until the next read writes it** (a day, R, or a change the check confirms); every
  read since 1.7.1 keeps each title once.
- **Other builders still count a title listed twice** where they don't throw (decades, countries, highly rated, short
  films, franchises' "two or more"), on an index built by hand; the app's own indexes no longer have repeats. Fixed in
  1.7.3: every builder counts a title once (`CanonicalTitles`).

## Nixie 1.7.1: the library index on a big server
- **Fixed** (stream I, `wip/171-index`): "Nixie is reading your libraries" took minutes. On the owner's server (this
  PC's 1.6.1 log: 34,001 titles in 23 libraries) the index took 1,198 s, one page after another (pages sized for 2 s
  each, so ~275 titles a request there), and it was read again at every start and after any library change; 1.7.0's
  new index format threw the 1.6 index away. Now the saved index (1.6's too) shows at once and is read again behind it
  only when stale; pages of 1,000 come two at a time; a first read shows rows as its pages come.
- **Not measured on a big remote server.** The numbers are the throwaway 25,736-film instance on this PC (loopback,
  synthetic films with no people, tags or studios): titles 3.0 s and people 5.2 s for a first read. A real server with
  full metadata answers each page much more slowly (the owner's took ~3 s for ~275 titles); there the first read is
  still minutes, but it shows its first rows after the first page, says how far it has got, and happens once.
- **The daily refresh is still the whole library**, titles and people, in the background (two pages at a time). A
  change between two refreshes is read only when the server's check (how many films and shows, and the newest one
  added) differs: an edited tag, a new poster or a title watched on another device waits for the daily refresh or R.
  A change the check confirms reads the titles only (review M2): a film added then has no actors or directors until the
  daily refresh or R. (1.7.3: no read asks for people any more, so the daily refresh is titles and plays.)
- **A people pass closed midway starts again from its first page** (review M2 saved the titles before it, not its own
  pages): on the owner's server (682 s of people in 1.6.1's log) a Nixie never left open that long never finishes it.
  It is tried at most once an hour, counted across restarts; the people the index had are kept meanwhile. (Gone in
  1.7.3: there is no people pass.)
- **A page Nixie gave up on may still run on the server** (review M3): after 45 s the request is cancelled on this PC,
  but Jellyfin may finish it anyway. A page that ran out of time halves the pages after it (never below 250, kept on this
  PC for the next read), and three quick full pages in a row double them again, up to 1,000.
- **The Collections page's first row waits for the server's box sets** (`GetBoxSets`, which asks each box set's
  ChildCount): ~700 ms for 492 box sets on the throwaway instance, likely seconds on a big server. The saved index's
  collections are made meanwhile; keeping the box sets with the saved index would take the wait away.
- **Pages at fixed offsets:** a film added or removed on the server during a read can move one title across two pages:
  a repeat is dropped (by id); a title skipped that way comes at the next read.
- **The people pass still asks each title's whole cast** (Jellyfin's `People` field has no limit); Nixie keeps the
  directors and the first five actors. It now runs after the titles are in and saved, with no images or user data.
  (Gone in 1.7.3: there is no people pass.)
- **A first read that stops part way starts again from its first page** at the next ask, at once (BH-171-m1): the
  page now says "Nixie couldn't finish reading your libraries. It will try again next time." under the rows it made,
  but the pages read so far are not kept and there is no back-off between tries.
- **The server's check counts every film and show of the user**, in libraries the index skips too (BH-171-m5): Jellyfin's
  `/Items` takes one `ParentId`, so asking per indexed library would be one request per library (23 on the owner's
  server) instead of one. A change in a skipped library reads the titles of the indexed ones.
- **Fixed in 1.7.3** (the 1.7.1 bug hunt, `BH-171-*`; 1.7.2 was a hotfix alone), but m5, and stream C's three, which
  merge after the integration:
  - m3: the people pass runs with the Actors and Directors kinds off, and nothing turns the index off. **Fixed in 1.7.3:**
    there is no people pass (the owner's answer 1).
  - m4: when the reading line goes, every row moves up 40 px under the keyboard focus and the pointer (four themes).
    **Stream C of 1.7.3** (not merged yet).
  - m5: a library that changes every day (most big servers) still means a titles read about once a start; read only
    what was added since the saved newest (`MinDateCreated`) instead. **Not fixed: queued for 1.7.4.**
  - q1: the reading line has no total until every library's first page is in (84 s at 3-10 s a request, 23 libraries).
    **Fixed in 1.7.3:** "200 of about 2,600 films" from the first page (its "about" can be off: queued for 1.7.4).
  - M1's other half: a first read whose played-episodes request fails keeps no plays until the next read. **Fixed in
    1.7.3:** they are asked again, at most hourly (`PlaysDue`).
  - The Collections page's arrival focus can stay on Back (1 of 7 Drive-in runs under load, from the server's
    collections tile; eed7bc12's code): `TakeArrivalFocus` tried 1 ms after the rows, before Loaded, and returned
    through its `!IsLoaded` branch with neither the Loaded retry nor the timer running after it. The log says "the
    section's first tile could not take the keyboard on arrival (it was on Button PART_BackButton)" with no retry.
    **Stream C of 1.7.3** (not merged yet).
  - A title the server lists in two libraries counts under the first one only (1.7.1's read keeps each id once; 1.7.0
    kept one entry per library). On the server the owner uses, 3,167 of 34,006 titles sit in more than one library, so
    a library's own presets can miss titles it shares with another. An entry with all its libraries fixes it; whole-index
    builders then count each title once. **Fixed in 1.7.3** (P0-2).
  - Drive-in's Libraries page: a library tile whose server artwork is a logo on a dark card shows only the card's top
    strip (the crop is anchored to the top). Seen on the owner's 1.7.1 test copy; 1.7.1 didn't change that page.
    **Stream C of 1.7.3** (not merged yet).
- **Fixed before the build (1.7.1 blocker, review and bug hunt, 2026-11-22):** the Collections page's first read was over
  before its Made by Nixie rows were in (a race a cold start lost; the rows came a moment later); rows that stayed kept
  their first page's collections through the whole read (M1); a read again was saved only after its people, so closing
  Nixie mid-read read every title again at the next start, and the once-an-hour limit was forgotten at each start (M2);
  people pages stayed at 1,000 after timing out (M3; with the app's client the timeout was never even seen); a library
  change's read dated the index anew, so the day's read of the people never came; R during a read read everything again
  after it; new outside lists brought back a stopped first read's reading line. From the bug hunt: a refresh whose
  played episodes failed saved no plays (BH-171-M1); a read whose check failed was read again at every start, and the
  check had no time limit (m5); a switch of user during the saved index's disk read gave it to the new user (m6); a late
  ask after a sign-out read with the ended sign-in (m7); a refused page was tried three times (q3); the first Made by
  Nixie rows' time is logged (q2).

## Nixie 1.7.0: Discover
- **Merged** (2026-11-22, `wip/rel-1.7.0` from 1.6.1's build commit `9a50a01`): keyless (`0d0c8eb`), upnav (`7a83843`),
  trending (`4bbf744`), then `main` twice (1.6.1 as released, its Sandbox line and the roadmap). main's 1.6.1 is a
  squash of `9a50a01`, so git's merge base was the older `7093f7c` and every file 1.6.1 touched conflicted with itself:
  merged as this branch's tree plus what main has beyond `9a50a01` (docs only), checked by comparing the two diffs.
  Conflicts in the stream merges: CHANGELOG; `CollectionsViewModel.MadeByNixie` (trending's body with 1.6.1's local
  date, BH-160-m1); `OutsidePrivacyTests` (keyless rewrote it to run the app's whole path with two libraries and an empty
  one after trending had added its sources to the older version: trending's sources, fake and hosts carried into the
  rewrite, the page's trending collections through `TrendingServices.Collections`, each family of requests compared in
  its own order since the trending sources refresh beside the lists).
- **Trakt's labels** (the coordinator, from Trakt's API Use Policy of 2026-10-20: "Label discovery lists accurately so
  users understand what they represent"): Trakt's trending is what is watched right now and only TMDb's is the week's,
  so the Collections row is "Trending" (not "Trending this week"), Home's row "Trending in your library", and a trending
  collection's why line says each source's window for the sources that are on ("Trakt: watched right now · TMDb: this
  week"). "Box office this week" stays: Trakt's box office is the weekend's.
- **The bug hunt's fixes, before the build:** BH-170-M1 (Wikidata or TMDb's export files turned off mid-round kept
  asking: 11 of 12 queries, 1 of 2 files; now the switch is read before each request and when the answer comes back,
  and again before the round's lists are published), q4 (a trending match under way at sign-out isn't kept after it)
  and q1 (then-170-keyless turns nothing on unless the app's log says every source goes to the fake, with a source test
  over every then-*.ps1). Queued above: q2, q3, q5.
- **Known gaps (not verified, by us or anyone):** TMDb trending has never been asked live (no token on the build PC
  while it was made; its fake follows TMDb's documented answers); QLever has never been queried live (the live check
  asked query.wikidata.org, and query-next.wikidata.org refused it, as expected until it opens); the crumbs' lookup
  path (an episode that arrives without its series or season ids), Specials (season 0), long names and the gamepad order
  are unit-tested only: the fake server always sends the ids and has no season 0.
- **The owner's steps:** Trakt's Production Client ID (Trakt staff: request Production access before launch, with
  PRIVACY.md's URL on main), set on the build PC on 2026-11-22, and Nixie's own TMDb token (the owner's call the same
  day: built into the release like Trakt's ID, the user's own token still wins), `NIXIE_TMDB_TOKEN`. The release build
  refuses to run without either (`-RequireProductionTraktId`, `-RequireAppTmdbToken`) and says only which it took
  ("Trakt: Production ID", "TMDb: app token"), never a value.
- **Hand checks for 1.7 (not run, for the owner):** the four sources on your own server's libraries (Awards, franchise
  names, Trending in your library); TMDb trending with Nixie's key and with your own token; the crumbs on a real show
  with Specials and a long name; a gamepad on the crumbs.
- **The release's final pass, trimmed** (2026-11-22; "verify once": 1.6.1's full pass ran on `9a50a01` hours before, and
  1.7 changes no file under `backend\`, nor the installer, the updater or `verify-package`): off-screen, Debug x64
  builds of the branch, the fake server's strict auth on in every run, the bug hunt beside it on `a244319`. The shared
  pages and the keys matrix ran on `b79cef1` (the merges, Trakt's labels and the version); 1.7's own lists scripts on
  `ac65f13` (the hunt's fixes in) and again on `73678958` (Nixie's own TMDb token, the weekly export files, the reworded
  cards and TMDB's logo merged). The keys matrix 568/568 in Classic and Drive-in, 612/612 in Cinematic and 574/574 in
  Editorial (2,322 rows; Classic's M after a theme switch and two of Cinematic's "the bars are drawn" rows failed once
  under the full load, in the player 1.7 doesn't touch, and passed with the two themes rerun alone); the contrast floor
  in four themes, Dark and Light, 4,408 labels and none under its floor; then-170-keyless 28/28 and then-170-trending
  30/30 in each theme (28/28 before the TMDb merge), then-170-upnav 34/34 in each theme and in Editorial Light;
  then-160-collections 33/33, then-topbar160 119/119, then-search 36/36 and then-140-arrival (20/20, Editorial 19/19) in
  each theme, then-140-checks 71/71 but Cinematic's known 1.5.0 skeleton (21 px at 1920, 70/71). On the QA servers
  (10.11.11 and 12.4.0): then-search-live 14/14 on each and the smoke 20/20. FluentFin.Core.Tests 5,476 and
  FluentFin.Tests 1,630 after the TMDb merge, all passed (the live check skipped unless asked; MpvBackendHost's crash
  test, a timing test under load, passed alone 22/22), one of them after a test fix the merge needed (below); the bridge
  smoke 75/75; the fake server's tests 105, the QA tools' 23 and the release tools' 27 (7 more need `pefile`, which only
  the build's venv has).
- **Found and fixed in the pass:** the merge of the weekly export files (`2cd31272`) with keyless's cut-file test
  (`3ef30a5`, which the trending branch never had): the test made the list stale one day on, still fresh now; it moves a
  week on.
- **Skipped, by the trimmed brief:** the full backend suite, the end-to-end harness and the QA servers' playback runs
  (the playlist and transcode run, the quality run, the skip live check): nothing they cover changed since 1.6.1's pass.
- **The owner's test zip** (2026-11-22, from `390ac99e`, before the TMDb merge): `build-release -SkipTests`, the
  Production Trakt ID, no TMDb app token yet; not verified by `verify-package` (the release build is).

## Nixie 1.6.1: the fix release
- **Merged** (2026-11-21, `wip/rel-1.6.1`): skip, older, nav (with its review follow-ups). `verify-package.ps1` keeps
  both streams' checks, each with its own pass lines: skip's (the bundled backend's `--nixie-skip-check` snaps a rule to
  the cut with only the package's libmpv, no mpv.exe on PATH) and older's InstallSwitch run (the packaged Nixie.exe
  refuses `--install-update` without the upgrade test's marker and lets it through with it; nothing is installed). The
  fake server's `library.py` and its tests keep both older's and nav's changes.
- **Known issue (the owner's finding, 2026-11-21; ships in 1.6.1, not fixed):** an episode opened from Continue
  watching, search, Up next or a collection shows only the library above its title (for example "Shows ›"), with no
  link to its series or season, in every theme. Use Back, or search for the show. Fixed in 1.7.0 (the way up from an
  episode: Shows › series › Season n over the title, in every theme).
- **Left for 1.7.1:** the place and watched state of a playback whose sign-in ended (BH-151-m8, the rest: 1.6.1 says
  so in the player and offers Sign in again, but sending the last position after it is a new server call), and the
  items above from the 1.6.1 streams, its reviews and its bug hunt.
- **The release's final pass** (2026-11-21, off-screen, a Debug x64 build of the branch at `dcf706b`, the fake server's
  strict auth on in every run; the bug hunt beside it on the same commit): the keys matrix 568/568 in Classic and
  Drive-in, 612/612 in Cinematic and 574/574 in Editorial (2,322 rows, "/" leaving the search box empty among them); the
  contrast floor in four themes, Dark and Light, 4,408 labels and none under its floor; 63 theme runs (1.6.0's 58 with
  the 1.6 streams' scripts, then-signin in four themes, and then-160-collections with the colliding tidy names and
  Settings > Collections) and a full update flow in Drive-in: all green but Cinematic's 1.5.0 skeleton (21 px at 1920),
  once the reruns below were in. The full update flow passes in Classic now (40/40), as do the release notes' first
  focus on Close and Editorial's Made by Nixie arrival in every theme. Rerun alone after a load failure: arrival in
  Drive-in (a search suggestion not listed in time, then three R presses 2.5 s apart where one load outlasted the
  gap: 20/20 on the third run), Restart to update in Drive-in (Check now after the notes, above: 31/31 on the rerun) and
  then-160-collections in Drive-in (the Collections tile's focus on Back, queued above: 33/33 on the rerun). On the QA
  servers (10.11.11 and 12.4.0): then-search-live 14/14 on each (its first live run with "With <name>"'s episodes),
  the playlist and transcode run 44/44 (alone), the quality run 16/16, the skip live check 14/14 (snapping with
  libmpv), the smoke 10/10, live-save-playlist and live-server-index all passed. FluentFin.Core.Tests 5,141 and
  FluentFin.Tests 1,621, all passed (the process and pipe timing tests that failed under load passed alone); the
  end-to-end harness 26/26, the transitions check 4/4
  and the bridge smoke 75/75; the backend suite (7,042 tests in 304 modules) with only the 8 environment failures once
  the 6 modules its 300 s limit cut under load were run alone (`test_shell_playback` 165 in 949 s) and the one real
  failure was fixed (below); the fake server's tests 75, the QA tools' 23 and the release tools' 21 (7 more need
  `pefile`, which only the build's venv has); `verify-package` on a dry run's package 134/134, the skip snap check
  and the `--install-update` refusal among them.
- **Found and fixed in the pass:** `backend\tests\test_skip_rules.py:778` (a review follow-up) wrote its empty rules
  file in text mode, which the repository's own `test_no_platform_text_reads` refuses: "wb" now. And the harness:
  then-160-player counted the Playback stats' `get_stats` lines in the backend log, which 1.6.1 logs at DEBUG, so it
  read 0 in every theme and its "never asked while closed" checks passed on nothing; a new -Before script,
  `before-backend-debug.ps1`, has the run's backend log at DEBUG, and the script checks that it does (46/46 in four
  themes: 3 questions in 3 s open, none closed).

## Nixie 1.6.0: collections
- **Merged** (2026-11-21, `wip/rel-1.6.0`): colpage, topbar, skip, player, collections, then main with 1.5.1.
- **Esc in the player** closes the innermost thing first: an open menu or flyout (the skip mark sheet is one), the
  Playback stats, Cinematic's panel, then the "Skip this every episode?" question as Not now, then the page's own Esc
  (leaving full screen). Not "hide the controls" before that: in a window Esc shows them, and F shows the bars, so it
  would never leave full screen. Cinematic's panel holds the bars as a menu does but is no flyout
  (`MpvControlsViewModel.FlyoutOpened`): the keys matrix's new Esc rows caught Esc dead under it at the merge.
- **Collections** opens the collections stream's page from the top bar (`GlobalCommands.OpenCollections`); topbar's
  interim route (the server's box-set view, else the Libraries page lit as Collections) and its in-place re-light are
  retired. Collections chosen on a "See all" or the server's box-set view opens the page. The top bar's tabs and the
  Libraries page's tiles use the tidy library names.
- **Intros that move:** 1.5.1's rule (per season, the first hand skip of each episode, the same part 10 s or more apart)
  in the skip stream's learner (`SkipStore.note_hand_skip`), replacing that stream's first form of the rule; 1.5.1's
  marks in `learned-intros.json` ("seasons") migrate with the ranges into `skip-rules.json`.
- **The release's final pass** (2026-11-21, off-screen, Debug x64 builds of the branch, the fake server's strict auth on
  in every run): the keys matrix 566/566 in Classic and Drive-in, 610/610 in Cinematic and 572/572 in Editorial (the new
  Esc rows among them); the contrast floor in four themes, Dark and Light, 4,408 labels and none under its
  floor; 58 theme runs (then-140-checks, arrival, then-search, then-update, the season switch, rewatch, paging and T5 in
  four themes, search in Cinematic Light, the full update flow and Restart to update, and the 1.6 streams' own:
  then-topbar160 in four themes and its hub fixture, then-160-colpage in four themes with the long title and the list
  view, then-160-skip, then-160-player and then-160-collections in four themes): 54 green once the harness knew the
  Collections page and the reruns were in, the other four queued above (Drive-in's skeleton, Editorial's Made by Nixie
  focus, the release notes' focus in Classic) or the 1.5.0 issue (Cinematic's skeleton, 21 px at 1920). On the QA
  servers (10.11.11 and 12.4.0): then-search-live 14/14, the playlist and transcode run 44/44 (run alone: beside the skip
  live check, which plays the same seeded episodes as the same user, the server's activity log counted both), the
  quality run 16/16, the skip live check 14/14, the smoke 20/20, live-save-playlist 10/10 and live-server-index 12/12 on
  each. FluentFin.Core.Tests 5,055 and FluentFin.Tests 1,592, all passed (two process timing tests alone); the
  end-to-end harness 26/26, the transitions check 4/4 and the bridge smoke 75/75; the backend suite (6,733 tests in 304
  modules) with only the 8 environment failures once the 8 modules its 300 s limit cut under the harness load were run
  alone, all passed (`test_shell_playback` 165 in 1,078 s); the fake server's tests 74, the QA tools' 23 and the release
  tools' 20 (`test_libmpv_stub` needs `pefile`, which only the build's venv has); the package from a dry run of
  `build-release` 121/121 in `verify-package`.
- **Found and fixed in the pass:** a Nixie collection's page drew neither its eyebrow ("Made by Nixie · Franchise") nor
  its why line, in every theme: each stream had left drawing them to the other (fixed at the integration, with a test).
  And the harness itself: the server's Collections and Playlists views now open the Collections page (`drive.ps1`'s
  `Find-MediaCard`, then-140-checks, then-topbar160 and then-160-collections, whose page markers were a Text any top bar
  tab also has), the tidy library names in the top bar's menu (then-topbar160's hub fixture), and the contrast run's
  crops (a Made by Nixie row's name made a path over Windows' 260 characters; a preset clipped to 5 px at the edge of
  Cinematic's scrolling strip was measured as a label).

## Nixie 1.5.1: the fix release
- **The release's final pass** (2026-11-21, off-screen, a Debug x64 build of the branch after its review fixes, the fake
  server's strict auth on in every run): the keys matrix 538/538 in Classic, Drive-in and Editorial and 576/576 in
  Cinematic; 44 theme runs (then-140-checks, arrival, then-search in four themes and Cinematic Light, then-update in four
  themes with the full flow and Restart to update, the season switch with its latency rules, rewatch, paging, T5, the Alt
  keys, Classic's states, then-signin in four themes) all green but the skeleton check (Cinematic at 1920, the 1.5.0
  issue below) and one Editorial season run that stopped on a screenshot's GDI+ error and passed 83/83 run again; on the
  QA servers (10.11.11 and 12.4.0) then-search-live 14/14, the playlist and transcode run 44/44, then-signin 16/16 and
  the smoke 20/20 on each; FluentFin.Core.Tests 4,598 and FluentFin.Tests 1,559, all passed; the end-to-end harness 26/26,
  the transitions check 4/4 and the bridge smoke 75/75; the backend suite (5,885 tests in 300 modules) with only the 8
  environment failures (`test_audio_settings` 6, `test_mpvtk_pilfont` 2) once the 20 modules its 300 s limit cut under
  the harness load were run again alone, all passed (`test_shell_playback` 165 in 1,509 s); the release build's own run of
  the suites (and the bridge tests, 472) green, after a first try beside those reruns lost one UI test to the load.
- **What it fixed:** the 1.5.0 bug hunt's three Majors (BH-150-M1 Classic Home's notice under the top bar, M2 a refused
  sign-in read as a dead server and the per-PC device id, M3 Enter, Enter on a show) and its Minors m1, m2, m4 to m8 (m3
  in part), "Skip intro" at 0:00 on shows whose intro moves, Display's date-added choice, and show-level anime ids for
  later seasons. CHANGELOG.md has them in words.
- **Signing in again** (BH-150-M2): the app takes a sign-in as ended only when the server answers 401 to the request and
  again to `GET /Users/Me` (Jellyfin also answers 401 with a good token, for Quick Connect turned off). A proxy in front
  of the server that answers 401 on every path for its own sign-in reads as an ended sign-in too; a 403 never does.
  Pages that loaded before the sign-in ended keep their content until they load again (R); the player and every load
  after it say it.
- **The device id after the update:** each copy is a new device on the server (the PC's id hashed with the copy's data
  and program folders and the Windows user). The 1.5.0 entry stays in the server's Devices list until removed there,
  its token unused (Nixie never stored it). A portable copy unzipped to a new folder for each version is a new device
  each time, and so is a copy run with another `--data-dir`.
- **Intros that move:** the mark is per season, from the viewer's own skips, the first in each episode, in the intro's
  window (the first 10 minutes, a 30 to 150 s jump), compared only with skips of a similar length (within 15 s: a recap
  skipped in one episode is not the intro of the next). A viewer who jumps into a scene in the first 10 minutes, as far
  as they usually skip the intro, marks the season too, and loses its learned spot (the button that never skips by
  itself). A hand skip that overshoots the intro by much more than usual is taken for another part and missed. The 1.6
  skip stream rewrites the learner and keeps the rule.
- **Search's new kind** (Videos: home videos and music videos) is one request more per search. Songs and albums are left
  out (BH-151-M2): an album found would open with Play off, as Nixie plays no music; music search comes with playback.
- **Not checked live:** Display's date-added choice against a real server (`POST /System/Configuration/metadata` changes
  the shared QA servers' settings); the code path is the client's own, unit-tested.

## Never verified
- **Parts of a real Jellyfin server.** The automated tests use fake servers and fake tokens.
  The user's own server (remote, HTTPS, signed in as a user who is not an administrator) has
  been in use since: sign-in, browsing and playback work, played with FluentFin's own player
  controls (the user's report, 2026-11-15). Its first try (2026-11-14) found the slow start
  (one play request per season, 31 s for a 250-episode show), fixed the same day in round 7.
  Still not seen on a real server: a real **transcode** and its restart on a track change,
  and the server's handling of a duplicate Stopped.
- **A real sign-out in the app shell** (Log out / Switch user while a video plays). Covered
  by view-model tests and the end-to-end harness (`tools\ui-harness\e2e`, scenarios
  `signout_*`), not by driving FluentFin itself: its data folder can't be redirected, so a
  fake login would land in the real FluentFin settings.
- **The libmpv path.** The release bundles MSYS2's mpv 0.42.0 as libmpv (`vendor\libmpv\VENDORED.md`),
  tested off-screen and by the user on their server. mpv's controls still run an external mpv (mpv-CI via winget).
- **A rare crash opening a Season page** (seen twice: a dev-harness Debug run on 2026-11-15 and one release
  verification run on 2026-11-16; reruns of the same scenario pass). FluentFin ends with a WinUI stowed exception
  (COMException 0x80004005, 0xc000027b in Microsoft.UI.Xaml.dll via combase.dll) even though
  `App_UnhandledException` marks it handled. No dump was kept; under investigation for 1.0.1.
- **The admin Playback Reporting charts page** uses SkiaSharp's native view, which needs the Microsoft
  Visual C++ runtime (MSVCP140, VCRUNTIME140, VCRUNTIME140_1). The release zip doesn't ship it, so that page
  needs the VC++ Redistributable on PCs without it (found from the DLL imports, not seen failing).
- **Upstream's FFmpeg DLLs in `ui\FluentFin\FFMpeg\`** (for the Flyleaf player, build N-126175-g0056dd32fd) are
  in the repository and its source archive, as upstream FluentFin ships them; the release zip leaves them out.
  Their exact source is upstream FFmpeg at that commit; a SOURCES row or `export-ignore` is due in 1.0.1.
- **With libmpv unavailable and mpv not installed**, the page still says "Switch to FluentFin's controls",
  which is already the setting; a separate message is due in 1.0.1
  (`ui\FluentFin.Core\Mpv\MpvNotFoundMessage.cs`).
- **"Player controls: FluentFin"** (the default; mpv's are the other choice; docs\research\player-controls.md). Built
  and measured off-screen only (`tools\ui-harness\composition-spike`, `tools\ui-harness\fluentfin-controls`).
  Not seen on the real desktop: the real mouse (hover, wheel event, clicks, hidden pointer), Tab order
  and Narrator, menus over the video, light/high-contrast themes; a DPI change, independent flip and
  HDR on an HDR display (Windows HDR is off on every display here). Its libmpv (`vendor\libmpv`) is
  downloaded per checkout, not in git: without it (or with one too old for composition output) FluentFin falls back to
  mpv's controls and says so.

## Player controls: FluentFin's (`ui\FluentFin\Mpv\MpvCompositionVideo.cs` and around)
- **One crash domain**: mpv runs inside FluentFin, so an mpv or GPU-driver crash ends the app (a
  `quit` only ends the core; the next playback makes a new one on a new pipe and restarts the backend).
  The mpv choice keeps mpv out of process.
- **No trickplay or Up Next card yet** with FluentFin's controls. Skip intro/credits is there
  (2026-11-16, skip-ui: a Skip button bottom right, above the seek bar, while the backend's
  `segments` event offers one, also with the bars hidden, and the S key, `skip_segment`), and
  the Next button follows the `next` event (disabled at the end of the queue, the next episode
  named in its tooltip). Which segments are offered is the backend's settings' (ask/always/off); Settings > Playback's *Show Skip intro
  and Skip credits* only hides the button (S still skips). Trickplay tiles still have no bridge
  path (the UI would ask the server). The shim skips by itself the segment types its settings set
  to always.
- **What the shim's command line gave mpv is not there in attached mode**: thumbfast/mouse scripts
  (unused: mpv's OSC is off) and `tls_*` client certificates (a server that needs one will not stream).
  `hwdec` follows the shim's setting as on the other path (its default is `no`).
- **The display is read when a playback starts**: a window moved to a display with another HDR state
  or refresh rate mid-playback keeps the first one's until the next playback.
- **The composition probe can run on the UI thread** (whether libmpv loads and does composition output):
  when no pre-warm ran first, the player page's first start runs it there, about 27 ms once warm.
- **Dev builds only: two FFmpegs with the same DLL names.** A dev build has Flyleaf's `FFMpeg\avcodec-63.dll`
  and the others beside libmpv's own in `libmpv\`; Windows loads a module of a name only once per process,
  so after Flyleaf has played, libmpv can be handed Flyleaf's FFmpeg. The release ships no Flyleaf FFmpeg
  (`tools\release\build-release.ps1`), so it cannot happen there.
- **A pause or volume change can be undone for a moment by a state echo**: volume and mute are held
  for 1.5 s after a change (the wheel's case, measured); pause is not, a stale `state` can flip the
  button back until the next one.
- **A volume above 100** (an mpv.conf's `volume=130`) stays until it is lowered: `set_volume` is 0-100,
  so a step up (wheel, Up) leaves it, only unmuting; a step down sets it within 0-100.
- **Full screen cover** (1.1.3): with FluentFin's controls a black cover lies over the whole window (the title bar row too) while
  the presenter changes, so the half-done layout (old XAML layout in the new window size) never shows; the screen is black for
  about 40-75 ms going in and 65-90 ms going out (it waits for the picture's hold to end), at most 300 ms (the UI thread's limit;
  500 ms the compositor's own, for a stuck UI thread). 1.1.2 cloaked the window instead, which showed whatever is behind it
  for 60-95 ms. To compare on a real screen: `NIXIE_FULLSCREEN=cover` (default), `cloak` (1.1.2's) or `none`
  (`SEAGLASS_FULLSCREEN`, its name before 1.2.0, still works); `FLUENTFIN_NO_CLOAK=1` still means none. Not judged on a real desktop yet (off-screen captures read the window's own surface).
  mpv's own controls are not hidden. Numbers and frame strips: `docs\research\fullscreen-smoothness.md`.
  **The 300 ms limit can lift the cover early** when mpv is slow to draw its first frame of the new size: the held, scaled
  picture then shows until it does (seen once in 24 off-screen switches, a switch out that waited about 640 ms for mpv).
- **Full screen and back**: mpv renders at a new size 2 to 5 frames after the panel took it; until it has
  presented a frame of that size, the panel holds its old frame scaled to where the new picture goes (the user saw
  the picture jump by the title bar's height and then snap). Left: mpv presents on its own thread, so its first new
  frame can meet the hold, or the hold's end come just before it, for up to one frame; and WinAppSDK passes a
  maximized window through its normal size (here 1920x1023) for about 5 ms on the way in and out, seen in one
  picture of the way out. With mpv's own controls the video is mpv's window in the backend's process: that one
  flash, and about 60 ms of mpv's old picture stretched in its resized window, remain there.
  `FLUENTFIN_MPV_VIDEO_TRACE=1` logs, per frame for 2 s after a change, the panel, the swap chain and the hold.
- **Keys off-screen**: a key message posted to FluentFin reaches WinUI only through the window's
  `InputSiteWindowClass` child (the thread's focus window), not its `DesktopChildSiteBridge`; the
  fluentfin-controls harness posts there (Space, F, Escape through the page's key path).

## Nixie 1.5.0: search, updates, fixes
- **The release's final pass** (2026-11-20, off-screen, a Debug x64 build of the merged tree, the fake server's strict
  auth on in every run): the keys matrix 538/538 in Classic, Drive-in and Editorial and 576/576 in Cinematic; the
  contrast matrix's 16 runs, 4,129 labels, none under its floor; 41 theme runs (then-140-checks, arrival, then-search in
  four themes and Cinematic Light, then-update in four themes with the full flow and Restart to update, the season
  switch with its latency rules, rewatch, paging, T5, the Alt keys, Classic's states) all green but the skeleton check
  below; on the QA servers (10.11.11 and 12.4.0) then-search-live 14/14, the playlist and transcode run 44/44 and the
  smoke 20/20 on each; FluentFin.Core.Tests 4,560 and FluentFin.Tests 1,548, all passed; the end-to-end harness 26/26,
  the transitions check 4/4 and the bridge smoke 75/75; the backend suite with only the 8 environment failures v1.4.1
  has too (`test_audio_settings` 6, `test_mpvtk_pilfont` 2) once the 8 modules the suite's 300 s limit cut under that
  load were run again alone: 7 passed (`test_shell_library` 314, `test_live_tv` 258, `test_shell_settings` 170,
  `test_shell_books` 147, `test_list_page` 126, `test_dpi_matrix` 25, `test_playlist_edit` 8), and `test_shell_playback`
  passed all 165 once it ran alone with no time limit (the first run, cut at 20 minutes, had reached 119 with none
  failing; its layout sweeps rebuild the player bar hundreds of times).
  The bug hunt ran beside it; its findings are in the Fix queue above, none fixed in 1.5.0.
- **Cinematic's library placeholders can stand off the grid.** then-140-checks' skeleton check (Cinematic, 1920 wide)
  failed in the final pass and again alone: on the first visit to Movies the placeholders stood 21 px below the cards
  that replaced them, so the cards move up as they land. The app logs the same mismatch ("[layout] library skeleton:
  differs from the grid", 11 to 76 px apart) 3 or 4 times a run, in 1.4.1's runs too, where the check's own visit
  happened to match. The other themes and Cinematic at 1100 wide match.
- **UI timing tests under load:** a few FluentFin.Tests timing tests (`JobProcessTests.ExitedAt` most often; the updater
  stream saw five while the keys matrix ran beside them) can fail on a busy PC and pass alone. All 1,548 passed in the
  final pass beside three harness runs; the release build runs a failed test once more on its own.
- **Season switching, what is left after 1.5** (`then-season-switch.ps1` on the fake server's 10x20 series, four themes,
  with its latency rules; the 1.5 season stream measured before and after):
  - **The stills of a season not asked ahead still come after its rows,** 0.1 to 0.3 s now (0.5 to 1.1 s before 1.5).
    WinUI starts a picture's download only once its row is laid out and drawn; pictures now come 6 at a time (WinINet gave
    the app 2). The seasons either side of the one shown have their stills asked ahead (into WinINet's cache, which WinUI
    reads), so a click to one of them shows its pictures with its rows; a far season (Season 1 to 9) and a season page
    opened from far away do not, and neither does Editorial (it sizes its stills to its layout, so it cannot ask ahead).
  - **Drive-in's rows are still the slowest to build,** though no longer by much: its tube numbers are made of far fewer
    objects (the same drawing), which took most of its GC pauses away; a rare one still lands in a switch.
  - **A season page shows the season it was opened with first:** its name, overview and pictures from the pill or link,
    with the picker and the kept rows, and the server's season replaces it a moment later (cast and details come then). A
    link that carries only the season's id shows the rows first and the header when the season comes.
  - **A far season can still cost a round trip.** A show of at most 12 seasons has every season asked ahead, one at a
    time (1.5); a longer one asks only the seasons either side, so a jump further away waits for the server once. After an
    episode is played (or five minutes), a season further away is not asked ahead again (that was up to 11 requests a
    time): it is asked when shown, one round trip.

## Nixie 1.4.1: Continue Watching and season switching
- Season switching: what is left after 1.5 is under Nixie 1.5.0 above.

## Nixie 1.4.0: the consistency release
- **Editorial has no list view.** Its View menu offers the poster sizes only; the other three themes have the list.
- **Drive-in's chapter bulbs come later.** Drive-in has the Chapters menu in the player's bar like every theme, but no
  bulbs on its console's scrubber yet.
- **To check by hand at each release** (off-screen runs cannot do them):
  - **the lost-overlay fix:** open the player, press Space before any click, then move the mouse: the controls must come
    back. Off-screen, posted mouse moves are not real cursor moves, so the 1.3.1 case was never reproduced there; the fix
    hears every pointer move and polls the cursor every 250 ms, and logs a line if the case recurs;
  - **Esc inside YouTube's player in the trailer theatre:** the web view has the keyboard there, and Esc must still close
    the theatre.
  - **One real trailer plays in the theatre, with sound** (off-screen runs are muted; `then-trailer-live.ps1` is the opt-in
    off-screen check against real YouTube, `NIXIE_LIVE_YOUTUBE=1`).
- **Trailers depend on YouTube's embed rules.** The theatre plays a trailer inside Nixie's own page at
  `https://io.github.mbustos27.nixie` (YouTube's form for an app's identity). If YouTube changes what it accepts again,
  trailers show Nixie's message ("YouTube refused to play it here") with Watch on YouTube, rather than YouTube's error page.
  The app learns the player's state by reading Nixie's page every 300 ms (1 s once it plays); the page has no way to call
  the app. YouTube reports a video it no longer has with the same code as one its owner keeps on YouTube (150), so that
  message says both. No real video that refuses embedding was found for the live check (five official music videos all
  played); the message was checked live with a missing video's 150, and off-screen with the harness's 150 and 153.
- **R in a grid is Refresh first** (the coordinator's rule; the user asked for R to refresh): a first R refreshes, so a
  title that starts with R cannot be typed. Within a second of another letter R is a letter of the title. The R titles are
  on the A to Z's R, and on the A to Z a letter key presses that letter.
- **The skeleton and [perf] probes** (`LibraryProbe`) log one line per library opened; they measure, they never change
  the layout.
- **Still open, from the outside-services bug hunt** (`docs\reports\Bug hunt 2026-11-19 (outside services).md`; none blocks
  1.4.0; planned for 1.4.1, they wait for the next release: 1.4.1 has Continue Watching and season switching only):
  - **M1 (fixed in 1.5: truststore in the backend; libmpv's stream and a transcode's playlists and segments already
    read the Windows store through libcurl's native CA, and FFmpeg's GnuTLS, used only for what libcurl does not open,
    trusts the same stores; pinned by `LibmpvTlsTests`): a certificate Windows trusts but Mozilla's list does not: sign-in
    works, playback fails.** The UI checks the
    server's chain against the Windows store, the backend against certifi's bundle, so a home or private CA (or an
    HTTPS-inspecting antivirus root) browses but nothing plays (`SSLError: CERTIFICATE_VERIFY_FAILED`). Fix: `truststore`
    in the frozen backend (`truststore.inject_into_ssl()` in `backend_entry.py` and the bridge's main).
  - **m1 (fixed in 1.5: no answer for a split season or season 0; ARM has no episode offsets): split-season anime get the wrong part's skip times, or none** ("Look intros up online"): a TVDB season with more
    than one MAL entry (Attack on Titan's Season 3 and 3 Part II) is looked up as the first (`intro_online.py`
    `mal_id_for`). Fix: no answer when a season maps to several entries, or for season 0.
  - **m2 (fixed in 1.5: patched out with the key): mpv's controls offer "Search online" for subtitles,** which fails (uosc's `ziggy` is not shipped), and the
    vendored uosc still carries its OpenSubtitles key. Fix: patch `download_command` out of the subtitles opener.
  - **m3 (fixed in 1.5: the updater follows GitHub's redirect, `UpdateSource.Repository`; 1.1 to 1.4 still do not,
    docs\RELEASE.md section 5): renaming the GitHub repository would silently end one-click updates** in every installed copy (the release
    check accepts only `/mbustos27/fluentfin-mpv/` downloads). Never rename or transfer the repository without a release
    that accepts both paths first.
  - **m4 (fixed in 1.5: strict auth is the fake server's default, `--auth strict`; every run checks no request used a
    legacy form): the fake server accepts legacy auth** (`X-Emby-Token`, `api_key`) that Jellyfin 12 refuses, and reports 10.10.7:
    a regression to a legacy form would pass every harness run. Fix: a strict-auth mode by default in the harness.
  - The sub-path and trailing-slash server run (done in 1.5: `tools\qa-servers\subpath-probe.ps1` against both QA servers,
    behind `/jellyfin` and with a "/" typed at the end).

## Nixie 1.3.0: the layout themes
- **Editorial has no paused side column** (synopsis, cast and chapters beside a paused video), nor its segmented chapter
  scrubber: the theme no longer promises them (1.4.0). Its player is the shared one; chapters are in the bar's Chapters
  menu in every theme (1.4.0).
- **Back after a switch lands at a page's start.** A switch keeps the place on the page it happens on (Settings, or Home
  under "Choose a look"). The pages behind it are rebuilt in the new layout when Back reaches them, so Home comes back
  with the focus on the Home tab and its rows at their start, as Classic's Home always does after Back (seen off-screen:
  Drive-in without a switch returns to the focused card). Keeping each page's place in the history is for a later release.
- **Drive-in's scrubber has no chapter bulbs yet**: the player's view model has no chapters for it.
- **Checked off-screen only (Dark, Full; the switch Drive-in, Cinematic, Editorial, Classic and back on Home and a series
  page: the search text kept, the 200 ms crossfade logged, none with motion reduced); still to check by hand:**
  - high contrast in each theme (the harness cannot force a high-contrast theme; the dictionaries' HighContrast values
    are checked by tests, never seen);
  - the crossfade and Narrator's "Theme changed to ..." on a real screen, by eye and by ear;
  - Light + Solid after changing Glass while the app runs (the fix for dark parts of a light window was seen at start-up
    only), and each theme at 125 to 150 % scaling (Drive-in's small nixie digits).

## Nixie 1.2.0: the rename from Seaglass
- **The MSI upgrade from Seaglass to Nixie was not installed for real** on this PC: `upgrade-test.ps1` installs into the
  account it runs in, so it was not run (also not `-SelfCheck`). What is checked: the MSI keeps the upgrade code, builds,
  and holds exactly the zip's files (`build-release.ps1`), the zip passes `verify-package.ps1`, and the script parses.
  Run `upgrade-test.ps1` on a test account or a VM before publishing: Seaglass 1.1.3's MSI, then Nixie's over it.
- **Seaglass 1.1.x's "Download and install"** only finds an asset named `Seaglass-<version>-win-x64.msi`; the build
  makes one (a hard link of the Nixie MSI) for the release to carry (`docs\RELEASE.md` section 5). Not tried from a
  running Seaglass against a real release.
- **The data folder copy** (Seaglass, else FluentFinMpv, to Nixie) is tested on temporary folders only
  (`LegacyDataFolderTests`), never on a user's real folders; a Seaglass still running during Nixie's first start is
  covered for the journal (a SQLite backup) but its other files are copied as they are at that moment.
- **Pinned shortcuts**: a taskbar or Start pin of Seaglass points at `Programs\Seaglass\app\Seaglass.exe`, which the
  upgrade removes; the pin is left broken until it is pinned again (Windows does not move pins between programs).
- **Internal names that still say Seaglass or FluentFin** are compatibility names only: the `SEAGLASS_*` variables, the
  old data folder and registry key the copy reads, the Seaglass-named MSI the 1.1.x updater looks for, and the package
  identity (`Package.appxmanifest`'s `FluentFinMpv`) and release marker (`FluentFinMpvRelease`), which did not change.

## Nixie 1.2.0: the top bar, the desert look, the button system, the player, the administration
- **Checked off-screen only; still to see on a real screen and by hand:**
  - the glass and the dusk scene in a real, active window (the harness's window is never the active one, so it forces
    the active look), the scene's 20 s of motion and its rest, and the wordmark's glow at real sizes and DPIs;
  - the top bar on a real desktop: dragging the window by its empty middle, the caption buttons, the tabs folding as
    the window narrows (the drag regions are checked with WM_NCHITTEST off-screen);
  - Narrator by ear: the tabs ("Home, tab item, 1 of 6"), "Remove {server}", "Show more" expanded or collapsed, the
    states' announcements;
  - Windows' real high-contrast themes (the HighContrast dictionaries are checked by tests, never seen);
  - the player between episodes with sound (the harness plays digital silence), the Next countdown, and the speed and
    subtitle delay against a real file.
- **The harness ran at 2560 x 1440 and 1920 x 1080** (the display changed during the integration). Four scripts assumed
  the larger screen and were made size-proof (then-continue, then-wheel, then-rows, then-admin's hero menu); a real 4K
  or 125-150 % scaled screen was not tried.
- **The server administration never ran against a real server.** Restart, Shut down and every delete were tried against
  the fake server (each asks first and sends exactly one request), not against Jellyfin.
- **The Windows Media Player bar** (the legacy player's controls) is not driven by any harness script; its new labels
  and Tab stops are checked by tests only.
- **The player's audio and subtitle menus keep the server's track titles** ("English - Dolby Digital - 5.1"): the item
  pages' plain words ("English (5.1)") need the channel count, which the bridge's track list does not carry yet.
- **There is no in-app theme setting:** the app follows Windows' light or dark choice (`NIXIE_THEME` forces one for
  screenshots).
- **Button review verdicts left for a later release:** done in 1.5: the off switch for Home's N and C keys (L1, L2),
  Ctrl+Z in the shortcuts sheet (L3), the Filter button's name "Filter (2 on)" (L25), an Undo after Reset watch progress
  (L38), a visible hint on the disabled Sign in (S2), "Refresh" for an empty folder in the picker (S19), Select disabled
  while loading or failed (S20) and Save disabled until something changed, with "Saved" or why not (S22; the NFO and
  transcoding pages keep their own Save, always on). Still open: sentence case for the row names ("Continue watching",
  L6: "Next Up" became "Up next" in 1.4.0; the rest waits because the search stream's then-search.ps1 finds the row by
  name).
- **Tests that can fail under load and pass alone:** `ItemUndoTests` can take an Undo offer from `ItemToggleTests` running
  at the same time (the offer is a static event); `JobProcessTests.ExitedAt` and a few bridge timing tests. The release
  build runs a failed test once more on its own.

## Seaglass 1.1.3: rail, Settings, item menus, keyboard, glass, player
- **Checks only a real screen and real hands can settle.** The off-screen harness posts keys to the window and reads UI
  Automation (`then-rows`, `then-wheel`, `then-season`, `then-library`, `then-volume`, `fullscreen-burst`); it cannot
  post Shift (WinUI reads the keyboard's real state) or drive a touchpad, a gamepad or Narrator's voice. Still to try by
  hand: Shift+Tab out of a row, a grid
  and an episode list back to where it came from; Shift+F10 on a card and on an episode row (the harness uses the Menu
  key); a touchpad's two-finger swipe and a gamepad's D-pad and A/B in rows, grids and episode lists; Narrator by ear
  (a card's "12 of 320", an episode's "S1E4 Title, watched", "4 of 8"); holding an arrow key in a 320-card grid (real
  key repeat). In the player: the full-screen cover against `SEAGLASS_FULLSCREEN=cloak` in a dark room (which one is
  calmer to the eye), holding F or Esc with the keyboard's real repeat, a real double click on the video while playing
  (posted clicks do nothing off-screen: `VideoClicksTests` only), the wheel-volume bar with a real notched wheel and a
  touchpad, and the player's menus on the shared glass over a playing video.
- **Copy Download URL is removed** (it put the access token on the clipboard). A token copied with an earlier version
  stays valid until that sign-in ends; signing out ends it (`/Sessions/Logout`).
- **Delete's question is tested as behaviour** (`ItemMenu.RunAsync`: nothing runs without the yes); the delete request
  after the yes is upstream FluentFin's, unchanged, and was not run against a real server here.
- **Jellyseerr** (Settings > Integrations) was not tried against a real Jellyseerr: the fake server has none.
- **Menus and tooltips are windows of their own**: their system backdrop never shows in an off-screen capture
  (PrintWindow draws them over black), so their colours were measured as the worst case and the look over real content
  is still to see on a screen, in both themes.
- **The accent on artwork** is wired on cards' and episode rows' progress bars and on the episode row's watched mark.
  The card's own corner badge (watched check or unplayed count, `CardBadgeBackground`) keeps its own colours. In the
  light theme the episode's watched mark is now the light aqua: about 1.8:1 against a very bright thumbnail around it
  (4.2:1 with the old deep aqua), while its dark check has 7.7:1 on it (measured; 4.2:1 before). Measurements:
  `docs\design\DESIGN.md`, 1.1.3.

## UI (`ui\FluentFin.Core\Mpv`, `ui\FluentFin`)
- **Play on a season starts at the series' first unplayed episode**, not the season's
  (`MpvQueue`): what FluentFin's own player does, kept so the mpv path starts where the old
  path did. The queue follows the server's series order (`GET /Shows/{id}/Episodes`, as
  jellyfin-web plays a series), where FluentFin's playlist went season folder by folder: it
  differs from the old path only for specials the server places within a season (they play
  there) and for missing episodes (left out). An episode the server's list does not have
  plays alone (the old path started at the first unplayed episode instead).
- **Sign-out with a slow or hung server**: after the 2 s bound the token is revoked while
  the backend's Stopped is still pending, so it fails with 401 (accepted cost, bounded).
- **A cast during a sign-out** (fixed 2026-11-15, signout-ux: no `play` goes out while a
  sign-out runs, and no page that exists when it ends ever plays; the sign-out's stop takes
  whatever generation is newest under the service's gate). Left: the cast still opens a
  player page that shows nothing until the sign-out navigates away (≤ ~2 s), and that page,
  removed with the shell, sends a `stop` to the idle backend after the token is revoked
  (nothing plays, so no server request).
- **Sign-out feedback** (fixed 2026-11-15, signout-ux): a progress ring and "Stopping
  playback…" / "Signing out…" in the title bar from the click until the logout request
  returns; Log out and Switch user are disabled meanwhile, and one invoked anyway does
  nothing. Never seen on screen yet (see the visual check below). A logout request that hangs
  keeps both disabled until it returns (upstream's own command already did that for itself).
- **A track or quality change that cannot be applied** (fixed 2026-11-15, signout-ux): a
  non-fatal `error` once the item plays is shown above the video for 8 s as a dismissable
  warning with the backend's own text, and playback is not touched. That text is the user's
  since osc-polish (the visual check showed the item id and "ServerUnreachable ... HTTP 500"):
  `MPV: The audio track of "Series — S6E4 · Episode" could not be changed: the server
  answered with an error. It goes on playing as before.` The backend says which change it was
  (audio, subtitles, quality; "track or quality change" when it cannot tell) and keeps ids,
  URLs and status codes in its log. The "MPV: " prefix is the UI's. Showing it pushes the video area down while it is up (airspace: XAML cannot
  draw over mpv's window). The same notice is all a failed `next` / auto-advance gets (its
  item never opened, `state: error`): once it hides, the page stays on a stopped video
  until the user leaves.
- **Visual check still to do** (the user was using FluentFin, so neither was seen): sign out
  while a video plays and look at the title bar (ring and text, then the login page), open
  the account flyout again during the wait (both items greyed out) -- since 1.1.4 the Account
  menu is hidden on the player page, so the title bar no longer offers a sign-out while a
  video plays (the stop-and-report path stays for any other way in); make a track or quality
  change fail and look at the warning above the video (readable, closable, gone after 8 s,
  the video resizes back).
- **A kill that never takes but whose job close does end the backend**: the crash's own
  Stopped from FluentFin can come after the close has returned and be cut off (trade-off of
  not waiting twice for the same exit).
- **Settings > Playback with the MPV player** (2026-11-15): languages and subtitle mode reach
  it through the server (the per-item default tracks the backend falls back to), but four
  settings do not: *Show Skip intro and Skip credits*, *Change the volume with the mouse
  wheel* and *Volume step* are kept on this PC and used by the built-in players and
  FluentFin's controls only (mpv's own controls ignore them: the bridge has no field for them,
  v1), and *Play the next episode automatically* is the
  server's per-user setting, which the backend does not read (it uses its own `auto_play`,
  on); since 1.1.3 its card is shown only with Windows Media Player, which follows it. The cards say so. Needs a bridge field or command, or the backend reading the user's
  configuration at `connect`.
- **The mpv loading screen ends when the backend takes `play`**: from then on mpv's window
  covers the page while the item opens (PlaybackInfo, stream start), black until the first
  frame. Keeping the window hidden but sized until `playing` would need an `MpvVideoHost`
  change and a harness check.
- **Test gaps**: nothing automated drives a real root-frame sign-out, and the player page's
  `Unloaded` hook is covered only through its rule (`FluentFin\Mpv\PlayerPageRemoval.cs`,
  tests in `VideoPlayerViewModelMpvTests.PageRemoval.cs`; signout-ux), not through WinUI
  raising `Unloaded` on a real page; four reviewer mutations survive
  (the sign-out wait's 2 s bound, the redundant `_mpvLeaving` early return, the
  `ExitHandled` term in `Owed`, the re-check after subscribing in `WaitForStopReportsAsync`).

## mpv's bar in bridge mode (uosc, `backend\fluentfin`, osc-polish)
- **Seen off-screen only** (`tools\backend-harness\osc\osc_harness.py` on a private desktop,
  PrintWindow shots at 100 % and 150 % text size, windowed and full screen): not yet with the real
  mouse on the user's desktop, nor at a real 150 % display scale (the harness sets uosc's scale;
  mpv's own HiDPI scale on top of it is not measured), nor in light or high-contrast themes.
- **uosc's icons take the accent**: uosc has one foreground colour for icons, sliders and the
  seek bar's fill, so FluentFin's accent fills the elapsed time and also tints the icons (white
  icons would make the fill white too).
- **uosc's own menus** (audio, subtitles, chapters) list mpv's tracks, as the fork's buttons
  cycle them: a track mpv cannot see (a transcode's burned-in subtitle) is not in them, and a
  pick there is mpv's, not a renegotiation (FluentFin's own pickers and remote `set_audio`/
  `set_subtitle` go through the shim). Its subtitle menu also offers Load and Download, which
  need uosc's helper (`ziggy`, not vendored) and fail.
- **The Skip prompt is mpv's OSD message** ("Skip Intro  (S)", top left, 4 s) beside the bar's
  Skip button; uosc has no toast of its own. The button and prompt text come from the shim's own
  translations, the bar's tooltips are uosc's English.
- **Attached mode keeps the fork**: FluentFin's in-process mpv gets no construction option, so no
  `osd-fonts-dir` for uosc's icon font. `MpvInProcessCore` would need it (and `volume-max=100`) to
  run uosc there; with FluentFin's controls mpv's bar is hidden anyway.
- **A frozen backend** (`fluentfin-mpv-backend.exe`, later) must ship `backend\fluentfin\mpv`
  beside the package (`fluentfin_bar.ASSETS`); without it the bar falls back to the fork.
- **A uosc whose start fails** (a broken file) is found 2 s after it loaded and the fork
  loads instead (`fluentfin_bar.load`): for those 2 s, and for a uosc that fails later, there is
  no bar. A uosc that is merely slower than 2 s to start is taken for running.
- **The restore after a hide is deferred 150 ms** (`fluentfin.lua`): a `sub-pos` the bridge writes
  with `set_controls` "ui" lands within that and is kept; a later one is not told apart from the
  shim's. The race it closes was seen once in about ten runs and cannot be forced in a test.

## Start-up and pre-warm (stream `startup-perf`)
- **The window before the automatic sign-in was run, off-screen only**: the real Nixie.exe
  (Release, private desktop, its own data folder, the dev-harness fake server; before = main with
  the same data-dir change, 5 runs each, interleaved, on a machine busy with other test runs)
  shows its window 910 ms after the process started against 1469 ms before (850 / 1438 ms at
  50 ms per request), and the sign-in request now goes out after the window, not before; the
  first home row is asked for ~100 ms sooner; the pre-warm then takes ~0.6 s in the background
  with no window of mpv's inside FluentFin's. Never looked at on the user's desktop: the
  "Signing in to …" line was timed, not seen. Not run at all: the lazy Flyleaf start (the first
  Flyleaf play now starts FFmpeg: ~128 ms warm, ~0.9 s the first time after an install; unit
  tests only) and time-to-first-frame in the app (the e2e harness times PlayAsync to `playing`,
  below).
- **Pre-warm follows sign-in only**: choosing the MPV player in Settings while signed in does
  not pre-warm (the first play starts the backend, as before; the next sign-in pre-warms).
  Choosing another player leaves an idle pre-warmed backend running until sign-out or close.
- **With FluentFin's player controls (attached) the pre-warm only starts the backend**: the
  `connect` needs FluentFin's in-process mpv, which is made at the first play, so that play
  still waits for the connect (~0.5 s on a real server) and the in-process mpv. The setting is
  read when the pre-warm runs (2 s after sign-in): switching to FluentFin's controls after a
  pre-warm that connected restarts the backend at the next play (the window changed), as any
  switch of that setting does. Never run with libmpv here (this worktree has none): unit tests
  only.
- **A pre-warmed backend's first play** has mpv build its video output then, alongside the
  start's server requests (the price of no mpv window, GPU surface or attached input queues
  while idle): e2e PlayAsync to `playing` 165 ms loopback / 224 ms at 40 ms per request,
  against 66 / 198 ms with the output built at `connect` and ~640 / ~850 ms cold. If that
  first-play difference matters more than ~40 MB and the attached queues while idle, the UI
  can drop `prewarm` from its pre-warm `connect` (one argument in `MpvBackendHost.PrewarmAsync`).
- **mpv before 0.41** keeps the output after the first file only because `force-window` is
  stored (it is not acted on while idle there); only mpv 0.41 is installed, so untested.
- **O7 (players and subscriptions never disposed) not done**: it does not apply to the mpv
  player (no IMediaPlayerController is made; leaving the page stops playback and the backend
  stays, by design), and for VLC, Flyleaf and the Windows player the report's dispose-order
  risk needs its playback matrix and soak on a real server, which could not run here.

## Backend (`backend\jellyfin_mpv_shim\bridge*.py`, marked upstream diffs)
- **A hung trickplay fetch can push `shutdown` past its 8 s deadline** (pre-existing
  upstream: `trickplay.stop()` joins with no timeout, called from `terminate`). Seen at
  ~11 s, after the Stopped was already delivered; the UI kills the backend at 10 s, so no
  server state is lost, but BRIDGE.md §3's ~9 s bound does not hold for that endpoint.
  Fix: run `terminate` on a thread joined to the deadline.
- **A failed track/quality change on a transcoded item plays on only as long as mpv's
  buffer lasts** (upstream ends the old transcode before negotiating the new one; BRIDGE.md
  §5 "Known limit").
- **A `play` that fails leaves a stale `replaced` mark** on the item that plays on; if that
  item is later lost without another mark, `ended.reason` says `replaced` instead of
  `stopped` (cosmetic; the UI acts only on `error`).

- **A held end that runs at a `stop` or shutdown skips the finished callback's
  interrupted-stream branch** (`_end_held`): for a stream that failed mid-item while a `play`
  was pending, the position reported is right but the "server stopped sending it" notice is
  missing.
- **If you add `log-file=` to the backend's `mpv.conf`, that log gets your access token.** (`%LOCALAPPDATA%\Nixie\mpv-backend\mpv.conf`.)
  mpv's log records the request headers the backend sets, and the token is one of them. Don't share that log, and
  remove `log-file=` when you are done. Since 1.2.0 the backend's own log warns about it at every start (without the
  token). Not fixed: hiding that line from mpv's log needs a change in mpv. The off-screen harness never writes
  `log-file` and removes one it finds in its own data folder's `mpv.conf`.
- **`play_file` (`--dev` only) does not end a held end** (`cmd_play_file`): a `play_file`
  that supersedes a `play` whose item's end is held (the last item at its end) calls only
  `stop()`, which returns early there, so that item stays current under the file with no
  Stopped until something else ends it: a later `play` reports it and a `stop` ends it
  (`_end_held`), but the shutdown reports it with mpv's position in the file, not the
  item's. Fix if it matters: `_end_held(pm)` after that `stop()`.

## Test harnesses (`tools\`)
- **`Send-Keys` checks focus, then sends** (`tools\ui-harness\common.ps1`): the keys (f,
  Esc, q) go to whatever window is in front when they are injected, so a focus change in
  that instant could still send them elsewhere. Run the UI harnesses only when you are not
  typing in another app.
- Process-age matching uses `-ge` on creation times, and CIM times pass through local time
  (`ToFileTime`): during the DST fall-back hour matching fails safe (nothing is tracked and
  cleanup falls back to the job object).
- `backend\tools\bridge_smoke.py` waits only 5 s for `state: paused` after `pause`; with a
  build running alongside it missed once (1 of ~15 runs, 2026-11-14) and passed on rerun.
- `run-dev.ps1` and `rundev-close.ps1` close every running copy of **this checkout's** dev
  build by design: don't run them while you are using the dev build.

## Privacy note (upstream behaviour, unchanged)
- Fixed in 1.5: the shim asked `checkip.amazonaws.com` for the public IPv4 address on each play when the
  server address was a public IPv4 (its `is_local` check). It now decides from the server's address and this PC's name
  lookup, with no request: loopback, private and link-local addresses, a single-label name, `.local`, `.lan`,
  `.home.arpa`, `.internal` names, and any name that resolves to a private address (split DNS at home) are local;
  anything else is remote.
- **A home server reached by a public address, or by a name that resolves to one (hairpin NAT, no split DNS), counts as
  remote** since 1.5, so its plays keep the remote bitrate cap: the backend's `remote_kbps` (10,000 kbps by default) in `conf.json` in the data folder's
  `mpv-backend` folder. Raise it there if that is wrong for you (Nixie has no setting for it in its own Settings).

## Release package (`tools\release`, docs\RELEASE.md)
- **Not code-signed**: SmartScreen warns on the first start ("More info", "Run anyway"), and
  `backend\fluentfin-mpv-backend.exe` is an unsigned PyInstaller build. Needs a code-signing certificate.
- **libmpv's DLLs**: the release's `libmpv\` folder is our own build (`tools\release\libmpv`, `vendor\libmpv\VENDORED.md`),
  libmpv-2.dll and every DLL it loads; Libmpv.cs loads them from that folder only. `tools\release\libmpv_check.py`
  checks the folder against its BUILD-MANIFEST.txt and lists any graphics-driver DLL it imports (`vulkan-1.dll` and
  the like), which README.txt then names as a requirement. On a PC where libmpv does not load, FluentFin falls back to
  mpv's controls and says why.
- **mpv's controls need mpv.exe on PATH** (or a WinGet install): the package has no mpv.exe. Without one the
  backend's `connect` answers `mpv_not_found` (BRIDGE.md section 2) and the player page says to switch to
  FluentFin's controls or install mpv, with the winget line (`Core\Mpv\MpvNotFoundMessage`). When FluentFin fell back
  to mpv's controls because libmpv would not load, the first of those does not help on that PC.
- **Only the MPV and Windows Media Player players**: the release leaves out libvlc and Flyleaf's FFmpeg, and FluentFin
  offers only the players whose libraries are there (`Core\Settings\MediaPlayerAvailability`); a saved VLC or Flyleaf
  choice plays with MPV and stays saved. Their managed assemblies (LibVLCSharp, FlyleafLib) still ship: the XAML
  references them. A dev build has both libraries and offers all four.
- **The backend is English only**: the shim's `.mo` catalogs are built by `gen_pkg.sh`, not in git, not shipped.
- **Encrypted DVDs aren't supported** (irrelevant for a Jellyfin client: nothing it plays from a server needs it). The
  release has no libdvdcss (DVD decryption): libmpv-2.dll imports libdvdread-8.dll (and libdvdnav-4.dll), and
  libdvdread-8.dll imports seven functions of `libdvdcss-2.dll`, so the `libmpv\` folder ships our stub of that name
  instead, GPL-3.0, its source in the release's sources. It has libdvdcss's functions and every call fails.
  `build-release.ps1 -LibmpvExclude` (default `libdvdcss-2.dll`) leaves the real one and its source package out and
  keeps only a DLL that BUILD-MANIFEST.txt marks a stub (its owner says "stub" and is no MSYS2 package);
  `libmpv_check.py` fails the build on the real one, or on a stub missing a function the folder imports from it, and
  `verify-package.ps1` checks the unzipped folder again.
- **libmpv's sources**: every DLL of the `libmpv\` folder has its source in the release's sources (`dist\sources\libmpv`:
  the MSYS2 source packages with their signatures, mpv's tarball; `tools\release\sources.py` fails the build when a
  DLL's source is missing). The MSYS2 packages' signatures are copied, not checked by the build.
- **Verified off-screen only**: the zip, unzipped outside the repo, against the fake server with nothing on
  PATH but Windows (`verify-package.ps1`), on this PC. Not yet run on a PC without the .NET SDK or a GPU driver.

## This PC's test environment
- 8 environmental failures in the backend suite: `test_mpvtk_pilfont` ×2 (an installed font
  Pillow cannot read), `test_audio_settings` ×6 (the fake player models the libmpv path
  only).
- The long `test_shell_*`, `test_live_tv`, `test_dpi_matrix` and `test_playlist_edit`
  modules can exceed the parallel runner's 300 s per-module limit when the machine is busy
  (late on 2026-11-14 even when idle); each passes alone with
  `run_suite.py --serial --timeout 1500 -- -k <module>`, and timed side by side they are
  exactly as fast on the baseline as on the pre-round copies.
