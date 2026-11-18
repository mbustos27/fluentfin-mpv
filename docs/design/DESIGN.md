# The design every stream follows

The glass design, made for Seaglass (1.1.x; the app is **Nixie** since 1.2.0, formerly Seaglass and FluentFin MPV): a
glassy, calm, Apple TV-like look on Windows. The evidence and the reasons are in `docs\design\DESIGN-RESEARCH.md` (the
research report, 2026-11-16). This page is the contract. (It was `SEAGLASS.md`; the design system's code names, such as
`NixiePalette`, `NixieTheme` and the `Nixie*` brushes, were `Seaglass*` before the 1.2.0 release.)

## The rule: glass on chrome and on section panels, content opaque

Pass 2 (1.1.0) made it glassier. Glass is now also the **section panel** (`GlassSurfaceStyle`, a ContentControl: frosted
brush, lit top edge, inner glow `GlassGlowBrush`, a soft shadow outside the panel only): on item pages the details (pickers,
genres), cast, "more like this" and seasons; Settings groups; the hero's text block (always dark-themed); the episode list
of a season (opaque episode cards inside one panel); and pills: the search box and the profile button. Still one layer
deep, never a panel per card, never blur over video. The materials are clearer than before (`GlassPanelBrush` tint 0.22,
luminosity 0.34 dark; 0.28 and 0.45 light) with a brighter edge (`GlassStrokeBrush` 55% white at the top) and
`GlassShadowOpacity`. Text on any panel keeps 4.5:1 over the brightest backdrop the ambient dim allows; `OverlayContrastTests`
measures it (worst case: dark panel 6.5:1 primary and 4.9:1 secondary text, strong 15.8 and 10.5; light panel 12.6 and 4.8).

- **Glass** (in-app acrylic, `GlassPanelBrush`) only on chrome: the navigation, the title bar area, flyouts, menus,
  dialogs, pills and buttons that float over content. At most a handful of glass surfaces on a screen, and one layer
  deep: never glass on glass.
- **Content is opaque**: posters, rows, hero artwork, backdrops, text blocks. No acrylic per card.
- **Behind everything**, the ambient backdrop: the current page's artwork, decoded tiny (about 64 px wide) and scaled
  up so it is soft, dimmed by `GlassAmbientDimBrush`, crossfaded when it changes. Pages set it with
  `Services\AmbientBackdrop.Set(uri)` (Home: the hero item's backdrop; item pages: the item's; elsewhere null).
  The artwork shows more (the dim is 45%, not 70%) and, in **Full** only, drifts: a compositor zoom between 1.06 and 1.16
  over 48 s with a sway of 2% of the width (never in Reduced or Solid). The window's own backdrop is Mica (BaseAlt) under
  artwork and in Solid, and **DesktopAcrylic** where there is no artwork (the deep colour then covers it at 55%).
- **Over video, no blur.** Acrylic cannot sample the libmpv SwapChainPanel (it blurs transparent black there).
  The player's controls sit on a gradient scrim and strongly tinted panels.

## Glass modes

`GlassMode`: **Full**, **Reduced**, **Solid**, chosen automatically and overridable in Settings > Appearance.
- Solid when Windows' Transparency effects are off (`UISettings.AdvancedEffectsEnabled`), in high contrast, or when
  the user picks it: `GlassPanelBrush` becomes an opaque surface of the same colour, edges stay. Solid is a designed
  look, not a fallback: it must look finished.
