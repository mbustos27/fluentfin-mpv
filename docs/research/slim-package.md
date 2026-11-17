# Slim package (Seaglass stream)

Measured on the 1.0.1 package. Start-up and first-play times are the harness's `[perf]` lines (Default run, off-screen).

| Step | Files in zip | app\ top level | Zip | Window shown | First play position |
|---|---|---|---|---|---|
| Before (1.0.1) | 789 | 534 | 247 MB | 0.8 to 0.9 s | 447 ms |
| 1: drop the desktop runtime's Windows Forms, Visual Basic, WPF themes, printing, designers | 742 | 506 | 235 MB | 0.85 to 1.3 s (noise) | 311 ms |

Verify: 113 original checks pass, plus the new slim check (file-count ceiling 510 at the top of app\, no Windows Forms or Visual Basic assemblies).

## Step 1 findings
- Flyleaf, LibVLC, Vortice, SharpDX (19 files) CANNOT be dropped without code changes: with `ExcludeAssets=runtime` the app never shows a window, because the generated XAML type table (`XamlTypeInfoProvider.InitTypeTables`) loads `FlyleafLib` at start-up (VideoPlayerPage.xaml and TransportControls.xaml name its types). Dropping them needs those XAML pieces split out of the release, which the player stream owns.
- WindowsBase, PresentationCore and PresentationFramework must stay: DeftSharp's keyboard hook uses WPF's `System.Windows.Input.Key`.
- Left out (release only): `FluentFinSlimRelease` target in `FluentFin.csproj`, removing files from `RuntimePackAsset` and `ReferenceCopyLocalPaths` (so also from `FluentFin.deps.json`).

## Not done (usage ran out)
Steps 2 to 5: trimming (`PublishTrimmed`, `TrimMode=partial`), single-file, PyInstaller excludes, and the libmpv proposal. Candidates seen: 342 files of app\ were never loaded in the harness runs (the Windows App SDK AI and projection assemblies, `Microsoft.ML.OnnxRuntime`, `onnxruntime.dll`, `DirectML.dll`, Azure/MSAL identity assemblies pulled in by Kiota); each needs a deps.json-consistent removal like step 1 and a run of every page.
