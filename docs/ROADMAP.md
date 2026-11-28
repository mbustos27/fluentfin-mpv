# Nixie roadmap

Updated 2026-11-28, 12:24 AM (Silo compatibility planned: the owner, "lets get silo compat"). Before that, 2026-11-27, 6:30 PM (1.9.0 released at 6:16 PM, after the rebuild and the owner's "Ship 1.9"; its Sandbox upgrade test passed at 6:30 PM; Nixie for TV 1.0.1 built, waiting on the owner's signature). Before that, 2026-11-26, 11:31 PM (1.9 held for the 'because you watched' rebuild; 1.9's names shipped as drafted, 1.9 resumed after a shutdown; 1.8.1 released at 9:41 PM, its Sandbox upgrade test passed; 1.9's phase 2 started; Collections 2 approved as 1.9 and started; the Player renumbered 1.10 and Social 1.11; franchises of 3+ films; Made by Nixie reviewed on the owner's library; the two repositories made alike, 2:56 to 3:30 PM; Nixie for TV 1.0.0 released at 2:25 PM; 1.8.1 running since 1:41 PM; 1.9 and 1.10 on hold). Before that, 2026-11-22, late (the owner: "review the whole roadmap to shorten the timeline", "Yes, with P1", then "Yes, on its own": collections P1 is its own release, 1.8, before the player, now 1.9, and Social, now 1.10; every release's tail is shorter, 1.8 and 1.9 no longer wait for TV 1.0, and the heavy streams follow the weekly usage reset. 1.7.2 released at 21:21; 1.7.3 running since 21:20; TV streams T, R, M and D running). These are
the planned releases in order, with the backburner ideas placed where they fit. Version numbers past 1.6 are a guide.
**Sizes are calendar time at the measured pace** (below): several streams in parallel, each building in hours, plus a
fixed tail of about 2 to 2.5 hours per release (half a day before 2026-11-22). A date past today is an estimate, not a promise; usage, the 3 test slots
and the owner's own steps are the real limits.

