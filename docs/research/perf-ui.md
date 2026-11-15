# FluentFin UI (C#) performance scout

Date: 2026-11-13. Scope: the C# half (`ui\`) of FluentFin MPV. All work was done on a pristine
upstream copy identical to the repo's import commit 4dc492e for `ui/`
(`<scratchpad>\upstream\FluentFin`). Nothing in the repo's `ui\` was built, run or edited; the
only file written in the repo is this report.
Paths below are written `FluentFin\...`; in the repo they are `ui\FluentFin\...` (same for
`FluentFin.Core\...` -> `ui\FluentFin.Core\...`). Line numbers are from 4dc492e.

`<scratchpad>` = `C:\Users\moise\AppData\Local\Temp\claude\4cb39b71e0149222\e4cf1356-33e7-412e-b62a-03eff8dc99f0\scratchpad`.
All logs and harnesses are under `<scratchpad>\perf-ui\` (temporary folder; copy what you want to keep).

> The repo's `ui\` is being changed right now (MPV player path in `MediaPlayerHost`,
> `VideoPlayerViewModel`, settings, and a backend host service). Every opportunity that lands in
> those files, or in `App.xaml.cs` where the host service is registered, is marked
> **RE-CHECK AFTER INTEGRATION**.

## 1. Baseline

### Build (pristine copy, Debug x64)

| Item | Value |
|---|---|
| Command | `dotnet build FluentFin.sln -c Debug -p:Platform=x64 -clp:Summary` (from the copy's root) |
| SDK | .NET SDK 10.0.404 |
| Result | Build succeeded, 0 errors |
| Wall time | 56.3 s (clean: no obj/bin before; packages already in the global NuGet cache, restore < 1 s) |
| Warnings | 6 reported = 4 distinct: CS8603 x2 in `FluentFin\Services\ContentDialogService.cs` lines 26 and 43 (each reported twice by the two XAML compile passes); CS0618 `Icon` obsolete in generated `XamlTypeInfo.g.cs`; MSIX `SigningCertificateNoCertSpecified` |
| Log | `<scratchpad>\perf-ui\build.log` |

`FluentFin.sln` contains only the 4 product projects; the two test projects are not in it, so
`dotnet test FluentFin.sln` runs nothing. Run them by project path (below).

### Tests (pristine copy)

| Project | Command | Total | Passed | Failed | Skipped | Test time | Wall time |
|---|---|---|---|---|---|---|---|
| FluentFin.Core.Tests | `dotnet test FluentFin.Core.Tests\FluentFin.Core.Tests.csproj -c Debug --no-restore` | 42 | 42 | 0 | 0 | 1.76 s | 4.0 s |
| FluentFin.Tests | `dotnet test FluentFin.Tests\FluentFin.Tests.csproj -c Debug -p:Platform=x64 --no-restore` | 2 | 2 | 0 | 0 | 3.26 s | 42.5 s (rebuilds the app for the win-x64 RID) |
| **Total** | | **44** | **44** | **0** | **0** | | |

Pre-existing failures: none. The app-test build adds 2 more warnings (CS0436 `NullEvents`
defined twice by the ReactiveMarbles source generator in the test project).

Restore note (environment): this PC's user `NuGet.Config` has an empty `<packageSources>`, and
the test-only packages (xunit 2.9.3, xunit.runner.visualstudio 3.0.2, Moq 4.21.0,
Microsoft.NET.Test.Sdk 18.11.0, coverlet.collector 10.1.0) were not in the global cache, so the
first `dotnet test` failed with NU1100 (kept as `baseline-tests.attempt1-no-sources.log`).
Restoring once with `dotnet restore <proj> [-p:Platform=x64] --source https://api.nuget.org/v3/index.json`
(4.5 s + 1.2 s) and then `dotnet test --no-restore` works. They are in the cache now. No config
file was changed.

