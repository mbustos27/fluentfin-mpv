# Seaglass: the design every stream follows

FluentFin MPV becomes **Seaglass**: the same app with a glassy, calm, Apple TV-like look on Windows. The evidence and
the reasons are in `docs\design\SEAGLASS-RESEARCH.md` (the research report, 2026-11-16). This page is the contract.

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
  `GlassAmbientDimBrush`, `SeaglassAccentBrush` (sea-glass aqua `#5CCFC1` dark / `#13877B` light).
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

The user: "its not very glassy lets get reallllllll themed", "we can get niche". Spec: `docseports\SeaGlass theme inspiration.md`
(its ideas only; nothing copied from the SeaGlass repo it studies, which has no licence). Solid and high contrast stay.

- **Per-title palette** (`FluentFin.Core\Theming`): the artwork's BlurHash (`ItemArtwork.BackdropBlurHash`, the hero slide's)
  is decoded to 16x9 cells (`BlurHash`), scored like Material's source colour (360 hue buckets, 30 degree window, chroma floor
  0.02), pulled 50 % toward teal and **clamped to the sea-glass hues** (seafoam -35 to cobalt +75 degrees around 175). Only the
  hue comes from the art. `SeaglassPalette` then lays a fixed OKLCH ladder (glass tint L 0.22 C 0.045, container +0.03, text
  L 0.96, muted L 0.82, accent L 0.78 C 0.11, rim, a violet rim fringe) and **asserts contrast at runtime** against the worst
  case: the tint at 0.82 over a backdrop the scrim holds to relative luminance 0.38 (`BackdropCeiling`, `GlassAmbientDimBrush`
  alpha 0.62). Under 4.5:1 it lowers the tint L, raises the tint alpha, then the text L. No hash: hue 175.
  Solid is the same hue with opaque surfaces at 7:1. `SeaglassTheme` recolours the shared dark-theme brushes in place and
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
- **No BlurHash**: `ArtworkSampler` decodes the artwork down to 16x9 pixels and `SeaglassPalette.HueFromCells` scores it as it
  scores a hash (the default hue until it is read).
- **Light theme**: `SeaglassPalette.Build(hue, solid, light: true)`: a pale tint (L 0.95), dark text (L 0.22), a deeper accent (L 0.50)
  with white text; checked at 4.5:1 over a backdrop at least 0.50 bright (7:1 in Solid). `SeaglassTheme` recolours both theme
  dictionaries. `SEAGLASS_THEME=Light|Dark` forces the theme (harness screenshots).
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
  `SeaglassTheme` applies it once (and for Solid); a title never recolours a panel or a button.
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
