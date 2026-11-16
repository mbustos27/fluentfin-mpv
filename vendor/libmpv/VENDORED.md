# libmpv for the in-process (composition) spike

Not in git (`.gitignore` here): download it again from the record below and check the hashes.

| | |
|---|---|
| Build | shinchiro/mpv-winbuild-cmake, release `20261111`, mpv master `git-bcde6b93cc` (after 0.42.0), x86_64 (not `-v3`) |
| Linked from | mpv.io Installation, Windows: "builds by shinchiro" → https://github.com/shinchiro/mpv-winbuild-cmake/releases |
| Archive URL | https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261111/mpv-dev-x86_64-20261111-git-bcde6b93cc.7z |
| Archive size | 31,776,374 bytes |
| Archive SHA-256 | `273c079b11ea5575822f972756f4c9a71149a5c10130353abeb8586738938452` (matches the digest GitHub publishes for the asset) |
| Downloaded | 2026-11-15, into `download\` (an empty folder of its own) |
| Extracted to | `20261111-git-bcde6b93cc\` with Windows' `tar` (libarchive); nothing from the archive was run except loading `libmpv-2.dll` into the spike host |
| `libmpv-2.dll` | 121,789,952 bytes, SHA-256 `88504ee5560942a955405e99dfb5971368e4cf851321f36565bf2a667271db1b` |
| `include\mpv\client.h` | SHA-256 `3da9b8c61c3fb4e2d76bd248cebf72f3ee830331411b71a76adbd5e05660cc50`, `MPV_CLIENT_API_VERSION` 2.6 |
| `include\mpv\render.h` | SHA-256 `f2550aa29b2e9b20988bd954b4383e9fa05fd9e40d1669fbc6edf2afac603f15` (no `render_d3d11.h` in the package; composition mode does not need it) |
| `libmpv.dll.a` | SHA-256 `bef1b89f534bc86b33135e1f04fa2d5064b9d48b5de8bc9866665bbf43def793` (unused: the host P/Invokes) |

Notes:
- The DLL imports `vulkan-1.dll` (shinchiro builds hard-link it, see backend STARTHERE.md). This PC has one from
  its GPU driver; a machine without one cannot load the DLL.
- Licence: mpv GPLv2-or-later, FFmpeg in this build GPL; combined with this GPLv3 project that is fine for local use;
  distributing it would mean shipping the corresponding source (docs/UPSTREAM.md).
- Composition support is present (`d3d11-output-mode`, `display-swapchain` and `d3d11-composition-size` are in the
  DLL's option table).