Logs: `baseline-tests.log`, `core-tests.trx`, `app-tests.trx` in `<scratchpad>\perf-ui\`.

What the 44 tests cover:

- `OverlayContrastTests` (26 cases): reads XAML files and checks scrims/contrast. No runtime UI.
- `ApiKeyExtensionsTests` (12): `WithApiKey` / `MaskApiKey`.
- `LocalSettingsServiceTests` (4): settings persistence.
- `LibraryViewModelTests` (1): sort + client-side paging of the library grid (Moq `IJellyfinClient`).
- `VideoPlayerViewModelSubtitleTests` (2, app project): subtitle open timing and delivery URL
  (Moq `IJellyfinClient` + `IMediaPlayerController`).

Nothing tests `JellyfinClient` itself (request shapes, request counts), any page view model
except the library, the playlist builder, activation/start-up, image URLs, or player lifetime.

### Package size (Release, self-contained, win-x64, as `publish.ps1` makes it)

Command: `dotnet publish FluentFin\FluentFin.csproj --self-contained -c Release -r win-x64 -p:Platform=x64 -o <scratchpad>\perf-ui\publish-x64 /property:BuildVersion=1.0` - 60.7 s, 0 errors.

**568.6 MB in 1,414 files.** Biggest parts:

| Part | Size | Removable without breaking a player? |
|---|---|---|
| `libvlc\win-x64` (422 files; plugins 101.7 MB) | 106.4 MB | Partly: ~18.3 MB of plugins not used for playback (see O9) |
| `FFMpeg\` (avcodec 41.5, avfilter 19.5, avformat 18.4, swscale 2.2, avutil 1.1, swresample 0.7, avdevice 0.1) | 83.5 MB | No (Flyleaf). avfilter/avdevice maybe, speculative (O9) |
| WPF + WinForms runtime (PresentationFramework, System.Windows.Forms, ... 62 files) | ~76.7 MB | **No**: FlyleafLib 3.11.13 has a `frameworkReference` to `Microsoft.WindowsDesktop.App`, DeftSharp.Windows.Input to its WPF part |
| WinAppSDK AI/ML/Search/Widgets components (onnxruntime 20.7, DirectML 17.8, Windows.AI.* 9.9, Search 3.5, Widgets 2.5) | ~54 MB | Probably yes, nothing in FluentFin uses them (O8) |
| Satellite resource folders (130 culture folders) | 21.4 MB | Yes for English UI, but changes Humanizer's language for non-English Windows (O9) |
| PDBs (4 files) | 1.7 MB | Yes, but not worth the bother |

The Debug output (614 MB, no RID) is not representative: it carries native assets for every RID
(`runtimes\` 275 MB of linux-*, win-arm64, win-arm64ec, win-x86, win-x64).

ReadyToRun variant (`-p:PublishReadyToRun=true`, 105.9 s): **647.8 MB (+79.2 MB, +14%)**.
Its start-up time was not measured: see "Start-up" below for why.

### Start-up (measured)

`launch.ps1` starts `publish-x64\FluentFin.exe`, waits for a visible main window, samples the
process 6 s later, then closes it. The child got `HTTP(S)_PROXY=http://127.0.0.1:9` (a dead
proxy) so no network request could leave the machine (the unpackaged build checks GitHub for
updates at start, see "Other observations"). `%LOCALAPPDATA%\FluentFin` did not exist, so the app
opened the server picker; nothing signed in anywhere. I removed the folder my runs created.

| Run | First visible window | Working set | Private | Threads | Modules | FFmpeg DLLs loaded |
|---|---|---|---|---|---|---|
| 1 (first launch after publish; Defender scans new files) | 5,017 ms | 269 MB | 206 MB | 68 | 292 | 4 |
| 2-5 (warm) | 911-943 ms, median ~925 ms | 226-236 MB | 172-175 MB | 68-69 | 292 | 4 |

So the FFmpeg libraries are loaded at every launch even though nothing plays (O5).

The R2R launch and a second round were skipped: at 23:55 another agent started the repo's build
(`ui\...\FluentFin.exe`, PID 28988), which uses the same `%LOCALAPPDATA%\FluentFin` folder and the
same process name, so a second FluentFin process could have disturbed its settings or its window
automation. To measure later (when no FluentFin runs):
`powershell -File <scratchpad>\perf-ui\launch.ps1 -Exe <scratchpad>\perf-ui\publish-x64-r2r\FluentFin.exe -Runs 5 -Label r2r`.

### Request counts and latency per page (measured with the real code against a fake server)

