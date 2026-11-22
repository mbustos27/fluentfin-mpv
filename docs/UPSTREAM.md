# Upstream sources

Both halves were imported as plain copies (shallow clones, `.git` removed) on 2026-11-13.
The first commit of this repository is the untouched import, so `git diff <first commit>`
shows every local change.

| Folder | Upstream | Commit | Licence |
|---|---|---|---|
| `ui/` | https://github.com/insomniachi/FluentFin | `c6247584fedc867832768ce236f36066a1671cbd` (2026-11-02, "Merge pull request #38") | GPL-3.0 |
| `backend/` | https://github.com/jellyfin/jellyfin-mpv-shim | `a3a9a31460cb3f15483a7052cbac18b9b065ec92` (2026-11-13, Weblate) | GPL-3.0-or-later |

Both are GPLv3, so the combined work is GPLv3. It is published on GitHub
(mbustos27/nixie, named mbustos27/fluentfin-mpv until 2026-11-22) under GPLv3, with both upstream notices intact.

To pull upstream changes later: clone the upstream at a newer commit, diff it against the
commit above, and apply that diff to the matching folder.

## Rename to Seaglass (1.1.0), then Nixie (1.2.0)

The app is named Nixie (formerly Seaglass, and FluentFin MPV before that), but the C# namespaces, project files and folders
stay `FluentFin.*`, so upstream FluentFin merges still apply. The rename is `AssemblyName` in `ui\FluentFin\FluentFin.csproj`,
`Core\AppIdentity.cs` (the client name, and the `NIXIE_*` environment variables with their `SEAGLASS_*` aliases), the data
folder in `Core\KnownFolders.cs`, and plain strings in `MainWindow.xaml`, `TitleBarControl.xaml`, `Package.appxmanifest` and
`Strings\en-us\Resources.resw`.
