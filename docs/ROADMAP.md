# Nixie roadmap

Updated 2026-11-21 (1.7 started beside 1.6.1 at the owner's call; 1.6.0 released, before its Sandbox upgrade test at
the owner's call; 1.5.1 released; Google TV ready for the owner's TV session; sizes re-measured). These are
the planned releases in order, with the backburner ideas placed where they fit. Version numbers past 1.6 are a guide.
**Sizes are calendar time at the measured pace** (below): several streams in parallel, each building in hours, plus a
fixed tail of about half a day per release. A date past today is an estimate, not a promise; usage, the 3 test slots
and the owner's own steps are the real limits.

| Release | Name | What | Status (estimate at the measured pace) |
|---|---|---|---|
| 1.4.0 | Consistency | The library layer for all four themes, plus the contrast floor and big libraries | **Released** 2026-11-20 |
| 1.4.1 | Continue Watching | Rewatched shows come back, Home updates as the player closes, faster season switching | **Released** 2026-11-20 |
| 1.5.0 | Search and updates | Better search and a responsive updater (both musts), plus the 1.4.0 follow-up fixes | **Released** 2026-11-21 |
| 1.5.1 | Fixes | The fix queue 1.5.0 left: the bug hunt's findings, the learned "Skip intro" at 0:00, small leftovers | **Released** 2026-11-21 |
| 1.6 | Collections | Favourite libraries in the top bar, tidy names, smart collections from the server's data, the Collection page, and theme polish (Drive-in's look on the shared pages, Editorial's list view), skipping repeated extras (its own stream), and the player's quality choice and playback stats (its own stream) | **Released** 2026-11-21, a day early (before its Sandbox upgrade test, at the owner's call; the test passed right after) |
| 1.6.1 | Fixes | Clears what 1.6's reviews and bug hunt queue up, and anything 1.5.1 didn't finish | **Now** (started 2026-11-21 18:10, 3 streams): release expected **2026-11-22** |
| 1.7 | Discover | Trending, awards and finer genres from outside sources, opt-in | **Now** (started 2026-11-21 19:40, 2 streams beside 1.6.1, which goes first): release expected **2026-11-23**. Trakt may not need a new registration (see the 1.7 section) |
| 1.8 | Player | Pop-out player, chapter extras | About 1 day once the owner confirms what the pop-out player means |
| 1.9 | Social | Friends and suggestions through the shared server, the Diary fixes | Research about half a day, building 1 to 2 days, after the owner's choices |
| 2.0 | Rust | A time-boxed spike, then go or no-go | The spike about 1 day; if go, a rewrite of 1 to 2 weeks even at this pace |
| TV 1.0 | Google TV | A separate app based on Glacier (a local fork), with Nixie's three themes; no home-screen Continue watching (the TV filters it) | Built, reviewed and bug-hunted **2026-11-21**; **ready for the owner's 20-minute TV session** (`Dev\nixie-tv\docs\TV-SESSION.md`), then the key and where releases live |

**Measured pace (1.5 on 2026-11-20, 1.6 and 1.5.1 on 2026-11-21, from the work ledger):**

| Step | Measured | Notes |
|---|---|---|
| A stream builds its items | **1 to 3.5 hours** (median about 2.5) | 1.5: search 1 h 55, updater 2 h 27, fixes 3 h 17, season 3 h 22, qacheck 3 h 21. 1.6: colpage 2 h 32, player 2 h 34. 1.5.1's fixes 1 h 07. Items once sized M to L each took one stream an afternoon |
| A review | **10 to 35 minutes** | One reviewer per stream, read-only |
| Fixing a review's Blockers and Majors | **1 to 2.5 hours** | Usually 1 to 4 Majors per stream |
| Integration, final pass, baseline and the release build | **about 2.5 hours** | 1.5: 17:54 to 20:30, every gate in four themes on both test servers |
| The bug hunt | **25 to 70 minutes** | Beside the final pass |
| The Sandbox upgrade test and publishing | **about 30 minutes** | When Sandbox works; on 2026-11-20 it stuck until a PC restart, which moved 1.5.0 to the next morning |
| **A feature release, end to end** | **about 9 to 10 hours of work**, so about a day | 1.5: launched 11:39, release build at 20:30, published the next morning (the Sandbox restart). 1.6: launched 08:12, published 17:50 the same day |
| **A fix release** | **about half a day** | 1.5.1: launched 08:12, fixes in at 09:19, review and hunt by 10:26 |

**What actually limits the pace:**
- **Usage:** on 2026-11-20, 1.5 took the weekly allowance to 100% and the owner reset it by hand; on 2026-11-21, after
  the reset, eight agents ran at once from about 15%. More parallel streams spend it faster, not slower.
- **The 3 off-screen test slots:** every stream's harness runs, the final passes, and now the TV emulator share them.
  Waits of up to 30 minutes were seen, and one hunt probe gave up waiting.
- **The owner's own steps:** hand checks, a restart when Sandbox sticks, the TV session, the release key, registrations
  (Trakt), and decisions (the skip questions, the pop-out player, social).

**Split (owner, 2026-11-20: "Yes, split it"):** the second review found 1.5 too big at about 40 items, with four streams
editing the same files. 1.5 now ships search, the updater and the fixes; the top bar, tidy names, collections and the
Collection page become 1.6.

**Hotfixes:** a Blocker ships right away as a 1.x.y hotfix. Everything else waits for the next planned release.

## Release rules (from 1.6; the owner, 2026-11-20: "yes")
The owner noticed that fixes kept pushing features down the road, and that releases were mostly fixes. 1.5's running
fix streams finish as planned. From 1.6:
1. **Two kinds of release:**
   - **feature releases** (1.6, 1.7, …) carry their planned features only;
   - **fix releases** follow each feature release and clear its fix queue: 1.5.1, then 1.6.1, 1.7.1, and so on (the
     owner, 2026-11-21: "shouldnt there 1.5.x fix builds?"). A fix release runs as one stream beside the next feature
     release's streams; when usage is short the feature gets it first, and what a fix release doesn't finish moves to
     the next one.
2. **Every finding is triaged** (reviews, bug hunts, the owner's reports):
   - a **Blocker** ships right away as a hotfix;
   - a **Major in the feature's own new code** is fixed before that feature merges;
   - **everything else**, including old bugs a review or hunt turns up, goes to the **fix queue** (a section in
     KNOWN-ISSUES) for the next fix release, and doesn't hold the feature.
3. **Features get usage first.** When usage is short, fixes outside the feature wait, not the feature.
4. **A release ships with its known Minors listed** in KNOWN-ISSUES, instead of fixing them all first.

**In practice:** reviewers and the pre-release hunt label each finding Blocker, Major-in-new-code or queue; streams fix
only the first two; the integrator moves queue items into KNOWN-ISSUES' fix queue.

## 1.4.1: Continue Watching (released 2026-11-20)
- **A rewatched show comes back.** Up next now asks for rewatches. Jellyfin 12.4 keeps an episode marked watched while
  it's rewatched, so in-progress watched items stay in Continue Watching. A show appears once across both rows.
- **Home updates the moment the player closes,** then confirms with the server.
- **Faster season switching:**
  - seen seasons ask the server nothing;
  - the seasons on either side load ahead;
  - the last click wins;
  - Classic's and Drive-in's season buttons keep the keyboard on the current season;
  - Editorial downloads each still once.
- **The local Jellyfin test servers' tools** (`tools\qa-servers\`).

## 1.4.0: Consistency (released 2026-11-20)
The full list is CHANGELOG `## 1.4.0`:
- filters you can see, a sort that names its direction, one A to Z, poster sizes and a list view;
- one Collection page, and "Part of" on film pages;
- every library as a top-bar tab with one lit destination;
- one accent and one highlight per meaning, and one button system;
- Up next and Chapters in every theme;
- a film page opens on Play.

**Late fixes:**
- the crash on Back to a Collection page;
- pages taking the keyboard;
- YouTube trailers (Error 153);
- **big libraries load a page at a time.** A 25,842-film library used to time out and never load.
- Editorial's freeze after a far scroll, and its leftover brief pauses;
- **the contrast floor** (plan item 3, never assigned until the roadmap review found it):
  - Drive-in Light's top bar at 4.5:1 (DI-B1, a Blocker in the default theme);
  - plates under Cinematic's controls on artwork (C-M2, C-M9);
  - a test over the worst-case art in all four themes.

**Checks by hand at release:**
- in the player, press Space before any click, then move the mouse;
- Esc inside YouTube's player;
- a real trailer plays.

## 1.5: Search and updates (released 2026-11-21)
Three parallel streams (search, the updater, fixes), each owning its files, as in 1.4.0. The musts go first.

**Progress (2026-11-20, 13:30):**
- **Search is done** (7 commits, in review; 14:45):
  - the why: Jellyfin 12.4 caps a search's total at 3 times its limit, and its built-in search can't match 300 films
    for "it's always sunny", so the owner's server must run a looser search plugin (`docs\research\search.md`);
  - the test servers now hold a "QA Search" library of 3,035 films and 29 shows;
  - its live check passes 12 of 12 on both test servers.
- The search review found no Blocker but three Majors, which the search stream is fixing before the merge:
  - "star" put *Starman* above *Star Wars* (word breaks);
  - the test servers' smoke check broke on the new library;
  - checks reported but never rerun.
- **The updater is done** (9 commits, in review; 15:15). All ten items are in, including the Sandbox test script,
  which so far has only run as a dry run. The keyboard check passes in all four themes. Its review found no Blocker and
  a sound download path. The one Major is fixed: a failed install turned back into "ready" and reinstalled silently at
  every close.
- **The fixes stream is done** (in review; 15:45):
  - the Alt ding is gone: Alt+Enter and Alt+X now get a silent answer;
  - T5 is fixed at launch and after a page change;
  - the rewatching switch;
  - the button leftovers.

  Its review found two Majors, being fixed: the rewatching switch took up to 5 minutes to reach Home, and a server
  behind a reverse proxy counted as local. Local or remote is now decided from the server's address alone.
- **Search and the updater are merged** into the 1.5 branch, and it builds clean (16:40).
- **Qacheck is done** (in review):
  - a playlist now plays as a playlist on both test servers (Play used to queue the first episode's show);
  - the sub-path and trailing-slash cases pass;
  - the fake server refuses the old token forms.

  Found, not fixed (the track code): a track change during a transcode reports a second start.
- **Season is done** (in review): season switching is about twice as fast, with pictures arriving with the rows. For
  example, Drive-in's next season went from 448 ms rows and 1.24 s pictures to 290 ms for both.
- **The owner reset the weekly limit at 100%** (17:00). Fixes and season finished their review fixes: no Blockers, all
  Majors fixed. Qacheck is fixing the transcode's double start, which was already in 1.4.1 and inflates play counts on
  the server.
- **The integration has started** (17:20): it merges fixes, season and qacheck, sets version 1.5.0, and runs the final
  checks. A performance baseline is new this release: start-up, first Home paint and memory, for 1.4.1 against 1.5.0.
  Then the release build, the bug hunt, the Sandbox upgrade test, and publish.
- **All five streams merged**, and version 1.5.0 is set (18:00).
- **The bug hunt found no Blocker** (`docs\reports\Bug hunt 2026-11-20 (1.5.0).md`). It found 3 Majors, all older than
  1.5:
  - Classic Home's error notice drawn under the top bar;
  - a refused sign-in reading as a dead server;
  - Enter, Enter on a show going back.
- **Its 8 Minors:** 5 are new in 1.5, among them:
  - "With <name>" opening on Back;
  - a home server reached by name counting as remote;
  - a test-only install switch working in release builds.
- **The owner: "Ship now, queue them"** (2026-11-20, 18:30). 1.5.0 ships without the fix round:
  - all 11 hunt findings go to KNOWN-ISSUES' fix queue for 1.6.1, and the noticeable ones are listed as known issues in
    the 1.5.0 notes;
  - the fix round had already committed fixes for 8 of them (m1–m6, m8, M1 and M3) before it stopped, but not finished
    their tests. They're kept on `wip/150-huntfix` as the start of 1.6.1;
  - M2 (a refused sign-in) and m7 (search and home videos) have no fix yet.
- **The final pass passed** (overnight): the keyboard check in all four themes, the contrast floor, 40 of 41 theme
  scripts (Cinematic's placeholder check at 1920 wide fails the same way in 1.4.1), search, playlists, a transcode and
  the smoke check on both test servers, both UI suites, Core, and the end-to-end and bridge checks. The release package
  passed its 121 checks, and Defender found nothing.
- **The first baseline** (median of 5 runs, 1.4.1 against 1.5.0): the window shows in 962 against 1,016 ms, Home first
  paints in 1,132 against 1,138 ms, and memory after the browse script is 575 against 584 MB (`docs\RELEASE.md`,
  "Baseline").
- **The Sandbox upgrade test didn't finish.** It installed 1.4.1 as a standard user, then stopped on a bug in the test
  script, not in Nixie: Windows PowerShell won't start a program with an empty argument list (fixed on main). The second
  try found Windows Sandbox itself stuck, which only a PC restart clears.
- **The owner: "Publish now"** (2026-11-21). **1.5.0 is released**
  (github.com/mbustos27/fluentfin-mpv/releases/tag/v1.5.0). Its notes say the upgrade test is still to run.
- **The Sandbox upgrade test passed** (2026-11-21, 07:45 to 07:53, after the owner's restart). As a standard user:
  1.4.1 installs and copies the Seaglass folder; 1.5.0 installs over it by the old flow and keeps the data; 1.5.0's
  Restart to update installs the test build and opens Nixie again, once. No 1.5.1 needed. The test's own three faults
  (a log check that a quiet start can't meet, results lost to a Windows PowerShell 5.1 quirk, the tester's exit code)
  are fixed on main with a permanent check. Still to watch: the "Updated" notice, first seen for real on 1.5.0 to 1.5.1.
- The fixes stream finished the backend and privacy items in its first half hour:
  - private certificates;
  - split-season anime;
  - "Search online";
  - chapters retry;
  - BH-140-m5;
  - no `checkip` call;
  - the Jellyseerr inventory.

  BH-140-m8 had already been fixed in 1.4.0.
- To ship sooner (owner: "Yes, split it"), the rest of fixes runs in three streams:
  - **fixes:** the Alt ding, T5, the rewatching switch, the button leftovers;
  - **season:** the season-speed leftovers;
  - **qacheck:** real playlists and a transcode on the test servers, the sub-path and trailing-slash checks, the fake
    server's strict auth.
- **The Alt ding went first.** Off-screen test runs were making Windows' error sound on the owner's PC.
- **Usage:** the owner resets the weekly limit by hand when it reaches 100%.
- **Target:** late on 2026-11-20, or 2026-11-21.

**Search stream (a must for 1.5, owner 2026-11-19: "search is pretty bad")**

On the owner's server, "it's always sunny" gives "Movies (100 of 295)": a fixed Movies group first, full of unrelated
films, with the show itself nowhere in view.

Today each type is asked separately (`/Items?searchTerm=`, `Limit=100`, no sort), and the groups always come in a fixed
order (`JellyfinClient.Search`, `SearchResultsViewModel`). The UX review's CL-m9 is the same finding.

- **Best match first:** an exact or near-exact title of any type leads as a top result. The groups follow in the order
  of their best match, not Movies first.
- **Ranking on the PC:** a whole title beats a title's start, which beats all the words, which beats some of them.
  - Apostrophes, punctuation and accents count as equal ("it's" = "its").
  - Original and sort titles count too.
  - Episodes lead to their show.
- **Weak matches stay out of the way:** show the strong matches, then "N weaker matches" on request, not 295 films.
- **Type chips:** All, Shows, Movies, Episodes, Collections.
- **Ask the server better before ranking** (review 2026-11-20). Re-ranking the server's first 100 per type can't
  surface a title the server never returned, so query Shows and `/Search/Hints` first, merge them, then rank.
- **Find out why the server returns 295 films:** compare `/Items` and `/Search/Hints` on the local test servers (10.11
  and 12). The test servers hold about 1,100 items, so first seed a few thousand titles there, including near-misses
  like "It's Always Fair Weather".
- **People:** a person result opens a "With <name>" list, a library filtered by that person, until the person
  collections arrive. People search for actors.
- **Tests:**
  - a fixture of tricky titles (apostrophes, "The", numbers, unicode, one-word titles);
  - a live check on the test servers, with the large seeded library;
  - search keeps its keyboard rules (Ctrl+F, /, Down into the suggestions, Enter, Esc).

**Updater stream (a must for 1.5, owner 2026-11-20: "definitely add to the roadmap a more responsive in app updater")**

What it does today:
- **Checking:** 20 s after start, then once every 24 hours while Nixie is open (`UpdateNoticeService`,
  `UpdateGate.Interval`), so a release published while Nixie is open waits up to a day.
- **"Download and install":** downloads the whole installer before showing anything, runs msiexec with its full window,
  and closes Nixie without reopening it.

The plan:
- **Find updates sooner:**
  - every few hours while Nixie is open;
  - when the window comes back after a while (focus, waking from sleep);
  - within GitHub's 60 requests an hour, honouring its Retry-After.
- **Show every step** (owner: "things like download progress time and other things otherwise now you just click check
  update and wait"):
  - **Check now:** says it's checking ("Checking for updates…"), then answers at once: "You're up to date · checked just
    now", "1.4.2 is available · 187 MB", or the reason it couldn't check.
  - **Downloading:** a progress bar with MB done of total, speed, time left, and Cancel.
  - **Then:** "Checking the download…", "Ready to install", "Installing…", each named as it happens.
- **Get it ready in the background:** download as soon as an update is found (a setting), with the same progress where it
  shows (Settings, the notice). Check it against its `.sha256` **and** against the digest GitHub's API reports for that
  asset, since the hash file sits beside the installer. There's no code signing.
- **One click:** "Restart to update" closes Nixie, installs quietly, and reopens Nixie where you were. "Install when I
  close Nixie" stays an opt-in, off by default. That the per-user installer needs no admin prompt is unproven until the
  Sandbox test shows it.
- **Afterwards:** "Updated to 1.x · What's new", with the release notes in the app.
- **Failures** say what went wrong, with Retry, and the release page as a fallback.
- **Tests:**
  - a fake release that appears while Nixie is open is found within the new interval;
  - progress, a checksum mismatch, and a resumed download;
  - the install-and-reopen handoff proven in Windows Sandbox (on since 2026-11-20), never in the owner's account.
- **The new updater can't update itself into 1.5** (review 2026-11-20). 1.4.1 users reach 1.5 through the old flow,
  which closes Nixie without reopening it. The Sandbox test covers both legs:
  - 1.4.1 to 1.5, the old flow;
  - 1.5 to a test build numbered higher, the new flow.

  The first real-world use of the new flow is 1.5 to 1.5.1, so watch it.

**Fixes stream** (bug hunts and the library review):
- **Private certificates:** a server with a private or company certificate plays, not only browses. Check both the
  backend's TLS and libmpv's.
- **Split-season anime** get the right skip times.
- **mpv's controls:** no "Search online" for subtitles, and no bundled subtitle key.
- **One-click updates** survive a repository rename.
- **The ledger's minors:**
  - collections and playlists of home videos or music videos (BH-140-m5);
  - a failed chapters fetch lasting all session (m7);
  - the Collection page unsubscribing (m8).
- **Season pages, the rest of the speed work** (after 1.4.1):
  - stills that arrive 0.4–0.9 s after the rows;
  - Drive-in's 0.4–0.7 s to build its rows;
  - a new season page waiting for the season item;
  - a far season costing a round trip.
- **T5:** Home never wears both the focus box and the underline at launch.
- **No Windows "ding" on Alt shortcuts:** Alt+Enter likely plays Windows' error sound, because the window has no menu
  to handle Alt+key (`WM_MENUCHAR`). Nixie should swallow it. Found 2026-11-19 while tracing system sounds during
  keyboard-matrix runs; not yet confirmed.
- **The button review's leftovers** in KNOWN-ISSUES.
- **Privacy:**
  - stop the backend's `checkip.amazonaws.com` lookup on every play (an upstream jellyfin-mpv-shim habit). Ask the
    server for its own address instead, or drop the check;
  - take stock of what the bundled Jellyseerr plugin calls before 1.7 promises "nothing about you leaves the PC".
- **Real playlists and a transcode through Nixie itself** on the test servers. The servers' own smoke checks pass, but
  Nixie's playlist page (the old X2 Blocker) has never run against a real server.
- **Checked:** a reverse proxy on a sub-path, and an address ending in "/".

**Test tooling, in place:**
- off-screen test runs 3 at a time;
- local Jellyfin test servers 10.11 and 12 (`tools\qa-servers\`);
- the fake server refuses what Jellyfin 12 refuses, and gets one owner, since the desktop streams and the TV app all
  change it.

**New checks from 1.5 on** (see "Every release"):
- real-server checks;
- the upgrade test in Windows Sandbox;
- accessibility by hand;
- the performance baseline.

## 1.5.1: Fixes (released 2026-11-21)
The first fix release (the owner, 2026-11-21). One stream from v1.5.0 (`wip/151-fixes`), with 1.5.0's gates: the full
suites, a review, the bug hunt, the release build, and the Sandbox upgrade test from 1.5.0 to 1.5.1, the first real
check of the "Updated" notice.
- **The bug hunt's fixes already written** on `wip/150-huntfix` (M1, M3, m1 to m6, m8): finish their full suites and
  harness runs, review them, one revert check per fix.
- **The two the fix round didn't reach:**
  - M2: a refused sign-in reads as a dead server (and the per-PC device id behind it);
  - m7: search doesn't find home videos, music videos or audio.
- **Sunny's learned "Skip intro" at 0:00:** skips 10 s or more apart mark the show as "moves", and its fixed spot is no
  longer offered.
- **If small:** Display's date-added choice, show-level anime ids for seasons 2 and on, sentence case for row names.
- **Usage:** 1.6 gets it first. Anything not ready by 1.5.1's build waits for 1.6.1.

**Progress (2026-11-21):**
- **The fixes are in** (09:15, 14 commits): the hunt's fixes, M2 (sign in again; a device id per copy, and a 1.5.0
  sign-in still works), m7 (search finds home videos, music videos, albums and songs), Sunny's "moves", Display's
  date-added choice and show-level anime ids. Version 1.5.1 is set. The suites pass, and the sign-in check passes in
  all four themes on both test servers.
- **The Sandbox test gains a leg** where 1.5.0's own Restart to update installs the real 1.5.1 and must report
  "Updated".
- **Left for 1.6.1:** sentence case for row names (61 names across 16 test scripts, over the hour's limit) and the
  episode search for "With <name>".
- **The review and the bug hunt found no Blocker** (`docs\reports\Bug hunt 2026-11-21 (1.5.1).md`). Three Majors in
  this release's own code, being fixed:
  - "moves" was judged across seasons, so a show whose intro sits elsewhere in season 2 would lose its learned Skip in
    every season. It's judged within a season now;
  - search offered songs and albums, but an album can't play yet. Music leaves search for 1.5.1 and comes back with
    playback in 1.6.1;
  - the Sandbox test's new leg looked for `Updated` without the quotes Nixie's log puts around it.
- **The rest go to 1.6.1's queue,** among them: the saved-password sign-in has no test; the live-updates connection
  keeps redialing with a refused sign-in; a portable copy counts as a new device; time watched is lost when the sign-in
  ends mid-playback.
- **The final pass passed** (14:00): the keyboard check in all four themes, 43 of 44 theme runs (Cinematic's placeholder
  check, the 1.5.0 issue), every run on both test servers, both UI suites, the backend and the bridge. The release
  package passed its 121 checks, Defender found nothing, and 1.5.1 measures the same as 1.5.0 in the baseline.
- **The Sandbox upgrade test passed every check** (13:58 to 14:12, as a standard user): 1.5.0 to 1.5.1 by the old flow,
  1.5.1's own Restart to update, and, the first real check of it, **1.5.0's Restart to update installing 1.5.1, which
  opens again and logs "Updated"**.
- **1.5.1 is released** (2026-11-21, 22:19 UTC; github.com/mbustos27/fluentfin-mpv/releases/tag/v1.5.1), squashed onto
  main as `fa95aa0`.
- **From launch to release:** about 6 hours (08:12 to 14:19): fixes 1 hour, review and hunt 1 hour, their fixes and the
  final pass with the build about 3.5 hours (the test slots were shared with six streams), the Sandbox test and
  publishing 30 minutes.

## 1.6: Collections (released 2026-11-21)
**Started 2026-11-21** (the owner: "Start 1.6"), every branch from v1.5.0:
- the top bar, collections and colpage streams;
- **theme polish goes to colpage,** which already owns the Collection page, the library pages and their headers;
- the skip stream: step 0 and the skip rules first. Until the owner answers its open questions (below), it builds the
  plan's defaults: a Skip button, not skipping by itself, and rules per show;
- Google TV phase 0 beside them (below);
- **the player stream** (added the same day; below).

**Player: quality and playback stats (added 2026-11-21, about M, a stream of its own).** The owner: "we need to be able
to adjust the quality of the stream adjust bitrate that kind of things and also have a little activatable for nerds
window like in youtube that is toggleable in the settings it will be a button thats toggleable".
- **Quality, in every theme's player:** Auto (today's behaviour and the default), Original (direct play, no transcode),
  and caps like Jellyfin's own list (about 120, 80 and 60 Mbps for 4K; 40, 20, 10 and 8 for 1080p; 6 and 4 for 720p;
  3 and 1.5 for 480p). The menu says what's playing now ("Direct play · 1080p · 12 Mbps", "Transcoding · 8 Mbps") with
  the server's reason. A change picks up at the same position as the same viewing, through a new `set_quality` bridge
  command (the backend can already renegotiate: the shim's `trs_ovr`). Settings: a default for home and one for away,
  using 1.5's local or remote rule.
- **The owner's server isn't theirs:** a lower quality makes it transcode, and their account may not be allowed to. A
  refusal is said in plain words and playback goes on. Nixie never moves the default away from Auto by itself.
- **Playback stats** (Nixie's own name, not YouTube's): a Settings switch, off by default, puts a button in the player
  that opens and closes a small glass panel over the video, in each theme's style. It shows the play method and the
  server's transcode reasons; the container; video codec and profile, resolution (source to output), frame rate, HDR
  and hardware decoding; dropped and delayed frames; audio codec, channels, sample rate and track; the subtitle
  format; the server's and the measured bitrate; buffer ahead (seconds and MB) and network speed; local or remote and
  the mpv version. It updates about once a second while open and reads nothing while closed. A Copy button for bug
  reports never includes the token. A toggle key that clashes with no other.
- **Gates:** the keyboard matrix in four themes, contrast, shots, the QA servers (one transcode, one direct play), and a
  review (the bridge and player files are sensitive).
- **Shares the player controls, Settings and the bridge with the skip stream:** each keeps its own marked blocks, and
  the integrator merges them one after the other.

The top bar, tidy names, collections and the Collection page touch the same pages (the Collection page, its cards and
chips, the top bar's Collections destination, the fake server), so they're built together. Theme polish joins them
(release rules, 2026-11-20), since it's on the same pages.

**Theme polish** (was 1.6.1):
- **Drive-in's own look** on the Libraries and Collection pages.
- **Editorial's list view** (from the backburner).

**Top bar stream** (decided 2026-11-19):
- favourite libraries in the top bar, starting from the pins 1.3 users made, which are still stored
  (`TopNavigation.cs`, `PinsSetting`);
- one Collections destination;
- a "Libraries ▾" with a single meaning.

**Collections streams** (`docs\reports\Nixie smart collections plan.md`, phases 0 and 1):
- **Check the data first:** run `live-server-index.ps1` on the test servers. Ship each kind of collection only where most
  titles carry its data (TMDb collection ids, keyword tags).
- **Tidy collection names:** "[HUB] Marvel" shows as "Marvel". Titles like "[REC]", "+1" and "#Alive" stay as they
  are. The owner's server also names **libraries** "[HUB] Trending Movies", "[HUB] Kids Movies" and so on, and they fill
  the top bar. Tidy library names the same way.
- **The collections:**
  - franchises, decades, directors, actors, studios, genre mixes and keywords like "Heist";
  - "Highly rated and unwatched", short films and unfinished series.
- **Where they show:**
  - one Collections page;
  - up to three Home rows;
  - a presets strip in each library;
  - "Save as collection".
- **The Collection page and cards:**
  - one meaning per card chip (LB1);
  - one Unwatched control (LB2, LB3);
  - a header backdrop and a themed header (C1, C5);
  - room for titles in Drive-in and Editorial (L2, C4, LB4);
  - a long Play label no longer pushes Shuffle off the line (C6);
  - no "6 films" three times (C2).
- **"Save as playlist":** an explicit button that copies a Nixie collection to the server as the user's own playlist.

**Skip repeated extras (proposed for 1.6, 2026-11-20; plan written):**
- **What the owner sees:** on their server, "What We Do in the Shadows" episodes carry FX network extras (bumpers,
  promos) baked into the files. The owner: "we need a way to like cut a general bit i guess or learn what we skip".
- **What Nixie has today:**
  - **it already learns one skip:** a forward jump of 30 to 150 s in an episode's first 10 minutes is offered as "Skip
    intro" on that season's later episodes. Nothing shorter or longer, nothing at the end or mid-episode, one per
    season, and none once the server, the chapters or the online lookup gave an intro;
  - it reads the server's own "Commercial" and "Preview" segments, but they're off with no switch in Settings, and the
    owner's server likely marks none. The owner isn't an admin there, so a server plugin (Intro Skipper) is out.
- **Recommended** (a stream of its own: the player, Settings and the backend's skip files, none of the collections
  work's):
  - **Step 0 (S):** a Settings switch for the server's ad and preview segments, and file chapters named like
    "Commercial" or "Ad break" count as ads.
  - **Skip rules (M):**
    - a per-show **"Skip this part every episode"** the user marks once in the player, measured from the start or
      before the end;
    - **learned skips:** after the same skip in two episodes of a show, Nixie asks "Skip this every episode?";
    - kept on the PC per server, user and show, shown with the Skip pill every theme already has;
    - Settings' "Learn intros from my skips" becomes "Learn skips from my seeks", with a list of rules to forget.
  - **The bridge** (`docs\BRIDGE.md`): commands to list, add and forget rules, a `skip_suggestion` event, and a
    `"rule"` segment source.
- **Release fit (release rules: a planned feature, so a feature release):** all of it is 1.6's skip stream, a stream of
  its own (about L): step 0 (S), the skip rules (M) with their margin (S), then audio-matched intros (M). What doesn't
  fit becomes the first item of 1.7, not a 1.6.x.
- **Intros that move (the owner, 2026-11-20):** "certain shows such as its always sunny in philadelphia has the title
  theme at random points in the first 6-7 minutes". The intro follows a cold open whose length changes every episode.
  - **Position rules can't catch it,** and today's learned skip misleads: it offers the last episode's spot during the
    cold open, and a skip elsewhere never corrects it that season. The fix (S; in 1.5.1 since 2026-11-21, and kept by the
    skip stream's rewrite): skips 10 s or more apart mark the show "moves", and its fixed spot is no longer offered.
  - **Evidence (the owner, 2026-11-20):** "Skip intro" shows on Sunny today, "but its sometimes right at the
    beginning", where pressing it skips the cold open. Most likely the learned skip (a forward jump near 0:00 in an
    earlier episode of that season); chapters, TheIntroDB or the server can also put an intro at 0:00. The pill's
    tooltip tells them apart, as does whether it shows before any skip in a season, and Settings > "Forget learned
    intros" removes a learned one. Workaround until 1.5.1: forget learned intros, and turn learning off. It's in
    1.5.1, not a hotfix: it's a button that never skips by itself, and seeking back undoes it.
  - **Try first, no build:** turn on "Look up intros online". TheIntroDB has Sunny with a time per episode (checked:
    S1E1 1:44–2:12, S5E3 1:37–2:08, S12E1 2:07–2:31), but its times are for its own copy, so a bumper before the
    episode in the owner's files would make them early. A constant shift can be taught by one correction (S).
  - **Audio matching becomes a real need** (M, 1.6's skip stream, after the rules): learn the theme's sound once, from
    a skip or a mark, out of what mpv has already read ahead; recognise it in later episodes in the first 10 minutes,
    so the Skip pill shows at the title card. No extra download beyond normal playback. Costs a fingerprint tool
    (`fpcalc`, a few MB) in the installer and temporary files during those 10 minutes.
  - **The two-day spike moves ahead** (S): a test tool and generated media on the local test servers, no shipped
    files. It checks whether mpv can save the read-ahead audio only, how far ahead it reaches, the tool's speed and
    licence, and hits and false hits. It runs once usage allows (1.5 has the usage until it ships).
  - Scanning whole episodes stays out: gigabytes from a server that isn't the owner's.
- **Plan:** `docs\reports\Nixie repeated extras skip plan.md`.
- **The owner answered (2026-11-20): the extras sit before and after the episode**, not mid-episode.
  - So the skip rules cover them: two rules per show, one measured from the start and one from before the end.
  - The audio-matching spike isn't needed for this case (Sunny's moving intro needs it; above).
  - Today's learned skip can't catch either part: it only looks in the first 10 minutes, for jumps of 30 to 150 s, and
    stops once an intro is known.
- **The owner's second answer (2026-11-20):** the extras' length **varies a bit** from episode to episode. A rule with a
  fixed end would land a little early, showing some promo, or a little late, cutting the episode. So the rules get a
  margin (proposed, adds about S):
  - **a Skip button by default,** not skipping by itself, so the viewer sees where it lands;
  - **the end snaps** to the nearest black frame or silence within a few seconds of the rule's end. It reads only that
    stretch of the file (a few MB), never the whole episode;
  - **each correction teaches the rule:** a seek back or forward right after a skip moves the rule's end.
- **Still to ask the owner:**
  - whether the player's Chapters list shows a chapter at the extras or at Sunny's theme;
  - a Skip button, or skip by itself;
  - one rule for the whole show, or per season;
  - Sunny's 0:00 "Skip intro": what its tooltip says, and whether it's gone after "Forget learned intros";
  - with "Look up intros online" on, whether it lands on Sunny's title card;
  - a fingerprint tool in the installer, and temporary files while watching: OK?

**Released (2026-11-21):**
- **Five streams** were reviewed with no Blocker, and every Major was fixed: the top bar, collections, the Collection
  page with theme polish, skipping repeated parts, and quality with Playback stats. They were then integrated with 1.5.1:
  1.5.1's per-season rule for intros that move now runs inside the skip learner.
- **The final pass:**
  - the keyboard matrix (2,314 rows) and the contrast floor (4,408 labels), in all four themes;
  - both QA servers, e2e and the bridge smoke test;
  - 54 of 58 theme runs; the 4 that failed are queued for 1.6.1;
  - the integration found one gap and fixed it: a Nixie collection's page showed neither "Made by Nixie" nor where it
    came from.
- **The package:** verify-package passed 121/121, and Defender found nothing (22 minutes). The baseline matches 1.5.1,
  except that Home's first cards come about 115 ms later.
- **1.6.0 is released** (2026-11-22, 01:50 UTC, which is 17:50 on 2026-11-21 in California;
  github.com/mbustos27/fluentfin-mpv/releases/tag/v1.6.0). It was squashed onto main as `ab57230` and tagged `v1.6.0`.
  Its 8 assets match their digests. It was published before its Sandbox upgrade test, at the owner's call (Decided,
  below); the test from 1.5.1 runs right after.
- **After publishing:** the bug hunt and a review of the three integration commits found no Blocker. Their findings
  are in 1.6.1 (below).
- **The Sandbox test's first run:** legs 1 and 2 passed every check (1.5.1 to 1.6.0 by the old flow, and 1.6.0's own
  Restart to update). Leg 3 failed on a fault in the test: it started 1.5.1's install switch without the test's marker,
  which 1.5.1 itself requires. The fix is `f71865a`, with a test.
- **The Sandbox upgrade test passed every check** on its second run (18:08 to 18:20, as a standard user):
  - 1.5.1 to 1.6.0 by the old flow;
  - 1.6.0's own Restart to update;
  - **1.5.1's Restart to update installing 1.6.0, which opens again and logs "Updated"**.

  The release notes say so.
- **From launch to release:** about 9.5 hours (08:12 to 17:50). The streams took about 4 hours; reviews and their
  fixes about 2.5 hours. Integration, the final pass and the build ran alongside, about 5 hours, sharing the test slots
  with the TV work. Defender took 22 minutes.

## 1.6.1: Fixes (now)
- Clears the fix queue in KNOWN-ISSUES: the Minors and old bugs that 1.6's reviews and hunt put there, and the owner's
  reports. No features.
- Also anything 1.5.1 didn't finish. (1.5.0's own queue moved to 1.5.1 on 2026-11-21.)
- **What 1.6.0's checks found** (2026-11-21, after publishing; no Blocker):
  - **the bug hunt:**
    - skip rules never snap to the cut on most PCs, because snapping needs a separate mpv that the package doesn't
      include;
    - collections whose tidy names match read the same outside the Collections page;
    - three smaller items;
  - **the review of the merges:** "See all", then Back, then the same "See all" does nothing (an older cause, which
    also affects the library tabs), plus four smaller items.

  The two hunt Majors are in the release's Known issues.
- **Started 2026-11-21 18:10, in three streams** (brief `briefs\161-fixes.md`):
  - **navigation and collections:** Back, tidy names everywhere, seasonal rows by the local date, Save as playlist,
    release-notes focus, the top bar's and collections' queue;
  - **skipping and the player:** snapping with the shipped libmpv, 1.5.1's "moves" marks kept through forget and undo,
    migrating once, the skip and stats queue, the sign-in ending mid-playback;
  - **the older queue:** the device id kept in the data folder, the sign-in-ended leftovers, Display's Save, "/" in
    search, guest roles in "With <name>", the Collection page's queue.

  Then one review as integration starts, a short bug hunt beside the integration and the build (Decided), the Sandbox
  test and publishing.
- **Not in 1.6.1:** sentence case for Home's row names (61 names in the test scripts), and music in search with playback
  (a feature, for 1.7).

## 1.7: Discover (now)
Plan phases 2 and 3 (`docs\reports\Nixie smart collections plan.md`). Each source is off until the user turns it on. Nixie fetches public lists and matches them on the
PC. It never sends the library or the watch history. Each source gets its current terms checked, a live check beside
its test fake, and Nixie's own message when it refuses.

- **TMDb's daily export files:** franchise and keyword names. No key.
- **Wikidata:** awards and finer genres. No key. Its query endpoint moves by June 2027, so the endpoint is a setting.
- **Trakt:** trending, popular, anticipated and box office, with the Client ID only and no sign-in. A live check doesn't
  count as use, so the 2026-11-19 registration lapses on 2026-12-19 unless a build in regular use calls it first (see
  "Trakt's registration" below).
- **TMDb trending,** with each user's own token at first.
- **Started 2026-11-21 19:40, in two streams beside 1.6.1** (brief `briefs\170-discover.md`). 1.6.1 keeps priority on the
  test slots and usage.
  - **the keyless sources:** the shared outside layer (Nixie's User-Agent, one request at a time, pauses on 429, a
    cache with each source's maximum age, a "Look up lists online" section in Settings with a "What is sent" card per
    source), TMDb's daily export files (franchise and keyword names), and Wikidata (awards and finer genres, the
    endpoint as a setting);
  - **trending:** Trakt (trending, popular, anticipated, box office; the Client ID built into the release, no sign-in)
    and TMDb trending (each user's own token, kept with DPAPI), shown as "Trending this week in your library" on Home and
    as rows on the Collections page; plus `PRIVACY.md`, which Trakt's Production request needs.
  - Every request is chosen from the settings alone, never from the library; a permanent test proves no request carries
    anything from it.
  - A terms re-check of all four sources runs first; what it finds overrides the plan's table.
- **Trakt's registration:** 1.7 now ships before 2026-12-19, so a 1.7 build in regular use may keep the 2026-11-19
  registration alive. The trending stream's live check tells whether it still answers; the owner registers again (a few
  minutes) only if it doesn't.
- **Before 1.7 ships, the owner requests Trakt Production access** (a few minutes, the drafted fields in the plan's
  Appendix A). Trakt's staff said on 2026-11-17 that an app should do this before launch, and that an app nobody signs
  in to gets read-only Production access. The Production Client ID is the one the release carries. The request needs
  `PRIVACY.md`'s URL, so `PRIVACY.md` goes on main ahead of the release.
- **The terms re-check (2026-11-21):** no term changed. The plan had Trakt's paging (read its headers instead of
  assuming 250) and box office (an unpaged top 10, updated Mondays) wrong, and left out Wikidata's limit of 30 failed
  queries a minute. Wikidata's old service runs through June 2027, with no shutdown date.

## 1.8: Player (from the backburner)
- **A pop-out picture-in-picture player** (WinUI compact overlay with the libmpv swap chain). Confirm the idea first.
- **Drive-in's chapter bulbs.**
- **Editorial's paused side column** with a chapter scrubber.

## 1.9: Social (research first)
- **The journal's social side:** friends, and suggesting shows to each other, **through the shared server** (for
  example shared playlists), so nothing about what you watch leaves it. A Trakt sign-in for friends anywhere would send
  watch history off the PC and need an https redirect on a domain the project controls, so it's only an explicit
  exception.
- **The Diary fixes from the UX review.**
- **Discover extras to pick from:**
  - MDBList (the user's own key);
  - anime trending (AniList);
  - "Airing this week" (TVmaze).

## 2.0: Rust (a time-boxed spike first)
- **A spike of about 3 agent-days.** Go only if:
  - libmpv embeds with real glass on Windows;
  - one theme's library and player pass the keyboard matrix;
  - start-up time and memory are no worse than today.
- **Otherwise no rewrite.**

## Nixie for Google TV (a separate app, based on Glacier: phase 1 started 2026-11-21)
**The base is now Glacier** (the owner, 2026-11-21: "https://github.com/Glacier-Jellyfin/glacier-androidtv use this as a
base"), asked right after "why are we waiting for google tv we're on low usage". Glacier is a GPL-3.0 Jellyfin client
for Android TV in Kotlin and Compose for TV, with Media3 and the Jellyfin SDK, released weekly (v0.10.1 on 2026-11-18).
It already has what the plan's phases 2 and 4 would have built: adaptive playback, skip intro and credits, the
next-episode countdown, careful audio and subtitle tracks, trickplay, versions, profiles, music, photos, live TV, an
updater, and an FFmpeg audio decoder for 32-bit ARM too (DTS and TrueHD, which the owner's box can't pass through).
- **Nixie for TV is a local fork of it:** Glacier's history kept with a fetch-only `upstream` remote, based on v0.10.1;
  rebranded (Nixie's name, application id, banner and icon; no Glacier name or art in the UI); the GPL-3.0 licence and
  every notice kept, and the README and About screen say it's based on Glacier. The updater points at Nixie's own
  releases or is off, Glacier's store CI and any telemetry are off, and nothing is pushed anywhere. The phase 0
  skeleton stays on the branch `nixie-skeleton`.
- **Nixie's look is a theme layer** on Glacier's design system: Drive-in first (sign-in with Quick Connect on nixie
  tubes, the top bar, Home), then Cinematic and Editorial. Glacier's screens change through that layer and small marked
  hooks, and `DIVERGENCE.md` lists every change and why.
- **The upstream-merge rule:** merge Glacier's `main` at each of its releases that builds; Nixie's changes stay in the
  theme layer and marked hooks, so the merge stays small; DIVERGENCE.md is updated at each merge.
- **Phase 1 (started 2026-11-21): Nixie on Glacier:** the fork, the rebrand, the licence credit, everything outbound off,
  Drive-in's sign-in, top bar and Home, an emulator check (Quick Connect by D-pad against the test server, then Home),
  and `docs\GLACIER-BASE.md`: what Glacier covers, feature by feature, against the plan, and the phases re-estimated.
- **Re-estimate (provisional, until GLACIER-BASE.md):** most of the playback and browse work is done, so TV 1.0 is
  mostly Nixie's three themes and Nixie's own features: roughly phase 1 about 2–3 agent-days, the three themes across
  the rest about 5–8, hardening and release about 2–3, so about 9–14 agent-days instead of 19–25.
- **The fork's survey** (`Dev\nixie-tv\docs\GLACIER-BASE.md`, 2026-11-21): Glacier covers most of the plan's phases 1,
  2 and 4; in the plan's agent-days, 19–25 becomes 14–21 (phase 1 2–3, phase 2 4–6, the themes 3–5, hardening 2–3,
  Nixie's own features 3–4). At the measured pace with parallel streams that's the **2 to 4 days** below.
  - **Jellyfin 12.0 or newer for TV 1.0:** Glacier's SDK (1.9.4) needs it. The owner's server is 12.4.0; the 10.11
    test server becomes a "too old" check. The desktop app keeps supporting 10.11.
  - **The FFmpeg decoder's native part** (DTS and TrueHD on the onn box) needs Glacier's 18 MB CI build plus the NDK and
    CMake (about 2 GB); it's built in phase 2, and the release checker refuses a release without it.
  - **Phase 1's exit check passed** (2026-11-21, 11:59, on the emulator): Quick Connect first, its code on nixie tubes,
    signed in by D-pad only, Home in Drive-in with Resume focused, Back from a row returns to the top and the bar, and
    Back on the bar asks "Quit Nixie?". On the same day: the rebrand, the theme contract, Drive-in's sign-in, top bar
    and Home, Cinematic and Editorial built in parallel and merged, screenshot tests that need no emulator, and a
    contrast test every theme must pass (it caught Drive-in's buttons at 3.0:1; now 5.1:1). 521 unit tests pass.
  - **Phase 2 done the same afternoon** (main green, 609 unit tests): the contract for library pages, item pages and
    the player's chrome, filled by all three themes (Drive-in's marquee and tubes; Cinematic's film-strip seek bar and
    full-bleed stages; Editorial's ruler seek bar, colophon facts and numbered episodes, with item pictures printed in
    one dark rose tone so text holds 4.5:1 over any image, a look the owner can judge on the TV); Settings > Appearance
    switches themes live and keeps focus; **trailers play through Nixie's own origin**
    (`https://io.github.mbustos27.nixie.tv/trailer`, as on the desktop) with no Error 153 on the emulator; the play
    features merged (skips from four sources, credits as watched, the server's autoplay, Play labels, search ranking).
  - **Hardening done the same day** (main green, 714 unit tests): the home features and the search fix merged; text up
    to 1.3x in every theme; glass tiers (boxes of 2 GB or less, like the owner's onn, get pre-blurred images and no soft
    shadows); a 2 GB emulator held Home, a library, a movie and a series at about 185 MB; a start-up profile ships in
    the release build; the updater's files are `Nixie-TV-<version>.apk` with no check at start. No sign-in shortcut in
    the app: a test script signs in to the test server by Quick Connect instead.
  - **A review of the whole app** found the network side clean (no telemetry, tokens only in headers, lookups off by
    default, trailers on Nixie's origin), and two Blockers about Glacier's name and credit, being fixed: the share-log
    screen pointed at Glacier's issue tracker, and the licence file dropped Glacier's copyright from files taken from
    it. Also two Majors in the home code (holding OK on a card removed it; focus jumped when a row changed).
  - **The review's fixes and a bug hunt done the same evening** (main green): Glacier's tracker and copyright fixed; the
    hunt (no Blocker) found five Majors, all fixed: focus on Home after Undo, Back and a live change; search showing
    bare episodes; and the TV test server breaking whenever the desktop fake changed (it now keeps its own pinned copy).
    On the 2 GB emulator the player used about 211 MB (peak 272), with no crash. The debug build for the session is
    built.
  - **Next (was):** those fixes, a bug hunt on the emulator, then **the owner's 20-minute TV session** (frame timing and the
    player on the real box, the three themes, a trailer, one sign-in to their own server by Quick Connect, done by the
    owner), then the release key and where TV releases live (a GitHub repo of the owner's choosing).
  - **Running (2026-11-21):** phase 1 (Drive-in, screenshot tests, the exit check), Cinematic, Editorial, and two
    feature streams: **home** (a device id per Quick Connect attempt, Continue Watching with Undo, two rows, the hero
    rule, live rows from the server's websocket, Undo after marking watched) and **play** (skips from four sources,
    credits count as watched, the server's autoplay, Play labels, search ranking). After 1.0: Made by Nixie rows, the
    quality choice and stats, skip rules for extras.
- **Sped up (the owner, 2026-11-21: "Yes, speed it up"):** in calendar time at the measured pace, phase 1 lands today
  and **TV 1.0 in about 2 to 4 days**. As soon as phase 1 commits Drive-in's theme contract, **Cinematic, Editorial and
  "Nixie features on TV"** (what GLACIER-BASE.md shows Glacier lacking against desktop Nixie, split further if long)
  start as parallel streams under the same fork rules. Most checks run as JVM screenshot and UI tests, so the one
  emulator slot isn't the bottleneck; desktop final passes still go first. **The owner's real-TV check** is one batched
  session of about 20 minutes near the end (wireless debugging has to be turned back on after a TV restart), and the
  release key is their 10 minutes at the very end.

Phase 0 needed about 30 minutes of the owner's time: the TV test is done; the release key waits until a release.
**Downloads approved** (the owner, 2026-11-21: "it can download whatever is needed"): Android SDK components, system
images, the NDK if needed, and Gradle/Maven dependencies, from official sources only (sdkmanager, Google's Maven, Maven
Central, the Gradle plugin portal, the projects' own GitHub releases), each one logged with its size. Not covered: the
release key (the owner makes it), anything on the owner's TV, and any account sign-in.
Plan: `docs\reports\Nixie Google TV architecture.md`, which replaces the earlier draft (`Nixie Google TV plan.md`); its
own-build phases below are kept for the record, and the Glacier base above replaces phases 1, 2 and 4's building work.
All three themes are in 1.0, because adding layouts to finished screens is the main risk. It builds from 1.4.0's
behaviour.

| Phase (the plan before the Glacier base) | What | Effort |
|---|---|---|
| **0. Groundwork** (first) | The repo, version catalog, banner and icon, the release key and its backups, fake-server profiles, a home-screen row test on the owner's TV | About 1 agent-day, 30 min of the owner's time |
| **1. Foundation** | Network, tokens, focus, the theme contract, Drive-in's sign-in, top bar and Home | 4–5 agent-days |
| **2. Browse and player** | Libraries, search, item pages, the Media3 player and its track rules | 7–9 agent-days |
| **3. Cinematic, Editorial, switching** | | 4–6 agent-days |
| **4. Harden and release** | Low-memory check on the Chromecast, release script, update checker, a review, the owner's couch pass | 3–4 agent-days, 1–2 h of the owner's time |

That's about 19–25 agent-days.

**Phase 0 (2026-11-21): done but the release key.** The repo `Dev\nixie-tv` (local, no remote): the planned modules,
flavors, the TV manifest, a nixie-tube banner and icon, a debug build with the Watch Next test and a device-facts
probe, `nixie-release` (the release checks, Rust), and test-server profiles for Jellyfin 12, 10.11 and 10.10. The
owner ran the TV steps the same day:
- **The TV:** an onn. 4K Plus Streaming (Android 14, the Google TV launcher), paired over wireless debugging.
- **The debug banner shows** in Your apps.
- **Watch Next doesn't show:** the app added its test program, but the launcher logged "Watch next verification data
  is empty" and filtered it out. So, as the plan says, **home-screen Continue watching is dropped for 1.0**, and
  Google's partner-only Engage isn't attempted. Continue Watching inside Nixie is unaffected.
- **The box's facts** (`testing\devices\onn-4k-plus.json`):
  - **32-bit ARM only** (armeabi-v7a): any native library needs a 32-bit ARM build, which the build and the release
    checker already require;
  - 1.9 GB of memory, 256 MB per app;
  - 4K at 59.94 with HDR10, HLG and HDR10+;
  - hardware AVC, HEVC, VP9, AV1, Dolby Vision, MPEG-2 and VC-1;
  - passthrough AC3, E-AC3 with Atmos, TrueHD and AC-4, but no DTS.
- **Still the owner's, when a release needs them:** creating the release key, its two backups, and signing and
  installing the first signed build.

**Fitting it on this PC:**
- The emulator and Gradle need about 6–8 GB (about 21 GB free on C: on 2026-11-21), and the emulator takes one of the 3
  off-screen test slots while it runs.
- Phase 1 started 2026-11-21: weekly usage was at 15%, so the earlier wait for the reset no longer applies.

## Decided for 1.5 and 1.6 (2026-11-20, the owner: "Make 1.5 choices")
Since the split, tidy names, favourites and the Made by Nixie rows ship in 1.6. Rewatching has been on since 1.4.1, and its Settings switch rides 1.5 with the rest.
- **Release pace:** one planned release at a time, about weekly at most; a Blocker ships right away as a hotfix.
  Nothing else ships in between, so update notices stay rare.
- **The upgrade test runs in Windows Sandbox** (this PC runs Windows 11 Pro), never in the owner's account. Sandbox is
  on (2026-11-20). Its window shows on the desktop while the test runs, so it comes with a heads-up and plays no media.
- **The updater:**
  - checks every 4 hours while Nixie is open, and when the window comes back after an hour or more away (focus or
    waking from sleep);
  - the installed copy downloads an update in the background as soon as it finds one;
  - it installs only when you click "Restart to update", or when you've ticked "Install when I close Nixie" (off by
    default);
  - the portable zip can't update itself, so it shows the update with a link to the release page.
- **Search:** a best match first, then groups ordered by their best match, with weak matches behind "Show N more".
  Person results open a "With <name>" list (changed after the 2026-11-20 review: people search for actors).
- **Rewatching in Up next:** on by default, with a Settings switch ("Include shows I'm rewatching") to turn it off.
- **Tidy names:** collection and library names are tidied ("[HUB] Trending Movies" reads "Trending Movies").
  Playlists keep their names, since people name those themselves.
- **Favourites in the top bar:** start from the pins people made in 1.3. With no pins, the Movies and Shows libraries
  (by their kind), then the most opened, never just the server's order. On a server whose first libraries are
  "[HUB] Trending …", that order would fill the bar with hubs. A star on each library (in "Libraries ▾" and on the
  Libraries page) adds or removes it. Diary moves from the content tabs into the account menu (T4).
- **Made by Nixie Home rows:** "Highly rated, not watched" and "Unfinished series" are on by default, plus a seasonal row
  in season. Each can be turned off in Settings, and the other kinds rotate one a day.

## Decided for 1.6 and 1.7
- **1.6.0 ships before its upgrade test** (the owner, 2026-11-21: "Do asap and notify me when we have the GitHub
  link"). 1.6 changed neither the updater nor the installer (new settings, and one line of build-release.ps1), so the
  Sandbox test runs right after publishing, as it did for 1.5.0. A failure there makes a hotfix.
- **1.6.1's bug hunt runs beside the build** (the owner, 2026-11-21: "Hunt beside the build"). The review still runs as
  integration starts. The short hunt starts on the integration branch once the three streams are merged, in parallel
  with the final pass and the build, instead of before them (that wait was up to an hour). A Blocker it finds still
  stops the release. A Major in 1.6.1's own new code that's quick to fix is fixed before the build finishes; everything
  else goes to the next fix queue in KNOWN-ISSUES.
- **1.7 starts beside 1.6.1** (the owner, 2026-11-21: "We can start 1.7"). The release rules run each fix release beside
  the next feature release, and weekly usage was at 38%, so there was no reason to wait. 1.6.1 is close to its release,
  so it keeps priority on the test slots and usage. The keyless sources (TMDb's export files, Wikidata) and TMDb trending
  with each user's own token start at once; Trakt needs only its Client ID, never a sign-in, and any registration is the
  owner's own step.
- **TMDb (1.7):** each user brings their own token at first. An app-wide key is reconsidered once people use it.
- **Save as playlist (1.6):** yes, as an explicit button that copies a Nixie collection to the server as the user's own
  playlist (an ordinary user can). Nothing is written to the server otherwise.

## Still open (later releases)
- Skip repeated extras: one skip stream in 1.6 (the rules with their margin, then audio-matched intros once the spike
  passes); audio intros become 1.7's first item if 1.6 is full. Waits on the owner: chapters, a button or skip by
  itself, per show or per season, how Sunny behaves today and with online lookup on, and the fingerprint tool.
- The pop-out player's meaning (1.8).
- Friends through the server only, or a Trakt exception (1.9).
- Rust: go or no-go after the spike (2.0).

## Every release
- **Bug hunts** before every release and every Sunday, including the outside services Nixie talks to and the fakes the
  tests use instead.
- **Gates:**
  - the keyboard matrix in four themes;
  - the UI and core tests;
  - the off-screen checks;
  - the contrast test;
  - a review;
  - package checks and a Defender scan.
- **Real-server checks** (from 1.5) on the local test servers:
  - a transcode and its restart on a track change;
  - real playlists;
  - the admin actions.
- **The upgrade test** (from 1.5): Seaglass to Nixie, and the previous release to this one, in a sandbox, never the
  owner's account.
- **A baseline:** start-up time, first Home paint, and memory after browsing, measured the same way each release. It
  shows regressions, and gives the Rust spike something real to beat.
- **Hand checks are recorded:** each release's checks by hand get a pass or fail line in KNOWN-ISSUES or the release
  notes. 1.4.0's three (Space before any click, Esc in YouTube's player, a real trailer) are still unrecorded.
- **Accessibility by hand, every minor release:**
  - Narrator by ear;
  - 125 and 150 % scaling;
  - high contrast in each theme.
- **Downloads:** an installer and a portable zip, plus a `Seaglass-<version>` copy of the installer for old updaters.

## Not planned
- Performance-only optimization passes (dropped 2026-11-14).
- Code signing.

Visual version: the "Nixie Roadmap" artifact.
