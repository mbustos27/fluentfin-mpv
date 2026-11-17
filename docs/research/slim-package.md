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

## Step 5: libmpv proposal (research only; nothing switched)
Today: `app\libmpv` is 141 files, 184 MB (MSYS2's mpv 0.42.0 and each dependency DLL, plus our libdvdcss stub).

**Option A: shinchiro's `mpv-dev-x86_64` build** (github.com/shinchiro/mpv-winbuild-cmake, release 20261111, `mpv-dev-x86_64-20261111-git-bcde6b93cc.7z`, 30 MB). I downloaded it and read it: one `libmpv-2.dll` of 116 MB (plus headers and an import library), statically linked. Its import table names only Windows system DLLs (kernel32, user32, d3d/dwrite, opengl32, vulkan-1, winmm, ws2_32 and the api-ms-win-crt set). So `app\libmpv` would go from 141 files to 1, and 184 MB to 116 MB (zip about 70 MB smaller). Vulkan and OpenGL still come from the graphics driver, as now.
- Costs: libdvdcss would no longer need a stub (check whether this build links it; our rule is no real libdvdcss). It is a rolling nightly build of mpv git, not a tagged 0.42.0; the user would pin one dated release. `libmpv_check.py`, `VENDORED.md` and the BUILD-MANIFEST logic assume the MSYS2 layout and need adapting (single DLL, no stub).
- GPL source: the build is GPL; shipping the binary needs the corresponding source of mpv and of every statically linked library. The cmake repo pins each package to a git revision (`packages/*.cmake`) but does not publish source tarballs the way MSYS2's `.src.tar.zst` do, so `sources.py` would have to fetch each pinned revision (about 80 packages) from its upstream, or we build it ourselves from those pins and archive the checkouts. More work than today's per-DLL MSYS2 source packages.

**Option B: our own static build** with the same cmake project: identical result, we control the feature set (could drop unused codecs, Vulkan shader compilers, Lua/JS extras), and the source archive is the checkout we built from. Needs an MSYS2/Linux cross toolchain and hours per build; the highest saving and the most upkeep.

**Option C: prune MSYS2 dependencies**: drop optional DLLs (for example the ggml and whisper family, avdevice, rarely used codecs libraries). Each DLL must be unimportable by the rest (`libmpv_check.py` already proves that), so it only works for libraries nothing imports; likely a few files, not a hundred.

Recommendation: A after checking its libdvdcss status and licence list, keeping the per-package source obligation as the open work; C is the cheap fallback.

## Loading FlyleafLib lazily (so its 19 files could go)
What names FlyleafLib at start-up, found by search of `ui\FluentFin`:
1. `Controls\TransportControls.xaml` line 27: `<cc:SwitchConverter TargetType="fmp:Status">` with `xmlns:fmp="using:FlyleafLib.MediaPlayer"`. This is the one the XAML type table trips on (`InitTypeTables` resolves every type named in a page's XAML). Fix: a converter on an own enum or a plain bool/string, in a place the release can compile without Flyleaf.
2. `Views\VideoPlayerPage.xaml` (`flyleaf:`, `lvs:` xmlns) and `Views\MediaSegmentsEditorPage.xaml` (`flyleaf:`) declare the namespaces but no element uses them (grep finds no `flyleaf:`/`lvs:` use); drop the declarations.
3. Code: `Controls\MediaPlayerHost.xaml.cs` (`FlyleafHost`, `CreateFlyleaf`), `MediaPlayers\FlyleafMediaPlayerController.cs`, `FlyleafEngine.cs`, `VlcMediaPlayerController.cs`. These load their assemblies only when the method using them is JIT-compiled, so keep each use inside its own `[MethodImpl(NoInlining)]` method reached only when the player type is Flyleaf or VLC. Cleaner: compile them out of the release (`#if FLUENTFIN_PLAYERS` with the property off in `FluentFinMpvRelease`), which also removes the PackageReferences for the release.
These files belong to the player and shell streams, so this needs their agreement. The gain is 19 files (Flyleaf, Vortice, SharpDX, SharpGen, LibVLCSharp), plus the WPF assemblies WindowsBase and PresentationCore/Framework only if DeftSharp's keyboard hook were replaced too.

## Step 2: PublishTrimmed, TrimMode=partial (tried, reverted)
Trial results on the release publish: app\ top level 487 files down to 398, zip 651 files (from 742), 198 MB (from 235 MB). Problems met, in order:
1. The path gate failed: ILLink rewrote six NuGet assemblies (WinUI.TableView, SkiaSharp, HarfBuzzSharp, CommunityToolkit.Common, Microsoft.Xaml.Interactivity, Vortice.Mathematics) with a debug-directory path into the build's obj folder. `TrimmerRemoveSymbols=true` fixed it.
2. The app failed to start: "Reflection-based serialization has been disabled" (PublishTrimmed turns `JsonSerializerIsReflectionEnabledByDefault` off). Setting it to true (with System.Text.Json rooted) got the window up.
3. Still broken: 28 of 110 verify checks failed. Playing an episode did nothing (no play, no error, nothing logged: no in-process mpv, the backend never got `play_file`; the SelfTest and Kill runs also failed). The build had 844 trim warnings (IL2026 and IL2091 from ReactiveUI's WhenAnyValue, the Jellyfin SDK, `ServiceCollectionExtensions` and the generated XamlTypeInfo), so reflection users are being trimmed. Finding which framework members the play path needs, and rooting them, is open-ended and each guess costs a full build and verify, so it was reverted rather than risk a start-up-clean package that cannot play.
To retry: start from `PublishTrimmed`, `TrimMode=partial`, `TrimmerRemoveSymbols=true`, `JsonSerializerIsReflectionEnabledByDefault=true`, then run the Default run alone with a debugger or `DOTNET_` event tracing to find the swallowed exception, and root those assemblies with `TrimmerRootAssembly`.

## Step 4: backend (PyInstaller)
`tools\release\fluentfin-mpv-backend.spec` now drops PIL's Tk bridge (`_imagingtk`) and `_wmi`: two files of 97 in `_internal`. The rest is needed (the stdlib extension modules that the shim's imports reach: `ssl`, `sqlite3`, `xml` for the epub reader, `lzma`/`zipfile` in `archive_errors.py`, Pillow's webp and avif for server artwork). The gain is small: the backend is already minimal.