| Release | Name | What | Status (estimate at the measured pace) |
|---|---|---|---|
| 1.4.0 | Consistency | The library layer for all four themes, plus the contrast floor and big libraries | **Released** 2026-11-20 |
| 1.4.1 | Continue Watching | Rewatched shows come back, Home updates as the player closes, faster season switching | **Released** 2026-11-20 |
| 1.5.0 | Search and updates | Better search and a responsive updater (both musts), plus the 1.4.0 follow-up fixes | **Released** 2026-11-21 |
| 1.5.1 | Fixes | The fix queue 1.5.0 left: the bug hunt's findings, the learned "Skip intro" at 0:00, small leftovers | **Released** 2026-11-21 |
| 1.6 | Collections | Favourite libraries in the top bar, tidy names, smart collections from the server's data, the Collection page, and theme polish (Drive-in's look on the shared pages, Editorial's list view), skipping repeated extras (its own stream), and the player's quality choice and playback stats (its own stream) | **Released** 2026-11-21, a day early (before its Sandbox upgrade test, at the owner's call; the test passed right after) |
| 1.6.1 | Fixes | Clears what 1.6's reviews and bug hunt queue up, and anything 1.5.1 didn't finish | **Released** 2026-11-22 00:16 (started 2026-11-21 18:10, 3 streams; its Sandbox upgrade test passed before publishing) |
| 1.7 | Discover | Trending, awards and finer genres from outside sources, opt-in | **Released** 2026-11-22 08:37 (https://github.com/mbustos27/nixie/releases/tag/v1.7.0; started 2026-11-21 19:40, 3 streams). The release carries Nixie's own TMDb key and the Trakt Production Client ID, never the repository; TMDb trending was checked live with it. Its Sandbox upgrade test ran right after publishing, at the owner's call, and passed (08:41-08:54) |
| 1.7.1 | Fixes | The library index on a big server: "Nixie is reading your libraries" took minutes on the owner's server (the owner, 2026-11-22: "figure out why and release 1.7.1 quick fix bc thats the whole thing"). The saved index shows at once and is read again behind it only when stale; a first read comes two pages of 1,000 at a time and shows its rows as it goes | **Released** 2026-11-22 15:27 (https://github.com/mbustos27/nixie/releases/tag/v1.7.1; 1 stream, a review, a bug hunt through a proxy slowing every request by 3-10 s, two re-reviews and three fix rounds). Measured on a test Jellyfin with 25,736 films, not yet on the owner's server. Its Sandbox upgrade test ran right after publishing, as 1.7.0's did, and passed (15:29-15:41); the owner's test copy is 1.7.1 now |
| 1.7.2 | Hotfix | "Couldn't load your collections" on the owner's server with 1.7.1 (their test copy, 2026-11-22 20:09): the saved index from 1.7.0 lists titles twice, a genre-mix builder (1.6 code) throws on a repeat, and 1.7.1 builds Made by Nixie inside the page's load, so the whole page failed. 1.7.0 met the same throw after its read and quietly showed no Made by Nixie. The fix: a saved index keeps each title once, no builder throws on a repeat, one failing builder can't take the page down, and the error card no longer sends the user to the server | **Released** 2026-11-22 21:21 (https://github.com/mbustos27/nixie/releases/tag/v1.7.2; started 20:15; a review found no Blocker or Major). The version is 1.7.2, not 1.7.1.1: Windows Installer ignores a fourth number. Its Sandbox upgrade test ran right after publishing and passed (21:23-21:36); the owner's test copy is 1.7.2 |
| 1.7.3 | Fixes | **Made by Nixie, fewer and better** (the owner: "too many, too generic"; "Yes, plan 1.7.3"): no directors, actors, studios, networks, decades, countries or genre pairs as collections; a title counted once across its libraries and copies; "Acclaimed, not seen yet" and "Under 90 minutes". The owner's answers ("Yes, all three"): the actors-and-directors pass stops, pins of those kinds go, the library preset strip keeps decades, genre mixes and countries. Plus the queued fixes: the rename's constants, Up next's count, the rows' 40 px jump, the reading line's total, the Collections page's arrival focus, Drive-in's logo crop, errors that blame the server only when it is the server's fault, "the server" wording everywhere, and the slow exit under load | **Released** 2026-11-23 02:44 (https://github.com/mbustos27/nixie/releases/tag/v1.7.3; started 2026-11-22 21:20, 5 streams, `briefs\173-plan.md`). The builders' Major, the bug hunt's two and the re-review's one were fixed before the build, with every Minor; the close crash (since 1.6.1) was found and fixed. Its Sandbox upgrade test ran right after publishing and passed (02:45-02:59) |
| 1.7.4 | Basics | A tester's four Majors: a Quick Connect sign-in forgotten at restart, a server's "…" opening its sign-in (Remove can't be pressed), the player's controls stuck almost invisible in a window after a size change, a season's episode row that can't scroll. Two Minors from the same screenshot: the top bar lighting "Home" on a season page, cast names running together. And what the desktop basics sweep finds (the owner: "Like test all basic functions"). Each fix brings its own permanent test. Also, if ready in time (else first in 1.7.5): **Back to a page you just left is instant** (the owner, 2026-11-23: "going back to the collection from a movie in the collection makes u wait for the collection to load again"): the page as it was (items, scroll, focus, Order and Hide watched) with no loading state, on every page, refreshing behind it only if needed | **Running** (three fix streams since 2026-11-22 23:00, merged in the integration; the basics sweep of 1.7.3 found four sign-in Majors (Sign out not sticking, automatic sign-in with one server and one user only, a failed start saying nothing, a refused password retried at every start) and the integration a close crash from Classic's or Cinematic's Home: all in 1.7.4). The integration stalled 03:48-08:05 when a session ended. **Released** 2026-11-23 23:13 (https://github.com/mbustos27/nixie/releases/tag/v1.7.4), after a pause for the owner's work; "Back is instant" moved to 1.7.5. Its Sandbox upgrade test ran right after publishing and passed (23:14-23:27) |
| 1.7.5 | Fixes and the basics gate | **Every remaining desktop fix** (the owner: "Fix all noticed issues and pointed out issues"): each Minor in KNOWN-ISSUES from 1.7.2's and 1.7.3's reviews and hunt, the slow lookup with many pinned collections, reading only what changed since the last read (BH-171 m5), and the error copy's last gaps. **The basics gate** (the owner: "Testing should catch these kinds of things"; rule 12): sign-in by each method across a restart, servers, every menu item pressed (nothing else may happen), page checks at real sizes (1280×720 to 2560×1392, 100% and 150% DPI, a live resize, four themes), the player's controls after each interruption, real-shaped fixtures. It fails the release build like any other gate | **Released** 25 Nov, 3:09 AM (https://github.com/mbustos27/nixie/releases/tag/v1.7.5), with "Back is instant" (the owner, 23 Nov). Eight streams from 8:14 AM on 24 Nov (two session restarts stopped them; both caught and resumed), each reviewed as it merged, a bug hunt, then the basics gate's first run on a release package: it found one app bug (a kept season page showing an episode's old progress after playback) and five faults in its own checks, all fixed before the final build. Its Sandbox upgrade test passed right after publishing (3:13–3:28 AM). The gate is required from 1.7.5 on |
| 1.8 | Collections, better | **Collections P1, its own release** (the owner, 2026-11-22: "Yes, with P1", then "Yes, on its own"): the lineup assembler, anchors with "Next in" and "More from", the weekly Spotlight, and titles with reason lines and award badges, on top of 1.7.3's fewer, better collections. Also **a reverse toggle for the sort** (the owner, 2026-11-23: "we should be able to reverse the direction of the sort in collections"): next to Order on the Collection page (collections, playlists and Made by Nixie) and on the Collections page's grid; it reverses whichever order is chosen, survives Back, Forward and R like Order and Hide watched, works in every theme, is reachable by keyboard and gamepad, and its Narrator name says the direction | **Released** 25 Nov, 8:21 AM (https://github.com/mbustos27/nixie/releases/tag/v1.8.0): the weekly lineup; Next in, More like and More from with the index's people step; the Spotlight and the reverse toggle; titles, reason lines and badges; a switch for each new kind. Home's first Made by Nixie row shows in about 1 s on a first start (11–15 s before). Four streams, each reviewed as it merged; the bug hunt's two Majors (This week losing the keyboard, the switches) and its memory fix (858 MB to 200 MB) went in before the build. The basics gate found no app bug. Also fixed: a start that ended because another copy was updating (since 1.5). Its Sandbox upgrade test passed right after publishing (8:22–8:36 AM). Next: 1.8.1, Back to Drive-in's Collections under load and the queued Minors |
| TV 1.0 | Google TV | A separate app based on Glacier (a local fork), with Nixie's three themes, and Continue watching published for third-party home screens (Google TV's own filters it) | **Released** 2026-11-25 2:25 PM as Nixie for TV 1.0.0 (https://github.com/mbustos27/nixie-tv/releases/tag/v1.0.0, a public repository; signed with the owner's release key, made that day). On the TV: `is.gd/nixietv` in Downloader, or the Downloader code `6673217`; both lead to `releases/latest/download/nixie-tv.apk`, which every full release attaches. Before it: built, reviewed and bug-hunted 2026-11-21; the owner's TV session that evening; two fix rounds timed on a real Jellyfin 12.4 with 25,736 films; Home as Continue watching then Up next, with Collections in the top bar; the TV basics gate (39 checks a theme, 117/117 on the box's build); the owner's re-check of build 10104 ("All good", 2026-11-25); the privacy scrub of shared logs; a one-commit public history on Glacier 0.10.1. 1.0.0 went onto the owner's box by adb beside the debug build at 2:37 PM; the owner skipped their own check after sign-in ("just publish"). 1.0.1 takes the short link in Settings › System |
| Repos | Both repositories alike | The owner, 2026-11-25 at about 2:54 PM: "Yes, fix both" (the work ran 2:56 to 3:30 PM): the desktop and TV repositories with the same community files, security settings, banner, screenshots and social preview | **Done** 2026-11-25 3:30 PM, except the owner's two social preview uploads. **Desktop** (`mbustos27/nixie`): secret scanning, private reporting, Dependabot alerts and a ruleset on main (no force pushes or deletion), turned on by the parent session; SECURITY, CONTRIBUTING, CODE_OF_CONDUCT, a pull request template, "A security problem" first in the issue chooser and a "Nixie for TV" README section (588f8932, 3:01 PM); community health 100%; the About text names the TV app. **TV** (`mbustos27/nixie-tv`): the banner, badges and "How it differs from Glacier" (#1, 3:07 PM) and the Fire TV install steps (#2, 3:10 PM), both merged by another session at the owner's asks; one line there corrected, since the Fire TV emulator checks (F0) haven't run (40dc7a98, 3:10 PM); the social preview, brand notes and the true server versions: Jellyfin 12.0 or newer, an older server marked "not supported" with Connect anyway (61ace61c, 3:12 PM); nine theme screenshots, public-domain films and art only (a5424569, 3:30 PM). C: was cleared the same afternoon for 1.8.1's gate: 21.1 GB free at 3:23 PM, 150.4 GB at 3:33 PM (finished worktrees and scratch copies). **Waiting on the owner:** Settings › General › Social preview on each repository, with its `docs\brand\social-preview.png` |
| 1.8.1 | Fixes | The queued desktop fixes (`briefs\181-plan.md`): Back to Drive-in's Collections under load, Back with a popup open, Editorial's library scroll, the film page's memory (BH-175-M2, a Major since 1.7.3), and the queued Minors | **Released** 2026-11-25 9:41 PM (https://github.com/mbustos27/nixie/releases/tag/v1.8.1; started 1:41 PM, 3 streams: back, mem, minors). Made by Nixie tuned on the owner's real library (picks by what was watched most recently, More like without started titles, "You watched 36 of 39 episodes", Christmas from a fixed 20 November, franchises of 3 or more films), Back to Collections at once, pages come back to by Back no longer holding 6 to 15 MB, Narrator's place in a Collection, one device across a quick reopen, no whole-library read on a mixed library. Its bug hunt found an old first-start settings race (Made by Nixie, Collections and the Movies and Shows tabs broken until a restart, 1 in 9 first starts): fixed in a hotfix before publishing. Gate 92 of 98 on the release package, no app bug (the 2560 monitor off; a Back cell slow only under load). Left for the next fix release: docs\KNOWN-ISSUES.md, "Nixie 1.8.1". Its Sandbox upgrade test passed right after publishing (9:50 to 10:04 PM, every check, the owner's OK for its window and for downloading 1.8.0's installer) |
| TV 1.0.1 | Fixes | What 1.0.0 left | **Built, waiting on the owner's signature** (2026-11-27): main pushed to `258744da` at 1:49 AM after its review fixes (detekt green), and the unsigned release APK made at 1:52 AM. Then the release checks, the tag `v1.0.1`, the 3 files and the two links (`is.gd/nixietv`, the Downloader code) checked. It brings the original file when a device refuses the playback, the refusal remembered for 24 hours, a link test, the install link, and 12- or 24-hour clocks. Before it, **queued:** the short link in Settings › System beside the source link; "Ends at" following the TV's 12- or 24-hour setting (it is always 24-hour now, found by the screenshots 2026-11-25); a basics-gate check for an older server's "not supported" and Connect anyway (no test covers it); `nixie-release` rebuilt in the main checkout before the release (its copy there is from 11-21). Fire TV's F0, the emulator checks, can ride with it |
| Silo | Silo compatibility | Nixie for Windows and for TV working well with Silo servers through Silo's Jellyfin-compatible API (`briefs\silo-plan.md`) | **Planned** 2026-11-28 (the owner: "lets get silo compat"). Silo emulates Jellyfin 12.1 as a supported subset: no `/Playlists`, no Tags filter, a 1,000-row page cap, a rate limiter answering 429 with `Retry-After`, `PlaybackUnavailable` when nothing fits, Quick Connect supported. Steps: a local Silo QA server (WSL 2 and Docker Engine on F:, bound to 127.0.0.1, the QA media read-only; about 1.8 GB of downloads, waiting on the owner's yes, and Virtual Machine Platform needs a restart); a compat sweep of desktop (harness and basics gate) and TV (the emulator) before the Monday 1 December reset, with a gap report marking each area works, degraded or broken; then fixes and a "silo" fake-server profile in the release gate in desktop 1.9.1 and TV 1.0.2; Silo's own extras (Similar, recommendations, markers) only on a detected Silo server, in a feature release; a referential README line ("with Silo servers through Silo's Jellyfin-compatible API", no logo) once it works. About 18 to 33 agent-hours |
| 1.9 | Collections 2 | Netflix-style categories: named micro-genres like "Gritty 1970s Crime Dramas" or "Hong Kong Action Movies" in place of Franchise, Keyword and Highly rated, chosen for the viewer (`briefs\collections2-plan.md`) | **Released** 2026-11-27 6:16 PM (https://github.com/mbustos27/nixie/releases/tag/v1.9.0; `93de2611`, tag `v1.9.0`). The Sandbox upgrade test from 1.8.1 passed every check (6:16 to 6:30 PM), including 1.8.1's own Restart to update saying "Updated". Next: 1.10 Player and 1.11 Social, both on hold; taste clusters (R4) after the weekly usage reset (Monday 2026-11-30) if the owner wants them. Before it: **Approved** (the owner, 2026-11-25, after the 4:27 PM plan: "yes do that", to the plan as recommended): step 1 for films and shows is 1.9; keyword collections become the categories' parents (provisional until the owner reviews the names); one Categories row of about 12 on the Collections page, up to 2 in This week, Home unchanged; the step 2 local-model pilot after step 1, off by default; no online AI ever sees the library; a note to TMDb about its AI terms, drafted for the owner to send. **The owner reviews the list of category names before it ships.** Folded in from the 2026-11-25 review: the Franchises row ordered by relevance, a Spotlight weighted by taste, very new titles and concert films scored more cautiously, and a Thanksgiving row. **Started at 4:34 PM** (`briefs\190-plan.md`): the catalogue (about 400 drafted, the best 200 to 300 kept, a names list for the owner) and the matcher, new files only and no harness slots until 1.8.1 ships; wiring, lineup, UI and the shows' text pass after it. About 34 to 52 agent-hours, 1.5 to 2 days with 3 streams. **The catalogue and the matcher are done** (300 categories of 447 drafted, 194 films and 106 shows; the matcher runs 300 rules in about 5 ms after a 35 ms build), and the owner's review page for the names is up. **Phase 2 started at 10:08 PM**, after 1.8.1 shipped: four streams (wire, lineup, text for the shows' summaries, ui), not waiting for the names review. **Names: shipped as drafted** (the owner, 2026-11-26 shortly before 9:40 PM: "Ship as drafted"): the 300 names as they are in the catalogue; renames and drops can come in 1.9.1 or later, and the review page stays up. The summary word lists were tightened after their review (no category renamed). Resumed 2026-11-26 at 9:23 PM after a shutdown at about 1 AM; the four streams' review fixes are done, ui's last gate runs finishing, then the merged review, a bug hunt and the offline run on the owner's library, and straight on to the release. **The owner's test copy** (the owner, 9:47 PM: "if we have a test build close current nixie and open the 1.9"): 1.9 as built at 5d30aeef, opened at 9:53 PM on a copy of their data, their 1.8.1 closed normally. Their first note (9:56 PM): "already i dont think the categories should be alphabetical": Categories' See all opens in the lineup's "For you" order (fit to what they watch × quality), A to Z still a choice, sent to the ui stream; landed in `384e8fdb` and in the second test build (b2, opened by the owner at 11:03 PM). All of 1.9 merged at 10:24 PM (`a278808a`). The pre-release checks: the integrated review (merges clean; the week saved before the summary pass covered the shows), the bug hunt (show categories ranked below films for lack of critics' ratings; the same week-save), and the offline run on the owner's current library (about 152,800 films and 37,700 shows with thin metadata: 20 of the 87 shows they played land in a shown category, at least 27 after the fixes). A fix stream started at 10:46 PM. **Two more from the owner's test copy:** "scrolling the collections page takes a longgg time" (a release blocker: a stream measuring wheel distance against frame time at 190,000 titles, with a timed full-scroll gate check, started at 11:05 PM), and, shortly after 11 PM, "we need some way to like sort by rating of the category or like popularity or something": Categories' See all gets For you, Top rated (the titles' community ratings, smoothed by size), Most titles (no honest popularity exists for an account that isn't the server's admin) and A to Z, started at 11:06 PM. Both go into a third test build (b3) and the release. **Then the owner: "and i feel like the long list of categories isnt very useful", "do a full critical review".** An independent review (`briefs\collections2-critical-review.md`) found it half useful: the 12-tile row works (7 of 12 point at shows they watch), but See all is a long list of filters, and its "For you" is mostly the weekly draw because the quality score is flat on this library. **The owner: "hold for the rebuild". 1.9 is held** for a "because you watched" rebuild inside 1.9 (`briefs\190-plan.md` Phase 3, written at 11:31 PM): one More like per show played in the last 30 days, category reasons that name the played shows inside, filter-only categories kept out of the row without 2 or more played shows, and the Spotlight from their taste; taste clusters from their 87 played shows as a stretch. The bar is the offline run on their library: clearly more than 27 of the 87 shows they played in a shown category, aiming for 45. Kept: See all showing only the categories their history supports, plus the explore pick, without the draw; fixes A–I; the scroll fix; For you and A to Z. The Top rated and Most titles sorts stay out unless Top rated is made fair per kind. **The rebuild** (2026-11-27, merged by 4:01 AM): More like rows for what they watched in the last 30 days (3 episodes or a finish), reasons that name the played titles inside, filter-only categories out of the row without 2 or more played titles, up to 6 taste categories carried week to week, and the Spotlight from the nearest by likeness. Reviewed, and its Blocker, Major and Minors fixed (test data anonymised). The offline run on the owner's library: 31 of the 87 shows they played in a shown category (20 in 1.8), short of the 45 aimed for; the most the made categories allow there is 33, so the rest needs clusters or catalogue reach (KNOWN-ISSUES, 1.9's Left). The owner was asked (via the parent, 1:28 AM) whether clusters go in now or after Monday's reset. **Also in 1.9:** the scroll fix (a full step per wheel notch at any speed, Page Down; a timed release check, 33 of 33 in every theme) and the library index fix, asked for by the parent at 1:32 AM because of server load (a whole read at most once a day per server and user, deltas otherwise, played marks and favourites at the start and on R: 13 requests and 163 KB where it made about 200 and 329 MB on 190,000 titles). A third test build (b3) for the owner at 2:13 AM. **The owner, 2026-11-27 at about 3:55 PM: "Ship 1.9".** Built at 4:08 PM (Core 6,329 and UI 1,896 tests passed); the basics gate 99 of 110 jobs with no app bug among the misses; Defender clean; published at 6:16 PM with all 8 files' digests matched |
| 1.10 | Player | A floating picture-in-picture player, chapter extras, and a filled-in Report a problem (all decided 2026-11-22; planned as 1.8 until the collections release went first) | **On hold** (the owner, 24 Nov: "lets focus on this for now": 1.7.5, TV 1.0 and 1.8 first). The plan, unchanged (`briefs\180-plan.md`): the 30-minute PiP spike on 23 Nov beside 1.7.3; chapters and Report a problem may start on 23 Nov once 1.7.3 merges, if usage allows; PiP after the weekly reset. PiP waits for 1.7.3's stream C (`App.xaml.cs`, the main window), Report a problem for B (Settings). ETA: 25 Nov (24 Nov is full: 1.8 first, 1.7.5 beside it, and the 3 test slots and the CPU are the limit). If it is ready before the collections release, the two numbers swap. **Renumbered 2026-11-25** (was 1.9; Collections 2 goes first) |
| 1.11 | Social | Friends and suggestions through the shared server, the Diary fixes, Discover extras | **On hold** (the owner, 24 Nov: 1.7.5, TV 1.0 and 1.8 first). **Research done** 2026-11-22 (`docs\reports\Nixie 1.9 social research.md`, local): an ordinary user can share a private playlist with chosen people on the same server since Jellyfin 10.9, no admin needed; a friend code covers users the server hides; 11 Diary findings are open (about 14.5 h); TVmaze, AniList and MDBList all allow a free app. **Decided 2026-11-22** (the owner, "Yes, all ten"; see "Decided for Social, 1.10" below; planned as 1.9 until 2026-11-22). About 64 h of agent work, 1 to 1.5 days in parallel streams after the player release (1.9), in the new usage week: about 27–28 Nov. **Renumbered 2026-11-25** (was 1.10) |
| 2.0 | Rust | A time-boxed spike, then go or no-go | The spike (about 1 day) in a scratch worktree, any day from the weekly reset (24 Nov); it conflicts with nothing but usage. If go, a rewrite of 1 to 2 weeks even at this pace |
| Fire TV | Fire TV | The same TV app on Amazon's Fire OS devices (not Vega OS, which runs no Android apps), **sideloaded only** | **Planned, sooner** (`Dev\nixie-tv\docs\FIRE-TV-PLAN.md`): its compatibility work (about half a day, on Android TV emulators: no device, no Appstore, at the owner's call) starts on a branch once streams T, R and M merge, after the weekly reset, instead of after TV 1.0 ships. The same GitHub APK with its updater. What an emulator can't prove is listed as known gaps. ETA: 24–25 Nov |

**The shorter timeline (the owner, 2026-11-22: "review the whole roadmap to shorten the timeline"; adopted: "Yes, with P1"):**
- **Each release's tail, about 2 to 2.5 hours instead of half a day:**
  - each stream is reviewed as it merges, not after all of them;
  - the bug hunt runs beside the last stream hour and the build;
  - the release build's own test run is the final pass (no full pass and then a rebuild of the same commit), and
    only the gates a fix touches run again;
  - fix rounds only for a Blocker or a Major in new code;
  - publish, then the owner's test copy, then the Sandbox;
  - no TV emulator or TV build on the PC during a desktop release build (that load once pushed verify-package's
    exit check past 20 s).
- **No serial gates between releases:**
  - 1.8 and 1.9 don't wait for TV 1.0 (different repositories; they share only the 3 test slots and usage);
  - Social's research ran first (done 2026-11-22);
  - the TV repository's contents are prepared beside the TV streams;
  - Fire TV starts once T, R and M merge;
  - the Rust spike runs any day in a scratch worktree.
- **Checked for overlaps:** a 1.8 or 1.9 stream starts only after the 1.7.3 stream that owns its files merges:
  - PiP waits for C (`App.xaml.cs`, the main window);
  - P1 waits for M1 and M2;
  - Report a problem waits for B (Settings).
  On TV, the Report a problem QR waits for D (diagnostics). Desktop final passes keep first call on the test slots.
- **The real limit is usage.** The weekly limit was at 71% on 2026-11-22 evening and resets 24 Nov at about 05:00. In
  order:
  1. the work in flight (1.7.3; TV's T, R, M and D);
  2. cheap work beside it (the PiP spike, Social's research);
  3. the heavy streams after the reset (1.8's collections P1, 1.9's PiP, the Rust spike, Fire TV).
  At 99% everything stops until the reset.

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
| **A fix release** | **about half a day** | 1.5.1: launched 08:12, fixes in at 09:19, review and hunt by 10:26. 1.6.1: launched 18:10, published 00:16 (about 6 hours, with the hunt beside the build) |

**What actually limits the pace:**
- **Usage:** on 2026-11-20, 1.5 took the weekly allowance to 100% and the owner reset it by hand; on 2026-11-21, after
  the reset, eight agents ran at once from about 15%. More parallel streams spend it faster, not slower.
- **The 3 off-screen test slots:** every stream's harness runs, the final passes, and now the TV emulator share them.
  Waits of up to 30 minutes were seen, and one hunt probe gave up waiting.
- **The owner's own steps:** hand checks, a restart when Sandbox sticks, the TV session, the release key, registrations
  (Trakt), and decisions (the skip questions, social).

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
  (github.com/mbustos27/nixie/releases/tag/v1.5.0). Its notes say the upgrade test is still to run.
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
- **1.5.1 is released** (2026-11-21, 22:19 UTC; github.com/mbustos27/nixie/releases/tag/v1.5.1), squashed onto
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
  github.com/mbustos27/nixie/releases/tag/v1.6.0). It was squashed onto main as `ab57230` and tagged `v1.6.0`.
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

## 1.6.1: Fixes (released 2026-11-22)
- **Released 2026-11-22 00:16:** https://github.com/mbustos27/nixie/releases/tag/v1.6.1 (squash c7182f6, tag
  v1.6.1, 8 assets matching their digests).
  - **Each stream's review:** no Blocker or Major. Their Minors were fixed before the merge: a crumb test on the real
    navigation service, tidy names before the list is read, a separate device id per install kind, the locked id file,
    a skip rule added inside its part, the snap at below-normal priority.
  - **The bug hunt beside the build (the owner: "Hunt beside the build"):** no Blocker or Major; four items queued for
    1.7.1. The device id held in every case tried, and the skip snap found the cut in 0.6 s with no mpv on the PC.
  - **The final pass:**
    - the keyboard matrix (2,322) and the contrast floor (4,408 labels);
    - 62 of 63 theme runs green (the one left is Cinematic's old skeleton), all QA runs green;
    - Core 5,141, UI 1,621, the backend with only its 8 known environmental failures;
    - verify-package 134/134, with the new skip-snap and install-switch checks;
    - Defender clean.
  - **The Sandbox upgrade test passed before publishing** (23:55 to 00:08), all three legs, ending in 1.6.0's Restart to
    update installing 1.6.1, which said "Updated".
  - **Launch to release:** 18:10 to 00:16, about 6 hours.
  - **An owner finding** (no link from an episode to its series or season) shipped as a known issue. It's fixed in 1.7.
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

## 1.7: Discover (released 2026-11-22)
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
  - **the way up from an episode** (the owner's finding on 1.6.0, added 2026-11-21 evening, third stream
    `sg170-upnav`): "notice how theres no back to series or back to season when we go from the continue watching". In
    every theme, an episode reached from Continue watching, search, Up next or a collection shows only the library crumb.
    1.7 adds `Shows › <Series> › Season <n>` on episode pages and `Shows › <Series>` on season pages, as buttons
    reachable by keyboard and gamepad, in all four themes. 1.6.1 lists it as a known issue. If 1.7 slips past
    2026-11-23, this branch ships on its own as 1.6.2.
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

## 1.8: Collections, better (its own release)
- **Collections P1** (the owner, 2026-11-22: "Yes, with P1", then "Yes, on its own"): from `docs\reports\Useful Made by
  Nixie collections.md` (local), on top of 1.7.3's P0 work. Sizes in `briefs\173-plan.md`: the lineup assembler (7 h),
  anchors with "Next in" and "More from" (6 h; the people pass comes back, the top 3 billed only), the weekly Spotlight
  (3.5 h), and titles with reason lines and award badges in four themes (4.5 h). P2 and P3 aren't approved.
- **Version numbers:** 1.8 is this release, 1.9 the player, 1.10 Social. The updater and the installer compare
  versions as numbers (`UpdateVersion.cs`), so 1.10 comes after 1.9. If the player is ready first, the numbers swap.

## 1.10: Player (from the backburner; planned as 1.8 until 2026-11-22, then 1.9 until 2026-11-25)
- **A floating picture-in-picture player** (the owner, 2026-11-22: "Yes, floating PiP"): a small always-on-top player
  window you can move and resize, while the rest of Nixie, or other apps, stays usable.
  - **The plan** (`briefs\180-plan.md`, 2026-11-22, from the player code):
    - **Hosting:** the default composition swap chain moves to a panel in a new unowned, always-on-top WinUI window.
      The code already allows one panel to take over from another, and the backend never touches an attached mpv's
      window, so there's no reload, no position jump and no audio change. It's not CompactOverlay, because Windows
      picks its sizes; and not the backend's child window, which is tied to the main window and can't carry
      controls. PiP is offered only on that default path.
    - **Enter:** "Mini player (P)" beside Full screen (P confirmed by the owner, 2026-11-22). The main window goes back
      to the item page, and playback isn't stopped.
    - **Restore:** the same player at the same place, with tracks, quality, skip rules and stats as they were, and no
      new play sent.
    - **Close:** one stop, reported to the server once.
    - **Keys:** the PiP opens without taking the keyboard. When focused, it takes the player's keys; the main window
      keeps its own, and only media keys reach the PiP. Esc in the PiP shows the controls and never closes.
    - **Edge cases:** closing Nixie closes the PiP; sign-out or a new Play closes it without a second stop; Up next
      plays on inside it; the skip question waits for the full player.
    - **Tests:** a harness script that drives both windows and counts the server's reports.
  - **The order:**
    1. A 30-minute spike: the live swap chain in a second window, switched and back with no jump. If it fails, the
       fallback (the main window shrinks into the mini player, losing browsing) goes back to the owner.
    2. A shared setup commit.
    3. Two streams: PiP (about 12 hours) and chapter extras (about 7).
  - Starts after 1.7 and TV 1.0, when usage and the 3 test slots allow.
- **Drive-in's chapter bulbs.**
- **Editorial's paused side column** with a chapter scrubber.
- **Report a problem, filled in** (the owner, 2026-11-22: "yes"). Today Settings › About › Report a problem opens a
  blank issue.
  - **The form:** it opens the bug report form (`?template=bug.yml`) with its fields filled from the link: version,
    theme, colours, glass, Windows, the server's Jellyfin version and the install kind. Dropdown values match the
    form's option text exactly. Never the server address, user name or token.
  - **"Save a log for this report":** the recent log lines, anonymised as the TV's Diagnostics does it (server
    addresses, names, user names and tokens removed), saved to a file, and the folder opens so the user can drag the
    file into the form. The form's "Log lines" help text says so.
  - **Nothing is sent by Nixie;** the user submits it in their browser. People without a GitHub account may get a
    Discussions fallback.
  - **The link is built from the release's repository name,** so it follows the rename to `mbustos27/nixie`.
  - **Checked once against GitHub's real form,** read only and never submitted (design rule 2): a fake can't show
    whether GitHub takes the values.

## 1.11: Social (research done; planned as 1.9 until 2026-11-22, then 1.10 until 2026-11-25)
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
  - **The owner's TV session done (2026-11-21, evening, on the onn. 4K Plus;** `Dev\nixie-tv\docs\TV-SESSION-RESULTS.md`):
    - **Good:** episodes, playing a movie, subtitles, typing in search, Editorial.
    - **Four findings:** a big library's first load is slow (TV-S1); scrolling it is slow, with 11% janky frames from
      layout and draw work, not images (TV-S2); the trailer controls feel odd (TV-S3); search results take far too
      long (TV-S4).
    - **Memory was fine:** about 230 to 320 MB.
    - **Not seen:** DTS and TrueHD, Skip intro, Up next.
    - **The owner delegated the open decisions** ("make all these decisions"):
      - the TV themes follow the desktop's design ("keep the design philosophy of desktop");
      - Nixie debug stays installed;
      - the release key is made at TV 1.0's release by the owner (keytool asks the passphrase itself);
      - **releases live in a public `mbustos27/nixie-tv`**, a GPL-3.0 fork of Glacier with credit, created when 1.0 is
        ready.
  - **The fix round started 2026-11-21 evening** (brief `briefs\tv-fix-session.md`), in two streams measured against a
    big fake library:
    - library paging and the scroll (TV-S1, S2);
    - search and the trailer player (TV-S4, S3);
    - **Continue watching for other home screens** (Watch Next; the owner, 2026-11-21: "yes"). Google TV's own home
      filters it, but Projectivy, Plume and LtvLauncher show it, searchable. The rows are kept in step with resume
      points, removed when finished, on removal (and put back on Undo) and on sign-out, with a setting to turn them off
      and no telemetry. A separate session is installing such a launcher on the owner's box for the re-check.

    Then a review, a short emulator hunt, the owner's quick re-check on the box, the key and the repository, and **TV
    1.0**.
  - **The fix round merged** (nixie-tv `main` at `aff8d55b`, with the A–Z fix; 824 JVM tests) and **went onto the
    owner's box on 2026-11-22** (build 10099, over Nixie debug, same key).
    - **Good:** playback is responsive.
    - **Wrong on their real server** (about 25,800 films; the fake had answered everything at once):
      - Home takes about a minute on every start (TV-S6, a Blocker for 1.0);
      - Movies' first row takes about 10 s (0.66 s on the fake);
      - a trailer takes about 25 s and opens small, then jumps to full screen (TV-S9);
      - Quit doesn't work while Home is still loading (TV-S10, Major).
    - **From their anonymised log:**
      - Continue watching on the launcher is emptied at every start;
      - a paused player is held all night in standby;
      - playback assumes a 1080p display on their 4K TV (TV-S7, TV-S8).
  - **A second fix round, running (2026-11-22),** each stream timed against a throwaway real Jellyfin with about 25,000
    items, never the owner's server:
    - H: Home and start-up (TV-S6, S10, Watch Next);
    - L: the library's first page and an A–Z jump by offset;
    - P: 4K, standby and the trailer (TV-S7 to S9).

    Detailed logs now record the waits' durations, so the next re-check on the box gives numbers without adb.
  - **Report a problem for TV 1.0** (the owner, 2026-11-22: "yes"): Settings › Diagnostics gets a "Report a problem"
    QR beside the log-download QR. It opens the TV repository's issue form, filled in with the device model, Android
    version, app version, theme and server version, and never a server address. `mbustos27/nixie-tv` gets its own issue
    forms when it's created at 1.0.
  - **Open a section from outside: remote buttons and launchers** (the owner, 2026-11-23: "lets put that on the
    roadmap"; not scheduled, after TV 1.0 unless the owner moves it). Today another app can only open Nixie's Home or one
    title (`OPEN_TITLE`, used by the Watch Next cards). The owner maps the onn remote's spare buttons with tvQuickActions
    Pro and wants them to land in Nixie sections. Plan: an `OPEN_SECTION` action on the exported `MainActivity` with a
    `section` extra for the existing top-bar targets (home, search, movies, shows, libraries, favorites, collections,
    livetv, settings) plus **resume** (play the first Continue watching item, Home when there is none); unknown values go
    to Home; it works from a cold start and while open; Settings keeps its parental PIN and nothing skips sign-in. Static
    app shortcuts (Resume, Search, Favorites, Movies, Shows) so mappers and launchers can pick them from a list. Until
    then the buttons open plain Nixie (box setup: `Dev\tv-research\docs\LAUNCHER-SETUP.md`).
  - **Media keys in the video player** (found 2026-11-23 on the owner's box: "CH+/CH− did nothing"; not scheduled,
    after TV 1.0 unless the owner moves it). The owner maps the remote's channel buttons to fast-forward and rewind
    with tvQuickActions, which sends media keys. While Nixie plays a video, Android reports "Media button session is
    null": the video player has no active `MediaSession` (only `music/MusicService.kt` has one), so play/pause,
    fast-forward, rewind, next and previous from any remote, phone or mapper go nowhere. Plan: a Media3
    `MediaSession` for the video player while it plays (released on exit), with fast-forward/rewind mapped to the
    player's own seek steps, plus a check that a media key reaches it.
  - **Fire TV, planned 2026-11-21** (the owner: "also plan amazon fire tv release"; `Dev\nixie-tv\docs\FIRE-TV-PLAN.md`):
    - **One codebase and one repository.** Fire OS 7, 8, 14 and 16 only; Amazon's Vega OS sticks run no APKs and allow
      no sideloading, and Amazon says future sticks will be Vega.
    - **Nixie already fits:** minSdk 28 (Fire OS 7), 32-bit ARM libraries, no Google services, and the FFmpeg decoder
      (Fire TV passes through only Dolby).
    - **Reworked the same evening at the owner's calls:** no device ("lets just emulate it i dont want to buy one") and
      no Appstore ("we'll do this sideload only on firetvb").
    - **F0 compatibility** (about half a day): Fire OS detection with a debug switch "Behave as Fire OS", Watch Next
      hidden on Fire OS (Fire's own row needs an Amazon partnership), the manifest, 16 KB-aligned libraries, refresh rate
      matching behind a setting, and Amazon's test criteria as tests. It's tested on two Android TV emulators without
      Google APIs, at API 28 (Fire OS 7) and API 30 (Fire OS 8, Amazon's own advice). No Fire OS emulator exists, and
      Amazon's free cloud Fire TVs need a developer account the owner chose not to make.
    - **F1 sideload:** the same GitHub APK with its updater, and a README section for Fire TV (the devices, Downloader,
      the known gaps).
    - **Known gaps, said plainly** ("built for, not yet tried on a device"): HDMI passthrough, HDR and Dolby Vision
      output, refresh-rate switching, real-box speed and memory, Fire's launcher, the sideload and updater flow on Fire
      OS, remote buttons and the screensaver. Each is handled by shared code checked on the onn box and the emulators,
      and by user reports.
    - **Nothing for the owner to buy or sign up for.**
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
- **The repository becomes `mbustos27/nixie`** (the owner, 2026-11-22, on whether to stage it: "its early on we can just
  update it"). The order:
  1. 1.7 is published under the current name, so its Sandbox leg (1.6.1's Restart to update installing 1.7) stays as
     tested.
  2. The repository is renamed right after.
  3. 1.5.0 and later already follow GitHub's redirect for a renamed repository (BH-140X-m3), so installed copies keep
     updating. The owner's own copy checking for 1.7 after the rename is the first real-world check of that.
  4. 1.4.1 and earlier would need one manual download.
  5. The next fix release switches every old-name place to the new name: never before the rename, since the API
     URL would 404. That is 1.7.3: 1.7.1 became the library-index fix alone, at the owner's call, and 1.7.2 its hotfix.
  6. A new repository named `fluentfin-mpv` is never created, because it would break every redirect. The local folder
     `Dev\fluentfin-mpv` keeps its name, and only the git remote changes.
  - **Done 2026-11-22, right after 1.7.0:**
    - renamed to `mbustos27/nixie`, with the same repository id;
    - the old API address answers 301 to it, and followed it gives v1.7.0;
    - the old page and download links answer 301;
    - the docs' links switched on main.

    The owner's installed copy finding 1.7.0 is the real-world check. 1.7.3 switches the code's constants (the
    updater's repository, the installer's links, the sources link); until then they follow the redirect.
- **1.7.0's Sandbox test runs after publishing** (the owner, 2026-11-22: "sandbox test after publishing and opening
  test build"). The order: build, verify-package, Defender, publish, open the release build on the owner's desktop
  as their test copy, then the Sandbox. As with 1.5.0 and 1.6.0, a failure there makes a hotfix.
- **Twelve design rules, for desktop and TV** (the owner, 2026-11-22: "remember the issues we have and make them a part of
  the design philosophy to look for things like that"): [DESIGN-RULES.md](DESIGN-RULES.md), each from a real bug.
  - The first screen waits only for what it shows.
  - Time against a real Jellyfin at scale.
  - No whole-library work on an interaction path.
  - Nothing destructive on "not loaded yet".
  - Standby releases what it holds.
  - Device facts come from the real source.
  - No layout jump.
  - A way up from every page.
  - A switch turned off stops in-flight work.
  - Detailed logs record durations.
  - The user is not the server's admin (the owner, 2026-11-22: "always remember im not a jellyfin server admin"; added after 1.7.1's Collections card).
  - Every basic path is a release gate, at real shape (the owner, 2026-11-22: "Testing should catch these kinds of things"; added after a tester's six basics bugs).

  Every TV and desktop brief carries them, and the bug hunts check them as class 12.
- **A filled-in Report a problem in the player release (1.9; 1.8 when decided) and TV 1.0** (the owner, 2026-11-22: "yes"): the issue form opens filled in
  with the app's and the server's versions, with an anonymised log to attach, and nothing sent by Nixie.
- **P opens the mini player (1.9; 1.8 when decided)** (the owner, 2026-11-22, asked "Should P open 1.8's mini player?": "Yes, P"). Plain P is
  free: the app's shortcut maps use P only as Shift+P (Previous, `MpvControlsViewModel.cs:1882`,
  `AppShortcuts.cs:191`), and the keys matrix has no P case.
- **The pop-out player (1.9; 1.8 when decided) is a floating picture-in-picture window** (the owner, 2026-11-22, asked whether it meant that:
  "Yes, floating PiP"). It's a small always-on-top window you can move and resize, beside chapter extras. It no longer waits for TV 1.0
  (the shorter timeline).
- **Fire TV is sideloaded only and tested by emulation** (the owner, 2026-11-21: "lets just emulate it i dont want to
  buy one", then "we'll do this sideload only on firetvb", meaning Fire TV). There's no Amazon Appstore build, listing,
  account or review, and no Fire TV device. Fire TV gets the same GitHub APK with its updater, tested on Android TV
  emulators at Fire OS 7's and 8's levels, with what an emulator can't prove listed as known gaps.
- **TV 1.0 publishes Continue watching for third-party home screens** (the owner, 2026-11-21, asked whether 1.0 should
  or leave it for later: "yes also lets start another session to install a new launcher on my android tv"). Google
  TV's own home filters uncertified apps' Watch Next (phase 0), so this serves launchers like Projectivy. It's a small
  stream in the TV fix round, so 1.0 isn't held for it.
- **1.7 starts beside 1.6.1** (the owner, 2026-11-21: "We can start 1.7"). The release rules run each fix release beside
  the next feature release, and weekly usage was at 38%, so there was no reason to wait. 1.6.1 is close to its release,
  so it keeps priority on the test slots and usage. The keyless sources (TMDb's export files, Wikidata) and TMDb trending
  with each user's own token start at once; Trakt needs only its Client ID, never a sign-in, and any registration is the
  owner's own step.
- **TMDb (1.7):** ~~each user brings their own token at first~~. **Changed 2026-11-22:** the owner read PRIVACY.md's
  table ("this seems not great") and agreed to three fixes ("ya lets do that"):
  1. **Nixie's own TMDb token** (the v4 Read Access Token, sent as `Authorization: Bearer`) is baked into the release
     build like the Trakt Client ID: never in the repository, and the release refuses to build without it. A user's
     own token stays as an optional override. The owner registers the TMDb app and sets it themselves.
  2. **"Kept at most" reads as Nixie's own copy of public lists,** not stored data, in PRIVACY.md and the Settings
     cards alike.
  3. **A gentler refresh:** TMDb export files weekly instead of daily, trending daily, Wikidata weekly.
- **Save as playlist (1.6):** yes, as an explicit button that copies a Nixie collection to the server as the user's own
  playlist (an ordinary user can). Nothing is written to the server otherwise.

- **TV Home (TV 1.0):** the owner, 2026-11-22: "the goal of home tv loading should be continue watching then up next and then that on one page basically and then an easier way to access collections". Home shows Continue watching, then Up next, on one screen and nothing else; Favorites, each library, Music and Live TV stay in the top bar, and **Collections** gets its own top-bar entry (every collection on the server, A to Z). Home asks the server for those two rows only. Stream T (`briefs\tv-home-two-rows.md`, branch `tv-home-two-rows`).

## Decided for Social, 1.11 (2026-11-22, the owner: "Yes, all ten"; planned as 1.9 then, 1.10 until 2026-11-25)
From `docs\reports\Nixie 1.9 social research.md` (local), each the recommended answer:
1. **Suggestions** go through playlists between friends on the same server (a private playlist shared with chosen
   people works for an ordinary user since Jellyfin 10.9; no admin step).
2. **Friends** come from the server's login-screen user list, plus friend codes for users the server hides.
3. **One picks playlist per sender**, read-only for the friend.
4. **No sharing of what you watch** in this release.
5. **Suggestions show** as a Home row plus a Friends page.
6. **All three Discover extras:** TVmaze, AniList and MDBList (the user's own key, stripped from logged URLs), in no set
   order, so they can be built side by side (the owner: "tvmaze doesnt need to be first per se"). A one-word "TVmaze" in
   another thread was curiosity (the owner: "i was simply curious about it", then "i want all 10 reccs").
7. **AniList's id map** comes from Kometa's MIT-licensed file, matched on this PC.
8. **TVmaze:** one full schedule a day.
9. **The Diary** is restyled in all four themes now (its 11 open findings).
10. **Star ratings sent to Jellyfin** are tested on our QA servers first, then kept or removed.

## Still open (later releases)
- Skip repeated extras: one skip stream in 1.6 (the rules with their margin, then audio-matched intros once the spike
  passes); audio intros become 1.7's first item if 1.6 is full. Waits on the owner: chapters, a button or skip by
  itself, per show or per season, how Sunny behaves today and with online lookup on, and the fingerprint tool.
- Friends through the server only, or a Trakt exception (Social, 1.10).
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