Harness: `<scratchpad>\perf-ui\bench\` (`Program.cs`). It runs the **unchanged** `FluentFin.Core`
(`JellyfinClient` + view models) against a local Kestrel server on `127.0.0.1:0` that answers
with Jellyfin-shaped JSON (6 libraries, a 5,000-movie library, a series of 10 seasons x 12
episodes), counts the requests, and can add a fixed delay per request (50 ms here = a remote
server) and a bandwidth cap. No real server, no credentials (a fake token object is passed to
`JellyfinClient.Initialize`). Output: `bench-run2.log`. A stray `GET /socket` (the SDK's
websocket reconnecting to the fake server) sometimes adds 1 to a count; ignore it.

| Flow (code path) | Requests | 0 ms delay | 50 ms per request | Notes |
|---|---|---|---|---|
| Sign-in init `JellyfinClient.Initialize` | 3 + websocket | 168 ms | 174 ms | runs before the main window is shown (O4) |
| Home `HomeViewModel.OnNavigatedTo` | 9 | 41 ms | **427 ms** | 6 `Items/Latest` one after another |
| - those 6 requests sequential (today) | 6 | | 374 ms | |
| - the same 6 requests with `Task.WhenAll` | 6 | | **62 ms** | (same SDK, same server) |
| Library grid, 5,000 movies | 2 | 219 ms | 282 ms | 6.7 MB response; GetItems then GetFilters in sequence |
| Series page | 4 | 9 ms | **247 ms** | all 4 sequential, all independent |
| Season page | 2 | 9 ms | 125 ms | |
| Movie page | 2 | 4 ms | 126 ms | |
| Episode page | 1 | 10 ms | 62 ms | |
| Play an episode: playlist build `PlaylistViewModel.FromEpisode` | 12 | 22 ms | **743 ms** | GetItem(series) + seasons + 1 per season, sequential |
| Item start `GetMediaUrl` (remote, 40 Mbit/s) | 3 | 669 ms | **821 ms** | 3 MB bitrate test on every item start |
| - again for the next item | 3 | 638 ms | 818 ms | the test is not cached |
| Admin dashboard | 6 | 25 ms | **365 ms** | 6 sequential, all independent |

CPU-only numbers (median of 7 after warm-up, Release, this PC):

| What | Result |
|---|---|
| Kiota parse of the 5,000-movie library response, with `Overview` | 6.40 MB, 144.9 ms (thread pool) |
| same without `Overview` | 4.45 MB (-30%), 125.3 ms |
| `BaseItemViewModel.FromDto` x 5,000 (UI thread) | 7.5 ms |
| `LibraryViewModel` load of 5,000 parsed items (FromDto + cache + sort + first page, UI thread) | 12.4 ms |
| Blurhash 20x20 decode per card (x:Bind converter, UI thread) | 17.7 us |
| `FlyleafLib.Engine.Start` with FluentFin's exact config (harness `flyleafstart\`) | 941 ms first ever, then ~128 ms; +18.5 MB private, +2 threads, +69 modules |
| Sign-in request to an unreachable server (`10.255.255.1`, SDK default HttpClient) | fails after **21.1 s** (TCP connect timeout); refused port: 2.0 s |
| LibVLC + MediaPlayer per player-page visit, never released (harness `vlcleak\`, no video output, 10 instances) | not disposed: +21 threads, +255 handles, +27.6 MB; disposed: +9 threads, +240 handles, +23.4 MB. So ~1.2 threads, ~1.5 handles, ~0.4 MB stay per undisposed instance; dropping the references does not help (finalizers do not free them) |

## 2. Coverage gaps and the characterization tests to add first

Rule: add these **before** the matching optimization and run them against the unchanged code
first; they must pass on both sides of the change.

How to fake things (all fit the existing xunit + Moq projects):

- **View models**: `Mock<IJellyfinClient>` as in `LibraryViewModelTests` /
  `VideoPlayerViewModelSubtitleTests`; keep the static `RxAppBuilder.CreateReactiveUIBuilder().BuildApp()`.
  To prove order does not depend on timing, return `TaskCompletionSource` tasks and complete them
  in reverse order.
- **`JellyfinClient` request shapes**: `JellyfinClient` builds its own `JellyfinApiClient`
  (`FluentFin.Core\Services\JellyfinClient.cs:46`), so Moq cannot see its HTTP calls. Two options:
  1. No production change: a `FakeJellyfinServer` xunit fixture (Kestrel on `127.0.0.1:0`; add
     `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to FluentFin.Core.Tests) that
     records method, path and query and returns canned JSON. `bench\Program.cs` already does
     exactly this against the unchanged code; its `FakeServer` class can be lifted into the test project.
  2. A small seam: `JellyfinRequestAdapter` has a constructor that takes an `HttpClient`
     (`(IAuthenticationProvider, JellyfinSdkSettings, HttpClient)`), so `JellyfinClient` could take
     an optional `HttpMessageHandler` and tests pass a recording handler.
  The Flurl calls (`GetJsonAsync`/`PostJsonAsync` for branding, encoding, xbmcmetadata) can use
  Flurl's `HttpTest` or the same fake server.
- **Settings folder**: `KnownFolders` always uses `%LOCALAPPDATA%\FluentFin`
  (`FluentFin.Core\KnownFolders.cs:9`), which the upstream app and FluentFin MPV share. Add an
  override (for example an environment variable `FLUENTFIN_DATA_DIR`) so tests and launch
  measurements can run without touching the user's real settings and saved passwords.

