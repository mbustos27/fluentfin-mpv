# What Nixie does

Everything Nixie adds to [FluentFin](https://github.com/insomniachi/FluentFin)'s interface and
[jellyfin-mpv-shim](https://github.com/jellyfin/jellyfin-mpv-shim)'s playback, moved here from the README. What
changed in each version is in [CHANGELOG.md](../CHANGELOG.md); every key is in [SHORTCUTS.md](SHORTCUTS.md).

- **Four layouts to choose from** (1.3.0), in Settings → Appearance, switched at once with your place kept:
  **Drive-in** (the default for new installs: a drive-in at dusk with nixie-tube numbers and marquee boards),
  **Cinematic** (the artwork first: a full-window hero, 16:9 stills, a floating glass bar), **Editorial** (a
  magazine for a big library: big type, more titles per screen, details that open in place) and **Classic** (the 1.2
  layout, where upgrades start). Each lays out Home, the series, season, film and episode pages, Library, the
  player's controls and the sign-in screen its own way.
- **The desert-sunset look.** Deep plum glass with a hot-pink accent (pale peach and crimson in the light theme) on
  the top bar, search, menus, tooltips, dialogs and page sections, over a dusk scene of a striped sun and desert
  hills; the app's name glows like a nixie tube on the sign-in card. Posters and artwork stay solid. Settings →
  Appearance → Glass: **Automatic** (follows Windows' transparency, animation and high-contrast settings), **Full**,
  **Reduced** (no motion) or **Solid** (no translucency). One set of buttons (six kinds, two sizes) everywhere. The
  design and the research behind it: [docs/design/DESIGN.md](design/DESIGN.md).
- **A top bar, no side rail.** Home, your libraries, Diary and the libraries you add are tabs in the window's one
  bar, with search and the Account menu (where Settings is). A narrow window folds the tabs into one menu.
- **Home, item pages and the player, redesigned.**
  - **Home:** a hero of up to five items new to you that never autoplays trailers, poster rows whose cards lift on
    hover, and a Continue watching row you can clean up (remove an item with Undo, or hide what you have not played
    for a month or more).
  - **Libraries:** one scrolling grid with Sort (it says its direction in words), Filter (check boxes with counts),
    a one-click Unwatched switch, a row of the filters on with their crosses, an A–Z jump, type-to-jump and small,
    medium or large posters or a list (1.4.0: the same in every layout).
  - **Collections and playlists** (1.4.0): a page of their own in every layout, in the collection's or playlist's
    own order, with Play or Resume on the first unwatched, Shuffle and Hide watched; a film's page says which
    collections it is part of. A direction button next to Order (1.8) reverses whichever order is chosen ("Newest
    first" or "Oldest first", "A to Z" or "Z to A"), on the Collections page's See all too, and is kept through Back,
    Forward and R.
  - **Item pages:** a full-width backdrop with the title's logo, info chips (year, rating, runtime, resolution, HDR,
    audio), and Play as the one highlighted action.
  - **The player's own controls:** on a dark gradient, with Skip intro/credits, Previous and Next, a Next episode
    countdown near the end, speed (0.5–2×), subtitle delay, and a subtitle **Text size** slider (50–200 %,
    remembered).
- **Skipping** (1.1 to 1.6).
  - Skip intro and Skip credits come from the server's segments, from the file's chapters (Intro, Opening, Recap,
    Credits, Ending…) when the server has none, from an intro you skipped by hand earlier in the season, and, if you
    turn it on, from **Settings → Look up intros online** (TheIntroDB and AniSkip get the show's public id, the
    season, the episode and its length).
  - **Skip the parts a show repeats** (1.6): Shift+S marks the part you just jumped over, and Nixie offers "Skip
    extra" there in every episode of the show. Skip the same part in two episodes and Nixie asks "Skip this every
    episode?". The skip lands on the cut: the nearest black frame or silence within a few seconds.
  - **Settings → Playback** has "Learn skips from my seeks", "When a show has a skip rule" (a button, or skipped by
    itself) and the list of skip rules, with Forget for each rule, each show, and all.
- **Collections made by Nixie** (1.6; fewer and better in 1.7.3; picked for the week in 1.8), from the server's
  libraries, on this PC (nothing is sent anywhere): franchises (two or more films TMDb puts in one collection),
  keywords, "Acclaimed, not seen yet" (the best 20 titles you haven't watched, weighted by how much backs each rating),
  "Under 90 minutes" (films of 60 to 90 minutes you haven't watched, best first), shows you left over a month ago, and
  Halloween or Christmas in season. A title counts once however many files or libraries hold it. There are no
  collections of directors, actors, studios, networks, decades, countries or genre pairs; a library's filters keep its
  decades, genre mixes and countries. They share one Collections page with the server's collections and playlists, open
  like any other, and **Save as playlist** copies one to the server as a private playlist when you press it.
  - **This week** (1.8): ten collections or fewer, first under Made by Nixie, picked so that no two are near copies,
    none is too small or too broad and no kind takes more than two. The same all day; some turn each day, themes each
    week. Home shows two of them after New in (three with Settings > Collections > Made by Nixie on Home > "A third row
    of picks"; "This week's picks" turns them off there), then Unfinished series.
  - **Picks tied to what you finished** (1.8): a film, show or anime you finished (a show at 90 % of its episodes) or
    made a favourite brings "Next in <franchise>" (from the next film), "More like <title>" (titles that share its rarer
    traits, not just a genre) and "More from <person>" (its director or one of its three leads, with five or more
    titles here you haven't seen). Each holds for three days; with nothing finished, none show. Each kind has its
    switch in Settings > Collections > Made by Nixie collections; with More from and Spotlight off, Nixie doesn't read
    the titles' people.
  - **The weekly Spotlight** (1.8): from Monday to Sunday, one director, lead actor or studio with a house style (Studio
    Ghibli, Pixar, A24 and 27 more) with well-rated titles on the server, five or more of them not watched, the best of
    those first.
  - **Every tile says why it's there** (1.8): one or two facts, how many titles are here and how many you haven't
    watched, and an end date when there is one ("TMDb keyword “heist” · 5 films, none watched"). With Wikidata's awards
    on, up to two award badges ("Best Picture winner"). Narrator reads all of it. A pin that has moved on (last week's
    Spotlight) stays in Saved, made again where Nixie can, or marked "Ended" with a way to unpin it.
- **Discover** (1.7): **Settings → Look up lists online**, each source off until you turn it on. Award winners and
  finer genres from Wikidata, franchise names from TMDb, and what's trending on Trakt and TMDb, each matched to your
  libraries on this PC: an Awards row and Trending, Popular, Most anticipated and Box office rows on the Collections
  page, and "Trending on this server" on Home (1.8; it was "Trending in your library"). Each source has a "What is
  sent" card: a request for a public list, the same for everyone, with Nixie's name and version; never the server you
  use, your account, libraries or what you watch. Details: [PRIVACY.md](../PRIVACY.md).
- **The way up from an episode** (1.7): an episode shows its show and its season over its title ("Shows › What We Do
  in the Shadows › Season 3"), each a button that opens that page, and a season shows its show, in every theme and
  however you got there.
- **Mouse and keyboard.**
  - Mouse buttons 4 and 5, Alt+←/→ and the keyboard's Back and Forward keys go back and forward between pages.
  - Back to a page you just left (1.7.5) shows it as it was, at once: its items, how far down it was, the card you left
    from, its Order and Hide watched. A title you marked watched or a favourite meanwhile shows so, and the page checks
    the server behind it only when something may have changed (search results too; a library waits for R). A big
    library left before all its titles came reads on from where it stopped. R still loads it again.
  - Shortcuts everywhere: search (Ctrl+F or /), Settings (Ctrl+,), and the usual player keys (Space/K, J/L, ←/→, M,
    C, V, B, F, 0–9 and more).
  - Press **?** or **F1** (or Settings → About → Keyboard shortcuts) for the full list, or see
    [docs/SHORTCUTS.md](SHORTCUTS.md).
- **mpv playback through jellyfin-mpv-shim.** The player is "mpv" (the default). The shim runs headless in a *bridge
  mode*. It takes care of PlaybackInfo and the transcode decision, track selection, trickplay and skip-intro, and
  reports progress to the server. FluentFin talks to it over a stdio JSON-lines protocol
  ([docs/BRIDGE.md](BRIDGE.md)), and mpv draws inside FluentFin's player page.
- **A polished mpv bar.** By default the bar is [uosc](https://github.com/tomasklaen/uosc) in FluentFin's look, with
  the shim's own bar as the fallback. It shows the series and episode title, fills the seek bar in your accent
  colour with trickplay previews, offers previous, next and a Skip Intro button, keeps subtitles above the bar,
  takes FluentFin's keys (k, j/l, arrows, s, Shift+N/P), and the mouse wheel changes the volume.
- **Nixie's own player controls (the default).** With Settings → Playback → Player controls → Nixie, libmpv renders
  in-process into a WinUI composition swap chain, and Nixie draws its own controls over the video: seek, play/pause,
  ±10/30 s, Skip intro/credits (button and S key), next episode, volume, audio and subtitle menus (with text size),
  full screen, and auto-hide. This mode needs libmpv: the release includes it, and a source build needs it in
  `vendor\libmpv` (see `vendor/libmpv/VENDORED.md`). When libmpv cannot load, Nixie falls back to mpv's controls and
  says so.
- **A TV journal.** A Serializd-style diary of what you watched, kept locally in SQLite, one journal per server and
  user.
  - Episodes are logged automatically when they're watched to the end, and synced from Jellyfin's played state (with
    exact dates when the Playback Reporting plugin is installed).
  - Each entry can have a half-star rating, a review and a rewatch flag.
  - You can filter entries and export them to CSV or JSON.
  - Ratings are sent to Jellyfin only if you turn that on.
- **Home shows what it has at once.** The libraries list is cached per session, rows are requested in parallel with a
  placeholder each, and the last Home page is saved and shown at once on the next start while the rest fills in as it
  arrives. On a first start, Home's first Made by Nixie row comes from the first titles the index reads, about a second
  after Home opens on a library of 25,000 titles (1.8), and the rows from the whole library take its place.
- **Clearer states.** A loading screen with Cancel, plus error and empty states with Try again on Home, libraries,
  item pages, Search and the server page.
- **Updates, checked on by default** (1.5). Nixie asks GitHub only for its latest release: when it opens, every 4
  hours while it stays open, and when you come back to it after an hour or more away. An installed copy downloads a
  new version in the background (not on a metered connection) and checks it against its `.sha256` and the digest
  GitHub keeps; **Restart to update** installs it quietly and opens Nixie again on the page you were on, and
  **Install when I close Nixie** does it as you close. A portable copy shows each new version with a link to its
  release page. **Settings → Updates** names every step as it happens, and its **Check for updates** turns the
  checks off.
- **Settings in titled groups,** Appearance first and About last. Audio and subtitle language are your Jellyfin user
  settings; the skip prompt and wheel volume are kept on this PC. **Settings → About** has the version and the
  licence, Keyboard shortcuts, Report a problem and Contribute; **Settings → Integrations** opens Jellyseerr.
- **Server administration that asks first.** Restart, Shut down and every delete ask by name before they act.
- **Item menus that only offer what works.** The More menu lists only entries that do something; the editing tools
  (metadata, images, identify, refresh) are shown to server administrators only. **Delete** asks first and names the
  item, and removing a server or a saved sign-in names it too; Cancel is the default.
- **Keyboard and screen-reader work.**
  - Rows, season pages' episode lists and library grids are one Tab stop each, and Tab comes back to the card or
    episode you left; the arrow keys, Home/End (and PageUp/PageDown in a grid) move inside them, and Up/Down leave
    them for what is above or below.
  - 1.7.4: every row reaches its last card: with the mouse over a row, arrows show at its ends (each only while there
    is more that way); Shift+wheel, a tilt wheel, a touchpad's sideways swipe and a drag move it, and so does the plain
    wheel once the page can't scroll further. A gamepad's D-pad moves along it and its shoulder buttons page it. Cast &
    crew is a row of keyboard stops too, each name kept within its photo (the whole of it in the tooltip and Narrator).
  - An episode is one stop: Enter plays it, and the Menu key or Shift+F10 opens its menu.
  - Narrator reads each card by name with its place ("12 of 320"), each episode as "S1 E4 Title, watched", every
    icon button has a name, and an item page's title logo is read as its heading.
  - Item pages offer "Resume at…" and "Play from start".
  - Long text wraps, with Show more.
  - Colours follow high-contrast themes.
- **Sign-out feedback.** Signing out shows progress, playback stops cleanly first, and nothing can start playing
  during a sign-out.
- **Less waiting at start and at the first Play.**
  - The window appears before the automatic sign-in finishes and says "Signing in to …".
  - After sign-in, the mpv backend starts in the background, so Play has less to wait for. With mpv's controls the
    backend also opens mpv ahead of time. With Nixie's own controls (the default), the in-app mpv starts at the first
    Play.
  - The Flyleaf engine and the Windows player's codec check run only when those players are used.
  - Start-up and play timings are written to the log as `[perf]` lines.
