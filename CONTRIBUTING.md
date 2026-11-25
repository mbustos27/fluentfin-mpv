# Contributing to Nixie

Thanks for your interest. Nixie is a personal project with one maintainer, who reviews every change and decides what
goes in. This page explains how to help without anyone's time going to waste. Everyone taking part is expected to
follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Problems and ideas

Use the [issue forms](https://github.com/mbustos27/nixie/issues/new/choose). For a problem, the form asks for Nixie's
version (Settings → About) and says where the logs are (`%LOCALAPPDATA%\Nixie\Logs`). Paste only the lines around the
problem, and read them first: tokens are already removed, but a log can still show the server's address. Never paste a
server address, user name or token: this is a public page.

Before suggesting an idea, look in [docs/ROADMAP.md](docs/ROADMAP.md) and the open issues. A comment or a thumbs-up on
an issue that's already there counts for more than a new one.

Security problems go privately, never into an issue: see [SECURITY.md](SECURITY.md).

A problem with the Jellyfin server itself (its libraries, users, plugins or transcoding) belongs to the server and
whoever runs it, not to Nixie.

Nixie is built from [FluentFin](https://github.com/insomniachi/FluentFin) and
[jellyfin-mpv-shim](https://github.com/jellyfin/jellyfin-mpv-shim). Please report problems you find in Nixie here,
never to their authors: they can't fix Nixie, even in code the three share.

## Pull requests

Small fixes can go straight to a pull request. For anything larger, open an issue first and wait until the approach
is agreed, so the work isn't done for nothing. The maintainer reviews, may ask for changes, and merges or closes the
pull request.

- **Build** as [Build from source](README.md#build-from-source) says; `.\run-dev.ps1` builds and launches.
- **Test** with the suites [Development](README.md#development) lists: `ui\FluentFin.Core.Tests` and
  `ui\FluentFin.Tests` for the UI, and for a change under `backend\`, the backend's suite through the off-screen
  runner in [tools/README.md](tools/README.md), not a bare `unittest discover`, which opens real mpv windows.
- **Tests come with the change**, and never touch a real server: use the fakes the suites already have, or the fake
  Jellyfin server in `tools\ui-harness\fakeserver`. Timing a path users wait on needs a throwaway Jellyfin with tens
  of thousands of titles, never someone's own server ([docs/DESIGN-RULES.md](docs/DESIGN-RULES.md), rule 2).
- **Use the keyboard as well as the mouse**, and check a visible change in each of the four themes, in dark and light.
- **Check the design rules:** [docs/DESIGN-RULES.md](docs/DESIGN-RULES.md) lists what every change is checked against
  (the first screen waits only for what it shows, standby releases what it holds, every page has a way up, the user is
  not the server's admin, and more). The visual rules are in [docs/design/DESIGN.md](docs/design/DESIGN.md).
- **Keep the two halves in step.** A change to the messages between the UI and the backend updates
  [docs/BRIDGE.md](docs/BRIDGE.md) and both sides in the same pull request.
- **Keep upstream mergeable.** The C# namespaces, projects and folders stay `FluentFin.*`, so FluentFin's updates still
  apply ([docs/UPSTREAM.md](docs/UPSTREAM.md)).
- **Style:** follow the surrounding code; `ui\.editorconfig` sets the C# formatting.
- **No personal details:** no home-folder path, PC name, server address, user name or token in the code, tests, docs,
  screenshots or logs.
- Keep a pull request to one change.

## Language

Nixie is in English. Everything Nixie adds to this repository is written in English: code, identifiers, comments,
documentation and commit messages.

## Licence

By contributing, you agree that your contributions are licensed under the GNU General Public License v3.0, as the rest
of Nixie.
