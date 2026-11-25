# The basics gate: g1's checks (tools\gate\README.md). Class 1, sign-in and sessions; and the existing closes and self-test.
# Class 1 in every theme without running it four times: the server page, the sign-in page and the Account menu
# (Views\LoginPage, SelectServerPage, TitleBarControl) are the same in every theme, so the whole of class 1 runs in Classic,
# and in Drive-in, Cinematic and Editorial the parts whose pages are the theme's own (its Home after each way back in, its
# library page when the sign-in ends, its Account menu's Sign out and Switch user): the *-themes entries.
@{
    Checks = @(
        @{
            Id       = 'signin-methods'
            Class    = 1
            Owner    = 'g1'
            What     = 'each sign-in method (password, Quick Connect, a saved user picked from the list) is still signed in after a close, an abrupt end and an update; a wrong password says so once; a session ended on the server says so with no request loop; the sign-in pages fit every size'
            Kind     = 'then'
            Then     = 'tools\gate\checks\1-signin-methods.ps1'
            Steps    = 'wait:1'
            Themes   = @('Classic')
            Sizes    = 'gate'
            Minutes  = 30
            Fails173 = 'the Quick Connect legs: 1.7.3 forgot a Quick Connect sign-in at every start (the friend''s report); switch-qc and picked fail on 1.7.4 and rel 34f39c6b (Switch user ended the Quick Connect sign-in; a pick did not sign in)'
        }
        @{
            Id       = 'signin-methods-themes'
            Class    = 1
            Owner    = 'g1'
            What     = 'in the other themes, a remembered Quick Connect sign-in is back on that theme''s Home after a close, an abrupt end and an update, and a session ended on the server says so on its library page with no request loop'
            Kind     = 'then'
            Then     = 'tools\gate\checks\1-signin-methods.ps1'
            Steps    = 'wait:1'
            Env      = @{ GATE_SI_PARTS = 'qc,revoked' }
            Themes   = @('DriveIn', 'Cinematic', 'Editorial')
            Sizes    = 'none'
            Minutes  = 20
            Fails173 = 'as signin-methods'' qc legs'
        }
        @{
            Id       = 'signin-sessions'
            Class    = 1
            Owner    = 'g1'
            What     = 'a remembered password sign-in survives a restart; Sign out sticks; two users and two servers: the last one signs in; a refused, unreachable or silent server says why on the right page, once'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-174-sessions.ps1'
            Steps    = 'wait:1'
            Themes   = @('Classic')
            Sizes    = 'none'
            Minutes  = 30
            Fails173 = '1.7.3: Sign out came back signed in at the next start (bs6), the second of two users was dropped (bs8), a refused saved password was sent at every start (bsC)'
        }
        @{
            Id       = 'signin-sessions-themes'
            Class    = 1
            Owner    = 'g1'
            What     = 'in the other themes, Sign out from the Account menu sticks across a restart, and Switch user to a second user is that user after a restart'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-174-sessions.ps1'
            Steps    = 'wait:1'
            Env      = @{ SS_PARTS = 'bs6,bs8' }
            Themes   = @('DriveIn', 'Cinematic', 'Editorial')
            Sizes    = 'none'
            Minutes  = 20
            Fails173 = 'as signin-sessions'' bs6 and bs8'
        }
        @{
            Id       = 'signin-qc-remember'
            Class    = 1
            Owner    = 'g1'
            What     = 'a Quick Connect sign-in with Remember me is still signed in after a restart (no new sign-in), Sign out forgets it, a refused token opens the sign-in page saying so'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-174-qc-remember.ps1'
            Steps    = 'login'
            Themes   = @('Classic')
            Sizes    = 'none'
            Minutes  = 20
            Fails173 = '1.7.3 forgot a Quick Connect sign-in at the next start (the friend''s report): the restart showed the server page'
        }
        @{
            Id       = 'close-player'
            Class    = 8
            Owner    = 'g1'
            What     = 'closing Nixie from the player page ends it within 5 s with exit code 0 and no first-chance NullReferenceException'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-173-close.ps1'
            Steps    = 'season,play'
            Themes   = @('Classic')   # the 2 h budget: the other themes in close-player-themes, nightly
            Sizes    = 'none'
            Minutes  = 15
            Fails173 = 'none: fixed in 1.7.3 (1.7.2 took up to 20 s and crashed, 0xc000027b)'
        }
        @{
            Id       = 'close-player-themes'
            Class    = 8
            Owner    = 'g1'
            What     = 'close-player in Drive-in, Cinematic and Editorial'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-173-close.ps1'
            Steps    = 'season,play'
            Themes   = @('DriveIn', 'Cinematic', 'Editorial')
            Sizes    = 'none'
            Minutes  = 15
            Fails173 = 'as close-player'
            Tier     = 'nightly'
        }
        @{
            Id       = 'close-drivein-home'
            Class    = 8
            Owner    = 'g1'
            What     = 'closing Nixie from Drive-in''s Home as its hero advances ends it within 2 s with exit code 0'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-173-close-home.ps1'
            Steps    = 'home'
            Themes   = @('DriveIn')
            Sizes    = 'none'
            Minutes  = 15
            Fails173 = 'none: fixed in 1.7.3''s fix round (the hero''s timer had no closing check)'
        }
        @{
            Id       = 'close-nompv'
            Class    = 8
            Owner    = 'g1'
            What     = 'with mpv''s controls and no mpv on the PATH, the player page says mpv isn''t installed, and closing from it ends Nixie within 2 s with exit code 0'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-173-close-nompv.ps1'
            Steps    = 'season'
            Settings = @('MpvPlayerControls=Mpv')
            RunArgs  = @{ WithoutMpv = $true }
            Themes   = @('Classic')
            Sizes    = 'none'
            Minutes  = 15
            Fails173 = 'none: fixed in 1.7.3''s fix round (verify-package''s NoMpv run crashed at the close)'
        }
        @{
            Id       = 'selftest'
            Class    = 6
            Owner    = 'g1'
            What     = 'Nixie.exe --mpv-selftest plays mpv''s test pattern in the player page with FluentFin''s controls, then a normal close ends it and everything it started'
            Kind     = 'script'
            Script   = 'tools\gate\checks\6-selftest.ps1'
            Themes   = @('Classic')
            Sizes    = 'none'
            Minutes  = 10
            Fails173 = 'none: passes on 1.7.3 (a regression check)'
        }
    )
}