- Reduced: glass, but no tilt, no auto-advance, crossfades only, when `UISettings.AnimationsEnabled` is false.
- Full: everything below.
High contrast always uses `SystemColor*` brushes (`Styles\Glass.xaml`'s HighContrast dictionary).

## Tokens (`ui\FluentFin\Styles\Glass.xaml`)

Use the keys; never hard-code these values on a page.
- Brushes: `GlassBevelBrush` (the inner top-edge stroke), `GlassMilkBrush` (6 % white over the tint), `GlassPanelBrush`, `GlassPanelStrongBrush`, `GlassStrokeBrush` (the light top edge), `GlassHoverOverlayBrush`,
  `GlassPressedOverlayBrush`, `GlassTextPrimaryBrush`, `GlassTextSecondaryBrush`, `GlassImageScrimBrush`,
  `GlassAmbientDimBrush`, `NixieAccentBrush` (sea-glass aqua `#5CCFC1` dark / `#13877B` light).
- Shapes (no capsules: the user does not like oblong ovals): `GlassPanelCornerRadius` 20, `GlassCardCornerRadius` 12,
  `GlassButtonCornerRadius` 12 (the buttons, "pills", the search box, the player's pills), `GlassChipCornerRadius` 8 (info chips,
  the hero's page dots), `GlassCircleCornerRadius` 999 (only for equal-sided round icon buttons), `GlassPillHeight` 44.
- Type (Segoe UI Variable; no Apple fonts or assets): `GlassHeroTitleTextBlockStyle` 48, `GlassPageTitleTextBlockStyle`
  34, `GlassSectionTitleTextBlockStyle` 22, `GlassMetaTextBlockStyle` 13; card titles 14 SemiBold.
- Styles: `GlassPanelStyle` (Border), `GlassPillButtonStyle`, `GlassAccentPillButtonStyle`, `GlassIconButtonStyle`.
- Accent: only on the primary action (Play / Resume) and on progress. Everything else is neutral glass.

## Layout and motion (starting values; tune by eye on screenshots)

- Dark first; light must stay legible. Generous spacing: 48 px page margins at 1920 wide, 24 px between rows.
- Text over artwork always sits on `GlassImageScrimBrush` (or a left-to-right scrim); contrast at least 4.5:1.
- Poster tilt and sheen (Full only, `PosterTilt`): the pointed side dips away by up to 7 degrees, in perspective (distance
  900 px), following the pointer over about 140 ms, with a soft specular radial sheen (a Composition sprite) under the
  pointer. Mouse and pen only: touch and keyboard focus get the lift without tilt.
- Cards: lift on pointer-over and keyboard focus: scale about 1.05 on a spring (damping 0.7, period about 50 ms),
  a deeper shadow; a visible focus ring for the keyboard (never colour alone). No tilt in Reduced or Solid.
- Hero: up to five items, crossfade 500 to 700 ms, auto-advance every 7 s, paused on hover and focus, off in Reduced.
  No autoplaying trailers or theme music.
- Artwork: BlurHash placeholder (Jellyfin's `ImageBlurHashes`) that fades into the image; decode at display size
  (`DecodePixelWidth`).
- Item pages: full-bleed backdrop with bottom and left scrims, the Logo image (text in the title style when there is
  none), a chip row (year, rating, runtime, resolution, HDR, audio), Play as the only accent button.
- Player: controls on a bottom gradient scrim, pill buttons, Skip intro/credits and Next as pills, auto-hide after
  about 3 s without input, menus on `GlassPanelStrongBrush`.

## Keep

Keyboard reach and focus visuals, Narrator names on every card and icon button, high contrast, the harness
(`tools\ui-harness`) working, start-up and scrolling speed (users on Reddit reward speed over looks), and every test.

## Pass 3: real sea glass (all-in, niche on purpose)

The user: "its not very glassy lets get reallllllll themed", "we can get niche". Spec: `docs\reports\SeaGlass theme inspiration.md`
(its ideas only; nothing copied from the SeaGlass repo it studies, which has no licence). Solid and high contrast stay.

- **Per-title palette** (`FluentFin.Core\Theming`): the artwork's BlurHash (`ItemArtwork.BackdropBlurHash`, the hero slide's)
  is decoded to 16x9 cells (`BlurHash`), scored like Material's source colour (360 hue buckets, 30 degree window, chroma floor
  0.02), pulled 50 % toward teal and **clamped to the sea-glass hues** (seafoam -35 to cobalt +75 degrees around 175). Only the
  hue comes from the art. `NixiePalette` then lays a fixed OKLCH ladder (glass tint L 0.22 C 0.045, container +0.03, text
  L 0.96, muted L 0.82, accent L 0.78 C 0.11, rim, a violet rim fringe) and **asserts contrast at runtime** against the worst
  case: the tint at 0.82 over a backdrop the scrim holds to relative luminance 0.38 (`BackdropCeiling`, `GlassAmbientDimBrush`
  alpha 0.62). Under 4.5:1 it lowers the tint L, raises the tint alpha, then the text L. No hash: hue 175.
  Solid is the same hue with opaque surfaces at 7:1. `NixieTheme` recolours the shared dark-theme brushes in place and
  crossfades them in OKLab over 500 ms (at once without motion).
- **Material kit** (`GlassSurfaceStyle` template, so every section panel): bottom to top the frosted brush, the milk, the
  **grain** (`GrainOverlay`: a 256 px zero-mean noise bitmap generated at run time by `Grain`, tiled, 4.5 % in Full, 2 % in
  Reduced, none in Solid; seam-free because every pixel is independent, tested), the inner glow, the **bevel** (1 px inside,
  lit along the top), the **rim** (`GlassStrokeBrush`: white at the top left, the title's aqua at the bottom right), and **two
  shadows** (contact: blur 8, y 2; ambient: blur 28, y 10). The volume indicator uses the same rim and grain.
- **Player**: no blur over video, as before. The volume indicator (wheel, volume keys and mute) is larger (a 34 px icon, 26 px
  text, a 240 px fill) in the same place: it fades and grows in (with motion), the fill glides 160 ms between levels, it fades
  out 1.5 s after the last change, muted shows "Muted" and an empty bar, and Narrator is told the new volume
  (`RaiseNotificationEvent`). The subtitles menu is a flyout: the tracks, then **Text size** (50 to 200 %, steps of 10, default
  100, live through `set_subtitle_size`, remembered in the local settings `SubtitleSize` and sent when the controls take over;
  the backend keeps it as its own `subtitle_size`, so mpv's controls use the same value).
## Pass 3, part 2 (1.1.2): every glass feature

- **The rail is a floating slab**: the pane is transparent and holds a near-opaque glass slab (`GlassPanelStrongBrush`, radius 20,
  grain, glow, rim) 8 px in from the window edge; the rail is 84 px wide (`RailNavigationViewOpenPaneLength`).
- **Poster frames and title plates**: a card's artwork (opaque, sharp) has a lit rim (`GlassStrokeBrush`, radius 12); under it the
  title sits on a plate (`GlassPlateBrush`: the title's tint at 60 %, opaque in Solid, a 10 px radius, the rim). No acrylic per card.
- **Pointer highlight** (`PanelSheen`, in the `GlassSurfaceStyle` template): a soft radial highlight follows the mouse or pen over
  a glass panel, clipped to the panel's corners. Full mode with motion only; none for touch, Reduced, Solid.
- **Page transitions** (`PageTransition`, from `NavigationService.OnNavigated`): Full: the page fades in, rises 14 px and grows from
  98 % in 350 ms on the curve (0.05, 0.9, 0.1, 1.0); Reduced: a 150 ms fade; Solid, animations off and the player page: none.
- **No BlurHash**: `ArtworkSampler` decodes the artwork down to 16x9 pixels and `NixiePalette.HueFromCells` scores it as it
  scores a hash (the default hue until it is read).
- **Light theme**: `NixiePalette.Build(hue, solid, light: true)`: a pale tint (L 0.95), dark text (L 0.22), a deeper accent (L 0.50)
  with white text; checked at 4.5:1 over a backdrop at least 0.50 bright (7:1 in Solid). `NixieTheme` recolours both theme
  dictionaries. `NIXIE_THEME=Light|Dark` (or the older `SEAGLASS_THEME`) forces the theme (harness screenshots).
- **Accessibility**: the grain, the panel highlight and the window's backdrop images are `AccessibilityView` Raw: outside the control
  view Narrator and the harness's scan use. The one image the scan still names is the item page's logo, which is the page heading.
- **Cost** (`tools/ui-harness/offscreen/then-gpu.ps1`, Home scrolled to the bottom and back 8 times, off-screen): the app's CPU
  was 0.31 % of all cores on average (max 1.28) in Full against 0.02 % (max 0.09) in Solid. The "GPU Engine" counters read 0 in both:
  a private desktop is not composed by the GPU, so the GPU cost on a real desktop was NOT measured. Measure it on a screen before
  quoting a number.
- **Still open**: an intensity slider; the GPU numbers above.

## Pass 4: cleaner glass (supersedes pass 3 where they differ)

The user, on his own library: "its quite ugly" (olive-brown panels, a pale green Resume, recognisable blurred faces in the backdrop,
boxes around every poster title). So:

- **One fixed palette for all chrome**: neutral dark glass (a little chroma, hue 175; pale glass in the light theme) and the accent
  always sea-glass aqua (`#5CCFC1` dark, `#13877B` light; the light accent fill stays `#0F766B` so white text reaches 4.5:1).
  `NixieTheme` applies it once (and for Solid); a title never recolours a panel or a button.
- **The title's hue is a faint tint on the backdrop only** (`TitleTint`, at most 10 %, a clean mid colour at that hue).
- **The ambient backdrop is a smooth colour field**: the item's BlurHash drawn as a 32x18 picture (a handful of cosine terms: no shapes,
  no faces); with no hash, the artwork decoded 10 px wide. The dim is 78 % (`BackdropCeiling` 0.22).
- **Cards**: opaque artwork with 12 px corners and one very subtle light edge; the title and subtitle are plain text underneath. No plate,
  no border around the text, no second rim.
- **Glass panels**: one thin light edge, brightest along the top (`GlassStrokeBrush`); no bevel, no violet fringe; the soft shadow stays
  (contact 22 %, ambient 30 %). The grain is 1.5 % in Full (0.8 % Reduced, none in Solid): only noticed on a large flat panel.
- **Kept**: the floating rail, the hero's text panel (now neutral glass), page transitions, the pointer highlight, Solid, contrast.
- **Judging on real art**: the fake server can serve photographs: set `FAKESERVER_PHOTO_DIR=<folder of .jpg>` (Windows' own wallpapers
  were used: scenery, not faces); its BlurHashes are still those of the colour blocks.

## 1.1.3: one overlay recipe (supersedes the overlay lines above)

Menus, context menus, the profile flyout, tooltips and every ContentDialog: one blur under a strong tint, the light edge
(`GlassStrokeBrush`), **`GlassOverlayCornerRadius` 12**, the glass text colour. Never acrylic on acrylic. **The player's menus
too**: `MpvPlayerControls.xaml`'s `PlayerMenuPresenter` (the audio menu) and `PlayerFlyoutPresenter` (volume, subtitles) are
`BasedOn` the keyed styles below and set only their padding (`GlassOverlayTests.OverlayStyles`).
- **In the window** (Flyout, ContentDialog): `GlassPanelStrongBrush` (in-app acrylic). A dialog is ONE surface: its title, content and
  buttons are transparent on it, with a `GlassDividerBrush` hairline above the buttons. Dialogs made in code get the same template
  (the implicit `ContentDialog` style in `TextBlock.xaml`), capped at WinUI's 548 px (`GlassDialog.PanelMaxWidth`; the dialog
  pages keep 1000 px); a dialog's title wraps.
- **A window of its own** (MenuFlyout, ToolTip): in-app acrylic has nothing to blur there, and a menu already has Windows' acrylic
  under it (WinUI's default `SystemBackdrop`). So their background is the flat `GlassOverlayBrush`: the palette's container colour at
  90 % (`NixieTheme.OverlayColor`), opaque in Solid (`GlassOverlaySolidBrush`, swapped by `GlassTheme` like the panels).
- Keyed styles for other files: `GlassFlyoutPresenterStyle`, `GlassMenuFlyoutPresenterStyle`; a button on a glass panel uses
  `GlassInsetPillButtonStyle` (the flat `GlassInsetFillBrush` with `GlassInsetTextBrush`: ButtonFace with ButtonText in high
  contrast), not a second acrylic.
- **The accent on artwork** (a progress bar on a poster's dark track, a watched badge) is `NixieAccentOnImageBrush`: the
  light aqua in both themes (the light theme's deeper aqua has 1.6:1 on the track over a bright poster). `BaseItemDtoCard`'s and
  `EpisodeRow`'s progress bars use it on a `Black65O` track **as tall as the bar**: WinUI's ProgressBar draws its Background as a
  1 px line (`ProgressBarTrackHeight`), so each bar sets that resource to its own height; without it the bar sits on the poster
  itself, and the light aqua on a bright poster is about 1.6:1. `EpisodeRow`'s watched mark is the same aqua with its check in
  `NixieOnAccentOnImageBrush` (dark in both themes; HighlightText on Highlight in high contrast). Tests:
  `OverlayContrastTests` (`ProgressOnArtwork_*`, `EpisodeWatchedMark_*`). Measured in light-theme screenshots over bright
  posters (#F0F0EA): before, the card's #13877B bar sat on the poster (3.8:1 against it, a 1 px track line) and the episode
  row's #0C5F56 the same way (6.5:1 there, about 2.8:1 on a black poster); now the bar has 4.0:1 on its track and the track
  6.6:1 on the poster, whatever the poster. The episode's watched mark: its check 7.7:1 on the aqua (4.2:1 before, a light
  check on #13877B), the aqua itself about 1.8:1 against a bright thumbnail (4.2:1 before).
- `SEAGLASS_THEME` now sets the application's theme, so the harness's light screenshots show light dialogs, menus and tooltips too.
- Measured contrast (screenshots' pixels): `shots\contrast.md` of the 1.1.3 glass stream.

## 1.2.0 (Nixie): dusk, really glassy by default (supersedes the palette, accent and no-artwork lines above)

The user: "really themed", "niche is fine", "all the glass features", glassy **by default** (GlassMode Full; Reduced and Solid
keep their purpose), "no oblong ovals", and not ugly (pass 4's lesson holds: no colour on the chrome per title, no heavy grain,
no thick rims). Then: "be inspired by these colours" (a flat desert-sunset illustration), retro-futurist desert motifs
(atomic-age Americana, Googie starbursts, neon motel signs, drive-ins, a faint CRT glow; nothing from Fallout), and the name
**Nixie** (the glowing number tube). The theme guide is `docs\reports\Seaglass rename and retro desert theme.md`; the feature
list with each feature's status is the glass stream's `shots\glass-features.md`.

- **Palette** (`NixiePalette`, Glass.xaml's Dark and Light dictionaries, App.xaml's accent ramps). Dark: the glass is a calm
  deep plum from the sunset's maroon end (OKLCH hue 8, tint L 0.22 C 0.04; deep `#170508`), text a warm white. Light ("noon"):
  pale peach glass (hue 26, L 0.95) with deep maroon text. **Accent**: hot pink `#F96985` with a dark label `#24060F` (6.7:1;
  white on hot pink is 2.8:1 and is never used), crimson `#BC1E47` with white (6.2:1) in light; on artwork the light pink
  `#FF9EB1` on its dark track. Hot pink means "you can act on this" (Play, progress, focus); decoration is coral and pale sun.
  No aqua is left. A title's hue (at most 10 %, on the backdrop only) is clamped to the sunset's hues, 343 to 43.
- **The dusk scene** (`FluentFin.Core.Theming.DuskScene`, drawn by `AmbientBackdropView`; vector shapes and gradients, no image
  files): a banded sky (maroon, crimson, rose, coral, hot pink, peach; light: neon-core white to coral), a striped sun with three
  thin rings, six four-point sparkles with curved sides (none where a page title can fall), rolling hills and buttes with curved
  slopes, one saguaro with rounded elbows, faint scanlines at dusk only (every 3 DIP, 3.5 %) and a vignette; the bands meet over
  a short soft edge (the art pass: `shots\art\README.md`). **In full** outside the shell (the server page and sign-in, even over
  the server's splash image) and behind an empty state's `SceneCard` (which holds the scene while it is loaded and puts its text
  on the strong glass); **a strip** of rolling hills (28 % / 20 %, no butte) along the bottom inside the shell
  where there is no artwork; **nothing** over artwork. Text straight on the strip keeps 4.5:1 (no strip colour above luminance
  0.08 in dark, none below 0.50 in light; `DuskSceneTests`). Motion (Full, window active, the full scene only): the stars
  glint, the sun's rings breathe, the hills sway for 20 s after the scene appears or the window comes back, then everything
  eases still. The strip never moves: any continuous motion makes the compositor redraw the whole window, acrylic included,
  every frame (measured: the strip's endless sway cost about 0.6 of a core on an idle page, against about 0.05 in 1.1.3).
  Reduced is still; Solid keeps the shapes but drops the scanlines; high contrast shows none of it.
  Desktop acrylic is no longer used: the scene covers the window wherever there is no artwork.
- **Glass**: every glass surface (and the rail) has a body (`GlassVolume`: a frosted rim, clear in the middle, and a bottom shade;
  none in Solid), the rail is lifted by the panels' two shadows, and the pointer lights the rim near it (`PanelSheen`'s edge
  light). In the background (`GlassActivity`) the light edge and glow dim to 45 %, the backdrop stops and the pointer light goes out.
- **Neon**: keyboard focus on a glass pill glows (hot pink close in, the nixie orange `GlassNeonGlowColor` `#FF7A50` further out,
  made on first focus) besides WinUI's two focus rings: in dark the neon core's near-white `#FFF4EE` around the deep colour (a
  pink ring vanished on the hot-pink Play), in light crimson around white. The accent pill has a soft resting glow. Every glow's
  opacity is `GlassShadowOpacity` or `GlassContactShadowOpacity` (0 in high contrast). Never give a glow a key of its own at the
  top of Glass.xaml as a fallback: a top-level key wins over the theme dictionaries in every theme (that hid every glow once;
  `GlassDuskTests` now forbids a key both at the top and in a theme dictionary).
- **One glass layer per surface** (the 1.1.3 layout audit's "boxes in boxes"): panels have 12 px corners; `GlassSectionStyle` is a
  section with the panel's spacing and nothing behind it, for shelves on item pages and Settings groups; keep `GlassSurfaceStyle`
  for at most one form-like block a page and never nest it. The glassiness comes from the chrome, the backdrop and the overlays.
- **Buttons** (`docs\reports\Nixie button review.md`, section 3; keys in Glass.xaml): six roles in two sizes, Large 44 on
  media surfaces and Compact 32 in forms, one radius of 8, no pills. `NixiePrimaryButtonStyle` / `...Compact...` (the accent,
  one per view; the Large one glows softly at rest), `NixieButtonStyle` / `NixieCompactButtonStyle` (glass: the flat inset fill
  and the lit edge), `NixieSubtleButtonStyle`, `NixieIconButtonStyle` (44 circle), `NixieIconToggleButtonStyle` (on: the accent
  glyph and a 1.5 px rim, never a filled disc), `NixieCompactIconButtonStyle` (32 rounded square), `NixieDestructiveButtonStyle`
  / `...Compact...` (the danger ink #FF8A80 / #B3261E, never filled, never glowing), `NixieLinkButtonStyle` (neutral,
  underlined), `NixieTabButtonStyle` (a neon underline; SelectionStates), and `NixieDialogStyle` / `NixieDestructiveDialogStyle`
  (Cancel is the default there). Every state: hover and pressed are overlays (the weight never changes), focus is WinUI's two
  rings 3 px outside plus the glow (not on Destructive or Link), disabled is a neutral fill and text, never a fade. The 1.1.x
  keys (`GlassPillButtonStyle`, `GlassAccentPillButtonStyle`, `GlassInsetPillButtonStyle`, `GlassIconButtonStyle`,
  `ItemGlassToggleButtonStyle`) are aliases of these. Every Nixie* colour has a high-contrast system colour.
- **The top bar** (the side rail is gone, account stream): `GlassTitleBarBrush` (acrylic; `GlassTitleBarSolidBrush` in Solid),
  `GlassTitleBarStrokeBrush` for its bottom hairline, and the selected tab's `NeonUnderline` (a stroke-stack tube: hot-pink halo
  and glow around a near-white core; light: a crimson line; high contrast: the system highlight) or `GlassTabUnderlineBrush`.
  An in-app theme goes through `GlassTheme.SetAppTheme`, which themes the caption buttons too. No layer sits between the backdrop
  and the shell's pages (`NavigationViewContentBackground` is transparent; WinUI's default greyed the scene into mud).
- **The wordmark** (`AppWordmark`): the name as a lit tube, in Monoton (SIL OFL 1.1, `Assets\Fonts`, shipped unmodified beside
  its licence) from 36 px up (64 by default: Monoton's lines render unevenly at 56), letter-spaced, with two restrained masked
  glows (one faint crimson glow at noon) and one flicker on the first showing in Full; the plain display
  font in the tube's colour when small (the title bar). Body text stays Segoe UI Variable; no display font on server data.
