# FluentFin MPV: UX/UI audit against industry standards

Date: 2026-11-15. Code: `C:\Users\moise\Dev\fluentfin-mpv` at ff0c97c (clean tree).
Method: read-only code review of `ui\` and the bridge parts of `backend\`, the four upstream
screenshots in `ui\Screenshots\`, the repo's own research (`docs\research\perf-ui.md`,
`docs\research\winui-mpv-embed.md`, `docs\research\perf-backend.md`) and `docs\KNOWN-ISSUES.md`,
plus web research (WebSearch/WebFetch only). Nothing was built, run, edited or signed in to.

Paths are relative to the repo root. Numbers in square brackets point at the sources in §7.
"[code]" means the claim comes from reading this repo. "Inference" or "unverified" means a source
did not confirm it and it should be checked in a spike or on the real server.

Ratings:
- **Impact** H = blocks or clearly degrades the core loop (open app, find something, play, watch)
  in most sessions, or fails a WCAG 2.2 A/AA criterion on that loop. M = noticeable friction, or an
  AA issue on a secondary flow. L = polish.
- **Effort** S = up to 1 day. M = 2 to 5 days. L = 1 to 3 weeks.

---

## 0. Summary

- **The player is the biggest gap.** In bridge mode the backend forces `enable_gui=False`
  (`backend\jellyfin_mpv_shim\bridge.py:301`). That turns the shim's own jellyfin-styled HUD
  (`osc_style "mpvtk"`) into a fallback to **mpv 0.41's stock `osc.lua`**
  (`backend\jellyfin_mpv_shim\mpv_options.py:208-212`). Everything in the user's screenshot
  follows from that: the cache readout, the `1/2` and `3/3` track counters, the white bar, mpv's font,
  and subtitles under the bar. The stock OSC in 0.41 has no option to avoid subtitles or hide the
  cache readout [20].
  - The same fallback silently removes **Skip Intro**. With the stock OSC and the default
    `segment_intro: "ask"`, the only prompt is a "Seek to Skip" text that is gated behind
    `skip_intro_on_seek`, which is off by default (`backend\jellyfin_mpv_shim\player.py:2727-2771`).
    So a FluentFin MPV user gets no skip prompt at all. Jellyfin web, Plex, Emby, Netflix and
    Infuse all offer one [4][8][10][15][17].
- **The doubled title is a data/template problem, not mpv.** `Media.get_proper_title()` prints
  `"%s - s%de%.2d - %s"` (series, season, episode, episode name) with no check
  (`backend\jellyfin_mpv_shim\media.py:545-569`). It looks like the server's name for that episode
  equals the series name (probably missing episode metadata; unverified). Industry form:
  "Series - S6:E4 - Episode title" [2].
- **Home is slow by construction.**
  - The whole page stays a shimmer until the slowest of three loaders finishes
    (`ui\FluentFin.Core\ViewModels\HomeViewModel.cs:47-78`).
  - One of those loaders is 1 + N **sequential** requests (`JellyfinClient_User.cs:56-108`).
  - Nothing is cached, so every visit to Home refetches everything.
  - Measured in `perf-ui.md`: 9 requests, 427 ms at 50 ms per request; the 6 "Latest" calls take
    374 ms one after another and 62 ms in parallel.
  - Also a probable bug: Next Up sends `NextUpDateCutoff = now`, where jellyfin-web sends now minus
    365 days [26]. That may leave Next Up empty. Verify on the real server.
- **Keyboard and screen-reader users cannot open anything.**
  - Media cards open on `Tapped` of a `Grid`, and their buttons are collapsed until the pointer
    hovers (`ui\FluentFin\Controls\BaseItemDtoCard.xaml:68-100`).
  - Item pages' Play, Watched, Favourite and More buttons are icon-only, with no name and no
    tooltip.
  - Result: WCAG 2.1.1 Keyboard (A) and 4.1.2 Name, Role, Value (A) fail on the core loop [56].
- **Player-controls recommendation, in phases:**
  1. Now: polish mpv's OSC (mpv 0.42 options, title, subtitle margins, key map, skip prompt). S.
  2. Next: native Fluent controls in a **windowed XAML `Popup` over the video**, after a 1 to 2 day
     spike. This is the "owned overlay window" option, done with WinUI's own windowed popups instead
     of a hand-made layered window. FluentFin owns mouse and keyboard; mpv's OSC is off. M to L.
  3. Later: libmpv in the UI process, rendering into a `SwapChainPanel` through mpv 0.42's new D3D11
     render API or 0.41's composition mode [22][23][68]. This ends the airspace problem for good.
     L to XL.

  The reserved strip below the video is the fallback for windowed mode if the spike fails. §4 has
  the comparison.

---

## 1. Industry baseline

### 1.1 Home, browse, item, search, first run, settings, errors

| Area | What leading clients do (cited) |
|---|---|
| **Home** | **Jellyfin web:** up to 10 user-orderable sections. Default order: library tiles, Continue Watching, Next Up, Latest per library. Sections load **in parallel**, and each appears when its own data arrives [26]. Continue Watching 12 items; Next Up 24 (cutoff now − `maxDaysForNextUp`, default 365); Latest 16 per library. All use `imageTypeLimit=1` and `enableTotalRecordCount=false` [26]. **Emby:** any number of sections, drag to reorder, optional "Spotlight" hero view [30]. **Netflix:** Continue Watching first; rows thematically coherent; upper-left gets the most attention [31]. **Kodi Estuary:** "In progress" first, then Recently added [14]. |
| **Library** | **Jellyfin web:** paged, page size 100 by default; 10.11 added an A–Z picker; 12.0 added a sticky app bar and language filters [7][26]. **Plex:** item count, poster-size slider, sort with direction (support page via search snippet only). |
| **Item page** | **Jellyfin web:** "Resume" plus a Replay button when there is a resume point. Also logo, backdrop (≥ 1000 px wide), trailer button when a trailer exists, cast, "More like this" (12), series Next Up, seasons/episodes, chapters; blurhash placeholders [75]. |
| **Search** | **Jellyfin web:** as-you-type with a 500 ms debounce, results grouped by type (Movies, Shows, Episodes, People, …), empty groups dropped, a "no results" message [27]. |
| **First run / login** | **Jellyfin:** UDP 7359 server discovery (local subnet only); Quick Connect with a 6-character code approved from another signed-in device [29]. **Jellyfin web login:** public user tiles with avatars; "Remember me"; Quick Connect shown only when the server enables it; clear 401/403/unreachable messages [28]. **Plex Home:** fast user switching, optional auto sign-in to the last account [snippet]. |
| **Settings** | **Jellyfin per-user settings (server-side):** preferred audio and subtitle language, subtitle mode (Default/Always/OnlyForced/None/Smart), remember tracks, auto-play next episode [71]. **Per client:** skip action per segment type (Ask/Skip/None) [5]. **Plex "Player Experience":** Skip Intro/Credits (off/manual/auto), Up Next timing, auto-play countdown (5–60 s), "passout protection" [8]. |
| **Errors / empty** | **NN/g:** never leave an empty state blank — say what happened, what would appear, how to fill it, and never show "nothing" while still loading [34]. **Microsoft's archived app checklist:** first screen within 5 s, a clear message on lost connection, no empty pages, no stack traces [39]. |

### 1.2 Player controls

| Topic | Baseline (cited) |
|---|---|
| **Layout** | **Jellyfin web:** top bar with back and title. Bottom: seek bar, then prev/next item, prev/next chapter (only with >1 chapter), −/+ skip, play/pause, subtitles, audio (only with >1 track), settings, full screen, PiP, mute/volume [1]. **Netflix:** back top-left, full-width scrubber above one icon row [snippet]. **Kodi:** seek bar on top, transport left, Streams/Settings right [14]. **Microsoft:** prefer dark theme and full window; double-row transport layout when there is room [47]. |
| **Title** | **Jellyfin web:** "Series - S6:E4 - Episode title" (`itemHelper`) [2]. **jellyfin-mpv-shim:** "Series - s6e04 - Episode" via `force-media-title` [25][code]. |
| **Track pickers** | **Jellyfin web:** menus anchored to their buttons, items labelled with the server's `DisplayTitle`. Audio: language, codec, layout, Default. Subtitles: language, "Hearing Impaired", Default, Forced, codec, External. "Off" first; secondary subtitles in 10.9+ [1][3][6]. **Netflix:** one Audio & Subtitles panel, SDH badge [11]. **Stremio:** Languages, then Variants, then Settings (delay, size, position) [13]. |
| **Skip intro/credits** | **Jellyfin web:** "Skip Intro"/"Skip Outro" button, hidden after 8 s and shown again with the OSD; "Ask to skip" is the default since 10.10.1 [4]. Segments come from plugins [5]. **Plex:** off/manual/auto [8]. **Netflix:** S key skips intro [10]. **Emby:** ignore/auto/button [15]. **Infuse:** auto or manual [17]. |
| **Next episode** | **Jellyfin web:** Up Next card 30–40 s before the end (episodes ≥ 10 min), "Next {0} Playing in {1}" [1]. **Plex:** countdown 5–60 s, Up Next at end or during credits [8]. **Netflix:** "still watching" after two episodes without interaction [74]. **Jellyfin 12.0:** adds a still-watching prompt [7]. |
| **Trickplay / chapters** | **Jellyfin 10.9+:** trickplay tiles [6]. **Jellyfin web:** preview bubble with tile, chapter image or time; chapter ticks; "Ends at"; click toggles total/remaining [1]. **Plex:** BIF thumbnails [9]. **mpv 0.41+:** OSC Preview API, which the shim already feeds [20][code]. |
| **Auto-hide** | **Jellyfin web:** 3000 ms, cursor hides with the OSD; single click pauses (300 ms delay); double-click full screen; wheel changes volume [1]. **Stremio:** 3000 ms, stays while paused or a menu is open [13]. **Stock mpv OSC:** `hidetimeout=500` [20]. |
| **Keyboard** | **YouTube:** k/Space pause, j/l ±10 s, ←/→ ±5 s, ↑/↓ volume, f full screen, m mute, c captions, 0–9 percent seek [12]. **Netflix:** Space/Enter pause, F, Esc, ←/→ 10 s, ↑/↓ volume, M, S skip intro [10]. **Jellyfin web:** Space/k, f, m, ↑/↓ volume, j/← and l/→ skip, Shift+N/P next/previous, 0–9, PageUp/PageDown chapters [1]. **Stremio:** Ctrl+/ shows the shortcut list [13]. |
| **Subtitles vs controls** | **Emby web:** raises subtitles while the progress bar shows (stopped for ASS in 4.8.8) [16]. **Chrome native captions:** move up when controls show [19]. **mpv 0.42 OSC:** `sub_margins` + `dynamic_margins` do the same [20]. **ModernZ:** ships both on [24]. |
| **Stream stats** | No mainstream client puts a cache readout in the main bar. **Jellyfin web:** "Playback Info" in the settings menu [72]. **Netflix:** hidden stats overlay (Ctrl+Shift+Alt+D, third-party sources). **Kodi:** `o`. **Stremio:** `D` [13][14]. |

### 1.3 Perceived-performance norms

- **Microsoft interaction classes** [35]:
  - Fast: 100 ms ideal / 200 ms maximum (a button's first response).
  - Responsive: 500 ms / 1 s (navigate to a page).
  - Launch: 1 s / 3 s.
  - Launch overall: under 1 s is "excellent", under 5 s "good".
  - Also: cache content that is expensive to access, and on a cache miss show placeholder UI as
    quickly as possible.
- **Microsoft startup guidance** [36]:
  - Show UI that does not depend on the network first, then load network content asynchronously,
    and consider caching it locally.
  - Defer UI with `x:Load`; use page caching (`NavigationCacheMode`).
  - For lists: UI virtualization is "the most important improvement", plus `x:Phase` /
    `ContainerContentChanging` [37].
  - Size image decoding to the control [77]. `await` alone does not leave the UI thread [38].
- **RAIL** [33]: respond within 100 ms; 1 s keeps the user's flow; over 10 s they leave. The page
  now points to Core Web Vitals for goals.
- **NN/g** [34]:
  - Limits of 0.1 s / 1 s / 10 s.
  - Skeletons for full-page loads under 10 s.
  - A spinner for 2–10 s on one module.
  - Nothing under 1 s.
  - Avoid "frame-only" skeletons.
- **Stale-while-revalidate** [33]: show the cached copy now and refresh in the background.
  jellyfin-web uses TanStack Query, which keeps data 5 min and refetches on mount and focus [32].
  Whether Plex or Jellyfin apps show a cached Home on cold start was **not verified**.

---

## 2. Platform standards

### 2.1 Fluent 2 / WinUI 3

| Topic | Guidance |
|---|---|
| **Type** | Segoe UI Variable. Ramp: Caption 12/16, Body 14/20, Body Strong, Body Large 18/24, Subtitle 20/28, Title 28/36, Title Large 40/52, Display 68/92. Semibold, not Bold. Sentence case. Minimum 12 px regular or 14 px semibold [40]. |
| **Spacing** | 8 epx between buttons and between a control and its header; 12 between a control and its label and between content areas; 16 between a surface edge and text [41]. |
| **Geometry** | 4 px corners on in-page controls, 8 px on overlays, flyouts and dialogs [42]. |
| **Materials** | Mica for the window base; Acrylic only for transient light-dismiss surfaces; Smoke behind modal dialogs [43]. |
| **Navigation** | NavigationView: Left for 5–10 top-level destinations; adaptive at 641/1008 epx [44]. Back button top-left, in the title bar when the title bar is custom [45][70]. |
| **Title bar** | 32 px, or 48 px with search or an avatar. All empty space draggable. Title in Caption style [45]. |
| **Motion** | 83/167/250 ms durations; entrance `cubic-bezier(0,0,0,1)`; connected animation for poster-to-detail [46]. |
| **Media** | MediaPlayerElement / MediaTransportControls: dark theme, full window encouraged, double-row layout when there is room; captions and audio choice built in [47]. |

### 2.2 Windows accessibility and WCAG 2.2 AA

| Topic | Guidance |
|---|---|
| **Keyboard** [48] | Everything a pointer can do must work from the keyboard. Wrap clickable images in a Button. `AccessKey` / `AcceleratorKey` only *describe* shortcuts; `KeyboardAccelerator` creates them. |
| **Focus** [49] | System focus visuals: 2 px outer + 1 px inner. Do not suppress them. |
| **Names and headings** [50][51] | Icon-only buttons need `AutomationProperties.Name` (no "button" in the name). Use `HeadingLevel` for headings and `LandmarkType` for regions. |
| **Announcements** [52] | `LiveSetting` plus a raised `LiveRegionChanged` (or `RaiseNotificationEvent`). |
| **Contrast themes** [53] | `{ThemeResource}` brushes, a `HighContrast` dictionary, no hard-coded colours; `HighContrastAdjustment=None` only once brushes are correct. |
| **Text scaling** [54] | 100–225%. Layouts must use Auto/\* sizing and wrapping; `SwapChainPanel` content does not scale. |
| **Targets** [55] | About 40×40 epx touch targets. |
| **WCAG 2.2** [56][57] | 1.2.2 captions (A); 1.4.3 / 1.4.11 contrast; 1.4.13 content on hover/focus (seek-bar previews, tooltips); 2.1.1 keyboard (A); 2.2.2 pause/stop/hide for auto-advancing content such as Up Next countdowns (A, inference); 2.4.7 focus visible; **2.4.11 focus not obscured (new in 2.2)**, which matters for any always-on-top overlay; **2.5.8 target size ≥ 24×24 (new in 2.2)**, which WCAG2ICT maps to epx; 3.3.1 / 3.3.3 error text and suggestion; 4.1.2 name/role/value; 4.1.3 status messages without moving focus. |
| **Section 508** [58] | 503.4: caption controls at the same level as volume. |

---

## 3. Audit by area

Each finding gives what the user sees, the standard it falls short of, the change, files, impact and
effort.

### 3.1 First run and login

| # | What the user sees | Standard | Change | Files | I | E |
|---|---|---|---|---|---|---|
| F1 | With a saved login, **no window at all** until sign-in finishes: 4–6 round trips normally, **21 s** if the server is unreachable (measured, perf-ui O4). | Launch 1 s ideal / 3 s max [35]; show non-network UI first [36]. | Activate the window first with a "Signing in to *server*…" state, plus *Use another server* and *Cancel*; then authenticate. | `ui\FluentFin\Services\ActivationService.cs:36-41`, `Activation\DefaultActivationHandler.cs:51` | H | M |
| F2 | Server page: a bare text box under a bold "Server" label. No example address, no input scope. **Next** gives no progress for up to 21 s on a bad address. | Labelled inputs (4.1.2) [56]; NN/g 1 s / 10 s limits [34]. | `Header="Server address"`, `PlaceholderText="https://jellyfin.example.com:8920"`, `InputScope=Url`. Progress ring on Next. A ~10 s connect timeout with "Can't reach … check the address, port or VPN". Run **Detect** automatically on first open (LAN only [29]). | `Views\SelectServerPage.xaml:60-95`, `ViewModels\SelectServerViewModel.cs:40-45` | M | S |
| F3 | Login: the "Username"/"Password" labels are separate TextBlocks, so Narrator reads unnamed edit boxes. Three equal **accent** buttons (Switch server, Quick Connect, Login), so there is no primary action. No progress while signing in. | 4.1.2, 3.3.1 [56]; one accent button per view (Fluent). | Use `Header` on the inputs. Login is the only accent button; the others are standard or hyperlink. Disable plus a ProgressRing while signing in. Show errors in an inline InfoBar rather than a modal dialog. Optionally public-user tiles as in jellyfin-web [28]. | `Views\LoginPage.xaml:30-70`, `ViewModels\LoginViewModel.cs:45-55` | M | S |
| F4 | Quick Connect dialog reads "Enter code X to login" but not *where*. Its button says **OK**, and pressing it leaves the 5 s poll running (only success disposes it). | Jellyfin: code approved from Settings > Quick Connect on a signed-in device [29]. | "On a device that is already signed in, open Settings → Quick Connect and enter **X**". Button **Cancel**, which disposes the poll. | `ViewModels\LoginViewModel.cs:58-98` | M | S |
| F5 | With two saved servers, or two users on one server, every launch stops at the server picker. There is no "last used" sign-in. | Plex auto-signs in to the last account [snippet]; Microsoft: get to content fast [35]. | Remember the last server and user; sign them in automatically (F1's flow) with a visible "Switch". | `Activation\DefaultActivationHandler.cs:49-70` | M | S |

### 3.2 Startup and perceived performance (cross-cutting)

| # | What the user sees | Standard | Change | Files | I | E |
|---|---|---|---|---|---|---|
| P1 | The Flyleaf engine starts on the UI thread before the window on every launch, whatever player is chosen: ~128 ms warm, 941 ms first ever, +18.5 MB (perf-ui O5). | Defer work not needed for the first frame [36]. | Start Flyleaf lazily from `MediaPlayerHost.CreateFlyleaf()`. | `ui\FluentFin\App.xaml.cs:249, 308` | M | S |
| P2 | The first **Play** in a session waits on "Starting mpv…" then "Connecting…": the Python launcher, 424 ms of imports, and mpv spawn plus pipe (perf-backend §2). | Prefetch [35]; Responsive class 500 ms / 1 s [35]. | After sign-in, at low priority, start and `connect` the backend so mpv sits idle. Skip this on battery saver, and measure the idle memory cost first. | `ui\FluentFin.Core\Mpv\MpvPlaybackService.cs:163-185`, `MpvBackendHost.cs:290` | M | M |
| P3 | Every Back to Home, Library or an item page rebuilds the page and refetches everything (no `NavigationCacheMode`). | Page caching [36]; stale-while-revalidate [33]. | Cache Home and Library pages and refresh in the background on return. Needs the view-model lifetime fix perf-ui notes: `HomeViewModel.OnNavigatedFrom` disposes for good. | `Services\NavigationService.cs`, `HomeViewModel.cs:41-45` | H | M |

### 3.3 Home

| # | What the user sees | Standard | Change | Files | I | E |
|---|---|---|---|---|---|---|
| H1 | One big shimmer until **all** of Continue Watching, Next Up and every "Recently added" row are loaded. The slowest request gates the page. This is the user's "initial home page is kind of slow". | jellyfin-web shows each section as it arrives [26]; NN/g skeletons [34]; Microsoft: placeholders on a miss [35]. | Give each section its own loading state: skeleton row → content. Render Continue Watching as soon as it lands. Drop the page-level `IsLoading` switch. | `ui\FluentFin.Core\ViewModels\HomeViewModel.cs:47-78`, `ui\FluentFin\Views\HomePage.xaml:33-154` | H | M |
| H2 | "Recently added" rows: `UserViews`, then one `Items/Latest` per library **in sequence**. That is 374 ms vs 62 ms in parallel at 50 ms per request (perf-ui O1), and seconds on a remote HTTPS server. `UserViews` is fetched again on every visit though sign-in already has it. | Chunky, parallel requests [37]; jellyfin-web loads sections in parallel [26]. | Start all Latest requests together and yield in library order. Cache `UserViews` for the session. | `ui\FluentFin.Core\Services\JellyfinClient_User.cs:56-108` | H | S |
| H3 | Cold start always shows empty skeletons, even though the last Home was seconds or days old. | Cache expensive content [35]; stale-while-revalidate [33]. | Persist the last Home snapshot (ids, names, image tags, blurhash) to the data folder. Render it instantly, then refresh and update in place. | `HomeViewModel.cs`, new small cache service in `FluentFin.Core` | H | M |
| H4 | **Next Up may be empty or short.** FluentFin sends `NextUpDateCutoff = now`; jellyfin-web sends now − `maxDaysForNextUp` (365 by default). The parameter is the "starting date of shows to show in Next Up" [26]. Whether the server then drops every show (or keeps only first-episode fallbacks) is **unverified**. | Parity with jellyfin-web [26]. | Use `now − 365 days` (make it a setting later). Check on the real server: is Next Up empty today? | `JellyfinClient_User.cs:44` | H (if confirmed) | S |
| H5 | Continue Watching and Next Up scroll sideways with **hidden scroll bars and no arrows**. A mouse-wheel user cannot reach the 4th+ card, and the keyboard cannot either (cards are not focusable, A1). The "Recently added" rows do have arrows. | 2.1.1 [56]; consistency. | Use `PagedScrollView` (with arrows) for all rows, or show the scroll bar on hover. Make arrows and cards keyboard reachable. | `Views\HomePage.xaml:44-95` | M | S |
| H6 | Empty, error or unreachable server: the page is simply blank. Every loader swallows exceptions and returns null, so sections hide. | NN/g empty states [34]; Microsoft "no empty pages", "clear message on lost connection" [39]. | "Couldn't reach *server*" InfoBar with **Retry**. For a truly empty library: "Nothing here yet. Add media in the Jellyfin dashboard." | `HomeViewModel.cs:81-108`, `JellyfinClient_User.cs` | M | S |
| H7 | Section headers use Title (28 px) and "Recently Added In Movies" in title case. | Fluent: Subtitle (20) for section headers, sentence case [40]. | `SubtitleTextBlockStyle`, "Recently added in Movies", `HeadingLevel=Level2`. | `Views\HomePage.xaml:39-42, 69-72, 103` | L | S |

### 3.4 Library and browse

| # | What the user sees | Standard | Change | Files | I | E |
|---|---|---|---|---|---|---|
| L1 | Opening a big library downloads **all** items in one response (6.4 MB for 5,000 movies, with `Overview`), then pages on the client (perf-ui O6). | Incremental loading, "chunky not chatty" [37]; jellyfin-web pages on the server [26]. | First drop `Overview` from the grid request (low risk). Later, server-side paging with `startIndex`/`limit`. | `JellyfinClient_Items.cs:10-40`, `LibraryViewModel.cs:24, 150-160` | M | S→L |
| L2 | No A–Z jump, no item count, no "Played/Unplayed" quick filter. Filters are free-text tokenizing boxes for genres, tags and years. | jellyfin-web A–Z (10.11), filters [7][26]; Plex item count [snippet]. | Add a count in the header, an A–Z strip when sorted by name (`nameStartsWith` [26]), and checkable lists for genres/years from `GetFilters`. | `Views\LibraryPage.xaml:45-180` | M | M |

### 3.5 Item pages (movie, series, season, episode)

| # | What the user sees | Standard | Change | Files | I | E |
|---|---|---|---|---|---|---|
| I1 | **Movie and episode descriptions are cut to one line** and run off the right edge. `TextBlock` without wrapping; visible in `ui\Screenshots\movie.png`, "…in exis". Series and season pages do wrap. | Readability; text scaling [54]. | `TextWrapping="WrapWholeWords"`, `MaxLines` 4–5 with a "More" toggle. | `ui\FluentFin\Views\MoviePage.xaml:285`, `EpisodePage.xaml:302` | M | S |
| I2 | Play is a 50 px **icon-only** circle among three similar toggles at the far right. No "Resume (23 min left)", no "Play from beginning". Play always resumes silently (`GlobalCommands.cs:32`, `MpvQueue.cs:68,79`). | jellyfin-web "Resume" + Replay [75]; Netflix/Plex primary labelled Play/Resume. | A primary accent `Button` with text: **Resume 23:14** or **Play**. A secondary "From the beginning". Keep Watched/Favourite/More as labelled icon buttons. | `MoviePage.xaml:151-188`, `EpisodePage.xaml`, `SeriesPage.xaml:148`, `SeasonPage.xaml:126` | H | S |
| I3 | Those buttons, and the PagedScrollView arrows, have **no accessible name and no tooltip**: 0 `AutomationProperties` and 0 tooltips in all four item pages. Narrator says "button". | 4.1.2 [56]; image-only buttons need `AutomationProperties.Name` [50]. | Names + tooltips on every icon button ("Play", "Mark as watched", "Add to favourites", "More options", "Scroll left/right"). | as I2, `Controls\PagedScrollView.xaml:41-57` | H | S |
| I4 | Tags sit in a hidden-scrollbar horizontal strip that runs off screen. Fixed sizes: poster 450×675, track ComboBoxes `MinWidth=800`. They break at narrow windows and at 150–225% text size. | Text scaling, Auto/\* sizing [54]; NavigationView adaptive widths [44]. | Wrap tags (`WrapLayout`). Remove fixed widths; use VisualStateManager breakpoints at 641/1008 epx; hide the poster under ~1000 epx (jellyfin-web hides backdrop under 1000 px [75]). | `MoviePage.xaml:203, 258, 276, 290-306`, `SeasonPage.xaml:175` | M | M |
| I5 | Hard-coded colours: yellow star, red favourite toggle; item pages force `RequestedTheme="Dark"`. | Contrast themes: no hard-coded colours [53]. | Theme resources with HighContrast fallbacks (`SystemColorWindowTextColor`). | `MoviePage.xaml:134-138, 178-180`, `Controls\BaseItemDtoCard.xaml` (favourite) | L | S |
| I6 | (Good) Audio and subtitle choice on the item page uses the server's `DisplayTitle` and passes the pick to the mpv backend. This is the label quality the player itself lacks. | [3] | Keep; reuse in the player (§4). | `MoviePage.xaml:250-283` | — | — |

### 3.6 Search

| # | What the user sees | Standard | Change | Files | I | E |
|---|---|---|---|---|---|---|
| S1 | Suggestions only, in a dropdown mixing movies, shows and people; no episodes. **Enter does nothing**; there is no results page, no "No results", and nothing under 3 characters. | jellyfin-web: grouped results page, "no results" text [27]. | Handle `QuerySubmitted` → a Search page grouped by type (add Episodes); "No results for …"; keep the suggestions dropdown for quick picks. | `ui\FluentFin\Behaviors\GlobalSearchBoxBehavior.cs`, `ui\FluentFin.Core\Services\JellyfinClient.cs:92-110` | M | M |
| S2 | Responses can arrive out of order (`SelectMany`), so an older query's results can replace the newer one's. | Correctness. | `Select(...).Switch()` (cancels stale requests). | `GlobalSearchBoxBehavior.cs:24-35` | L | S |
| S3 | The search box is a fixed 600 epx in the title bar. | Title bar: 48 px when it holds search [45]; adaptive layouts. | `MaxWidth=600` with stretch; collapse to an icon under ~800 epx. | `Views\TitleBarControl.xaml:48` | L | S |

### 3.7 Player, windowed and full screen

Today's player page with mpv (`ui\FluentFin\Views\VideoPlayerPage.xaml`):
- An InfoBar row for messages.
- A black `MpvVideoArea` that the backend's mpv window covers.
- mpv's stock OSC as the only controls.
- In windowed mode the NavigationView pane and the title bar (with search) stay visible.

| # | What the user sees | Standard | Change | Files | I | E |
|---|---|---|---|---|---|---|
| V1 | **Subtitles drawn over the control bar.** Stock `osc.lua` 0.41 cannot move subtitles; `boxvideo` needs `visibility=always` [20]. | Emby/Chrome lift captions while controls show [16][19]; mpv 0.42 `sub_margins` + `dynamic_margins` [20]. | Now: upgrade to mpv 0.42 and add `osc-sub_margins=yes,osc-dynamic_margins=yes` to the bridge's script-opts. On 0.41: raise `--sub-margin-y` permanently (measure in the harness). With native controls: the UI sends the bar height to the backend, which sets the subtitle margin offset (§4.6). | `backend\jellyfin_mpv_shim\mpv_options.py:216-246`, `bridge.py:285-320` | H | S |
| V2 | **Title "It's Always Sunny in Philadelphia - s6e04 - It's Always Sunny in Philadelphia".** The template prints the episode name even when it equals the series name; lowercase "s6e04". | jellyfin-web "Series - S6:E4 - Title" [2]. | In bridge mode build "Series · S6:E4 · Episode". Drop the episode part when it is empty or equals the series name (case-insensitive). Movies "Name (Year)". Set osc `title=${media-title}` (drops the `[n/m]` playlist prefix). Check the episode's real name on the server. | `backend\jellyfin_mpv_shim\media.py:545-569` (mark as a fluentfin-mpv diff) | H | S |
| V3 | **Audio/subtitle shown as bare "1/2" and "3/3".** The stock OSC can only show counts. | Language/`DisplayTitle` labels [1][3][11]. | Short term: left-click opens mpv's track menu instead of cycling (ModernZ does this [24]). Real fix: native pickers with `DisplayTitle` (§4). | as V1 | H | S / L |
| V4 | **"Cache: 1m40s" in the bar.** The stock bottombar always shows it; no option hides it [20]. | Stats live behind a "Playback info" panel [72]. | Short term: a layout without the cache element (agent reading of osc.lua source: box, slimbox, bottombar and topbar show it; slim and floating do not — unverified) or accept until native. Native: "Playback info" panel (play method, codec, bitrate, buffer, dropped frames). | as V1 | M | S |
| V5 | **Plain white seek bar, mpv font, mpv icons.** | Fluent type and icons [40]; accent colour. | mpv 0.42: `osc-icon_style=fluent`, OSC colour options, `--osd-font="Segoe UI"`. Whether Segoe UI Variable renders through libass is unverified; use Segoe UI. Native controls supersede this. | as V1 | M | S |
| V6 | **No Skip Intro / Skip Credits at all** under the stock OSC (`player.py:2727-2771`; `skip_intro_on_seek` off by default). | Every reference client [4][8][10][15][17]. | Now: OSD text "Intro · press S to skip" plus an `S` binding to skip the segment (Netflix [10]). Bridge event `segment` for the UI. Native: a Skip button bottom-right, hidden after 8 s and shown with the controls [4]. | `backend\jellyfin_mpv_shim\player.py:2700-2775`, `docs\BRIDGE.md` §4–5 | H | S |
| V7 | **No Up Next / countdown.** The queue auto-advances at end of file; after the last item the page returns. No "still watching". | jellyfin-web Up Next 30–40 s before the end [1]; Plex countdown [8]; Netflix still-watching [74]; WCAG 2.2.2 (a way to stop auto-advance) [56]. | Native: an Up Next card at the Outro segment or the last 30–40 s with **Play now** / **Cancel**, honouring the server's `EnableNextEpisodeAutoPlay` [71]. | new UI + bridge `ended`/`item` | M | M |
| V8 | **Keys differ from web conventions.** In mpv/the shim: ↑/↓ seek 60 s (web: volume); `k` kills shaders (YouTube/Jellyfin: pause); `c` opens the shim's text menu (YouTube: captions); `j` cycles subtitles (web: back 10 s); `s` is a screenshot (Netflix: skip intro). Esc in windowed mode **leaves playback** (`VideoPlayerPage.xaml.cs:151-168`) when XAML has focus. | [1][10][12] | Ship a bridge `input.conf`: Space/k pause; ←/→ ±10 s; j/l ±10 s; ↑/↓ volume ±5; m mute; f / double-click full screen; c subtitles on/off; s skip segment; Shift+N/P next/previous; 0–9 percent; `?` key list. Esc only exits full screen; Back / Alt+← leaves. | `backend\jellyfin_mpv_shim\conf.py:492-511` (bridge overrides), `VideoPlayerPage.xaml.cs` | M | S |
| V9 | **Windowed playback keeps the nav pane and the title bar search** next to the video; the video area is smaller than it needs to be. | Microsoft: encourage full window [47]; Netflix/Plex desktop take the whole window. | On the player page hide the pane (`IsPaneVisible=false`) and the search box even when windowed. Keep the title bar's back and caption buttons. | `Views\VideoPlayerPage.xaml.cs:130-149`, `TitleBarControl.xaml` | M | S |
| V10 | **Loading screen** is grey body text on black ("Loading the queue… / Starting mpv… / Connecting…"). No art, no title, no way out but Back in the title bar. Not announced to Narrator. | NN/g: progress indicator 2–10 s [34]; 4.1.3 status messages [56]. | Show backdrop/logo, title and S/E, a ProgressRing, the step text with `LiveSetting=Polite`, and a **Cancel**. Plain wording ("Starting the player…"). | `Views\VideoPlayerPage.xaml:44-54`, `ViewModels\VideoPlayerViewModel.Mpv.cs:166-172` | M | S |
| V11 | Messages: the InfoBar row pushes the video down when it opens, which resizes mpv. "MPV:" prefixes are jargon. | Avoid layout shift; plain language. | Reserve the row, or overlay the message in the title bar area. Drop "MPV:". | `VideoPlayerPage.xaml:36-42`, `VideoPlayerViewModel.Mpv.cs:247-252, 444-449` | L | S |
| V12 | Once playing, **keyboard focus goes into mpv's window** (`FocusVideo`, model A in `winui-mpv-embed.md` §3). WinUI accelerators stop working, Tab cannot leave the video, and Narrator sees an unnamed window. The OSC is pixels with no UI Automation. | 2.1.1, 4.1.2 [56]; Section 508 503.4 [58]. | Solved by native controls with FluentFin owning input (§4). Until then, give the host window an accessible name ("Video") and document the keys. | `VideoPlayerViewModel.Mpv.cs:280-285`, `ui\FluentFin\Mpv\MpvVideoHost.cs:196-205, 423-450` | H | L |
| V13 | A track change that fails shows nothing (KNOWN-ISSUES). | 3.3.1 error identification [56]. | Show a non-fatal error as a transient InfoBar ("Couldn't switch to English (5.1). Still playing Japanese."). | `VideoPlayerViewModel.Mpv.cs:247-252` | M | S |

### 3.8 Settings

| # | What the user sees | Standard | Change | Files | I | E |
|---|---|---|---|---|---|---|
| T1 | **No playback preferences**: preferred audio/subtitle language, subtitle mode, skip intro/credits behaviour, auto-play next, max bitrate, subtitle size/position. With mpv they are buried in the backend's `conf.json` and the server's user config. | Jellyfin user config [71]; segment actions per client [5]; Plex Player Experience [8]. | A **Playback** group. Server-side fields via `/Users/Configuration` (language, subtitle mode, auto-play next). Backend fields (`segment_intro`/`segment_outro`: ask/always/off, subtitle scale) via a new bridge `configure` command. | `Views\SettingsPage.xaml`, `SettingsViewModel`, `docs\BRIDGE.md` | M | M |
| T2 | A developer field ("MPV backend command … python.exe -I -m …") and a backend status block sit beside the player choice. | Progressive disclosure. | Move them into a collapsed "Advanced" SettingsExpander. | `Views\SettingsPage.xaml:69-86` | L | S |
| T3 | **Delete Server / Delete User are accent (primary) buttons** with no confirmation visible in XAML. The page is fixed `Width="1000"`. | Destructive actions not primary; confirm or allow undo; adaptive widths [54]. | Standard buttons, confirm in a ContentDialog, `MaxWidth=1000` instead of `Width`. | `Views\SettingsPage.xaml:22, 43-55` | M | S |

### 3.9 Dashboard (admin)

| # | What the user sees | Standard | Change | Files | I | E |
|---|---|---|---|---|---|---|
| D1 | Visually sound (SettingsCard pattern, Save/Reset; `ui\Screenshots\dashboard.png`). Loads 6 independent requests one after another: 365 ms at 50 ms per request (perf-ui O1). | Responsive 500 ms / 1 s [35]. | `Task.WhenAll`. | `ui\FluentFin.Core\ViewModels\DashboardViewModel.cs:51-77` | L | S |

### 3.10 Global navigation and title bar

| # | What the user sees | Standard | Change | Files | I | E |
|---|---|---|---|---|---|---|
| N1 | The rail has only Home and Libraries; individual libraries are one click deeper, though sign-in already fetches them (for the jump list). | NavigationView Left for 5–10 destinations [44]; jellyfin-web/Plex list libraries in the sidebar. | List the user's libraries as nav items under Home. | `Views\ShellPage.xaml:26-36` | M | S |
| N2 | **"Contribute"** in the rail opens upstream FluentFin's GitHub. This local fork's issues are not there. | Accuracy. | Remove it, or move it to an About section with both upstreams credited. | `Views\ShellPage.xaml:37-44`, `ShellPage.xaml.cs:66-69` | L | S |
| N3 | Account button and PersonPicture are named only by tooltip. Title reads "FluentFin" plus a version subtitle. | 4.1.2 [56]; title bar guidance [45]. | `AutomationProperties.Name="Account: {user}"`. Title "FluentFin MPV" if the fork should be distinguishable. | `Views\TitleBarControl.xaml` | L | S |
| N4 | Back works from the title bar, Alt+← and the GoBack key (`ShellPage.xaml.cs:36-40`). The mouse back button is not handled. This is a common desktop convention but not a cited standard. | Convention. | Handle `PointerPressed` with `IsXButton1Pressed` on the shell. | `Views\ShellPage.xaml.cs` | L | S |

### 3.11 Errors and empty states (cross-cutting)

- **Silent failures.** Most `JellyfinClient` methods log and return null. Pages then show
  blank areas: Home (H6), search (S1), and library filters. Standard: NN/g empty states and Microsoft's
  "no empty pages" [34][39].
  - Change: a shared pattern with an InfoBar ("Couldn't load … from *server*", Retry) for failures,
    and a sentence plus an action for real emptiness.
  - Impact M, effort M (page by page).
- **Modal errors.** Login and server errors use ContentDialogs. Fluent uses InfoBar for in-app status
  and ContentDialog for blocking decisions. Low.
- **Sign-out.** Up to 2 s frozen on the player page with no indication, and Log out + Switch user
  can run twice (KNOWN-ISSUES).
  - Change: disable the account menu and show "Signing out…" with a ProgressRing.
  - Covered by the sign-out/error UX stream.

### 3.12 Accessibility (cross-cutting)

| # | What the user sees (or cannot do) | Criterion | Change | Files | I | E |
|---|---|---|---|---|---|---|
| A1 | **Cards cannot be reached or opened from the keyboard.** The card opens on `Tapped` of a Grid. Play, Watched, Favourite and More are `Collapsed` until `PointerEntered`, so they are not in the tab order either. Applies to Home, Library, item rows and search results. | 2.1.1 (A), 4.1.2 (A) [56]; "wrap clickable images in a Button" [48]. | Make the card's image area a `Button` (or `ItemContainer` with `IsItemInvokedEnabled`), named "*Title*, *subtitle*, *n% watched*". Show the action buttons on keyboard focus as well as hover (`GotFocus`/`LostFocus`). Arrow-key navigation within rows (XYFocus / ItemsView). | `ui\FluentFin\Controls\BaseItemDtoCard.xaml:38-100, 241-300`, `BaseItemDtoCard.xaml.cs:33-48` | H | M |
| A2 | No headings anywhere (0 `HeadingLevel`), so Narrator's heading navigation finds nothing. | 1.3.1 (A) [56]; headings guidance [51]. | Page titles Level1; section titles ("Continue Watching", "Seasons", "Cast & crew") Level2. | all `Views\*.xaml` | M | S |
| A3 | 21 `IsTabStop="False"` controls, mostly FluentFin's own player TransportControls. They matter the moment those controls are reused for mpv. | 2.1.1 [56]. | Remove; give each an accelerator and tooltip with the shortcut. | `ui\FluentFin\Controls\TransportControls.xaml` | M (H once reused) | S |
| A4 | Status changes ("Starting mpv…", playback errors, "Skipped intro") are not announced. | 4.1.3 (AA) [56]; LiveSetting [52]. | `LiveSetting=Polite` on the status text and InfoBar message, plus `RaiseNotificationEvent` for transient events. | `VideoPlayerPage.xaml`, `VideoPlayerViewModel.Mpv.cs` | M | S |
| A5 | Hard-coded colours (yellow star, red favourite, `#101010`/`#202020` scrims, white skip button); `HighContrastAdjustment="None"` on the NavigationView. | Contrast themes [53]; 1.4.11 [56]. | HighContrast theme dictionary; test with "Aquatic" and "Desert". | `Styles\Button.xaml:22-30`, `MoviePage.xaml`, `ShellPage.xaml:20` | M | S |
| A6 | Fixed widths (see I4, T3, S3) break at 150–225% text size. Card titles are single-line with ellipsis, and ellipsised text has no tooltip. | Text scaling [54]. | Auto/\* sizing; `IsTextTrimmedChanged` → tooltip. | as listed | M | M |
| A7 | Motion: cards scale 0.9→1.0 with a blur on hover; FluentFin's player bar fades over **1 s**. The Windows "Animation effects" setting is not checked. | Fluent 83/167/250 ms [46]. | 167 ms fades; honour `UISettings.AnimationsEnabled`. | `BaseItemDtoCard.xaml:50-65, 100-130`, `TransportControls.xaml:173-181` | L | S |

