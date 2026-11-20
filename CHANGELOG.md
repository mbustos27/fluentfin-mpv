# Changelog

## Unreleased (1.4.x)

**Big libraries load**

- **A library of any size opens at once.** A library was asked for whole, in one request; on a server with 25,842 movies
  the server took over 100 seconds, the app gave up, and the Movies page stayed empty (1.3.1 and 1.4.0 alike). Now the
  first hundred titles come first and show at once, and the rest follow page by page, one request at a time, each page
  sized from how fast the server answered the last (never more than 1,000 titles). The head says how far it has got
  ("Loading… 1,200 of 25,842 movies"); Drive-in's tubes count up. Sort, Filter and the A to Z work on the titles in so far
  and are whole once every page is in. A page that keeps failing stops the rest, and the head says so ("1,200 of 25,842
  movies · the rest didn't load"); R tries again. Leaving the page stops the rest.
- **Back and R in a big library** come back to the poster you left: if its page is not in yet, the grid waits a moment for
  it, and otherwise shows at once and goes to the poster when it comes, unless you moved meanwhile.
- **Editorial's Home genre strip** reads libraries the same way (it sent the same whole-library request).

**Faster item pages**

- The season, series and episode pages ask for their lists together with the item: a season's episodes and the series'
  seasons, a series' seasons, episodes and More like this, an episode's season for Up next. Each waited for the one before.
- A series' episodes, asked twice while the first answer is on its way (Editorial's series and season pages asked again on
  every change), are one request.
- No list asks for each image's aspect ratio any more: nothing in the app reads it, and the server may open each image to
  measure it.

## 1.4.0

The consistency release. The library and collections review found that the library layer under the four themes was never
finished, and that the worst faults were shared by all four; 1.4.0 finishes it once, for every theme.

**Libraries**

- **Filters you can see.** Every library has a one-click Unwatched switch beside Sort and Filter, in every theme.
  - Under the toolbar, a row says what is shown ("Showing 223 of 320 movies") with a chip for each filter on; its cross
    takes that filter off, and Clear all takes them all off. Narrator hears the new count.
  - Two genres now mean either genre; a genre and a year mean both. The Filter menu's counts follow the other filters, and
    a choice that would leave nothing is greyed.
  - A filter that leaves nothing says so ("No unwatched favorite movies in Comedy") with Clear filters, instead of a blank
    grid that looked like loading. Drive-in's tubes read 0.
  - Cinematic's section tabs light only when they are exactly what is shown, and never reset your sort. Editorial's
    Unwatched and Favorites are in the toolbar (they were off-screen), its genre row shows when it scrolls on, and More
    filters opens the full Filter menu.
- **A sort that says what it does.** Dates, ratings and play count open newest, highest or most first. The button says the
  direction in words ("Date added · newest first"), the arrow beside it turns it in one click, and choosing the sort in use
  again turns it too. Under any sort but Title, the A to Z's row holds Oldest first and Newest first, so the grid never moves.
- **The A to Z in one shape.** # and A to Z in every theme, letters with no titles dimmed and skipped, at least 32 px high
  and on one row. It shows from 48 titles. The letter you are at is marked, a jump puts that letter's row at the top, and the
  row ends with where you are ("M · 171 of 320"). On the A to Z, a letter key presses that letter.
- **Libraries open on the first poster**, and Back returns to the one you left. Type a title's first letters on a poster to
  go to it.
- **R refreshes; while you're typing a title, R is a letter.** R alone refreshes a library as it does every page, keeping
  the sort, the filters and the poster. Within a second of another letter, R is part of the title ("m", "a", "r" goes to
  "Mar…"). Titles that start with R: the A to Z's R.
- **View: small, medium or large posters, or a list,** kept for each library; Cinematic also offers posters instead of stills.
- **Room for the titles.** Posters keep their shape at every window size (they were cropped at 1100 px). Cinematic's preview
  takes a third of the window at most and folds to a band as you scroll, with Sort and Filter above the grid. Editorial's
  rows run on across letters, each letter tagged on its first title, its heading folds to one line as you scroll, and its
  titles are a size larger. Drive-in's sky folds to a band as you scroll.
- **Loading looks like the page it loads,** the header and letter row in place and the posters where they will be.

**Collections and playlists**

- **A collection has a page of its own,** the same in every theme, drawn in that theme's type and colours: the box set's
  poster, its title, "6 films · 1979–2016 · 1 of 6 watched" with a progress bar, three lines of its overview (More shows
  the rest), **Play** or **Resume** on the first film you haven't watched, in the collection's order, **Shuffle**, and
  Watched and Favourite for the whole set. The films are in the collection's own order (the one set on your server;
  release date when none is set), each numbered; **Order** offers release date, name and date added, and **Hide watched**
  leaves the rest. No genre filter, and an A to Z only from 30 films sorted by name. A collection that is empty or does
  not load says so, with Retry.
- **A playlist opens on the same page,** as a numbered list in its own order.
- **Film pages say "Part of: Copper and Glass"** in every theme when the film is in a collection (servers from Jellyfin
  10.11); the link opens it.
- **Home no longer shows "New in Collections" or "New in Playlists".** A collection is made when its films are imported,
  so the row repeated "New in Movies".
