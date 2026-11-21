# 1.5 qacheck: a -Before script of tools\ui-harness\run-offscreen.ps1: the server's address is typed with a "/" at its end
# ("http://127.0.0.1:<port>/jellyfin/"), as a user copies it from a browser's address bar. drive.ps1's sign-in types
# $script:signInAddress instead of the server's URL; the harness's own calls keep the URL without it.
# Dot-sourced by drive.ps1 on the server page; it leaves the page as it found it. subpath-probe.ps1 runs it.

$script:signInAddress = $script:serverUrl.TrimEnd('/') + '/'
Say "before-qa-slash: the sign-in types '$($script:signInAddress)'"
