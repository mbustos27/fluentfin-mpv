# Local QA Jellyfin servers

Two real Jellyfin servers on this PC for the checks a fake server can't make: a transcode and its restart on a track
change, real playlists, the admin actions, the smart collections' data (`TmdbCollection`, Tags), and later the reverse
proxy, trailing slash and TLS cases from the outside-services hunt. The owner approved them on 2026-11-19. The tools
live in `tools\qa-servers\`; the servers and their data stay outside the repo.

## What's installed

| | 10.11 | 12 |
|---|---|---|
| Build | Jellyfin 10.11.11, Windows x64 zip with jellyfin-ffmpeg | Jellyfin 12.4.0 (latest stable), same kind |
| From | `repo.jellyfin.org/files/server/windows/stable/v10.11.11/amd64/jellyfin_10.11.11-amd64.zip` | `.../stable/v12.4/amd64/jellyfin_12.4-amd64.zip` |
| Size | 167,773,527 bytes | 208,872,165 bytes |
| SHA-256 (as published, `?sha256`) | `3b5d3e904415ac6bd2c0ee9fe93235b4145d41c4c85ae782d244ac9767f77374` | `d62b5b30390213680fd1fae5484082ae27b2a778cfd40a473ad7b1e4147d75a6` |
| Address | `http://127.0.0.1:8196` | `http://127.0.0.1:8296` |
| Folder | `%USERPROFILE%\Dev\jellyfin-qa\10.11` | `%USERPROFILE%\Dev\jellyfin-qa\12` |

Each folder has `server` (the unzipped build), `data`, `config`, `cache` and `log` (passed with `--datadir`,
`--configdir`, `--cachedir` and `--logdir`), `run` (the running server's `state.json`) and `seeded` (the snapshot that
`reset` restores). The test media (`media`, about 70 MB) is shared by both. The whole thing takes about 1.2 GB.

## Safe by construction

- **No system changes:** no installer, service, autostart, firewall rule, system setting or admin rights.
- **Loopback only:** `config\network.xml` is written before the first start. It binds `127.0.0.1` only, with
  auto-discovery, UPnP, IPv6 and HTTPS off. `serve.py` and `qa.ps1` refuse to start a server unless the file still says
  so. After each start, `qa.ps1` checks what the process listens on, and stops it at once if it listens anywhere but
  127.0.0.1. Before the first start, both builds' own `NetworkManager` code was run against these files: Kestrel binds
  127.0.0.1 only. Neither build has code that opens a browser.
- **Hidden, and only while needed:** each server runs on a private desktop inside a kill-on-close job held by
  `serve.py`, the pattern of `tools\backend-bench\hidden_run.py`. It ends after 4 hours at most (`-MaxMinutes`).
  `smoke.ps1` and `run-offscreen.ps1 -QaServer` stop a server they started.
- **Only our processes:** stopping uses the admin's `POST /System/Shutdown`, then `serve.py`'s stop file. As a last
  resort it ends `serve.py` itself, after proving by pid, creation time and command line that it is ours.
- **The server's own outside calls:** metadata from TMDb (public data, no account) and Jellyfin's plugin catalogue.
  Nixie and the tools call only 127.0.0.1.

## Accounts

`%USERPROFILE%\Dev\jellyfin-qa\qa-credentials.json` (outside the repo) holds two generated accounts, the same on
both servers:
- `qa-admin`, an administrator;
- `qa-user`, an ordinary user, the way the owner uses their own server.

The tools read the file. Never print it, commit it or copy it into a log. These accounts exist only on the two local
test servers.

## Seeded content

`seed.ps1` makes all of this:
- **Movies:** The Matrix and The Matrix Reloaded (one TMDb collection, 2344), Big Buck Bunny, Sintel (with a sidecar
  subtitle), Night of the Living Dead, Amélie (filed under its long French title), and Tears of Steel, which needs a
  transcode (HEVC Main 10 with DTS audio).
- **Shows:** The Twilight Zone (1959), with seasons 1 and 2 and Specials. Its S01E01 and S01E02 have 3 audio tracks,
  3 embedded subtitles (one forced) and a French sidecar.
- **Anime:** Spy x Family, with season 1 split in two parts (E01, E02, E13, E14) and a season 2.
- **Home video:** one, with a long unicode name. Home videos get no metadata lookup, so the name stays.
- **Music videos:** two.
- **Box set:** "QA Box Set", the admin's, with Big Buck Bunny and Sintel.
- **Playlist:** "QA Playlist", qa-user's own, with an episode, a movie and a music video.

The server settings: Quick Connect on, server name "Nixie QA <version>", en-US, and metadata in English for the US.
Every file is a test pattern with silent audio. The film titles are there only so the metadata lookup finds real
TMDb ids, collection ids and keywords (Tags).

## Commands (from the repo root)

```powershell
tools\qa-servers\qa.ps1 status                    # both: running?, version, wizard, listeners, seeded?
tools\qa-servers\qa.ps1 start -Version 12         # hidden; waits until ready; checks loopback
tools\qa-servers\qa.ps1 stop                      # both
tools\qa-servers\qa.ps1 reset -Version all        # back to the seeded snapshot (restarted if it was running)
tools\qa-servers\seed.ps1 [-Version 10.11|12|all] # rebuild from nothing (media made only if missing or changed)
tools\qa-servers\smoke.ps1 [-Version ...]         # 10 API checks per server, pass/fail table, no tokens
```

Pass `-Python <absolute path to a python.exe>` when `FLUENTFIN_MPV_TEST_PYTHON` is not set and no `pythonw.exe` is on
PATH (`serve.py`, `media.py` and `qaproxy.py` use only the standard library).