---

## 4. Player controls: native Fluent controls vs mpv's OSC

### 4.1 Constraints that decide the design

1. **Airspace.** mpv draws in a Win32 child of the main window. A child HWND sits above all WinUI 3
   XAML in that window, and there is no `HwndHost` equivalent. #4670 was closed as not planned and
   the #10050 proposal is open with no commitment [61]. The repo's own research reached the same
   conclusion (`winui-mpv-embed.md` §1).
2. **The host hides the video for any open popup** except tooltips
   (`ui\FluentFin\Mpv\MpvVideoHost.cs:255-266, 338-350`). Today a native flyout over the player
   blanks the video. Windowed popups (`ShouldConstrainToRootBounds=false`, available since App SDK
   1.4 [60]) are drawn outside the XAML root's bounds; `winui-mpv-embed.md` §1 records that they
   appear above the child. This is the opening that option A1 below uses.
3. **Input today is "model A".** mpv's window is enabled on the first mouse move and given the
   keyboard on click (`MpvVideoHost.cs:423-450`), so the OSC works but FluentFin loses keyboard and
   pointer input to mpv. Native controls want **model B**: leave mpv's window disabled, so Windows
   routes its mouse input to FluentFin's host window, and keep keyboard focus in XAML. Both models
   are described in `winui-mpv-embed.md` §3.
