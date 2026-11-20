# Nixie roadmap

Updated 2026-11-20 (1.4.1 released; 1.5 split after the second review). These are the planned releases in order, with
the backburner ideas placed where they fit. Nothing past 1.4.1 has a date. Version numbers past 1.6 are a guide. Sizes
are for one dev stream: S up to 2 days, M about a week, L 2 to 3 weeks, XL months.

| Release | Name | What | Status |
|---|---|---|---|
| 1.4.0 | Consistency | The library layer for all four themes, plus the contrast floor and big libraries | **Released** 2026-11-20 |
| 1.4.1 | Continue Watching | Rewatched shows come back, Home updates as the player closes, faster season switching | **Released** 2026-11-20 |
| **1.5** | Search and updates | Better search and a responsive updater (both musts), plus the 1.4.0 follow-up fixes | **Now** (M–L, in progress) |
| 1.6 | Collections | Favourite libraries in the top bar, tidy names, smart collections from the server's data, the Collection page | Next (L) |
| 1.6.1 | Theme polish | Drive-in's look on the shared pages, Editorial's list view | Planned (M) |
| 1.7 | Discover | Trending, awards and finer genres from outside sources, opt-in | Planned (M–L) |
| 1.8 | Player | Pop-out player, chapter extras | From the backburner (M–L) |
| 1.9 | Social | Friends and suggestions through the shared server, the Diary fixes | Research first (L) |
| 2.0 | Rust | A time-boxed spike, then go or no-go | Research first (XL if go) |
| TV 1.0 | Google TV | A separate app with all three themes: phase 0 alongside 1.5, the rest alongside 1.6 | Plan written |

**Split (owner, 2026-11-20: "Yes, split it"):** the second review found 1.5 too big at about 40 items, with four streams
editing the same files. 1.5 now ships search, the updater and the fixes; the top bar, tidy names, collections and the
Collection page become 1.6.

**Hotfixes:** a Blocker ships right away as a 1.x.y hotfix. Everything else waits for the next planned release.

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

## 1.5: Search and updates (now)
Three parallel streams (search, the updater, fixes), each owning its files, as in 1.4.0. The musts go first.

**Progress (2026-11-20, 13:30):**
- Search and the updater are near done: each has 4–5 commits, and is running its checks and screenshots.
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

## 1.6: Collections
The top bar, tidy names, collections and the Collection page touch the same pages (the Collection page, its cards and
chips, the top bar's Collections destination, the fake server), so they're built together.

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
- **Release fit:** step 0 in 1.6; skip rules beside 1.6 as their own stream, or in 1.8 Player if 1.6 is full.
- **Matching by audio (1.8, only if needed):** for extras at a different time in each episode. Scanning whole episodes
  would download gigabytes from a server that isn't the owner's, so it's out. Learning while watching, from what mpv
  has already downloaded, is a two-day spike first (S), then L.
- **Plan:** `docs\reports\Nixie repeated extras skip plan.md`.
- **Questions for the owner:**
  - where the extras sit: before the episode, after it, or mid-episode where the ad breaks were;
  - the same clip and length every episode, or different promos, and about how long;
  - whether the player's Chapters list shows a chapter at each break;
  - a Skip button, or skip by itself;
  - one rule for the whole show, or per season;
  - 1.6 or 1.8;
  - if the extras move around, whether the audio spike is worth it.

## 1.6.1: Theme polish
- **Drive-in's own look** on the Libraries and Collection pages.
- **Editorial's list view** (from the backburner).

## 1.7: Discover
Plan phases 2 and 3. Each source is off until the user turns it on. Nixie fetches public lists and matches them on the
PC. It never sends the library or the watch history. Each source gets its current terms checked, a live check beside
its test fake, and Nixie's own message when it refuses.

- **TMDb's daily export files:** franchise and keyword names. No key.
- **Wikidata:** awards and finer genres. No key. Its query endpoint moves by June 2027, so the endpoint is a setting.
- **Trakt:** trending, popular, anticipated and box office, with the Client ID only and no sign-in. The registration
  made 2026-11-19 will lapse unused; a live check doesn't count as use. Register again when 1.7 starts (a few
  minutes).
- **TMDb trending,** with each user's own token at first.

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

## Nixie for Google TV (a separate app: phase 0 alongside 1.5, the rest alongside 1.6)
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
- **TMDb (1.7):** each user brings their own token at first. An app-wide key is reconsidered once people use it.
- **Save as playlist (1.6):** yes, as an explicit button that copies a Nixie collection to the server as the user's own
  playlist (an ordinary user can). Nothing is written to the server otherwise.

## Still open (later releases)
- Skip repeated extras: skip rules beside 1.6 or in 1.8, and whether the audio spike is needed (the plan is written;
  waits on the owner's answers: where the extras sit, the same length each time, a button or skip by itself).
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