- **The Libraries page is in your theme** (its title, margins and ground), shows what each library holds ("Movies · 320 ·
  97 unwatched"), puts Collections and Playlists in a group of their own after the media libraries, shares the tiles
  evenly between rows (no tile alone on a row), says when it cannot load your libraries (with Retry), and puts the
  keyboard on the first tile.
- **One page grid per theme:** the Libraries page, a collection, Search results and Settings put their title where the
  theme's library puts its title, with the same side margins on both sides (on the 4 px grid).

**The top bar**

- **The top bar shows every library while it has room.** Each of your libraries is a tab, in your server's order, with
  Collections and Playlists last; the bar no longer folds five or more into one "Libraries" tab at any width. When the
  window is too narrow for them all beside the search box, the last ones move into **Libraries ▾**, whose menu starts with
  All libraries; only when even Home, Libraries ▾ and the search box do not fit does the bar fold into one Sections menu.
  The folded tab is named after the library you are in ("Movies ▾"), and its menu checks it. "Add to the top bar" is gone:
  every library already is a tab. All libraries is always one click away.
- **One place is lit in the bar, always the one you chose.** A tab, a library from the menu, the search box or Settings
  stays lit on every page you open from it (a film, a season, the player), so the same film no longer lights Home, Libraries
  or nothing depending on how you got there. The search box wears the underline while its results (or an item from it)
  show, the account button a ring while Settings does. Choosing the lit tab again on its own page takes it back to its top;
  choosing a library from a menu puts the keyboard in the page, not back on the tab.
- **A film you reach from Home or the search results names its library** above its title ("Movies ›"); a click opens the
  library.
- **The top bar sits on one line.** The window's Minimize, Maximize and Close are as tall as the bar, so they line up with
  everything else; Back is a quiet arrow 16 px from the edge (a dead Back no longer sits on a darker tile), with the same
  soft hover as the tabs; the tab underline is as wide as its label, and hovering a tab shows a soft fill instead of a line;
  the spacing is even (16, 24, 24, 8). The search box is centred in the window when there is room, otherwise it sits beside
  the account; its tooltip names Ctrl+F and /. The Nixie wordmark goes Home. Cinematic's floating bar is centred, with its
  menu on the same glass; Drive-in's sparkles stay out of the bar. The bar itself is no longer a Tab stop with nothing on it.

**Highlights and buttons**

- **One accent, used for three things.** Pink now marks only the page's main action, where you are, and progress.
  - A watched title shows a small, dim check in the top-right corner of its card.
  - A series, season or collection with episodes or films left shows how many in a small square in the top-left corner.
    Narrator says the same number.
  - The old pink capsule, which meant both "watched" and "this many left", is gone from every theme.
- **One highlight for each meaning, in every theme.**
  - The card the keyboard is on has a two-tone ring and its buttons.
  - The mouse lifts a card or brightens it. While the keyboard is on a card, the mouse only brightens the others, so two
    cards never look selected at once.
  - Only the artwork grows when a card is selected. The title under it no longer moves or overlaps its neighbours.
  - Cinematic's stills grow by at most 8 px a side, without the pink glow. Play shows on a still the keyboard is on only
    where Enter plays it.
  - Editorial: a poster whose details are open has an accent rim and a notch instead of the focus ring. Narrator says
    "expanded" or "collapsed" only when the details open or close.
- **One set of button shapes and sizes in every theme.**
  - Every library toolbar uses the small 32 px buttons.
  - Round 44 px icon buttons everywhere, Editorial included.
  - Each row has one "See all" link at its right; the row title stays a heading.
  - Editorial's chips are 32 px. A picked genre is a plain highlight; Unwatched and Favorites show a check when on.
  - A card's buttons sit in one strip along the foot of the artwork: Play, Watched, Favorite and More.
  - Drive-in's season tubes work like tabs.
  - Delete confirmations show a trash icon, and Delete shows its focus.
  - The update notice: Download and install is the main button, Release notes a link.
  - The player's buttons respond to hover, press and focus like every other button.
  - Editorial's sunset stripe under the top bar has no pink band and no longer shows over the video.

**Up next and the player**

- **The player's controls always come back with the mouse.** On 1.3.1, after opening the player and pressing Space before
  any click, moving the mouse could stop bringing the controls back until the player was opened again. Any move of the
  mouse over the video now shows them, whatever had the keyboard, and keys work in any order with the mouse.
- **The next episode looks and reads the same everywhere:**
  - In the player, **Next episode** is the main button (Primary, 44 high) with a skip-next icon, and the countdown runs as
    a clear 3 px line along its foot; the episode's title is in its tooltip and Narrator's name. **Watch credits**,
    **Keep watching**, **Skip intro** and **Skip credits** are secondary buttons of the same height, all on one line, in
    the theme's wine instead of a stray teal, and they line up with the bar's own edge in every theme (they overhung
    Editorial's bar by 16 px and Cinematic's by 64).
  - **One label, "Up next"**, on every Home, the episode pages and Drive-in's sign (it said Next Up, Next up and Coming
    up), and one episode code: Drive-in's tubes say "S1 E03" (they said "S1·E03").
  - **Home's next-up item opens its episode in every theme** (Drive-in's sign and Editorial's list played it, Classic and
    Cinematic opened it); its page's Play is one more Enter. Editorial's list lost the play mark that was not a button.
  - **The season page marks the episode its Play starts "Up next"** in every theme (Drive-in's showtimes said NEXT).
- **Chapters in every theme:** the player's bar has a Chapters menu (the chapters with their times, the one playing
  checked; a click goes there), disabled and saying so when the item has none. Editorial no longer promises a paused
  side column it never had.
- **Cinematic's player:** the bar's Audio and subtitles button opens the player panel's own Audio and subtitles part (one
  place for the job), and the window's glass island no longer floats over the video.
- **Home videos and music videos play** in Nixie's player, alone like a film (they could only say "can't play here").

**The keyboard**

