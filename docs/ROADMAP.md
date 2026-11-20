# Nixie roadmap

Updated 2026-11-20 (1.4.1 released). These are the planned releases in order, with the backburner ideas placed where they fit. Nothing past
1.4.1 has a date. Version numbers past 1.5 are a guide. Sizes are for one dev stream: S up to 2 days, M about a week,
L 2 to 3 weeks, XL months.

| Release | Name | What | Status |
|---|---|---|---|
| 1.4.0 | Consistency | The library layer for all four themes, plus the contrast floor and big libraries | **Released** 2026-11-20 |
| 1.4.1 | Continue Watching | Rewatched shows come back, Home updates as the player closes, faster season switching | **Released** 2026-11-20 |
| 1.5 | Collections | Better search and a responsive updater (both musts), the 1.4.0 follow-up fixes, favourite libraries in the top bar and smart collections, built together | Next (L, parallel streams) |
| 1.5.1 | Theme polish | Drive-in's look on the shared pages, Editorial's list view | Planned (M) |
| 1.6 | Discover | Trending, awards and finer genres from outside sources, opt-in | Planned (M–L) |
| 1.7 | Player | Pop-out player, chapter extras | From the backburner (M–L) |
| 1.8 | Social | Friends and suggestions through the shared server, the Diary fixes | Research first (L) |
| 2.0 | Rust | A time-boxed spike, then go or no-go | Research first (XL if go) |
| TV 1.0 | Google TV | A separate app with all three themes, alongside 1.5 | Plan written |

**Hotfixes:** a Blocker found in 1.4.0 ships right away as 1.4.x. Everything else waits for 1.5.

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

## 1.5: Collections
The 1.4.0 follow-ups and the collections work touch the same pages (the Collection page, its cards and chips, the top
bar's Collections destination, the fake server), so they're built together in parallel streams and ship as one
release. Each stream owns its files, as in 1.4.0.

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
- **Find out why the server returns 295 films:** compare `/Items` and `/Search/Hints` on the local test servers (10.11
  and 12), and use whichever ranks better.
- **Tests:**
  - a fixture of tricky titles (apostrophes, "The", numbers, unicode, one-word titles);
  - a live check on the test servers;
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
  shows (Settings, the notice), and check it against its `.sha256`.
- **One click:** "Restart to update" closes Nixie, installs quietly (the per-user installer needs no admin prompt), and
  reopens Nixie where you were. Or install it when you close Nixie.
- **Afterwards:** "Updated to 1.x · What's new", with the release notes in the app.
- **Failures** say what went wrong, with Retry, and the release page as a fallback.
- **Tests:**
  - a fake release that appears while Nixie is open is found within the new interval;
  - progress, a checksum mismatch, and a resumed download;
  - the install-and-reopen handoff proven in Windows Sandbox (once the owner turns it on), never in the owner's
    account.

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
- **Checked:** a reverse proxy on a sub-path, and an address ending in "/".

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

**Test tooling, first:**
- off-screen test runs 3 at a time (`wip/harness-parallel`);
- local Jellyfin test servers (10.11 and 12), approved 2026-11-19 and being installed (`tools\qa-servers\`);
- the fake server refuses what Jellyfin 12 refuses, and gets one owner, since the desktop streams and the TV app all
  change it.

**New checks from 1.5 on** (see "Every release"):
- real-server checks;
- the upgrade test;
- accessibility by hand.

## 1.5.1: Theme polish
- **Drive-in's own look** on the Libraries and Collection pages.
- **Editorial's list view** (from the backburner).

## 1.6: Discover
Plan phases 2 and 3. Each source is off until the user turns it on. Nixie fetches public lists and matches them on the
PC. It never sends the library or the watch history. Each source gets its current terms checked, a live check beside
its test fake, and Nixie's own message when it refuses.

- **TMDb's daily export files:** franchise and keyword names. No key.
- **Wikidata:** awards and finer genres. No key. Its query endpoint moves by June 2027, so the endpoint is a setting.
- **Trakt:** trending, popular, anticipated and box office, with the Client ID only and no sign-in. The registration
  made 2026-11-19 will lapse unused; a live check doesn't count as use. Register again when 1.6 starts (a few
  minutes).
- **TMDb trending,** with each user's own token at first.

## 1.7: Player (from the backburner)
- **A pop-out picture-in-picture player** (WinUI compact overlay with the libmpv swap chain). Confirm the idea first.
- **Drive-in's chapter bulbs.**
- **Editorial's paused side column** with a chapter scrubber.

## 1.8: Social (research first)
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

## Nixie for Google TV (a separate app, alongside 1.5)
Plan: `docs\reports\Nixie Google TV architecture.md`, which replaces the earlier draft (`Nixie Google TV plan.md`).
All three themes are in 1.0, because adding layouts to finished screens is the main risk. It builds from 1.4.0's
behaviour.

| Phase | What | Effort |
|---|---|---|
| **0. Groundwork** (first) | The repo, version catalog, banner and icon, the release key and its backups, fake-server profiles, a home-screen row test on the owner's TV | About 1 agent-day, 30 min of the owner's time |
| **1. Foundation** | Network, tokens, focus, the theme contract, Drive-in's sign-in, top bar and Home | 4–5 agent-days |
| **2. Browse and player** | Libraries, search, item pages, the Media3 player and its track rules | 7–9 agent-days |
| **3. Cinematic, Editorial, switching** | | 4–6 agent-days |
| **4. Harden and release** | Low-memory check on the Chromecast, release script, update checker, a review, the owner's couch pass | 3–4 agent-days, 1–2 h of the owner's time |

That's about 19–25 agent-days.

**Fitting it on this PC:**
- The emulator and Gradle need about 6–8 GB of the roughly 10 GB free, so the emulator takes one of the 3 off-screen
  test slots.
- Heavy phases start after the weekly usage reset (24 November).

## Decided for 1.5 (2026-11-20, the owner: "Make 1.5 choices")
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
- **Search:** a best match first, then groups ordered by their best match, with weak matches behind "Show N more". No
  people results until a person has a page in Nixie.
- **Rewatching in Up next:** on by default, with a Settings switch ("Include shows I'm rewatching") to turn it off.
- **Tidy names:** collection and library names are tidied ("[HUB] Trending Movies" reads "Trending Movies").
  Playlists keep their names, since people name those themselves.
- **Favourites in the top bar:** start from the pins people made in 1.3. With no pins, the first three libraries in the
  server's order. A star on each library (in "Libraries ▾" and on the Libraries page) adds or removes it.
- **Made by Nixie Home rows:** "Highly rated, not watched" and "Unfinished series" are on by default, plus a seasonal row
  in season. Each can be turned off in Settings, and the other kinds rotate one a day.

## Decided for 1.6
- **TMDb:** each user brings their own token at first. An app-wide key is reconsidered once people use it.
- **Save as playlist:** yes, as an explicit button that copies a Nixie collection to the server as the user's own
  playlist (an ordinary user can). Nothing is written to the server otherwise.

## Still open (later releases)
- The pop-out player's meaning (1.7).
- Friends through the server only, or a Trakt exception (1.8).
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
- **Accessibility by hand, every minor release:**
  - Narrator by ear;
  - 125 and 150 % scaling;
  - high contrast in each theme.
- **Downloads:** an installer and a portable zip, plus a `Seaglass-<version>` copy of the installer for old updaters.

## Not planned
- Performance-only optimization passes (dropped 2026-11-14).
- Code signing.

Visual version: the "Nixie Roadmap" artifact.
