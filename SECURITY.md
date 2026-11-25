# Security policy

## Reporting a vulnerability

Please don't report a security problem in a public issue. Report it privately through GitHub's private vulnerability
reporting: **[Report a vulnerability](https://github.com/mbustos27/nixie/security/advisories/new)** (the Security
tab of this repository). Only the maintainer sees the report, and the conversation stays private until a fix is out.
There is no e-mail address for reports.

Please include:

- the Nixie version (Settings → About), how you run it (the installer, the portable zip or a source build) and your
  Windows version;
- what someone could do, and how: the steps, or a proof of concept;
- what you think would fix it, if you know.

Leave out any server address, user name, password or token, also from logs and screenshots. Nixie's logs
(`%LOCALAPPDATA%\Nixie\Logs`) have tokens removed, but they can still show the server's address.

Nixie is built from [FluentFin](https://github.com/insomniachi/FluentFin) and
[jellyfin-mpv-shim](https://github.com/jellyfin/jellyfin-mpv-shim). A problem in code Nixie shares with them may
affect them too, but please don't send problems you found in Nixie to their maintainers: they can't fix Nixie. If you
believe FluentFin or jellyfin-mpv-shim is affected as well, say so in your report.

## Supported versions

Only the newest release receives fixes.

## Verifying a download

Nixie isn't code-signed, so Windows can't tell you who made the file (SmartScreen says "Unknown publisher"). The
checksum is the check: each release publishes the SHA-256 of its installer and of its portable zip.

- **`Nixie-<version>-win-x64.msi`**, with its SHA-256 in `Nixie-<version>-win-x64.msi.sha256`;
- **`Nixie-<version>-win-x64.zip`**, with its SHA-256 in `Nixie-<version>-win-x64.zip.sha256`.

GitHub also shows each file's SHA-256 next to it on the release page, and the two agree. Download the file and its
`.sha256` into the same folder, open PowerShell there, and compare them before you run anything:

```powershell
$file = 'Nixie-1.8.0-win-x64.msi'
$published = (Get-Content "$file.sha256" -Raw).Split(' ')[0]
(Get-FileHash $file -Algorithm SHA256).Hash -eq $published
```

`True` means the file is the one published here. `False` means it isn't: delete it and don't run it.

An installed copy's updater (Settings → Updates) takes only `Nixie-<version>-win-x64.msi` from this repository's
release, with its `.sha256`. Before installing it, Nixie checks that the `.sha256` agrees with the SHA-256 GitHub
records for the installer, and that the download matches it. A portable copy only opens the release page.
