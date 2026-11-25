# The basics gate: g4's checks (tools\gate\README.md). Classes 6 to 8: playback, the player's controls after each interruption,
# windowed playback, settings, lifecycle. Every check here but player-embedded needs a dev build's hooks (the harness's
# pointer, the fake update feed): Dev = $true.
@{
    Checks = @(
        @{
            Id       = 'playback-basics'
            Class    = 6
            Owner    = 'g4'
            What     = 'play, pause, seek, audio and subtitle switches, next and previous episode, Up next and Keep watching, Back with one Stopped at the position, the resume point on the season row and in Continue watching, Resume from there, close mid-play then Resume after the restart'
            Kind     = 'then'
            Then     = 'tools\gate\checks\6-playback.ps1'
            Steps    = 'wait:1'
            Themes   = 'all'
            Sizes    = 'none'
            Dev      = $true
            Minutes  = 10
            Fails173 = '1.7.3: after a close mid-play the next start opened the server page, not Home (Quick Connect forgotten: 1.7.4''s fix), so Continue watching couldn''t be reached (Classic, the stop and restart parts: 19/20)'
        }
        @{
            Id       = 'player-controls'
            Class    = 6
            Owner    = 'g4'
            What     = 'the player''s bars drawn whole or not at all (never faint, never clickable hidden) after pause, Alt+Tab, minimise, full screen in and out (F, double click, the button, Esc), a seek, a held key, a theme switch and a cut fade; windowed at 1906x1016 and the gate''s sizes, a live resize, maximise and restore, each monitor: the bars span the player, the picture centred and filling its side'
            Kind     = 'then'
            Then     = 'tools\gate\checks\6-player-controls.ps1'
            Steps    = 'season,play'
            Themes   = 'all'
            Sizes    = 'gate'
            Dev      = $true
            Minutes  = 15
            Fails173 = 'the 1.7.3 build has no cut hook (kind 7): with 1.7.4''s bars settle reverted in a scratch build (SettleBars and SettleBarsIfLeftHalfway return at once), the cut part fails as the friend saw: shown in UI Automation, the title not drawn (0 % of a clean show; Classic, 2 of its 6 checks)'
        }
        @{
            Id       = 'player-embedded'
            Class    = 6
            Owner    = 'g4'
            What     = 'with mpv''s own controls, mpv''s video window covers the player whole after each window change (no black band down a side)'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-174-embedded.ps1'
            Steps    = 'season,play'
            Settings = @('MpvPlayerControls=Mpv')
            Themes   = 'all'
            Sizes    = 'none'
            Dev      = $false
            Minutes  = 5
            Fails173 = 'passes on 1.7.3 too (19/19, Classic): the friend''s black band down the side did not come back off-screen; kept as the guard of the 1.7.4 fix'
        }
        @{
            Id       = 'settings-restart'
            Class    = 7
            Owner    = 'g4'
            What     = 'every switch, choice and number on Settings (Made by Nixie''s, the theme''s options and the expanders'' among them) shows its new value at once and keeps it across a restart; Check now finds a newer version on the fake feed, then up to date. The Settings page is one in every theme: Drive-in, the default, has a theme option of its own'
            Kind     = 'then'
            Then     = 'tools\gate\checks\7-settings.ps1'
            Steps    = 'wait:1'
            Env      = @{ G4_PARTS = 'updates,switches' }
            Themes   = @('DriveIn')
            Sizes    = 'none'
            Dev      = $true
            Minutes  = 15
            Fails173 = '1.7.3: its restart opens the server page (Quick Connect forgotten), as playback-basics'' revert run shows with the same restart helper (g4-common.ps1''s G4-Restart)'
        }
        @{
            Id       = 'theme-switch'
            Class    = 7
            Owner    = 'g4'
            What     = 'the layout theme switched in place from the run''s theme to each other one and back on a series'', a season''s and a film''s page (every ordered pair over the four runs): the page stays, drawn, the keyboard in it, Nixie running; Back to the library; chosen on Settings with the film page behind: Back shows that page in the new theme; the theme chosen is in use after a restart'
            Kind     = 'then'
            Then     = 'tools\gate\checks\7-settings.ps1'
            Steps    = 'wait:1'
            Env      = @{ G4_PARTS = 'midpage,theme' }
            Themes   = 'all'
            Sizes    = 'none'
            Dev      = $true
            Minutes  = 8
            Fails173 = 'main before 15f4cd18 (1.7.4''s code): leaving Drive-in on its film or series page ended Nixie (DriveInBoardTextBrush not found; runs mid1, fxmid-d, fxmid-Editorial); 1.7.3 has no theme hook, and its restart forgot Quick Connect. After the fix 25/25 in each theme'
        }
        @{
            Id       = 'lifecycle'
            Class    = 8
            Owner    = 'g4'
            What     = 'minimise and restore keep the page and the keyboard; with the server gone a page says what Nixie couldn''t do in rule 11''s words, with Try again, without hammering the address, and Try again shows it once the server is back; restarts (two in a row, and one started while the last copy is closing) open Home signed in, exit cleanly and stay one device on the server'
            Kind     = 'then'
            Then     = 'tools\gate\checks\8-lifecycle.ps1'
            Before   = 'tools\gate\checks\8-lifecycle-before.ps1'
            Steps    = 'wait:1'
            Themes   = 'all'
            Sizes    = 'none'
            Dev      = $true
            Minutes  = 10
            Fails173 = '1.7.3: Editorial''s film page shows Play and an Audio picker behind "Couldn''t load this movie" (rvoff-e); its restarts open the server page (Quick Connect forgotten)'
        }
        @{
            Id       = 'player-hud'
            Class    = 6
            Owner    = 'g4'
            What     = 'then-174-hud.ps1''s close checks (the title''s white at 90 % of a clean show): the player hidden and shown again, UI Automation''s Invoke in a fade, restored from maximised and sized in a fade, the player opened again'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-174-hud.ps1'
            Steps    = 'season,play'
            Env      = @{ HUD_PARTS = 'collapse,uia,start,open'; HUD_OPENS = '3' }
            Themes   = @('Classic')   # the 2 h budget: Editorial in player-hud-themes, nightly
            Sizes    = 'none'
            Dev      = $true
            Minutes  = 10
            Fails173 = 'not run on 1.7.3 (its collapse needs 1.7.4''s dev hook, kind 8); the same drawn-state reading fails in player-controls with 1.7.4''s bars settle reverted. 71/71 on main (Editorial, 4.3 min)'
        }
        @{
            Id       = 'player-hud-themes'
            Class    = 6
            Owner    = 'g4'
            What     = 'player-hud in Editorial: as player-hud'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-174-hud.ps1'
            Steps    = 'season,play'
            Env      = @{ HUD_PARTS = 'collapse,uia,start,open'; HUD_OPENS = '3' }
            Themes   = @('Editorial')
            Sizes    = 'none'
            Dev      = $true
            Minutes  = 10
            Fails173 = 'as player-hud'
            Tier     = 'nightly'
        }
        @{
            Id       = 'player-keys-overlay'
            Class    = 6
            Owner    = 'g4'
            What     = 'the keys matrix''s overlay section: Space pressed before any click into the player (1.3.1''s lost controls), then after every transition the bars show on a pointer move and on a key, drawn, and hide by themselves'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-keys-matrix.ps1'
            Steps    = 'season,play'
            Env      = @{ NIXIE_MATRIX_ONLY = 'overlay' }
            Themes   = @('DriveIn')   # the 2 h budget: Cinematic in player-keys-overlay-themes, nightly
            Sizes    = 'none'
            Dev      = $true
            Minutes  = 15
            Fails173 = 'guards 1.3.1 and 1.4.0''s lost controls (Space before any click), fixed long before 1.7.3: passes there by design; 183/183 on main (Drive-in, 7 min)'
        }
        @{
            Id       = 'player-keys-overlay-themes'
            Class    = 6
            Owner    = 'g4'
            What     = 'player-keys-overlay in Cinematic: as player-keys-overlay'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-keys-matrix.ps1'
            Steps    = 'season,play'
            Env      = @{ NIXIE_MATRIX_ONLY = 'overlay' }
            Themes   = @('Cinematic')
            Sizes    = 'none'
            Dev      = $true
            Minutes  = 15
            Fails173 = 'as player-keys-overlay'
            Tier     = 'nightly'
        }
        @{
            Id       = 'updates-start-check'
            Class    = 7
            Owner    = 'g4'
            What     = 'with Check for updates on, the start check asks the fake feed (never GitHub) with a User-Agent, the notice names the newer version with its action and Dismiss, and Settings'' Check now says the same'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-update.ps1'
            Steps    = 'home'
            Settings = @('CheckForUpdates=true')
            Env      = @{ FAKESERVER_RELEASE = '9.0.0'; UP_NARROW = '0' }
            Themes   = @('Classic')
            Sizes    = 'none'
            Dev      = $true
            Minutes  = 8
            Fails173 = 'guards 1.3.1 and 1.5''s update check (no 1.7.4 bug lived here)'
        }
    )
}