**The off-screen harness against a QA server.** Use `run-offscreen.ps1 -QaServer 10.11|12`, or set
`NIXIE_QA_SERVER`. The run starts the server once it holds the off-screen lock, and stops it again if it started it.
`tools\qa-servers\qaproxy.py` sits in front of the server in the run's job. It forwards every request unchanged and
writes the fake server's request log, so the driver's checks (who reported Playing and Stopped, when sign-in
answered) work as they do against the fake server. It also answers `/__harness/fixture` and
`/__harness/quickconnect/approve`, where qa-user approves the code Nixie shows. Sign-in is always Quick Connect; a
password is never typed. For example:

```powershell
$env:NIXIE_LAYOUT = 'Classic'
tools\ui-harness\run-offscreen.ps1 -QaServer 12 -DataDir <scratch>\data -Out <scratch>\run -Exe <Nixie.exe> `
    -Script movie -Then tools\qa-servers\then-qa-play-movie.ps1 -NoA11y
```

`then-qa-play-movie.ps1` plays the showcase movie (Big Buck Bunny) for 10 s (`NIXIE_QA_PLAY_SECONDS`), muted, and
stops with the Back key. It then checks the video showed, the movie page came back, mpv opened no audio device, and the
server heard one Playing and one Stopped, both from the backend, at about 10 s.

**Tests of the tools' own logic** (the `network.xml` gate, the proxy's routes, redaction and forwarding, the media
spec) need no server: `tools\qa-servers\test_qa.py`, run off-screen like any Python test here.

## Upgrading a server

1. Download the new zip from `repo.jellyfin.org/files/server/windows/stable/` into a new folder.
2. Check it against the published SHA-256 (the file URL with `?sha256`).
3. Stop the server.
4. Replace `<version>\server` with the zip's `jellyfin` folder.
5. Run `seed.ps1 -Version <version>`, then `smoke.ps1`.
6. Update the table above.

Never take a pre-release (`preview`, `unstable`) without the owner's yes.

## Seen on the real servers (2026-11-19)

- **Audio track needs the media source.** `PlaybackInfo` applies `AudioStreamIndex` or `SubtitleStreamIndex` only
  when the request also names the `MediaSourceId`. Without it, the server picks its default track, and on a transcode
  that means the wrong language with no error. The smoke's track-change check sends both, as Nixie's backend does.
- **Sidecar subtitles come first in Jellyfin's numbering.** Jellyfin numbers sidecar subtitle files before the file's
  own streams, so its `Index` is not ffmpeg's stream number. In the multi-track episode, Spanish audio is index 3 for
  Jellyfin and stream `0:2` for ffmpeg.
- **One empty box set on 12.4.** Once on 12.4.0, on a fresh server, the first `POST /Collections?name=&ids=` made an
  empty box set. 10.11.11 kept both films, and a second try on 12.4 kept them too. `seed.ps1` checks the count and
  adds the films with `POST /Collections/{id}/Items` if needed. Watch for this in Nixie's admin "new collection" flow.
- **Short items get no resume point.** Anything shorter than `MinResumeDurationSeconds` (300 s by default) and stopped
  past `MinResumePct` (5 %) is marked watched instead. After the 10 s proof, Big Buck Bunny (90 s) was ticked as
  watched and its button still said Play. A check that needs "Resume at ..." has to use a longer file or change that
  server setting, which the fake server doesn't model.
- **What an ordinary user meets.** As qa-user, Nixie's UI asks `GET /Plugins`, which needs an admin, and gets 403. It
  then sends `POST /user_usage_stats/submit_custom_query` and gets 404, because no Playback Reporting plugin is
  installed. It also asks for `/Branding/Splashscreen` and gets 404: a new server has no splash screen. Nixie logged no
  error for any of them.

- **Watching a show again (sg-cw).** With every episode of a show watched, its first episode played again:
  - Next Up has the show (its second episode) only with `EnableRewatching=true`, on both versions, Home's query and a
    series' own (`SeriesId`) alike. Jellyfin web keeps its "rewatching in Next Up" switch in the browser
    (`enableOnServer: false`), so the server has no setting to follow.
  - Stopped midway (on an item long enough to resume), it is in `/UserItems/Resume` on both. 10.11.11 unmarks the episode
    when it starts (`Played` false); 12.4.0 keeps it watched while it plays, so its resume item is `Played: true` with a
    position. A client that hides watched items from Continue Watching never shows it on 12.
  - The Stopped report is answered in 15-30 ms, and the item, Resume and Next Up show it at once.
  - "Mark as watched" (`POST /UserPlayedItems/{id}`) clears the resume point on both, also of an episode 12.4 keeps
    watched while it is watched again: it leaves Resume.
  - One show can have two Next Up episodes with rewatching: S1 E1 to S2 E1 watched, then S1 E2 played again to its end
    lists S1 E3 (first: the show was last watched through S1 E2) and S2 E2; marking S1 E2 watched again does not. On 12.4,
    S1 E2 played again only to 30 % is also in Resume: the show three times. Nixie shows it once.
  - An ordinary user can read `GET /System/Configuration` (200 on both), with `MinResumePct`, `MaxResumePct` and
    `MinResumeDurationSeconds`: Nixie reads them to place a play the moment it ends.

## Not covered yet

The outside-services hunt's untested cases still need setting up:
- a reverse proxy on a sub-path (`qaproxy.py` could take a base path);
- an address ending in "/";
- TLS with a private certificate. Jellyfin's HTTPS needs a certificate and the HTTPS port in `network.xml`, and making
  Windows trust a private CA is a system change: ask the owner first.