- **A film or an episode page opens with the keyboard on Play** in every theme (it was on the top bar's Back); Back and
  Forward still put it back where it was.
- **In the player,** Tab reaches the prompts first while they show, then runs left to right; after the player, the
  keyboard is back on the page's Play in every theme (Drive-in left it nowhere). With F1's shortcut sheet open over the
  player, the player's keys no longer act under it.
- **R keeps your place, and only once.** Holding R or F5 refreshes the page once (it reloaded on every auto-repeat), a
  second R while the page is still loading joins the first, and the keyboard goes back to the button it was on (Trailer,
  not Play) or the card it was on (not a card out of view). On a slow server, if you move first (a key, a click, the wheel,
  the search box), the refresh leaves the keyboard where you put it. R on a closed list box (Audio, Subtitles) picks the
  entry starting with R instead of refreshing; F5 and Ctrl+R still refresh there.
- **Ctrl+F has one owner,** the window's shortcuts: like every shortcut it does nothing while you type in a box.

**Fixes from the first bug hunt**

- **The trailer theatre keeps the keyboard.** While a trailer plays, Tab and Shift+Tab stay inside it, Esc and Back always
  close it, and every other shortcut (Ctrl+F, /, R, F1, Alt+Home, Ctrl+,) and a page's own keys (Home's N and C, Ctrl+Z)
  do nothing. Before, Ctrl+F moved the keyboard into the search box under the dim, and Esc then no longer closed it.
  A click on the dim's top band closes it too: that band was still the window's title bar, and a click there dragged the
  window (a double click maximized it).
- **After "Choose a look", the old Home lets go.** Classic's, Cinematic's and Editorial's Home listen to the page's data
  only while shown, as Drive-in's did: the Home you left could close the new one's "Removed from Continue Watching ·
  Undo" under your pointer.
- **Update checks survive a clock change.** When GitHub asks Nixie to wait, the wait is kept for a day at most, a wait
  that a clock set back put days ahead counts as over, and Check now says which day ("wait until tomorrow at 09:00").

**Verified, and the known limits**

- Every theme runs the keyboard matrix, the streams' harness scripts and the review's measurements as checks
  (`then-140-checks.ps1`): the top bar on one line, the loading placeholders where the cards land, every library's items
  opening, the focus named after every navigation, Narrator's names carrying the numbers on screen, the empty states, and
  in the player no prompt over the time or the seek bar and its labels at 4.5:1.
- Editorial has no list view (View offers its poster sizes only).
- Drive-in's chapter bulbs on the console come later; its Chapters menu is in the bar.
- Two checks are by hand, not off-screen: open the player and press Space before any click, then move the mouse (the
  controls must come back); and Esc inside YouTube's own player in the trailer theatre.

## 1.3.1

- **Trailers play in Nixie.** The Trailer button no longer sends you to the browser.
  - A **YouTube trailer** plays in a **theatre window** over the page: the page dims, the trailer plays at about 60 % of
    the window's width with its title and Close. Esc, Close or a click on the dimmed page close it, and the keyboard goes
    back to the Trailer button. It uses YouTube's privacy-enhanced player (youtube-nocookie.com) in a locked-down web view
    that may load nothing else; "Watch on YouTube" and any other link open in your browser.
  - A **trailer file on your server** plays in Nixie's player, like any video, and comes first when there are both.
  - Other trailer links still open in the browser.
- **Drive-in's theatre is a little drive-in:** a small outdoor screen on two posts under a bulb-lit marquee reading
  NOW SHOWING and the title, the dusk sky and the striped sun around it, and two cars with their speaker posts in front.
  Cinematic and Editorial frame the trailer in their own glass; Classic in the shared glass frame.
- **R refreshes the page** (F5 and Ctrl+R too): Home, a library, a See all grid, an item, series or season page, search
  results and the Diary load again from your server, in place: the card the keyboard was on keeps it, and Narrator says
  "Refreshed". Not while you type in a box, in a dialog or menu, or in the player; Settings has nothing to refresh.
- **Update checks are now on by default** (Settings > Check for updates to turn them off); Nixie checks each time it
  opens, and **Check now** asks at once. When a newer version is out, a notice above the page says so in every layout,
  on one row with Download and install, Release notes and Dismiss; nothing is downloaded until you click. Nothing is
  sent but the request for the latest release. If you had turned the check on before and then off, it stays off.
- **F1 closes the shortcut sheet too.** F1 and ? open the list of keyboard shortcuts; pressed again while it is open, they
  close it, and the keyboard goes back where it was. Esc and Close still close it.
- **Keyboard controls are the same in every theme, playing or paused.** The player's keys go through one router that takes
  each key before whatever control has the focus, so they work at once after you use the mouse: after a click on Play,
  Pause, the seek bar, the volume or the video, and right after unpausing, without waiting for the controls to hide.
  - Space always plays and pauses; Enter presses the button the keyboard is on. After the volume, speed or audio and
    subtitles menu closed, Space no longer opens it again, and with Full screen focused it no longer leaves full screen.
  - The player's buttons and the seek bar no longer keep the keyboard after a click (Tab still reaches each one).
  - In Cinematic's player tabs the arrows move between the tabs, and Esc closes its panel even after a click opened it.
  - A held Space, M, F or Shift+N does its job once; held arrows still seek and change the volume.
- **Double click:** a double click on the video goes full screen (or back) and keeps playing or paused as it was, also on
  the dark fade at the top and bottom of the video; a double click on a control never goes full screen.
- **Editorial's library:** the arrows go along the library (Right from a letter's last title no longer skips the next
  one), and Page Up and Page Down move a page of rows, as in the other themes.
- **Fixes from the library and collections review:**
  - **Playlists open.** A click or Enter on a playlist did nothing; it now opens with its items in the playlist's own
    order (the Sort button says "Playlist order" until you pick another sort).
  - **Cinematic's library preview shows the title you are on:** the still under the pointer, else the one the keyboard
    is on, else the first in view, and it follows every sort, filter and scroll. Its Play no longer plays a film the
    grid has moved past or filtered out, and with nothing left to show it goes. The place line and the A to Z mark
    follow the first title in view.
  - **Names sort the way Jellyfin files them:** "The Matrix" sorts and jumps under M, in every layout.
  - **Drive-in's player:** the Skip and Next prompts sit above the nixie console, no longer over the time left or the
    seek bar's end.
  - **Narrator:** the next-episode countdown names the button that stops it ("Keep watching", "Watch credits"), and
    Editorial's Sort button's name starts with the words it shows ("A to Z").