| # | Area an optimization touches | Tests today | Characterization test to add (what it asserts) | Protects |
|---|---|---|---|---|
| T1 | `JellyfinClient.GetRecentItemsFromUserLibraries` (`JellyfinClient_User.cs:56-108`) | none | Fake server; `Items/Latest` for library 1 answers last. Assert yielded libraries keep `UserViews` order, Music views are skipped, libraries with 0 items are skipped, each `Latest` has `limit=16`, `parentId`, `imageTypeLimit=1` | O1 |
| T2 | Home `HomeViewModel.OnNavigatedTo` (`HomeViewModel.cs:47-79`) | none | Moq client; Continue/NextUp/Recent filled; `HasContinueItems`/`HasNextUpItems`/`IsLoading` end values; `RecentItems` order with reversed completion; a `UserDataChangedMessage` on the socket subject with `Played=true` removes the Continue item and refetches NextUp | O1 |
| T3 | Series/Movie/Season/Episode/Dashboard VMs | none | Same properties end up set whatever order responses complete in; Movie/Episode stream pickers: default audio, the "None" subtitle entry first, `DefaultSubtitleStreamIndex` honoured, `HasSubtitles`; Dashboard: `OnNavigatedFrom` disposes the sessions subscription | O1 |
| T4 | `PlaylistViewModel.FromEpisode/FromSeason/FromSeries` (`PlaylistViewModel.cs:113-204`) | none | Fake client with 3 seasons (one of them "Specials", index 0) answering out of order: playlist order is season order as returned by the server, then episode order; count; `AutoSelect` picks the first unplayed | O2 |
| T5 | `JellyfinClient.GetMediaUrl` (`JellyfinClient_Media.cs:14-102`) | none (only via mocks) | Fake server: in-network -> no `/Playback/BitrateTest`, no `MaxStreamingBitrate`; remote -> one bitrate test, `MaxStreamingBitrate` = measured value; with a `PlaybackSelection` -> `MediaSourceId`, `AudioStreamIndex`, `SubtitleStreamIndex` sent; direct play URL vs transcode URL; ApiKey present exactly once | O3 |
| T6 | `JellyfinClient.GetItems` fields (`JellyfinClient_Items.cs:10-40`) | indirect only | Fake server: movies library -> `includeItemTypes=Movie`, `recursive=true`, fields; season -> episodes still requested **with** `Overview` (SeasonPage shows it, `Views\SeasonPage.xaml:268`) | O6 |
| T7 | `LibraryViewModel` filters | 1 (sort/paging) | Genres/Tags/Years/OfficialRatings filters give the same pages; `IsLoading` toggles; filter sources filled when GetItems and GetFilters complete in either order | O1, O6 |
| T8 | Start-up decision `DefaultActivationHandler.HandleInternalAsync` (`Activation\DefaultActivationHandler.cs:45-72`, app project) | none | Mocks: 1 server + 1 user + auth ok -> Shell; auth fails -> SelectServer; 0 or 2 servers -> SelectServer; GUID command-line argument -> Library. Plus a scripted check (like `launch.ps1`) that the window appears within 2 s when the saved server is unreachable (needs the settings-folder override) | O4 |
| T9 | Player lifetime `VideoPlayerViewModel.OnNavigatedFrom` (`ViewModels\VideoPlayerViewModel.cs:67-84`) | 2 (subtitles only) | Mock `IMediaPlayerController`: `Stop()` then `Dispose()` exactly once; no `Progress` call after navigate-from (inject the scheduler or wait > 1 s); `JellyfinClient.Stop()` called once; event subscriptions released (Subjects in the mock report `HasObservers == false`) | O7 |
| T10 | Image URLs `BaseItemDtoConverters.GetImage` (`Converters\BaseItemDtoConverters.cs:89-261`) | none | First extract the URI building into a pure function (it currently returns `BitmapImage`, which needs a XAML thread); then table-test: Thumb -> Primary fallback, Episode Primary -> series image, Season/Episode Backdrop -> series id, `tag`, `fillHeight` | O10 |
| T11 | Package changes (O8, O9) and Flyleaf lazy start (O5) | none | Publish, launch with the dead proxy (as `launch.ps1`), assert a window within N s; then the manual playback matrix below, per backend | O5, O8, O9 |

Manual playback matrix (needs a Jellyfin test server; for every backend that must keep working:
MPV, Flyleaf, VLC, Windows media player): direct play H.264 MKV, HEVC MKV, transcode (HLS),
embedded and external subtitles, switching audio track, seek, next episode, back to the
library. For O7 add a soak: play and leave the player 20 times per backend while sampling
private bytes, handles and threads (`Get-Process`), expecting flat lines.

## 3. Opportunities, ranked by user-visible gain / risk

Legend: **Measured** = numbers from this run; **Static** = from reading code; **Speculative** = plausible, not proven.

### O1. Run independent requests together (Home, Series, Movie, Dashboard, Library) - high gain, low risk. Measured.

- `FluentFin.Core\Services\JellyfinClient_User.cs:73-107` - one `Items/Latest` per library, awaited one by one. Start all, then yield in view order (keeps the `IAsyncEnumerable` contract and the order on screen).
- `FluentFin.Core\ViewModels\SeriesViewModel.cs:29-32` - `UpdateSeries`, `UpdateNextUp`, `UpdateSeasons`, `UpdateSimilar` are independent (the last three use the `dto` parameter, not the refreshed `Dto`) -> `Task.WhenAll`.
- `FluentFin.Core\ViewModels\MovieViewModel.cs:55-56`, `SeasonViewModel.cs:29-30` - same.
- `FluentFin.Core\ViewModels\DashboardViewModel.cs:51-77` - 6 independent awaits.
- `FluentFin.Core\ViewModels\LibraryViewModel.cs:150-160` - `GetItems` then `GetFilters`.
- `FluentFin.Core\Services\JellyfinClient_User.cs:61` and `:115` - `UserViews` is fetched at sign-in (for the jump list) and again on every Home visit; cache it per session (invalidate on sign-out/library change) - saves 1 request per Home visit.