4. **The bridge already carries most player state** (`docs\BRIDGE.md` §5):
   - It has: state, position (about every 500 ms), item (series, season and episode numbers), tracks
     (language, title, codec, channels, default, forced, external), and errors.
   - It is missing: segments (skip intro), chapters, track `DisplayTitle` and hearing-impaired flag,
     buffer/stats, and a way to turn mpv's OSC off or move subtitles.

### 4.2 What FluentFin's own (Flyleaf/VLC/WMP) controls already offer

Those players render into SwapChainPanel-type surfaces, so `TransportControls` is plain XAML on top
(`ui\FluentFin\Controls\MediaPlayerHost.xaml`, `TransportControls.xaml(.cs)`).

**Reusable as design and code:**
- Seek `Slider` with elapsed and remaining time.
- Trickplay preview from Jellyfin tiles (`TrickplayViewModel`, `TransportControls.xaml.cs:171-200`).
- The Skip button (`IsSkipButtonVisible`/`SkipCommand`).
- Logo, episode line (`BaseItemDtoConverters.ToSessionInfoItemName`) and clock.
- Transport CommandBar: back 10 s, previous, play/pause, next, forward 30 s.
- Right CommandBar: playlist flyout with episode thumbnails, full screen, volume flyout, cast,
  subtitles flyout listing `DisplayTitle` (`Converters.GetSubtitlesFlyout`), audio flyout.
