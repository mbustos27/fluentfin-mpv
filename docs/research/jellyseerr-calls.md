# What the bundled Jellyseerr plugin calls (1.5 fixes, 2026-11-20)

Read from the code at v1.4.1 (`ui\FluentFin.Plugins\Jellyseer\`: `IJellyseerClient.cs`, `JellyseerPlugin.cs`,
`ViewModels\JellyseerDashboardViewModel.cs`, `Views\JellyseerRequest.xaml.cs`). Not run against a real Jellyseerr: the
fake server has none, and nothing here was probed live.

## When it calls anything

Only from its own page (Settings > Integrations > Jellyseerr), and only when an address is saved for the signed-in
Jellyfin server (`JellyseerOptions.ServerMapping`, keyed by the server's id, in Nixie's LocalSettings). Start-up only
reads that setting; no other page, timer or background task uses the client. With no address saved, it calls nothing.
It also signs in only when Nixie kept the Jellyfin password (a password sign-in with "remember"): after a Quick Connect
sign-in it has no password and calls nothing.

## Every request

| # | Host | Request | When | What is sent | On failure |
|---|---|---|---|---|---|
| 1 | the user's Jellyseerr address | `POST <address>/api/v1/auth/jellyfin` | opening the page; saving an address | the **Jellyfin user name and password** (Nixie's saved copy, decrypted), JSON; Kiota's default User-Agent | a failed or throwing login keeps the old address (`SetServerUrl`); on opening, an exception leaves the page as it was (not handled) |
| 2 | same | `GET <address>/api/v1/request` | after a login | Jellyseerr's session cookie (from #1) | not handled |
| 3 | same | `POST <address>/api/v1/request/{id}/approve` or `/decline` | the Approve or Decline button | the cookie | not handled |
| 4 | same | `GET <address>/api/v1/movie/{tmdbId}` or `/api/v1/tv/{tmdbId}` | each request card as it shows | the cookie | the card stays without art |
| 5 | same | `GET <address>/_next/image?url=https://image.tmdb.org/t/p/original/<path>&w=1920&q=75` | each card (poster and backdrop) | the cookie | no picture |

`image.tmdb.org` is never contacted by the PC: Jellyseerr's own image proxy (Next.js `/_next/image`) fetches the
picture on the server and answers it. The PC talks to the Jellyseerr address the user typed, and to nothing else.

## Outside-services inventory rows (for `docs\bughunt\LEDGER.md`)

| Host | Caller | When | We send | Live | On failure the user sees |
|---|---|---|---|---|---|
| the user's Jellyseerr (`/api/v1/auth/jellyfin`, `/request`, `/request/{id}/approve\|decline`, `/movie/{id}`, `/tv/{id}`) | FluentFin.Plugins `JellyseerClient` | Settings > Integrations > Jellyseerr opened, an address saved for this server, a password sign-in kept | the Jellyfin user name and password (login), then Jellyseerr's cookie; Kiota's UA | not probed (no Jellyseerr here) | address kept only if the login works; other failures unhandled (the page stays empty or as it was) |
| the user's Jellyseerr `/_next/image` (proxy for `image.tmdb.org`) | `JellyseerRequest` card | each request card | the cookie, the TMDb path, `w=1920&q=75` | not probed | no picture |
| image.tmdb.org | Jellyseerr's server, not the PC | n/a | n/a | n/a | n/a |

## Verdict

Nothing clearly wrong for 1.5: every request goes to the address the user chose, about their own server's requests.
For 1.6 (and before 1.7 promises "nothing about you leaves the PC"):
- **The Jellyfin password goes to Jellyseerr** (that is how Jellyseerr's Jellyfin sign-in works). Say so where the
  address is entered, and refuse a plain `http://` address that isn't on this machine or the local network, or warn.
- **Failures are not handled** on opening the page, listing requests and approving: give each a Nixie message (the
  outside-services rule), and an opt-in live check once a Jellyseerr test instance exists (a fake that refuses a bad
  login the way Jellyseerr does).
- **Pictures are asked at 1920 px** for a card a few hundred pixels wide: ask at display size.
- **Kiota's default User-Agent**: send Nixie's, as the other clients do.
