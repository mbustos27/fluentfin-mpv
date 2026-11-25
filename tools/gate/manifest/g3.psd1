# The basics gate (Nixie 1.7.5), g3's checks: classes 4 (rows, the top bar, the way up, search, Back) and 5 (item pages), at
# real shape (the fake server's FAKESERVER_REAL_SHAPE, FAKESERVER_BIG_SERIES=24x13, FAKESERVER_COLLECTIONS). The pattern:
# tools\gate\README.md. Run times measured 2026-11-24 (per theme): rows at max 20-25 min, topbar at the gate's three sizes 6-8 min,
# item-buttons 3 min. Back's back-gate.ps1 ("Back is instant", class 4) joins in manifest\back.psd1 at back's merge: its slot.
@{ Checks = @(
    @{
        Id       = 'rows'
        Class    = 4
        Owner    = 'g3'
        What     = 'every horizontal row reaches its last item, in view, by keys, Tab, D-pad, shoulder, tilt, Shift+wheel, wheel, drag and the hover arrows; cast names apart'
        Kind     = 'script'
        Script   = 'tools\gate\checks\4-rows.ps1'
        Args     = @{ Sizes = 'max' }
        Themes   = 'all'
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 45
        Fails173 = '1.7.3 (Cinematic, max, season and cast): 13 of 24 fail: the 13-episode row shows E1-E6, wheel, drag and arrows do nothing, the Seasons strip fails every input, cast names wider than their photo and touching; and a maximised window under 2560 x 1392 fails (RG_MAX_MIN)'
    }
    @{
        Id       = 'rows-720'
        Class    = 4
        Owner    = 'g3'
        What     = 'the same rows at 1280 x 720 (the quick set of inputs: keys, D-pad, tilt, wheel, drag, the arrows)'
        Kind     = 'script'
        Script   = 'tools\gate\checks\4-rows.ps1'
        Args     = @{ Sizes = '1280x720'; Depth = 'quick' }
        Themes   = @('Classic', 'Cinematic')
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 30
        Fails173 = '1.7.3: no hover arrows and rows the wheel and drag did not move (the 1.7.4 rows stream''s grid before its fix)'
        # 1.7.5 (the coordinator's 2 h budget): an extra size of rows, which every release build runs at max in every theme
        Tier     = 'nightly'
    }
    @{
        Id       = 'topbar'
        Class    = 4
        Owner    = 'g3'
        What     = 'each top-bar entry opens its page lit; a show''s pages from Home light Shows, never Home; the way up episode > season > series > library; Back from each; search typed, cleared, opened, Back; Collections; a library of 5,000 whose End reaches its last title; a live resize'
        Kind     = 'then'
        Then     = 'tools\gate\checks\4-topbar.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_REAL_SHAPE = '1'; FAKESERVER_COLLECTIONS = '1'; FAKESERVER_BIG_LIBRARY = '5000' }
        Themes   = 'all'
        Sizes    = 'gate'
        Dev      = $false
        Minutes  = 30
        Fails173 = '1.7.3 (Classic, 1920): 7 of 36 fail: the episode, season and series pages reached from Home keep "Home" lit (the friend''s screenshot)'
    }
    @{
        Id       = 'upnav'
        Class    = 4
        Owner    = 'g3'
        What     = 'the episode page''s crumbs (series, season) by keyboard and mouse, each way up and Back with the keyboard on the crumb, R keeping them; at 1280 and 2560 with a 90-character series name'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-170-upnav.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_REAL_SHAPE = '1'; UP_SIZES = '1280,2560' }
        Themes   = 'all'
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 20
        Fails173 = 'none: the way up shipped in 1.7.0 (then-170-upnav.ps1, registered at real shape)'
    }
    @{
        Id       = 'search'
        Class    = 4
        Owner    = 'g3'
        What     = 'the search box (arrows, Enter, the box clearing, Back) and the results page (top result, groups, chips, people, each way out)'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-search.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_SEARCH = 'sunny' }
        Themes   = @('Classic')   # the 2 h budget: Editorial's results layout in search-themes, nightly
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 25
        Fails173 = 'none: 1.5''s search check, registered (4-topbar.ps1 adds clear and Back at the gate''s sizes)'
    }
    @{
        Id       = 'search-themes'
        Class    = 4
        Owner    = 'g3'
        What     = 'search in Editorial (its own results layout): as search'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-search.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_SEARCH = 'sunny' }
        Themes   = @('Editorial')
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 25
        Fails173 = 'as search'
        Tier     = 'nightly'
    }
    @{
        Id       = 'collections'
        Class    = 4
        Owner    = 'g3'
        What     = 'Made by Nixie on Home, the Libraries page''s Collections group, the Collections page, a collection and Back to the same place'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-160-collections.ps1'
        Steps    = 'home'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_COLLECTIONS = '1'; FAKESERVER_COLLIDE = '1' }
        Themes   = @('Classic')   # the 2 h budget: Drive-in in collections-themes, nightly
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 25
        Fails173 = 'none: 1.6''s Collections check, registered'
    }
    @{
        Id       = 'collections-themes'
        Class    = 4
        Owner    = 'g3'
        What     = 'Collections in Drive-in: as collections'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-160-collections.ps1'
        Steps    = 'home'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_COLLECTIONS = '1'; FAKESERVER_COLLIDE = '1' }
        Themes   = @('DriveIn')
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 25
        Fails173 = 'as collections'
        Tier     = 'nightly'
    }
    @{
        Id       = 'scrollstress'
        Class    = 4
        Owner    = 'g3'
        What     = 'fast scrolling where 1.7.3 crashed once (Drive-in''s library A to Z, a 24-season show''s rows): still running, no [ui] failure, a clean close'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-174-scrollstress.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_BIG_SERIES = '24x13'; FAKESERVER_COLLECTIONS = '1'; SS_ROUNDS = '2' }
        Themes   = @('DriveIn')   # where 1.7.3 crashed
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 20
        Fails173 = 'not reproduced on 1.7.3 either (KNOWN-ISSUES 1.7.4): a stress check kept in the gate'
    }
    @{
        Id       = 'item-buttons'
        Class    = 5
        Owner    = 'g3'
        What     = 'on a film, a series, a season and an episode: watched and favourite by keyboard and mouse, drawn and named, sent once, kept after R; R and Back while the Undo notice shows; Play and Resume open the player and Back returns to the page'
        Kind     = 'then'
        Then     = 'tools\gate\checks\5-item-buttons.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_REAL_SHAPE = '1' }
        Themes   = 'all'
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 25
        Fails173 = '1.7.3 and 1.7.4 (every theme): Back and R do nothing for the 10 s the Undo notice shows after a season is marked (found by this check; fixed in 1.7.5: WindowShortcuts.HoldsTheKeys)'
    }
    @{
        Id       = 'trailer'
        Class    = 5
        Owner    = 'g3'
        What     = 'Trailer opens the theatre (the fake embed, never YouTube) by keyboard and mouse, keeps the keyboard inside, closes on Esc, Close and a click outside with the keyboard back on Trailer'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-trailer.ps1'
        Steps    = 'movie'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{}
        Themes   = @('Classic')   # the 2 h budget: Cinematic in trailer-themes, nightly
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 15
        Fails173 = 'none: 1.3.1''s trailer check, registered'
    }
    @{
        Id       = 'trailer-themes'
        Class    = 5
        Owner    = 'g3'
        What     = 'the trailer theatre in Cinematic: as trailer'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-trailer.ps1'
        Steps    = 'movie'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{}
        Themes   = @('Cinematic')
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 15
        Fails173 = 'as trailer'
        Tier     = 'nightly'
    }
) }