- Dark scrim brush.
- Auto-hide after 3 s idle, with the cursor hidden (`MediaPlayerHost.xaml.cs:89-99`).

**Not reusable as is:**
- It is bound to `IMediaPlayerController`, which is URL- and player-shaped (`Play(Uri)`,
  `OpenExternalSubtitleTrack(url)`), and its XAML converter uses Flyleaf's `Status` type. mpv tracks
  are chosen by Jellyfin index over the bridge.
- The trickplay preview is a `TeachingTip`, a popup that would blank the mpv video (constraint 2).
- `OutlineTextControl` subtitles are not needed; mpv renders subtitles.

**Gaps to fix when reusing:**
- Every button has `IsTabStop="False"`.
- No keyboard shortcuts.
- The transport bar is laid out right-to-left (`FlowDirection="RightToLeft"`).
- The volume update is throttled by **200 s** (`TransportControls.xaml.cs:87`, a typo for ms).
- No chapters, "ends at" or Up Next.
- 1 s fades.

**Verdict:** reuse the layout, styles, `TrickplayViewModel`, the playlist template and the skip
button. Write a new `MpvTransportControls` bound to an `MpvPlayerViewModel` fed by bridge events,
instead of forcing mpv behind `IMediaPlayerController`.

### 4.3 What the backend already has

