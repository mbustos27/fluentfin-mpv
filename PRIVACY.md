# Nixie's privacy policy

*Last updated for Nixie 1.7 (November 2026).*

Nixie is a free, open-source (GPL-3.0) Windows app for watching your own Jellyfin server. It has no account of its own, no
analytics, no telemetry and no crash reporting. **Nixie collects nothing about you.** What it keeps, it keeps on your PC,
in Nixie's data folder (`%LOCALAPPDATA%\Nixie`), and on your own Jellyfin server.

## What leaves your PC, and where it goes

- **Your Jellyfin server.** Nixie signs in to the server you choose and plays from it, like any Jellyfin app. Your server
  belongs to you or to whoever runs it; Nixie sends nothing about it anywhere else.
- **GitHub, for updates** (on by default; **Settings → Updates → Check for updates** turns it off). Nixie asks GitHub for
  the latest Nixie release when it opens, every 4 hours while it stays open, and when you come back to it after an hour or
  more. When there is a newer one, an installed copy downloads its installer and checksum from GitHub (github.com hands the
  download to GitHub's file host, `release-assets.githubusercontent.com`) in the background; a portable copy only opens
  the release page. Nothing else is sent to GitHub.
- **YouTube, for trailers**, when you play one.
- **Look up intros online** (off until you turn it on): TheIntroDB, AniSkip and arm.haglund.dev get the show's public id,
  the season, the episode and its length. Nothing else.
- **Look up lists online** (each source off until you turn it on): see below.
- **Jellyseerr**, only if you set up your own Jellyseerr server in **Settings → Integrations**, and only when you open its
  page there. Nixie then talks to that address only: it signs in there with your Jellyfin user name and password (saved
  on this PC, encrypted for your Windows account), reads and approves or declines the requests on it, and reads the
  details of a requested film or show. Its posters come through your Jellyseerr's own image proxy, which fetches them
  from TMDb for you; Nixie doesn't ask TMDb for them.

Every service you reach sees your PC's IP address. That can't be avoided, and services keep it in their logs by their own
rules.

## Look up lists online

These sources give Nixie public lists: trending titles, award winners, finer genres and franchise names. Nixie
downloads each **whole public list**, the same list everyone gets, and matches it against your library **on your PC**.

**What is sent:** the list's fixed address (the same for every Nixie user), a User-Agent naming Nixie, its version and
its GitHub page, the key the source needs (below), and your IP address.

**What is never sent:** a title, an id or a name from your library, how many titles you have, anything you watched, your
server's address, or your account. Which lists Nixie asks for, and when, depends only on the switches you turned on,
never on what is in your library.

| Source | What Nixie reads | Key sent | Nixie's own copy (public lists, not your data) |
|---|---|---|---|
| **Trakt** (`api.trakt.tv`) | Trending, popular and most anticipated films and shows, and the US box office | Nixie's own Trakt Client ID, which identifies the app, not you | Nixie keeps its own copy of these public lists. Refreshed every 6 hours (the box office daily, and after its Monday update); the last copy is used for up to 24 hours if Trakt can't be reached. |
| **TMDb trending** (`api.themoviedb.org`) | Trending films and shows this week | Nixie's own TMDb key (or yours, if you enter one) | Nixie keeps its own copy of these public lists. Refreshed daily; the last copy is used for up to 7 days if TMDb can't be reached. |
| **TMDb export files** (`files.tmdb.org`) | Franchise (collection) names and keyword names | None | Nixie keeps its own copy of these public lists. Refreshed weekly; the last copy is used for up to 6 months if TMDb can't be reached. |
| **Wikidata** (`query.wikidata.org`, or the endpoint you choose) | Award winners and nominees, and finer genres | None | Nixie keeps its own copy of these public lists. Refreshed weekly; the last copy is used for up to 60 days if Wikidata can't be reached. |

Settings' "What is sent" card for each source says the same. Nixie never shows a copy past its limit, and deletes it then
(when it next reads it, and at every start).

- **No sign-in, ever.** Nixie never signs you in to Trakt, never asks for a Trakt account, and never sends Trakt what you
  watch. Nixie uses Trakt but is not endorsed or certified by Trakt.
- **No pictures from TMDb or Trakt.** Nixie matches the lists only; every poster you see comes from your own server.
- **TMDb's key.** Nixie's own TMDb key is built into the release, so TMDb trending works without one of yours. If you
  enter your own token in Settings, Nixie uses it instead; it is stored encrypted with Windows (DPAPI), so only your
  Windows account on this PC can read it. Either is sent only to `api.themoviedb.org`, in the request's `Authorization`
  header: never in an address, and never written to a log.
- **What matched** (which of your titles are on a list) is worked out on your PC, kept for that server and user only, and
  forgotten when you sign out.
- Nixie's logs stay on your PC. They never contain a TMDb key or token, or Nixie's Trakt Client ID.

## How to stop

Turn the source off in **Settings → Look up lists online**. Nixie stops asking it at once and stops showing its rows;
turning Trakt or TMDb trending off also deletes the lists Nixie saved from it. **Remove** next to the TMDb token forgets
your token. The other switches above (updates, intros) work the same way. Nixie talks to your Jellyseerr only while you
use its page.

## How to delete what is kept

- Nixie's copies of the public lists hold nothing about you, and they age out on their own: at most 24 hours for Trakt,
  7 days for TMDb trending, 60 days for Wikidata and 6 months for TMDb's export files.
- To remove everything Nixie keeps on your PC at once (saved lists, settings, saved sign-ins, the TV journal), close
  Nixie and delete the folder `%LOCALAPPDATA%\Nixie`. Uninstalling Nixie (Settings → Apps in Windows) removes the
  program but leaves that folder, so delete it as well.
- What your Jellyfin server keeps (watch history, favourites, ratings you choose to send) is on that server; its own
  settings or its administrator can remove it.

## Questions

Nixie is a personal project. Ask, or report a privacy problem, in the repository's issues:
<https://github.com/mbustos27/fluentfin-mpv/issues>.