Gain (50 ms per request): Home 427 -> ~120 ms (the 6 Latest calls measured 374 -> 62 ms),
Series 247 -> ~65 ms, Dashboard 365 -> ~65 ms, Movie/Season 126 -> ~65 ms. On a LAN (~1 ms) the
gain is small; for remote/VPN users it is the most visible change in browsing.
Risk to functionality: low. Continuations stay on the UI thread when awaited from the view
model; keep collection writes after the `WhenAll`. Watch: the home `RecentItems` order must not
change; servers on weak hardware get up to 6 parallel requests (Jellyfin web, as far as I know,
also loads home sections in parallel - not verified here).
Tests now: none (Library has 1). New: T1, T2, T3, T7; plus a fake-server timing test that asserts
"Home with 6 libraries at 50 ms delay finishes under 200 ms" so the gain cannot silently regress.

### O2. Build the play queue without one request per season - high gain, low risk. Measured. RE-CHECK AFTER INTEGRATION

`FluentFin.Core\ViewModels\PlaylistViewModel.cs:113-130` (`FromSeries` awaits `FromSeason` per
season), `:149-166` / `:168-185` (`FromEpisode`/`FromSeason` first fetch the full series only to
pass it to `FromSeries`, which only needs its `Id`), called from
`FluentFin\ViewModels\VideoPlayerViewModel.cs:160-167` before playback can start.
Measured: 12 sequential requests, 743 ms at 50 ms per request for 10 seasons, each season list
also carrying Overview/Tags/Genres the playlist does not use.
Change, lowest risk first: (a) fetch the seasons' episodes concurrently and concatenate in
season order (~2-3 round trips, ~150 ms); (b) skip the extra `GetItem(series)` (1 round trip);
(c) optionally one `GET /Shows/{seriesId}/Episodes` call - fastest, but its ordering of specials
and missing/virtual episodes can differ from today's folder order, so only with T4 green.
Risk: (a)/(b) low; (c) medium (queue order). Tests now: none. New: T4.
Re-check: the MPV path may let jellyfin-mpv-shim build the queue instead; `VideoPlayerViewModel`
is being changed.

### O3. Do not repeat the 3 MB bitrate test (and endpoint check) on every item start - high gain for remote users, low-medium risk. Measured. RE-CHECK AFTER INTEGRATION

