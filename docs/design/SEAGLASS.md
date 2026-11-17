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
- **Not done yet** (left for later passes): the navigation rail as a floating slab, glass frames and title plates on posters,
  pointer-following sheen on panels, page-transition depth, a 64 px thumbnail fallback for items without a BlurHash,
  an intensity slider, the Light theme (the palette recolours the dark theme only), and GPU timings on an integrated GPU
  (step 0 measured the grain only: seams and mean; no frame-cost numbers were taken).
