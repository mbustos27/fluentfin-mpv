# Nixie's brand files

- `banner-dark.svg`: the README banner at dusk, for GitHub's dark mode (1280 × 320).
- `banner-light.svg`: the same banner at noon, for the light mode.
- `social-preview.svg`: the repository's link preview (1280 × 640), dusk.
- `social-preview.png`: that preview rendered, for GitHub's Settings › General › Social preview (it takes PNG, JPG
  or GIF, not SVG).

All of it is drawn by hand from the app's own shapes and colours, with no stock art:

- **The dusk scene** follows `ui/FluentFin.Core/Theming/DuskScene.cs`: the sky's colours, the striped sun with its
  three rings, the curved four-point sparkles, and the hills and saguaro from
  `ui/FluentFin/Controls/AmbientBackdropView.xaml`. The app paints the sky in flat bands; here it is one smooth
  gradient, because at link-card size the bands read as banding. The social preview has no scanlines for the same
  reason: three-pixel lines shimmer when a card scales the image down.
- **The wordmark** is "NIXIE" in [Monoton](https://fonts.google.com/specimen/Monoton) (SIL Open Font License 1.1;
  the font and its licence ship in `ui/FluentFin/Assets/Fonts`), turned into outlines and letter-spaced 0.06 em as
  `ui/FluentFin/Controls/AppWordmark.cs` sets it. Lit like the sign-in card's: the tube `#FFC4A6` with a hot-pink
  glow `#F96985` and a wide nixie-orange halo `#FF7A50` at dusk, crimson `#BC1E47` with one faint crimson glow at
  noon.
- **The glass card** uses the panel colours in `ui/FluentFin/Styles/Glass.xaml`: the plum tint `#34181D` (noon:
  `#FFF3F1`), a lit top edge, and the text colours `#F8EFF0` / `#D3BFC1` (noon: `#2D0F16` / `#633A41`).
- **The text** is real text in Segoe UI Variable, with system fonts after it on other systems. The banners say "A
  Windows Jellyfin client / with mpv playback and four themes"; the social preview only "A Windows Jellyfin client",
  which still reads when a chat app shows the card about 400 px wide. Check a new version at that size before
  uploading it.

## The palette

- **Sky, top to horizon:** dusk `#500723` `#BC1E47` `#E6385F` `#EC6A79` `#F96985` `#F7AA9C`; noon `#FFF4EE`
  `#F9C9BF` `#F5A8A5` `#F7AA9C` `#F38C7D` `#EC6A79`.
- **Sun, top to bottom:** dusk `#F5A8A5` to `#F96985`; noon `#FFF4EE` to `#EC6A79`.
- **Hills, far to near:** dusk `#C73153` `#BC1E47` `#500723`; noon `#F0849A` `#BC1E47` `#500723`.
- **Accent:** dusk `#F96985`; noon `#BC1E47`.

## Changing them

The SVGs are plain text: edit the tagline in its `<text>` elements, and keep the dark and light banners in step. The
wordmark's letters are Monoton's own outlines; to set other letters, export them from the font (WPF's
`GlyphTypeface.GetGlyphOutline` gives the path data).

To render `social-preview.png` again, use Edge's headless mode, off-screen inside the repository's job-object
runner, from the repository folder (msedge.exe is in `C:\Program Files (x86)\Microsoft\EdgeCore\<version>` when the
Edge browser itself is not installed):

```powershell
backend\.venv\Scripts\python.exe -I tools\backend-bench\hidden_run.py --timeout 60 -- `
  <path to msedge.exe> --headless --disable-gpu --no-first-run --hide-scrollbars --force-device-scale-factor=1 `
  --user-data-dir=<a scratch folder> --window-size=1280,640 --screenshot=<scratch>\social-preview.png `
  file:///<repository>/docs/brand/social-preview.svg
```

Then save it over `docs/brand/social-preview.png` (keep it under 1 MB; it is about 290 KB) and upload it again on
GitHub: **Settings › General › Social preview › Edit › Upload an image**.