`FluentFin.Core\Services\JellyfinClient_Media.cs:31-32` calls `EndpointInfo()` and, when not
in-network, `BitrateTest()` (`JellyfinClient.cs:102-139`, 3 MB, 5 s cap) before every
`PlaybackInfo`, including each next episode. Measured 0.64-0.82 s per start on a 40 Mbit/s link;
3 MB at 10 Mbit/s is 2.5 s, and slower links hit the 5 s cap. Cache the result per server URL for
the session with a time limit (from memory, Jellyfin web also reuses its measurement for a
while - not verified here), keep
`IsInNetwork` per server, and re-measure after a playback error.
Risk: a network change mid-session (LAN -> VPN) keeps an old bitrate until the time limit; keep
it short (for example 30-60 minutes) and drop it on sign-out. Tests now: none. New: T5 (+ "second
call within the limit makes no bitrate request").
Re-check: if the MPV path hands playback to jellyfin-mpv-shim, this code may no longer run for MPV;
it still runs for the other three backends.

### O4. Show the main window before the automatic sign-in - very high gain when the server is slow or down, medium risk. Measured (timeout), static (ordering)

`FluentFin\Services\ActivationService.cs:36` awaits `HandleActivationAsync`; with one saved
server + user, `DefaultActivationHandler.cs:51` awaits `Authenticate`, which awaits
`JellyfinClient.Initialize` (capabilities, plugins, websocket, user views, jump list). Only then
`ActivationService.cs:39-41` maximizes and activates the window. So the user sees no window for
the whole sign-in: normally 4-6 round trips, but **21.1 s** when the saved server is unreachable
(measured connect timeout) and up to the HttpClient timeout (100 s by default; not measured)
when a server accepts the connection but does not answer.
Change: activate the window first with a "Signing in to <server>..." state, then authenticate
and navigate (Shell or the server picker). Measured warm time-to-window today without a saved
server: ~925 ms; with this change the window time should no longer depend on the network.
Risk: medium - start-up order, the jump-list launch argument, and the fallback to the server
picker must behave the same. Tests now: none. New: T8.
Note: `App.xaml.cs` is where the MPV backend host service gets registered; RE-CHECK AFTER
INTEGRATION that the backend start is not also put in front of `Activate()`.

### O5. Start Flyleaf only when Flyleaf is used - medium gain, low-medium risk. Measured. RE-CHECK AFTER INTEGRATION

`FluentFin\App.xaml.cs:238` calls `StartFlyleaf()` (`:285-297`) on the UI thread before
activation, on every launch, whatever player is selected. Harness with the same `EngineConfig`:
~128 ms warm (941 ms first time), +18.5 MB private memory, +2 threads (FluentFin also turns on
Flyleaf's 250 ms UI refresh), +69 modules; the launched app indeed has 4 FFmpeg DLLs loaded at the server
picker. That is ~14% of the ~925 ms time-to-window and ~11% of private memory at idle, paid by MPV,
VLC and Windows-player users. Change: an idempotent `EnsureFlyleafStarted()` called from
`MediaPlayerHost.CreateFlyleaf()` (`Controls\MediaPlayerHost.xaml.cs:109`) before `new FlyleafHost()`.
Risk: low-medium - every Flyleaf entry point must call it first (only `CreateFlyleaf` creates a
Flyleaf player today; `FlyleafLib.Engine` is referenced nowhere else). Tests now: none. New: T11 +
Flyleaf rows of the playback matrix, and "switch player to Flyleaf in Settings, then play".
Re-check: `App.xaml.cs` and `MediaPlayerHost` are both being changed.

### O6. Do not download `Overview` for the library grid - medium gain for big libraries, low risk. Measured

`FluentFin.Core\Services\JellyfinClient_Items.cs:19` asks for `Overview` for every list; the grid
never shows it, but the season page does (`Views\SeasonPage.xaml:268`). Measured for 5,000 movies:
6.40 -> 4.45 MB (-30%), parse 145 -> 125 ms; on a 40 Mbit/s link ~0.4 s less shimmer per library
open. Change: give `GetItems` a fields argument (or a separate `GetLibraryItems`) and drop
`Overview` only for the library grid. Note the grid loads the **whole** library in one response
(no `Limit`, `LibraryViewModel.cs:150`, paging is client-side); server-side paging would be the
big win for very large libraries but changes how sort/filter work - high risk, not proposed now.
Risk: low. Tests now: 1 (sort/paging). New: T6, T7.

### O7. Release players, LibVLC and their subscriptions when leaving the player - small-to-medium gain (unknown until the soak test), medium risk. Measured (harness) + static. RE-CHECK AFTER INTEGRATION

- Nobody calls `IMediaPlayerController.Dispose()` (`VideoPlayerViewModel.cs:67-84` only calls `Stop()`).
- `VlcMediaPlayerController.Dispose()` (`MediaPlayers\VlcMediaPlayerController.cs:105`) disposes the MediaPlayer but not `_libVLC` (created per controller, `:21`) nor `_media` (`:79`, replaced per item without dispose).
- `WindowsMediaPlayerController.Stop()` is only `Pause()` (`:215`), so the stream stays open until GC.
- `VideoPlayerViewModel.SubscribeEvents` (`:451-485`), the `WhenAnyValue` subscriptions (`:114`, `:122`) and `TransportControls.OnPlayerChanged` (`Controls\TransportControls.xaml.cs:68-90`) are never disposed; with native event sources they keep the page graph alive.

Harness (`vlcleak\`, no video output): compared with disposing, each LibVLC + MediaPlayer that
is not disposed keeps ~1.2 threads, ~1.5 handles and ~0.4 MB for the life of the process, and
becoming unreachable does not help (finalizers do not release it). That alone is small; with a
real swap chain, decoder and an open stream per session the cost is probably larger, but that
needs the soak test with a server (not measured here). The page graph kept alive by the
undisposed subscriptions is likewise unmeasured.
Trade-off measured too: when LibVLC is disposed, the next `new LibVLC()` reloads plugins
(~111 ms instead of ~4 ms), so the VLC player page would open ~0.1 s slower after the first time.
Change: in `OnNavigatedFrom`, dispose the subscriptions, then `Stop()` + `Dispose()` the
controller; make the VLC controller dispose `_media`, `_mp`, then `_libVLC`; let the WMP
controller set `Source = null` and dispose; Flyleaf `Player.Dispose()`.
Risk: medium - dispose order vs. native swap chains and late events (use-after-dispose crashes),
and the progress timer must be gone before the player is disposed. Tests now: none. New: T9 + the
soak. Re-check: `VideoPlayerViewModel` and `MediaPlayerHost` are being changed; the MPV controller
must follow the same rule (and must not leave an mpv/backend process running).

### O8. Leave the unused Windows App SDK AI/ML/Search/Widgets components out of the package - large size gain (~54 MB), medium risk. Speculative mechanism

Measured: `onnxruntime.dll` 20.7 MB + `DirectML.dll` 17.8 MB (from `Microsoft.Windows.AI.MachineLearning`
via `Microsoft.WindowsAppSDK.ML`), `Microsoft.WindowsAppSDK.AI` files 9.9 MB, `.Search` 3.5 MB,
`.Widgets` 2.5 MB. No source file uses `Microsoft.Windows.AI`, `.Widgets` or `.Search`. They come
in through the `Microsoft.WindowsAppSDK` 2.5.1 meta-package, which 15 other packages (CommunityToolkit,
Flyleaf, LibVLCSharp, ReactiveUI.WinUI, ScottPlot, ...) also depend on, so it cannot simply be
dropped. Possible route: direct `PackageReference`s to those 4 components (and
`Microsoft.Windows.AI.MachineLearning`) with `ExcludeAssets="all"` in `FluentFin.csproj`, or
removing their files in a post-publish target. Must be proven with a publish + launch + full
playback matrix (self-contained WinAppSDK may generate activation entries from these packages,
which would then point at missing files).
Risk: medium (start-up failure if the manifest still points at removed files). Tests now: none. New: T11.

### O9. Smaller, safe-looking package trims - medium size gain, medium risk

- VLC plugins not used for playback (`libvlc\win-x64\plugins\access_output` 8.4 MB, `stream_out` 3.6, `visualization` 2.0, `services_discovery` 1.2, `mux` 0.9, `video_splitter` 0.2, `lua` + `libvlc\win-x64\lua`, and the `.lib` import libraries 0.8 MB): **18.3 MB** measured. The VideoLAN.LibVLC.Windows package supports include/exclude item lists in the csproj. Risk medium: must keep `access` (http), `demux` (adaptive/HLS), `codec`, `packetizer`, `misc` (TLS), `spu`/`text_renderer` (subtitles), `video_output`, `audio_output`. VLC rows of the matrix must pass.
- `FFMpeg\avfilter-12.dll` (19.5 MB) + `avdevice-63.dll` (0.1 MB): FluentFin starts Flyleaf with `LoadProfile.Main`, whose comment says it does not load them, and the harness confirms they are not loaded at start. **Speculative**: Flyleaf may load them later for some feature; prove with Process Explorer during Flyleaf playback (speed change, audio/video filters) before removing. Risk medium-high.
- `<SatelliteResourceLanguages>en</SatelliteResourceLanguages>`: 21.4 MB of 130 culture folders. The app's own strings are English only (`Strings\en-us`), but Humanizer would then format "2 hours ago"/sizes in English on non-English Windows - a visible change for those users. Risk low-medium.
- Not acceptable: removing WPF/WinForms (~77 MB, Flyleaf and DeftSharp need them), FFmpeg core DLLs, or VLC core plugins - each would break a player.

### O10. Image requests: cacheable and right-sized - medium network gain on revisits, low risk. Static (no server to measure bytes)

- Person pictures `Converters\BaseItemDtoConverters.cs:91` have no `tag`, so the URL is not versioned and the server cannot let it be cached long; `BaseItemPerson.PrimaryImageTag` is available - add `tag=` (the item images at `:254-257` already do).
- Library folder images `:96` and `JellyfinVirtualFolderImageConverter` (`Converters\JellyfinImageConverter.cs:40`) request the **original** image with no size (admin Libraries page) - add `fillHeight`/`maxHeight`.
- Consider `quality=90` (Jellyfin web sends quality and both fill dimensions).
- Do not shrink further: Home cards ask `fillHeight=192` for 240-DIP cards (`Views\HomePage.xaml:55-56`), which is already below 1:1 at 125-150% display scaling.
- The blurhash for library cards uses Thumb while the image is Primary (`Controls\PagedScrollView.xaml:77-78`) - cosmetic.
Risk: low (URLs only). Tests now: none. New: T10.

### O11. Build card context menus on demand - probable UI-thread gain while scrolling grids, low-medium risk. Static, unmeasured

`Controls\BaseItemDtoCard.xaml:45` (`ContextFlyout`) and `:296` (More button `Flyout`) both bind
`JellyfinFlyoutConverter` (`Converters\JellyfinFlyoutConverter.cs:11-41`), so every realized card
eagerly builds **two** menus of ~12-17 items with icons (~60 XAML objects per card) that are
rarely opened. On a 100-card library page that is thousands of objects per realization pass.
Build the menu in the flyout's `Opening` event instead. Measure first: XAML frame analysis or a
WPR trace while scrolling the library page before/after. Risk: the menu content (admin-only items,
"Edit Segments" plugin check, played state) must be identical; check right-click and keyboard
(Shift+F10). Tests now: none. New: a unit test of the menu builder's item list per item type/admin
state (extract it from the converter first).

### O12. Card hover effect visual - speculative GPU gain, low risk

Every card attaches a `PipelineVisualFactory` with Blur + Tint (`Controls\BaseItemDtoCard.xaml:106-111`);
without a `Source`, the toolkit factory uses the backdrop as I understand its default (verify),
i.e. one backdrop-effect visual per realized card, alive even at blur 0. Create it on first hover. Speculative until a GPU trace
(PresentMon/WPR) while scrolling shows the cost. Tests: visual check of the hover animation.

### O13. Windows-player codec probe only when that player is used - small gain, low risk. RE-CHECK AFTER INTEGRATION (settings)

`ActivationService.cs:44` -> `WmpDeviceProfileManager.InitializeAsync` (`WmpDeviceProfileManager.cs:15-52`):
7 `CodecQuery` calls + `AudioGraph.CreateAsync` at every launch, after the window. Only the
Windows-player profile needs it. Note the ordering bug this hides: with a saved sign-in, the
capabilities (including the device profile) are posted during activation, **before** this
probe runs, so a Windows-player user's first capabilities post carries a null profile.
Risk: low, but fixing the order changes what the server receives. Tests: unit test that
`DeviceProfileFactory.GetDeviceProfile()` never returns null for each player type.

### O14. ReadyToRun - unmeasured start-up gain, certain size cost. Speculative

`PublishReadyToRun=true` worked and costs +79.2 MB (+14%). Usually it cuts JIT time at start-up;
whether it beats ~925 ms enough to justify the size must be measured with the command in
section 1. Trimming (`PublishTrimmed`) is **not** recommended: WinUI, ReactiveUI, Kiota,
CommunityToolkit and the XAML type info rely on reflection.

### Not worth doing (measured)

- Blurhash decode off the UI thread: 17.7 us per card (~1.8 ms for 100 cards).
- `BaseItemViewModel.FromDto` / library sorting on the UI thread: 7.5 ms and 12.4 ms for 5,000 items.
- Scrutor: referenced in `FluentFin.csproj` but no `Scan(...)` call exists; there is no assembly-scanning cost (only an unused package).
- Serilog file sink: modest volume (per-stream lines on each playback), no measurable start-up cost found.

### Bigger changes (high risk, listed for completeness)

- Server-side paging for libraries (O6 note).
- Keep Home/Library pages alive on back navigation (`NavigationCacheMode`): today every back
  navigation re-creates the page and view model and re-fetches everything
  (`Services\NavigationService.cs`), but `HomeViewModel.OnNavigatedFrom` disposes its
  `CompositeDisposable` for good (`HomeViewModel.cs:41-45`), so caching needs a lifetime redesign.

## 4. Files being changed by the MPV integration

Opportunities in these files must be re-checked against the integrated code before anyone
implements them: **O2** (`VideoPlayerViewModel` builds the queue), **O3** (`GetMediaUrl` may be
bypassed by MPV), **O4** (`App.xaml.cs` start order and the new host service), **O5**
(`App.xaml.cs`, `MediaPlayerHost`), **O7** (`VideoPlayerViewModel`, `MediaPlayerHost`, new MPV
controller), **O13** (settings / player type). O1, O6, O8-O12 and O14 are outside those files.

## Other observations (not performance, found on the way)

- `FluentFin\UpdateService.cs:26` checks `insomniachi/fluentfin` on GitHub at every launch of an
  unpackaged build, **downloads** a newer upstream installer into `%LOCALAPPDATA%\FluentFin\Updates`
  and offers to install it. A local FluentFin MPV build (version 1.0.0.0 unless `BuildVersion` is
  set) would be offered upstream FluentFin, which replaces the MPV variant.
- FluentFin MPV uses the same `%LOCALAPPDATA%\FluentFin` folder (settings, saved server passwords,
  logs) as upstream FluentFin; both installed on one PC share them.
- `JellyfinClient_Media.cs:34,40,93` sends `TimeProvider.System.GetTimestamp()` (a monotonic
  counter, not a position) as `StartTimeTicks` to PlaybackInfo and the stream URL.
- `TransportControls.xaml.cs:87` throttles volume updates by **200 seconds** (`FromSeconds(200)`).
- `HomeViewModel.cs:56` attaches `CollectionChanged` to the NextUp collection, but
  `UpdateNextUpItems` (`:102`) replaces the collection later, so `HasNextUpItems` stops tracking it;
  Continue items' `PlayedPercentage` is not observable, so progress bars do not move.

## Reproduce

All in `<scratchpad>\perf-ui\`: `build.ps1` (build), `tests.ps1` (restore + tests),
`publish.ps1` / `publish-r2r.ps1` (package), `launch.ps1` (time to first window, network blocked),
`bench\` (`dotnet run -c Release` for the page/latency table, `-- unreachable` for the timeout),
`flyleafstart\` (Flyleaf start cost; argument = FFMpeg folder), `vlcleak\` (arguments = libvlc
folder and `dispose`/`drop`/`keep`).