## 1.3.0

Nixie now has **four layouts to choose from**: not new colours on the same pages, but Home, the series and season pages,
the movie and episode pages, Library, the player's controls and the sign-in screen laid out anew for each.

- **Four layout themes.**
  - **Drive-in** (the default for new installs): a drive-in at dusk, drawn in vector shapes: a banded sky, the striped sun,
    hills, 1950s cars and a motel sign.
    - **Home:** the featured title on the **outdoor screen**, Resume focused as you arrive and the time left in
      **nixie tubes**; the motel sign's neon **Now showing** and the **Coming up** board of your next episodes, one
      button each; **marquee** row headers.
    - **Series and seasons:** the title and actions on a marquee board over the backdrop, the **SHOWTIMES** board of the
      whole season (Enter plays an episode) and the seasons as **nixie tubes**, the one shown lit. Films and episodes
      sit under the same board, Play with the time left in tubes.
    - **Library:** a **letter board** with the library's name on a marquee, its count in tubes and the A to Z as letter
      tiles (letters no title starts with stay dimmed).
    - **The player:** the bottom bar is a **nixie console** over the video (elapsed, remaining and the volume in tubes,
      the seek bar a lit neon tube), and the top bar shows when the film ends.
    - **Sign-in:** the form on the screen of a drive-in **lot**.
    - **Compact Home** (below) folds the scene into a short strip.
  - **Cinematic:** the artwork first, as in a cinema: dark around the pictures in Light too, with the top
    bar a **floating glass island**.
    - **Home:** one title **fills the window** under the island (its art, title, facts and Resume with the time left), a
      thumbnail pager, and the **Up next** shelf (Continue Watching, then Next Up) over the foot of the art; the
      libraries' "New in" shelves follow.
    - **16:9 stills everywhere,** with the title's art on each and a **focus zoom** with a neon rim (no zoom with reduced
      motion).
    - **Series, seasons, films and episodes** on one **full-bleed** stage: the series with season tabs and its episodes
      as a shelf of stills under the actions.
    - **Library:** the focused title fills a **preview stage** with its own Play; the library's sections (All,
      Unwatched, Favorites, Recently added) are tabs, with the A–Z jump on the grid's header line.
    - **The player:** a **tabs panel** drops from the top bar (Info, Episodes, Chapters, Audio and subtitles).
    - **Sign-in:** a dusk scene drawn around the form: the road, a saguaro and the sky.
  - **Editorial:** a magazine for a big library: big type, a smaller hero and more titles per screen,
    with a sunset rule along the foot of the top bar.
    - **Home** pairs a hero tile ("Tonight": what you watched last, with Resume) with a numbered **Up next** list (in
      progress, then next episodes, each one click), then a big-type **genre strip** that picks the shelf below it.
    - **Details in place:** a poster opens its details under its row (Down goes in, Up and Esc come back); "Full
      details" in its menu opens the item's page.
    - **Series and seasons:** a **Seasons panel** with each season's progress ("2 of 8 watched", "New"), and the whole
      season as a **grid of stills with synopses**, four across at 1920 px, with Hide watched.
    - **Library:** the library's name in big type, **genre chips** with counts, Unwatched and Favorites chips, and the
      grid in **letter sections** with the A–Z jump on the section bar.
    - **The player:** its two bars in the page's margins over the night scrims.
  - **Classic:** the 1.2 layout, exactly as it was.

  Search results, people, collections, Diary, Settings, the server administration, dialogs and menus are the same in
  every theme, and so are the desert-sunset colours, Dark, Light, Solid mode and high contrast.
