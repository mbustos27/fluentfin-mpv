# Third-party components in `backend/fluentfin`

FluentFin MPV's bar in bridge mode ships these, unchanged except where `VENDORED.md` lists a
local patch. Licence texts are in this folder, next to the component, or in the file itself.

| Component | Files | Licence | Licence text | Source |
|---|---|---|---|---|
| uosc 5.13.0, by tomasklaen and contributors | `mpv/scripts/uosc/` (locally patched: `VENDORED.md`), `mpv/fonts/uosc_textures.ttf` | LGPL-2.1 | `mpv/scripts/uosc/LICENSE.LGPL` | https://github.com/tomasklaen/uosc |
| Material Symbols, by Google (as shipped in uosc's icon font) | `mpv/fonts/uosc_icons.otf` | Apache-2.0 | `LICENSE.Apache-2.0` | https://github.com/google/material-design-icons |
| fzy (Lua port), in uosc | `mpv/scripts/uosc/lib/fzy.lua` | MIT | in the file | https://github.com/swarn/fzy-lua |
| thumbfast (a Windows FFI block), in uosc | `mpv/scripts/uosc/lib/utils.lua`, the block marked as taken from it | MPL-2.0 | https://mozilla.org/MPL/2.0/ | https://github.com/po5/thumbfast |

`LICENSE.Apache-2.0` holds the terms and conditions of the Apache License, Version 2.0
(sections 1-9), copied from the copy `requests` ships (`requests-2.34.2.dist-info/licenses/LICENSE`).

`mpv/scripts/fluentfin.lua` is FluentFin MPV's own, under the project's licence (GPL-3.0).