- **Stock OSC (`osc_style "mpv"`)** on mpv 0.41, with trickplay through the OSC Preview API and the
  shim's thumbfast (`player.py:1582-1617`).
- **The shim's `mpvtk` HUD**, disabled in bridge mode.
  - It is "jellyfin-web styled" and keyboard/remote navigable (`mpvtk_browser\hud.py`, 1,221 lines).
  - Features: named track pickers, chapters, quality, Skip Intro/Credits button, "Ends at",
    favourite, mute, settings menu (speed, aspect, subtitle size/position/colour), a Playback Info
    dialog, and auto-hide modes (hover/always/paused, `hud_hide_secs` 4).
  - It has UI and text-scale floors for low vision (`conf.py:678-720`).
  - It is "rendered by the library browser", so it falls back to the stock OSC when `enable_gui` is
    off (`mpv_options.py:208-212`). `docs\BRIDGE.md` §4 forbids starting the in-player browser.

### 4.4 Options

| Option | How | Pros | Cons / risks | Accessibility | Effort |
|---|---|---|---|---|---|
| **A1. Windowed XAML `Popup` over the video** (recommended next step) | A `Popup` with `ShouldConstrainToRootBounds=false`, `IsLightDismissEnabled=false`, holding a Fluent control bar (gradient scrim, not acrylic) sized to the video rect. Moved by the same sync that places the mpv host (`MpvVideoHost.Update`), plus window-move events. Track/settings flyouts also windowed. `MpvVideoHost` ignores windowed popups when deciding to hide. | Real XAML in the same tree: bindings, focus, UI Automation, contrast themes, text scaling, Fluent styles. Same component in windowed and full screen. No new top-level window code. The OS owns z-order and lifetime (owned by the main window). | **Unverified:** per-pixel transparency of a windowed popup over a foreign child HWND; whether it follows window moves on its own; activation/focus behaviour; effect on mpv's independent flip / MPO while shown (likely composed while the bar is up, restored when hidden [63]). Subtitles still sit under the bar, so the UI must raise them through the bridge. | Full (UIA, keyboard, HC), if focus never hides behind it (2.4.11) | M–L after a 1–2 day spike |
| A2. Separate transparent top-level window (`AppWindow` owned by the main window, or `WS_EX_LAYERED`) | Second WinUI window or raw layered window synced to the video rect. | Works on any HWND content; precedent: VLC 3's full-screen controller is its own window with opacity (inference) [69]. | WinUI 3 has no supported transparent window background (#2956 [61]). Workarounds (WinUIEx `TransparentTintBackdrop` [62]) report light-theme white backgrounds and border/shadow issues [66]. Separate XAML root: focus hand-off, Alt-Tab entry, DPI per monitor, all hand-managed. | Full but harder (two roots) | L |
| B. Reserved strip below the video (windowed only) | A Grid row under `MpvVideoArea` with native controls; video never covered. | Zero airspace risk; subtitles never overlap; trivial to build from FluentFin's TransportControls. Typical of desktop players' windowed mode (VLC; mpv's own `boxvideo` does the same) [20]. | Not how streaming apps look (Netflix/Plex overlay). Full screen still needs A1, D or a strip that resizes the video on every show/hide (cross-process resize jank, `winui-mpv-embed.md` §4). | Full in windowed; depends on full-screen choice | M |
| C. libmpv in the UI process → `SwapChainPanel` | mpv 0.42 D3D11 render API (mpv renders into the app's texture [23][68]) or 0.41 composition mode (`d3d11-output-mode=composition` + `display-swapchain` → `ISwapChainPanelNative.SetSwapChain`, `winui-mpv-embed.md` §5). Python backend becomes an IPC client of that libmpv, or the UI drives mpv itself. | Ends airspace for good: any XAML (flyouts, ContentDialogs, InfoBars) can sit over the video. Removes the cross-process input-queue hang class and the watchdog. mpv upstream recommends the render API over embedding [23]. | Big architecture change: libmpv not installed (`KNOWN-ISSUES`). mpv moves into FluentFin's process, so an mpv crash takes the app down. `SwapChainPanel` has no transparency and no acrylic sampling over it [59]. Composition mode reports colour space "unknown" (`winui-mpv-embed.md` §5). HDR and independent-flip behaviour to re-verify. The bridge contract (`window: embedded`) changes. | Full | L–XL |
| D. Style mpv's OSC (stock `osc.lua` options, or uosc / ModernZ) | Bridge appends `osc-*` script-opts: 0.42 `icon_style=fluent`, colours, `sub_margins`/`dynamic_margins`, `title`, `--osd-font`. Or `osc_style "custom"` with uosc [24]. | Hours of work. Fixes most of the user's visible complaints (subtitles, title, font/icons). uosc adds track menus with names, conditional buttons, buffered-time only when low. | Still mpv pixels: no UI Automation, no Tab focus, no contrast themes, no Windows text scaling. Track counters and cache readout cannot be removed from the stock bottombar [20]. Third-party scripts need checking against the shim's thumbfast and bridge. | None | S (stock) / S–M (uosc) |
| E. Enable the shim's mpvtk HUD in bridge mode | Run the HUD without showing the library browser (a bridge-only mode of `mpvtk_browser`). | Already built and tested: named tracks, skip button, chapters, ends-at, settings, playback info, low-vision text floors, remote/keyboard navigation inside mpv. | Coupled to the browser object (`build_hud(b, size)`); BRIDGE.md forbids the browser today. Jellyfin-web look, not Fluent. Throwaway once A1 or C lands. | Keyboard inside mpv only; no UIA | M (half-day check first) |

### 4.5 Recommendation

1. **Phase 0, now (S). Make the OSC tolerable (D, stock OSC).**
   - Move to **mpv 0.42**: it has the D3D11 render API, the `floating` layout and Fluent icons
     [22][20].
   - Add `osc-sub_margins=yes`, `osc-dynamic_margins=yes`, `osc-icon_style=fluent`, accent/neutral
     colours, `osc-title=${media-title}`, `osc-hidetimeout=3000` and `--osd-font="Segoe UI"`.
   - Fix the backend title (V2), the key map (V8) and the skip prompt with an `S` key (V6).
   - Test a layout without the cache readout (V4).
   - Spend half a day checking whether option E can run HUD-only. If it can, it is the better interim
     because it fixes track names and skip buttons at once.
2. **Phase 1, next (spike, then M–L). Native Fluent controls through A1.**
   - Spike go/no-go checks (1–2 days, in the off-screen harness and on the real desktop):
     1. A windowed `Popup` draws above the mpv child with its gradient alpha intact.
     2. It stays glued to the video through window move, resize, maximise, monitor/DPI change and
        full screen.
     3. It never takes activation and never shows in Alt-Tab.
     4. Narrator reads and Tab reaches every control.
     5. PresentMon shows no dropped frames while it is shown, and independent flip returns after
        auto-hide.
     6. It still behaves when mpv hangs (watchdog path).
   - **If 1–3 fail:** use **B** in windowed mode, and **D** (or E) in full screen only.
3. **Phase 2, later (L–XL). Option C**, when moving mpv in-process is acceptable. It removes
   airspace, the popup-hiding rule and the input-queue watchdog in one go. Plan it with the libmpv
   0.42 D3D11 render API [23][68]. Keep A1's control bar: it becomes ordinary XAML over the
   `SwapChainPanel`, so the work carries over.

### 4.6 Spec for the native control bar (phase 1)

**Layout** (Fluent tokens, dark theme, 8 epx grid). It follows Jellyfin web [1] and Microsoft's
double-row guidance [47].

- **Top band**, with a gradient scrim:
  - Back (←) on the left.
  - Title block: series or movie name in Subtitle 20 semibold, and "S6:E4 · Episode title" in
    Body 14 below it.
  - On the right: Playback info (i).
- **Bottom band:**
  - A full-width seek bar: accent fill, a lighter buffered range, chapter ticks, and a hover bubble
    with the trickplay tile, time and chapter name. Reuse `TrickplayViewModel`; the bubble is drawn
    inside the popup, not as a TeachingTip.
  - Elapsed time on the left. Remaining time or "Ends at 10:42 PM" on the right; clicking toggles.
  - Controls row, left side: play/pause; back 10; forward 30 (Jellyfin web defaults [26]);
    previous/next episode when there is a queue; mute plus volume slider.
  - Controls row, right side:
    - **Audio** and **Subtitles** menus, "Off" first for subtitles, labelled with the server's
      `DisplayTitle` [3] (e.g. "English · Dolby Digital 5.1 · Default", "English · Forced",
      "English · SDH").
    - Settings: speed, quality, subtitle size and position.
    - Full screen.
- **Skip Intro / Skip Credits** button bottom-right above the bar. It hides after 8 s unless the
  controls are shown; the key is S [4][10].
- **Up Next** card in the last 30–40 s or at the Outro, with **Play now** and **Cancel**, honouring
  the server's auto-play setting [1][71].

**Behaviour:**
- Auto-hide after 3 s without input [1][13]. Stay visible while paused, while a menu is open, while
  the pointer is over the bar, and while keyboard focus is inside it (2.4.11 [56]). Hide the cursor
  with it. Fade 167 ms [46].
- Pointer (model B: FluentFin's host window receives it): single click toggles pause after 300 ms;
  double-click toggles full screen; the wheel changes volume [1].
- Keyboard map as in V8, as `KeyboardAccelerator`s on the page, with shortcuts shown in tooltips.
- Subtitles move up while the bar shows (Emby/Chrome precedent [16][19]). The UI sends the bar
  height; the backend sets `sub-margin-y` (0.41) or the 0.42 margin offset [21].
- Smooth seek bar: interpolate between the 500 ms `position` events while playing.

**Bridge additions.** Additive, so protocol v1 can take them, as `reported` was added:

| Kind | Name | Purpose |
|---|---|---|
| Track fields | `displayTitle`, `isHearingImpaired` | Picker labels |
| Event | `segment` `{type, startTicks, endTicks, action}` | Skip button |
| Event | `chapters` `[{startTicks, name}]` | Ticks and chapter names |
| Command | `skip_segment` | Skip the current segment |
| Command | `set_osc` `{visible: false}` | Turn mpv's OSC off while native controls are on |
| Command | `set_subtitle_margin` `{px}` | Raise subtitles while the bar shows |
| Command | `set_speed` | Speed setting |
| Command | `get_stats` | Playback info panel: play method, codec, bitrate, buffer, dropped frames |

**Files:**
- New `ui\FluentFin\Controls\MpvTransportControls.xaml`.
- `ui\FluentFin\ViewModels\VideoPlayerViewModel.Mpv.cs`.
- `ui\FluentFin\Mpv\MpvVideoHost.cs`: windowed-popup exception; input model B.
- `ui\FluentFin.Core\Mpv\BridgeMessages.cs` and `BridgeCommands.cs`.
- `docs\BRIDGE.md` first, then `backend\jellyfin_mpv_shim\bridge.py` and `bridge_state.py`.

---

## 5. Ranked top 15

**Score** = impact (H=3, M=2, L=1) × (4 − effort, where S=1, M=2, L=3). Ties are ordered by how
directly they answer the user's feedback.

**Streams** column:
- **OSC** = mpv OSC polish
- **Native** = native player controls
- **Home** = home page load performance
- **Start** = startup performance
- **Harness** = dev harness
- **Errors** = sign-out/error UX
- **New** = not covered by any running stream

| Rank | Change | I | E | Score | Stream |
|---|---|---|---|---|---|
| 1 | **mpv OSC polish on mpv 0.42:** subtitles lifted while the OSC shows, Fluent icons and colours, Segoe UI, 3 s hide, title template fixed in the backend (V1, V2, V5; V4 by layout test) | H | S | 9 | OSC |
| 2 | **Skip Intro/Credits that actually appears:** OSD prompt + `S` key now, and a `segment` bridge event for the native button (V6) | H | S | 9 | New (OSC stream can take the interim part) |
| 3 | **Next Up cutoff:** `now − 365 days` like jellyfin-web; verify on the real server first (H4) | H* | S | 9 | New (*impact if confirmed) |
| 4 | **Parallel "Recently added" requests + cached `UserViews`** (H2) | H | S | 9 | Home |
| 5 | **Labelled primary Play/Resume + names on all icon buttons** (I2, I3, N3) | H | S | 9 | New |
| 6 | **Progressive Home:** per-section skeletons, Continue Watching first; then a persisted Home snapshot shown instantly and refreshed in the background, and page caching on Back (H1, H3, P3) | H | M | 6 | Home |
| 7 | **Window before sign-in** with a "Signing in…" state; lazy Flyleaf; backend pre-warm after sign-in (F1, P1, P2) | H | M | 6 | Start |
| 8 | **Keyboard and Narrator access to cards and rows:** card as a Button with a name, actions visible on focus, row arrows everywhere (A1, H5) | H | M | 6 | New |
| 9 | **Web-convention key map** for the player; Esc only exits full screen (V8) | M | S | 6 | OSC (interim) / Native |
| 10 | **Item page readability:** wrap the overview, wrap tags (I1, I4 first half) | M | S | 6 | New |
| 11 | **Player chrome and loading:** hide the nav pane and search on the player page, loading screen with art + title + Cancel + live status, no layout shift (V9, V10, V11) | M | S | 6 | New |
| 12 | **Errors and empty states:** Home/search/library InfoBar with Retry; server-page progress and a 10 s timeout; sign-out feedback; failed track change shown (H6, F2, S1 "no results", V13, §3.11) | M | S–M | 6–4 | Errors (sign-out, track change); rest New |
| 13 | **Settings → Playback:** languages, subtitle mode, skip behaviour, auto-play; Advanced section; safe delete buttons (T1–T3) | M | M | 4 | New |
| 14 | **Dev harness for UX work:** data-folder override, rich fake server (series with segments, chapters, trickplay, multi-audio with forced/SDH), off-screen screenshots per page, automated UIA checks (names, tab order, headings) and text-scale 200% shots | M | M | 4 | Harness (UIA checks New) |
| 15 | **Native Fluent player controls** (A1 after the spike; fallback B windowed + D full screen): §4.5–4.6, which also closes V3, V7, V12 and A3 | H | L | 3 | Native |

Notes:
- Rank 15 is the largest single UX gain. It scores low only because of its size. Run its 1–2 day
  spike in parallel with ranks 1–5, so phase 0 is not polish on a dead end for longer than needed.
- Search results page (S1), library A–Z and count (L2), responsive layouts and text scaling (I4,
  A6), headings (A2), contrast themes (A5) and Up Next (V7) are the next tier (score 4).

---

## 6. Verification notes

**Not verified here** (needs the real server, a spike or a measurement):
- Why the episode name equals the series name. Check the item's `Name` on the server.
- H4's effect on the real server.
- Windowed-popup transparency, move-tracking and MPO behaviour (A1).
- Whether Segoe UI Variable renders through libass.
- Which stock OSC layouts omit the cache readout (taken from a research agent's reading of
  `osc.lua`).
- mpv 0.42 availability on winget.
- Idle memory of a pre-warmed backend.
- Plex official pages: support.plex.tv refused fetching, so those facts are search snippets or
  forums.

**Jellyfin versioning.** Jellyfin's numbering changed: 12.0 (7 Sep 2026) followed 10.11, and the
current stable API reports 12.4.0 [7]. jellyfin-web behaviour above is from 10.10/10.11 sources
unless noted.

**Repo research already covering parts of this:**
- `docs\research\perf-ui.md`: request counts and timings, O1–O14.
- `docs\research\winui-mpv-embed.md`: airspace, input models, libmpv routes.
- `docs\research\perf-backend.md`: backend start costs.
- `docs\KNOWN-ISSUES.md`: sign-out feedback, silent track-change failure.

---

## 7. Sources

1. jellyfin-web 10.10.7 video player controller — https://github.com/jellyfin/jellyfin-web/blob/v10.10.7/src/controllers/playback/video/index.js
2. jellyfin-web itemHelper (display names) — https://github.com/jellyfin/jellyfin-web/blob/v10.10.7/src/components/itemHelper.js
3. Jellyfin server MediaStream.DisplayTitle — https://github.com/jellyfin/jellyfin/blob/master/MediaBrowser.Model/Entities/MediaStream.cs
4. jellyfin-web skip segment button — https://github.com/jellyfin/jellyfin-web/blob/v10.10.7/src/components/playback/skipsegment.ts ; 10.10.1 notes https://newreleases.io/project/github/jellyfin/jellyfin-web/release/v10.10.1
5. Jellyfin media segments — https://jellyfin.org/docs/general/server/metadata/media-segments
6. Jellyfin 10.9 release (trickplay, secondary subtitles) — https://jellyfin.org/posts/jellyfin-release-10.9.0/
7. Jellyfin 12.0 release — https://jellyfin.org/posts/jellyfin-release-12.0/ ; API spec https://api.jellyfin.org/openapi/jellyfin-openapi-stable.json
8. Plex "Player Experience" settings (forum announcement) — https://forums.plex.tv/t/player-experience/857990
9. Plex video preview thumbnails (search snippet only) — https://support.plex.tv/articles/202197528-video-preview-thumbnails/
10. Netflix keyboard shortcuts — https://help.netflix.com/en/node/24855
11. Netflix audio and subtitles — https://help.netflix.com/en/node/372
12. YouTube keyboard shortcuts — https://support.google.com/youtube/answer/7631406
13. Stremio shortcuts and subtitle menu — https://github.com/Stremio/stremio-web/blob/development/src/common/Shortcuts/shortcuts.json ; https://github.com/Stremio/stremio-web/blob/development/src/routes/Player/SubtitlesMenu/SubtitlesMenu.js
14. Kodi Estuary OSD and keymap — https://github.com/xbmc/xbmc/blob/master/addons/skin.estuary/xml/VideoOSD.xml ; https://github.com/xbmc/xbmc/blob/master/addons/skin.estuary/xml/Home.xml ; https://github.com/xbmc/xbmc/blob/master/system/keymaps/keyboard.xml
15. Emby intro skip — https://emby.media/support/articles/Intro-Skip.html
16. Emby subtitles raised over the progress bar — https://emby.media/community/topic/126497-the-position-of-ass-subtitles-is-affected-by-the-progress-bar
17. Infuse 8.4 (intros, auto/manual skip) — https://firecore.com/blog/infuse-84-extras-intros-and-favorites ; Infuse shortcuts https://support.firecore.com/hc/articles/215090987
18. Apple TV app playback controls — https://support.apple.com/guide/tvapp/control-playback-atvb5e386e66/web
19. W3C text-tracks list, caption movement with controls — https://lists.w3.org/Archives/Public/public-texttracks/2022Mar/0000.html
20. mpv OSC manual v0.41.0 and v0.42.0 — https://github.com/mpv-player/mpv/blob/v0.41.0/DOCS/man/osc.rst ; https://github.com/mpv-player/mpv/blob/v0.42.0/DOCS/man/osc.rst
21. mpv options manual (sub-margin-y, sub-use-margins, osd-font, title) — https://github.com/mpv-player/mpv/blob/master/DOCS/man/options.rst
22. mpv v0.42.0 release (D3D11 render API, floating layout, Fluent icons) — https://github.com/mpv-player/mpv/releases/tag/v0.42.0
23. libmpv render API headers — https://github.com/mpv-player/mpv/blob/master/include/mpv/render.h ; https://github.com/mpv-player/mpv/blob/master/include/mpv/render_d3d11.h
24. uosc / ModernZ / thumbfast — https://github.com/tomasklaen/uosc ; https://github.com/Samillion/ModernZ ; https://github.com/po5/thumbfast
25. jellyfin-mpv-shim configuration and player — https://github.com/jellyfin/jellyfin-mpv-shim/blob/master/docs/configuration.md ; https://github.com/jellyfin/jellyfin-mpv-shim/blob/master/jellyfin_mpv_shim/media.py
26. jellyfin-web home sections — https://raw.githubusercontent.com/jellyfin/jellyfin-web/master/src/components/homesections/homesections.js ; .../sections/resume.ts ; .../sections/nextUp.ts ; .../sections/recentlyAdded.ts ; user settings https://raw.githubusercontent.com/jellyfin/jellyfin-web/master/src/scripts/settings/userSettings.js ; 10.11 notes https://newreleases.io/project/github/jellyfin/jellyfin-web/release/v10.11.0
27. jellyfin-web search — https://raw.githubusercontent.com/jellyfin/jellyfin-web/v10.10.0/src/apps/stable/routes/search.tsx ; https://raw.githubusercontent.com/jellyfin/jellyfin-web/v10.10.0/src/hooks/searchHook/useSearchItems.ts
28. jellyfin-web login — https://raw.githubusercontent.com/jellyfin/jellyfin-web/v10.10.0/src/controllers/session/login/index.js
29. Jellyfin networking (discovery UDP 7359) and Quick Connect — https://jellyfin.org/docs/general/post-install/networking/ ; https://jellyfin.org/docs/general/server/quick-connect
30. Emby home screen preferences — https://emby.media/support/articles/apps/common/User-Prefs-HomeScreen.html
31. Netflix TechBlog on rows (mirror) — https://gwern.net/doc/www/netflixtechblog.com/2f5717e8bbdc98d2caf5c7a972359fbb7e7c0b28.html
32. TanStack Query defaults; jellyfin-web latest media — https://tanstack.com/query/latest/docs/framework/react/guides/important-defaults ; https://raw.githubusercontent.com/jellyfin/jellyfin-web/master/src/apps/legacy/features/libraries/api/useLatestMedia.ts
33. web.dev RAIL; stale-while-revalidate — https://web.dev/articles/rail ; https://web.dev/articles/stale-while-revalidate
34. NN/g response times, skeleton screens, empty states — https://www.nngroup.com/articles/response-times-3-important-limits/ ; https://www.nngroup.com/articles/skeleton-screens/ ; https://www.nngroup.com/articles/empty-state-interface-design/
35. Microsoft, Plan and measure app performance — https://learn.microsoft.com/en-us/windows/apps/develop/performance/planning-measuring-performance
36. Microsoft, App startup performance — https://learn.microsoft.com/en-us/windows/apps/develop/performance/app-startup-performance
37. Microsoft, Optimize ListView and GridView; data virtualization — https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-gridview-and-listview ; https://learn.microsoft.com/en-us/windows/apps/develop/performance/listview-and-gridview-data-optimization
38. Microsoft, Keep the UI thread responsive — https://learn.microsoft.com/en-us/windows/apps/develop/performance/keep-ui-thread-responsive
39. Microsoft, archived app test cases — https://learn.microsoft.com/en-us/previous-versions/windows/apps/dn275879(v=win.10)
40. Fluent typography — https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography
41. Content basics (spacing) — https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics
42. Geometry (corner radius) — https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry
43. Materials — https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/materials
44. NavigationView — https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview
45. Title bar design — https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design ; developer guide https://learn.microsoft.com/en-us/windows/apps/develop/title-bar
46. Timing and easing; connected animation — https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing ; https://learn.microsoft.com/en-us/windows/apps/develop/motion/connected-animation
47. Media playback guidance — https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/media-playback
48. Keyboard accessibility — https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility
49. Focus visuals — https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-visualfeedback
50. Basic accessibility information (names) — https://learn.microsoft.com/en-us/windows/apps/design/accessibility/basic-accessibility-information
51. Landmarks and headings — https://learn.microsoft.com/en-us/windows/apps/design/accessibility/landmarks-and-headings
52. AutomationProperties.LiveSetting — https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.automationproperties.livesettingproperty
53. Contrast themes — https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes
54. Text scaling — https://learn.microsoft.com/en-us/windows/apps/develop/input/text-scaling
55. Targeting — https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-targeting
56. WCAG 2.2 and Understanding documents — https://www.w3.org/TR/WCAG22/ ; https://www.w3.org/WAI/WCAG22/Understanding/focus-not-obscured-minimum.html ; https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html ; https://www.w3.org/WAI/WCAG22/Understanding/pause-stop-hide.html
57. WCAG2ICT — https://www.w3.org/TR/wcag2ict-22/
58. US Access Board ICT (Section 508) — https://www.access-board.gov/ict/
59. SwapChainPanel (no transparency in WinUI 3) — https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.swapchainpanel
60. Popup.ShouldConstrainToRootBounds — https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.primitives.popup.shouldconstraintorootbounds
61. microsoft-ui-xaml issues: transparency #2956, child HWND airspace #4670, hosting proposal #10050 — https://github.com/microsoft/microsoft-ui-xaml/issues/2956 ; https://github.com/microsoft/microsoft-ui-xaml/issues/4670 ; https://github.com/microsoft/microsoft-ui-xaml/issues/10050
62. WinUIEx custom backdrops — https://dotmorten.github.io/WinUIEx/concepts/CustomBackdrops.html
63. DXGI flip model and overlays — https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model
64. Win32 owned windows — https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features
65. (repo) winui-mpv-embed.md, perf-ui.md, perf-backend.md, KNOWN-ISSUES.md — `C:\Users\moise\Dev\fluentfin-mpv\docs\`
66. Microsoft Q&A, transparent WinUI 3 window — https://learn.microsoft.com/en-sg/answers/questions/1463839/how-to-make-winui-3-window-transparent
67. Richasy mpv-winui (libmpv → SwapChainPanel via OpenGL) — https://github.com/richasy/mpv-winui
68. mpv PRs: composition mode #16285, D3D11 render API #17764 — https://github.com/mpv-player/mpv/pull/16285 ; https://github.com/mpv-player/mpv/pull/17764
69. VLC 3 Qt (full-screen controller opacity) — https://raw.githubusercontent.com/videolan/vlc/3.0.x/modules/gui/qt/qt.cpp
70. Navigation history and back — https://learn.microsoft.com/en-us/windows/apps/develop/ui/navigation/navigation-history-and-backwards-navigation
71. Jellyfin UserConfiguration (TypeScript SDK) — https://typescript-sdk.jellyfin.org/interfaces/generated-client.UserConfiguration.html
72. jellyfin-web Playback Info — https://github.com/jellyfin/jellyfin-web/blob/v10.10.7/src/components/playerstats/playerstats.js
73. Microsoft, Optimize animations and media (image decode size) — https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-animations-and-media
74. Netflix "still watching" (How-To Geek) — https://www.howtogeek.com/685919/why-netflix-asks-are-you-still-watching-and-how-to-stop-it/
75. jellyfin-web item details — https://raw.githubusercontent.com/jellyfin/jellyfin-web/v10.10.0/src/controllers/itemDetails/index.js
76. Windows ClosedCaptionProperties (system caption style) — https://learn.microsoft.com/en-us/uwp/api/windows.media.closedcaptioning.closedcaptionproperties
77. (alias of 73) image decoding guidance — https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-animations-and-media