- **Switch at any time, without a restart.** The page you are on comes back in the new layout with your place kept (the
  item that had focus and each row's position) and the top bar as it was, search text included. Back still goes where it
  went: the pages behind take the new layout as you reach them. The change fades across in a fifth of a second (at once
  with reduced motion), and Narrator says "Theme changed to <name>".
- **Settings > Appearance** shows the four themes as small previews, with the chosen theme's own options below them.
  After the first sign-in, a one-time **"Choose a look"** step offers the same previews.
- **Upgrades start on Classic**, so nothing moves under you until you choose; **new installs start on Drive-in**.
- **Compact Home** (Drive-in, in Settings > Appearance) folds Home's drive-in scene into a short strip, for more rows on
  the first screen.

## 1.2.0

Seaglass is now **Nixie**, after the 1950s nixie tube, whose digits glow neon orange-pink. The GitHub repository keeps
its name (`fluentfin-mpv`).

- **The new name.** The app is `Nixie.exe` (the launcher at the top of the folder, and `app\Nixie.exe`). Release files
  are `Nixie-1.2.0-win-x64.zip`, `.msi`, `-sources.zip` and `.txt`. The window and title bar, About, the Start menu
  shortcut, Settings > Apps (publisher Nixie) and the installer say Nixie.
- **Updating from Seaglass or FluentFin MPV.** Installing the `.msi` replaces an installed Seaglass or FluentFin MPV
  (same upgrade code), into `%LOCALAPPDATA%\Programs\Nixie`; the old Start menu entry goes, so pin Nixie to the taskbar
  again if you had pinned the old app. Seaglass 1.1.x's own "Download and install" finds this release too: it also
  carries the installer as `Seaglass-1.2.0-win-x64.msi`.
- **Your data comes along.** The first start copies `%LOCALAPPDATA%\Seaglass` (or `%LOCALAPPDATA%\FluentFinMpv` when
  Seaglass holds no settings) to `%LOCALAPPDATA%\Nixie` and leaves the old folder as it was: saved servers and
  sign-ins, settings, the journal (a consistent copy even while the old app runs) and the mpv backend's configuration;
  old logs and downloaded updates are left behind. If the copy fails, Nixie says so, uses the old folder for that
  session and tries again at the next start.
- **The server sees Nixie, on the same device.** A saved sign-in is your user name and a password protected for your
  Windows account (no access token is saved), and the app signs in again with them at every start. The copied
  `LocalSettings.json` keeps the key that password needs, so it still works, and nobody has to type it again. The sign-in
  and the mpv backend send the client name Nixie with this PC's device id, the same one Seaglass sent: the server sees the
  same device sign in as Nixie and gives it a new token. What the server keeps per user, such as Continue Watching and
  played state, is untouched.
- **A new icon:** a nixie tube whose glowing digit is a play triangle, hot pink to orange on deep maroon, drawn from
  vector shapes (`tools\icon\make-icon.ps1`) for every size from 16 to 256 px. The installer's pictures match.
- **For scripts:** the environment variables are `NIXIE_THEME`, `NIXIE_FULLSCREEN` and `NIXIE_TESTS_ON_DESKTOP`; the
  `SEAGLASS_*` names still work. The design notes moved to `docs\design\DESIGN.md` and `DESIGN-RESEARCH.md`.
- **A top bar instead of the side rail.** Home, your libraries (each its own tab while they fit, else in a Libraries
  menu), Diary and the libraries you add to the top bar are tabs in the window's one bar, with search and the Account
  menu beside them. The tab you are on is underlined; Left and Right move between tabs, and a narrow window folds the
  tabs into one menu and search into a button. Settings is in the Account menu (and Ctrl+,). Home and the item pages run
  their artwork up under the bar, which darkens to keep its text readable. The server administration and the Playback
  reports page have their sections as tabs along the top too: no side pane is left anywhere.
- **The desert-sunset look.** Deep plum glass with a hot-pink accent in the dark theme, pale peach glass with crimson in
  the light one. Sign-in, the server page and empty or failed pages sit on a dusk scene (a striped sun over desert
  hills, which moves for 20 seconds and then rests); the rest of the app has a strip of hills at the bottom. The app's
  name glows like a nixie tube on the sign-in card and stands in the top bar. Solid mode and high contrast still have no
  glass and no motion.
- **One set of buttons.** Every button is one of six kinds (Primary, Secondary, Subtle, Destructive, Link and Tab) in
  two sizes, 44 and 32 px, with 8 px corners and the same hover, pressed, focus and disabled looks everywhere. A
  destructive confirmation draws its confirm in the danger colour and keeps Cancel the default.
- **Sign-in and server pages.** Errors are said on the page in plain words (a wrong password, a server that is off, an
  HTTPS certificate problem), with progress while the app waits. The server page's button says Connect; Quick Connect
  says when its code expires or was refused; "Change" waits while you sign in. Narrator hears each state.
- **Empty and failed pages** say what happened and what to do, with Try again or Refresh, and Narrator hears them.
- **Home.**
  - **Continue Watching stays clean:** "Remove from Continue Watching" in a card's menu takes it out at once, with Undo
    (and Ctrl+Z) for a few seconds; Settings > Appearance can hide items not played for 30, 60 or 90 days (on this PC only).
  - The hero features what is new to you (the latest unwatched items, then Next Up), not what Continue Watching already
    shows, edge to edge under the top bar.
  - N goes to the Next Up row and C to Continue Watching; a row's arrows, shown when it has more than fits, are
    larger and at full strength.
- **Libraries.** One scrolling grid instead of pages. The bar says the sort ("Sort: Name"); the Sort menu also holds
  Ascending and Descending. Filter is lists of check boxes with counts (genres, years, ratings, tags) and says how many
  are on; an A–Z jump goes to a letter; Back from an item returns to its card. The Libraries page has tiles that fill the
  row with the name under the art.
- **Item pages and cards.**
  - Buttons are named for what they act on ("Mark Inception as watched"), Play says what it plays ("Resume S1 E4",
    "Play from start"), and "Your rating" sits in the action row.
  - Watched and Favourite show the server's state everywhere and stay in step; marking a whole series or season
    watched offers Undo. The "…" menu on an item page leaves out Watched and Favourite, which are the buttons beside it.
  - Episode numbers are written one way everywhere, "S1 E4" (and "S2 E5–E6" for a double episode): cards, rows, the
    hero, Continue Watching, the player.
  - Audio and subtitle choices are plain words ("English (5.1)", "Spanish (Forced)").
  - The series page lists the episodes with a season picker, and a link opens the season's own page.
- **Search.** The arrows only move through the suggestions; Enter opens a results page grouped by type.
- **The player** (Nixie's controls).
  - **Previous and Next** buttons, **speed** from 0.5× to 2× ([ and ], Backspace for normal), and **subtitle delay**
    (Z and X).
  - Near the end, a **Next episode** countdown with "Watch credits" and "Keep watching"; turning auto-play off in
    Settings stops it. Leaving with Next 10 seconds into the credits still counts the episode as watched.
  - Trickplay thumbnails over the seek bar; one Audio and subtitles panel; the timecode easier to read.
  - **Esc only leaves full screen** (it never stops the video); a click pauses at once; Alt+Enter toggles full screen.
  - **Between episodes** the volume you set carries over, and the next episode keeps your audio and subtitle choice.
- **Server administration** asks before anything that cannot be undone, by name: Restart and Shut down, deleting a
  library, a user, a user's password, an image, a subtitle or a segment. Add library creates one, Scan all libraries
  scans, Edit metadata's Revert puts back what was loaded and Save sends only what changed.
- **Smaller things.** Jellyseerr's address can be changed once connected; "Show more" on an overview tells Narrator
  whether the text is open; a card focused by Narrator stays the card it is on (it could jump to the row's remembered
  card, so its menu acted on another item); the Windows Media Player bar says what each button does and stops playback
  here only after you pick another device to play on.

## 1.1.3

- **Copy Download URL is gone: it put your access token on the clipboard.** The link it copied carried your Jellyfin
  sign-in token, so anything that could read the clipboard (or Windows' clipboard history) could use your account. If you
  ever pasted one somewhere, sign out of Seaglass and sign in again: signing out ends that token on the server.
- **Delete asks first.** Delete in an item's menu now opens a question that names the item (a season's says Season), with
  Cancel as the default, so Enter or Escape keeps everything. Removing a server or a saved sign-in in Settings asks the
  same way, naming the server or the user.
- **Item menus only offer what works.** Play all from here, Shuffle, Add to playlist, Add to collection and Play On did
  nothing and are gone. Edit metadata, Edit images, Identify and Refresh metadata show only for server administrators
  (Edit subtitles also for users allowed to manage subtitles), as in Jellyfin's web app. A poster whose menu would be
  empty has no More button.
- **A simpler rail and tidier Settings.**
  - **Rail:** only places to go: Home, Libraries, Diary and Settings, and Settings is marked while it is open.
    Contribute moved to Settings > About; Jellyseerr, which showed for everyone, moved to Settings > Integrations.
  - **Settings:** every group has a title, in the order you need them: Appearance, Playback, Audio and subtitles,
    Servers and accounts, Integrations, Advanced and About.
  - **About:** the version and the licence, Check for updates, Keyboard shortcuts, Report a problem and Contribute.
  - The player list shows only when there is more than one player to choose, and "Play the next episode automatically"
    only for Windows Media Player, the one player that follows it.
- **Season pages and libraries, by keyboard and Narrator.**
  - **Episodes:** each episode is one Tab stop (it was five buttons). Up/Down/Home/End move between episodes, Enter
    plays, and the Menu key or Shift+F10 opens its menu (Episode details, favourite and the rest). Narrator reads
    "S1E4 Title, watched" and "4 of 8".
  - **Library and See all grids:** one Tab stop that comes back to the card you left; the arrows in all four
    directions, Home/End and PageUp/PageDown. Narrator says each card's place in the whole library ("12 of 320"), not in
    its page of 100.
  - **Leaving with Up and Down** goes to what is above or below on the page: Up from the first row reaches the hero again,
    and a See all grid no longer sends the focus to the title bar.
  - Narrator no longer says "100% watched" for an episode that is not finished.
- **Full screen without flashes.** Going in and out of full screen (with Seaglass's player controls) now shows black for
  a moment (about 40-90 ms) instead of whatever is behind the window, which in a dark room was a bright flash of the
  desktop.
  - Leaving the player from full screen no longer hides the window first.
  - Going back to a window no longer shows a stale, smaller picture for a frame.
  - Holding F or Esc no longer flips full screen over and over or leaves the player.
  - Subtitles move to their new place with the switch, not just after it.
  - A double click to go full screen no longer pauses and unpauses playback.
  - `SEAGLASS_FULLSCREEN=cloak` brings back 1.1.2's way to compare, `none` turns the hiding off.
- **A smoother volume bar.** With the wheel, the bar glides on from where it is instead of emptying and refilling on
  every notch, and a smooth wheel or touchpad that turns back no longer spends its first part undoing the other way.
- **One glass for menus, tooltips and dialogs.** Menus, context menus, flyouts, tooltips, the player's audio, volume and
  subtitle menus, and every dialog share the same glass, 12 px corners, light edge and text colour; opaque in Solid
  mode, system colours in high contrast. Menus no longer stack two blurs, a dialog draws its glass once, short dialogs
  (a sign-in error, a yes/no question) keep a readable width, and long dialog titles wrap.
- **Progress you can see on any poster.** Progress bars on posters and episode thumbnails sit on a dark track that runs
  the bar's whole height, in the light aqua in both themes; before, the bar sat on the artwork itself and could vanish on
  a poster of about its own lightness. The watched mark on episode thumbnails is the same aqua with a dark check.
- **Accent details:** links and secondary accent text use Seaglass's aqua instead of Windows' accent colour, and in high
  contrast the Home hero's text takes the system text colour (it disappeared on the light contrast themes).

## 1.1.2

- **A cleaner, glassier Seaglass.**
  - **Glass:** clean neutral glass everywhere, with a sea-glass aqua accent. A floating glass navigation rail, the hero's text on glass, and page sections, Settings and Diary on glass panels with a thin lit edge and a soft shadow.
  - **Backdrop:** the current title's artwork becomes a smooth, dark colour field behind the window, tinted only faintly by the title.
  - **Posters:** soft rounded corners, plain titles underneath.
  - **Motion:** in Full glass mode, a light that follows the pointer over panels and posters, and pages that settle in.
  - **Light theme:** a new light glass theme.
  - Solid mode and high contrast stay fully opaque.
- **A bigger volume indicator:** changing the volume with the wheel or the keys shows a larger, animated glass slider that fills smoothly, shows Muted, and is read by Narrator.
- **Smoother full screen:** going in and out of full screen no longer shows half-drawn frames. The window is hidden for a moment while it resizes, and comes back to exactly its old size and place.
- **Skip intro more often, without server admin:**
  - **Chapters:** the file's chapters (Intro, Opening, OP, Recap, Credits, Ending…) give Skip buttons when the server has none.
  - **Learned intros:** after you skip an intro by hand once, Seaglass offers it on the rest of the season (saved on this PC; Settings can forget them).
  - **Online lookups (opt-in):** Settings > Look up intros online asks TheIntroDB, and AniSkip for anime. It sends only the show's public ID, season, episode and length, for the episode playing; results are kept in memory for the session only.

- **A friendlier installer.** The MSI now opens a welcome page (Install / Cancel; nothing to accept, it says where the
  GPL licence and the source are), shows progress, and ends with "Launch Seaglass" ticked. Seaglass-style pictures,
  and Settings > Apps shows the publisher, the version, the icon, the website, help and update links. Every program in
  the package (Seaglass.exe, the launcher, the backend) has company, product, description and copyright in
  Properties > Details. There is still no code signing: SmartScreen says "Unknown publisher".
- **Fewer antivirus false alarms.** The backend's PyInstaller bootloader is compiled from source (MinGW-w64 gcc)
  instead of using the prebuilt one, and the build checks that nothing is UPX-packed.
- **Check for updates (off by default).** Settings > Check for updates: once a day Seaglass asks GitHub's Releases
  whether a newer version exists (nothing else is sent) and shows a quiet bar with Download and install (the MSI and its
  checksum, verified) and Release notes; a portable copy opens the release page instead.
- **An upgrade test** (`tools\release\upgrade-test.ps1`): installs the published MSI, runs the new one over it and checks
  the result; it refuses to run where Seaglass is already installed.
- **Rows scroll by axis, not by mode.** The mouse wheel always scrolls the page, even over a row; the tilt wheel,
  Shift+wheel and a sideways swipe move the row. Every row (Continue Watching, Next Up, Recently Added, Seasons, More Like
  This, Cast) is one control with always-visible arrows, a peek of the next card and a **See all** that opens the row as a
  grid.
- **Keyboard and gamepad, TV style:** one Tab stop per row (it returns to the card you left), Left/Right, Home/End along
  the row, Up/Down between rows, a focused card kept clear of the edges. A card is one Tab stop; Play, Watched and
  Favorite are in its menu (Menu key or Shift+F10). The hero's dots are one control with arrow keys, and a visible pause.
- **Narrator:** each row is a named list with "n of m" cards under a heading.
- Fixed: Tab never reached the later Recently Added rows on Home or the later episodes of a season.

## 1.1.1

- **Mouse back and forward.** Mouse buttons 4 and 5, Alt+Left/Right and the keyboard's Back/Forward keys go back and
  forward between pages, like a browser. Forward never starts playback again.
- **Keyboard shortcuts everywhere**, listed on a new shortcut sheet (**?** or **F1**, also in Settings and the player):
  - **Pages:** Ctrl+F, Ctrl+E or / search; Alt+Home Home; Ctrl+, Settings.
  - **Player:**
    - Space/K play-pause; ←/→ 5 s; J/L or Ctrl+←/→ 10 s, matching the on-screen buttons; Shift+←/→ 30 s.
    - 0–9 jump to a tenth; Home start; S skip; Shift+N/P next/previous.
    - ↑/↓ volume; M mute; C subtitles on/off; V next subtitle; B next audio track; F or F11 full screen; Esc leave.
  - No shortcut fires while you type. `docs\SHORTCUTS.md` has the full map.
- **Subtitle text size**: a Text size slider (50–200 %) in the player's subtitles menu, applied while you drag and
  remembered for every video.
- **Rounded rectangles** instead of oblong pill shapes for buttons, chips and the search box; round icon buttons stay
  round.
- **A smaller install**: the release leaves out .NET desktop pieces Seaglass never uses (Windows Forms, Visual Basic,
  WPF themes) and two unused backend modules: 759 files instead of 789.

## 1.1.0

FluentFin MPV is now **Seaglass**, with a new glassy look inspired by the Apple TV app (`docs\design\DESIGN.md`, then `SEAGLASS.md`;
the research behind it: `docs\design\DESIGN-RESEARCH.md`).

- **Glass.** The navigation rail, title bar, search, menus, dialogs, the hero's text, page sections and Settings sit on
  frosted glass with a lit edge; the current page's artwork, softened, tints the whole window and drifts slowly. Posters
  and artwork stay solid. Settings > Appearance > Glass: Automatic (follows Windows' transparency, animation and
  high-contrast settings), Full, Reduced (no motion) or Solid (no translucency). The accent is sea-glass aqua.
- **Home** opens on a hero of up to five items (Continue Watching first) with Resume and More info; it moves on every
  7 s (not while you point at it, and not with animations off), and never autoplays trailers. Rows have new cards that
  lift (and, in Full, tilt toward the pointer with a soft sheen); artwork fades in over a blurred placeholder
  (BlurHash) and is decoded at display size.
- **Item pages** (movie, series, season, episode): a full-width backdrop with the title's logo, a row of chips (year,
  rating, score, runtime, resolution, HDR, audio), Play as the one highlighted action, the journal as one glass card,
  cast, episode cards, season pills and Up next.
- **The player's own controls**: on a dark gradient (no blur over the video), a round play/pause, ±10/30 s, a thin
  seek bar with the time under the pointer, Skip intro/credits and Next as pills, auto-hiding after 3 s.
- **Contribute** opens this project's repository (it opened upstream FluentFin's).

- **The new name.** The app is `Seaglass.exe` (the launcher at the top of the folder, and `app\Seaglass.exe`). Release
  files are `Seaglass-1.1.0-win-x64.zip`, `.msi`, `-sources.zip` and `.txt`. The window and title bar, About, the dashboard,
  the Start menu shortcut and the installer say Seaglass. Installing the `.msi` replaces an installed FluentFin MPV (same
  upgrade code), into `%LOCALAPPDATA%\Programs\Seaglass`. The C# namespaces, project files and folders stay `FluentFin.*`
  so upstream FluentFin merges keep applying, and the GitHub repository keeps its name.
- **A new icon:** a frosted sea-glass pebble with a play triangle, for the window, taskbar, launcher, installer and the
  package logos. `tools\icon\make-icon.ps1` draws it.
- **Your data is copied, not moved.** The data folder is now `%LOCALAPPDATA%\Seaglass`. On the first start, if it does not
  exist and `%LOCALAPPDATA%\FluentFinMpv` does, the old folder is copied into it (recursively) and the old one is left
  untouched; the log says so. The journal is copied with SQLite's backup, so it is whole even while FluentFin MPV runs.
  If the copy fails, nothing is half-copied: Seaglass uses the old folder for that session, says so, and tries again at
  the next start. `--data-dir` is unchanged.
- **The client name sent to Jellyfin is "Seaglass"** (the UI and the playback backend both use it). Your server will show
  a new client in its dashboard and device list, and may ask you to sign in once.

## 1.0.1

- **An installer.** `FluentFinMPV-1.0.1-win-x64.msi` installs FluentFin MPV for your Windows user (no administrator
  rights needed) into `%LOCALAPPDATA%\Programs\FluentFin MPV`, with a Start menu shortcut. Uninstall it in Settings >
  Apps; your settings and data stay. A later installer replaces an earlier version. The zip stays, for a portable copy.
- **The program is easy to find in the zip.** The folder's top now holds only `FluentFin.exe`, `README.txt`, `LICENSE`
  and `THIRD-PARTY-NOTICES.txt`, plus an `app\` folder with everything else (in 1.0.0, `FluentFin.exe` was one of 675
  files and folders). The top `FluentFin.exe` is a small launcher that starts `app\FluentFin.exe` with the same
  arguments; shortcuts go to it. Settings and data are where they were.
- **English only.** FluentFin's own text is English, so the release no longer carries 134 folders of translations for
  Windows' built-in controls (21 MB). On a Windows set to another language, those controls' text (a text box's
  right-click menu, for example) is now English too.

## 1.0.0 (first public release)

A portable zip for Windows 10/11 x64: unzip it and run `FluentFin.exe`. .NET, the Windows App SDK, the playback
backend (with its own Python 3.14.10) and libmpv are inside; nothing is installed. Its `README.txt` says what it needs
and where it keeps your data (`%LOCALAPPDATA%\FluentFinMpv`, or the folder given with `--data-dir`).

### What it is

FluentFin's WinUI 3 interface with [jellyfin-mpv-shim](https://github.com/jellyfin/jellyfin-mpv-shim) as its playback
engine, running headless as a child process and talking to FluentFin over a stdio protocol (`docs/BRIDGE.md`). The
server sees one session, FluentFin's, and only the backend reports playback.

- **Two kinds of player controls** (Settings > Playback > Player controls). By default FluentFin's own, drawn over an
  mpv that runs inside FluentFin (the libmpv in the zip: MSYS2's mpv 0.42.0 package and its pinned dependencies, whose
  exact sources ship with the release). Or mpv's own bar, [uosc](https://github.com/tomasklaen/uosc)
  in FluentFin's look (the shim's bar as its fallback), with mpv in a window inside FluentFin's: chosen in Settings, or
  used automatically, with a notice, when libmpv cannot load on the PC. mpv's controls run mpv.exe from PATH.
  FluentFin's controls: seek, play/pause, ±10/30 s, Skip intro and Skip credits (a button above the seek bar while the
  server marks a segment, and the S key), Next episode (with the next episode's name, off at the end of the queue),
  volume, audio and subtitle menus, full screen.
- **The shim's playback**: PlaybackInfo and the direct play / transcode decision, track selection and remembered
  tracks, next episode, the media segments behind Skip intro/credits (which ones are offered is the shim's setting),
  trickplay previews with mpv's controls, progress reporting.
- **A TV journal**: what you watched, per server and user, with ratings, reviews, rewatches, filters and CSV/JSON
  export; kept on this PC.
- **A faster Home**, loading, error and empty states with Try again, Settings > Playback, keyboard and Narrator work,
  and feedback while signing out. README.md has the full list.

### Verified

- The automated suites: the UI tests (`FluentFin.Core.Tests`, `FluentFin.Tests`), the backend's bridge tests
  (the full backend suite last ran before the final backend change, with known module timeouts under load), the
  bridge smoke test and the end-to-end harness, which drives the real backend against a fake Jellyfin server.
- The release zip itself (`tools\release\verify-package.ps1`): unzipped outside the repository and run off-screen
  against the fake server, with a PATH of Windows' own folders only (no Python, no mpv) and no backend configured:
  sign-in, an episode played and stopped with the default settings (FluentFin's controls on the bundled libmpv), the
  same with mpv's controls (mpv on PATH), mpv's controls without mpv (the page says to switch to FluentFin's controls
  or install mpv, nothing is reported), a data folder whose saved player is VLC (it plays with MPV), and the
  self-test. Every process the app
  started and every DLL they loaded came from the package or from Windows, the backend was the bundled one, nothing
  was written into the package folder, and closing the app (or killing it) ended the backend.
- A real Jellyfin server: the developer's own (remote, HTTPS, signed in as a user who is not an administrator) has
  been used with it: sign-in, browsing and playback, with FluentFin's controls.

### Known limits

- **Not code-signed**: Windows SmartScreen warns on the first start ("More info", "Run anyway").
- **Real-server paths not seen yet**: a real transcode, and its restart on an audio, subtitle or quality change; how a
  server handles a duplicate Stopped report.
- **Not verified on screen** with FluentFin's controls: HDR on an HDR display, a DPI change, independent flip.
- **When libmpv cannot load on a PC, FluentFin falls back to mpv's controls** and says why (README.txt names any
  graphics-driver DLL libmpv needs). **mpv's controls need mpv.exe on PATH** (`winget install mpv-player.mpv-CI.MSVC`);
  the package does not include it, and without it the player says so.
- **Two players**: MPV (jellyfin-mpv-shim) and Windows Media Player. Upstream FluentFin's VLC and Flyleaf players are
  not in the release (their libraries are left out); a saved choice of either plays with MPV.
- With the mpv player: no SyncPlay, and remote "play next" / "play last" are not supported.
- The backend's own messages are in English.
- **Encrypted DVDs aren't supported** (nothing played from a Jellyfin server needs it): the release has no libdvdcss.
  The `libmpv\libdvdcss-2.dll` it ships is FluentFin MPV's stub, which lets libmpv's DVD libraries load and fails every
  call.
- **With FluentFin's controls, no trickplay previews or Up Next card yet** (Skip and Next are there); mpv's controls
  have both.
- More, with where each lives: `docs/KNOWN-ISSUES.md`.
