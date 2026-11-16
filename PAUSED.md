# PAUSED: wip/home-fixes (Home page review fixes)

Branch `wip/home-fixes`, cut from main b3985e7. Handoff for whoever picks this up. Not merged, not pushed.

## Task
Fix the reviewer's 7 findings on the merged Home page (HomeViewModel, HomeCache). Each fix gets a test
that fails without it (one revert check per fix, no mutation runs). Probes reused from the reviewer's
scratch `hp-review\ReviewProbeTests.cs`, `ReviewProbe2Tests.cs` (now in HomeViewModelTests).

## Done (committed)
1. HVM reads BaseUrl/UserId once at load start, saves under them, skips the save if either changed.
2. Empty-section placeholders only on Retry (a saved page's empty section stays hidden while it refreshes).
3. `HomeCache.MarkStale` when the player page opens and is left (VideoPlayerViewModel; also OnPageRemoved);
   `HomeCache.SignedIn` from `JellyfinClient.Initialize`. Opening matters: Back to Home reads the cache
   before the player page's OnNavigatedFrom runs (NavigationService calls it after the frame navigated).
4. `KeepCards`: same cards, `Dto` replaced, so a new ImageTag reaches the saved page.
5. `Forget` from `JellyfinClient.Logout` (memory + file); `ForgetLogin` from Settings Delete User
   (`SettingsViewModel.RemoveSavedUser`) and Delete Server. Files carry a hashed login (server id + user name,
   format v2; v1 files are no longer read). ForgetLogin also deletes files with no known login. No "Path"
   anywhere in the file. A refresh that read the cache before a sign-out/removal is not saved.
6. Change counters: Continue Watching, Next Up, and each "Recently added" row; older answers dropped.
7. GetUserViews comments made accurate; `LibraryChanged` now forgets the views (`OnLibraryChanged`).
- tools/ui-bench: CoreFactory needs a KnownFolders for the client's HomeCache now (scratch folder, never the user's).
- InternalsVisibleTo FluentFin.Tests in FluentFin.Core.csproj.

## Proof
- Items 1, 2, 4, 6: the new HomeViewModelTests failed on the unfixed code (right reasons), pass after.
- Items 3, 5, 7: reverted in a scratch copy (scratchpad\home-fixes-revert, script scratchpad\revert-check.ps1):
  every targeted test failed with its fix reverted.

## Not done / open
- App not launched (self-test gate writes the user's %LOCALAPPDATA%\FluentFinMpv log): DI wiring checked by build only.
- Lingering v1 files of users who never sign in again or remove their login stay on disk (unread).
- A refresh that races the backend's Stopped report can still read pre-stop data; it is saved stale (leave mark),
  so only that one visit shows it.
- Reviewer (fluentfin-mpv-reviewer) not run yet: ask the main session.
