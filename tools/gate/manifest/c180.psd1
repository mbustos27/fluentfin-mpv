# The basics gate (Nixie 1.8 "Collections, better"): 1.8's own checks (BH-180-m3: they were in the repo, in no manifest). The
# pattern: tools\gate\README.md. Release: what the user sees of the week's lineup (Home's rows, This week, See all's direction
# button) and of the tiles' words in the four themes at the gate's sizes (1280 x 720 and maximised); the Collection page's
# reverse toggle in the four themes; the keyboard on This week when the people come in (BH-180-M1) in the four themes; every
# Made by Nixie switch off (BH-180-M2). Nightly (the coordinator, 2026-11-25: "the rest nightly"): the census line and Home's
# first Nixie row timed, each a first start on a 25,000-film library.
@{ Checks = @(
    @{
        Id       = 'week180'
        Class    = 4
        Owner    = 'c180'
        What     = 'Home says and draws its Made by Nixie rows at its end; the Collections page shows This week first under Made by Nixie, its first tile on screen; See all''s direction button says "A to Z, switch to Z to A", Space runs the grid Z to A without the button moving, and the direction is kept after Back'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-180-week.ps1'
        Steps    = 'home'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_COLLECTIONS = '1'; FAKESERVER_COLLIDE = '1' }
        Themes   = 'all'
        Sizes    = 'gate'
        Minutes  = 15
        Fails173 = '1.7.x has no This week and no direction button (week180''s This week and See all checks fail); 1.8 before 54c7d9f2 kept a day-old This week after Back'
    }
    @{
        Id       = 'tiles'
        Class    = 4
        Owner    = 'c180'
        What     = 'a Made by Nixie tile''s words (title, facts, count) drawn inside it and on screen, its Narrator name saying its reason and count, one height a row, award badges with Wikidata on; Home''s Nixie row says its reason line (stream t''s then-180-tiles)'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-180-tiles.ps1'
        Steps    = 'home'
        SignIn   = 'quickconnect'
        Settings = @('LookUpWikidata=true')
        Env      = @{ FAKESERVER_COLLECTIONS = '1'; T8_SIZES = '1280x720,2560x1392' }
        Themes   = 'all'
        Sizes    = 'none'   # it sizes the window itself (T8_SIZES: the gate's two)
        Dev      = $true    # the outside lists from the fake server (NIXIE_OUTSIDE_BASE): a dev build's
        Minutes  = 20
        Fails173 = '1.7.x tiles have no reason line nor count (the tiles stream''s P1-4)'
    }
    @{
        Id       = 'reverse'
        Class    = 4
        Owner    = 'c180'
        What     = 'the Collection page''s direction button next to Order: its Narrator name says the direction, the cards run the other way after a press (keyboard first, then the mouse, then a gamepad''s A), it holds still when it flips, the direction survives Back, Forward and R (stream s''s then-180-reverse)'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-180-reverse.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_COLLECTIONS = '1' }
        Themes   = 'all'
        Sizes    = 'none'   # it sizes the window itself (1920 x 1080)
        Minutes  = 15
        Fails173 = '1.7.x has no direction button'
    }
    @{
        Id       = 'focus180'
        Class    = 1
        Owner    = 'c180'
        What     = 'the keyboard on a This week tile stays on a tile when the people step publishes (This week gains More from and the Spotlight), and a Right arrow after it is still on a tile, on a 25,000-film first start'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-180-focus.ps1'
        Steps    = 'home'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_BIG_LIBRARY = '25000'; FAKESERVER_REAL_SHAPE = '1' }
        Themes   = 'all'
        Sizes    = 'none'   # the keyboard, not the window's size
        Minutes  = 20
        Fails173 = '1.8 before its fix (BH-180-M1, 1dcf4ec8): the keyboard went to Back in Classic and Drive-in'
    }
    @{
        Id       = 'off180'
        Class    = 7
        Owner    = 'c180'
        What     = 'every Made by Nixie kind off in Settings > Collections: Home shows no Made by Nixie row, the Collections page no Made by Nixie tile and says every kind is off, and no request asks for people'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-180-off.ps1'
        Steps    = 'home'
        SignIn   = 'quickconnect'
        Settings = @('CollectionsKindsOff=Franchise,Keyword,HighlyRated,UnfinishedSeries,Seasonal,ShortFilms,NextIn,MoreLike,MoreFrom,Spotlight')
        Env      = @{}
        Themes   = @('Classic')
        Sizes    = 'none'
        Minutes  = 10
        Fails173 = '1.8 before its fix (BH-180-M2, 1dcf4ec8): Home showed 2 rows, This week Next in, More like and More from, and the people step ran'
    }
    @{
        Id       = 'census'
        Class    = 8
        Owner    = 'c180'
        What     = 'the census line at 25,000 films: each kind''s candidates, floor and cap, and the Spotlight''s own line after the first rows (stream s''s then-180-census)'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-180-census.ps1'
        Steps    = 'home'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_BIG_LIBRARY = '25000'; FAKESERVER_COLLECTIONS = '1' }
        Themes   = @('Classic')
        Sizes    = 'none'
        Minutes  = 20
        Fails173 = 'the Spotlight in the build Home''s first row waits on (s''s review Major 2, before cd64b5eb)'
        Tier     = 'nightly'
    }
    @{
        Id       = 'first-row'
        Class    = 8
        Owner    = 'c180'
        What     = 'Home''s first Made by Nixie row timed on a 25,000-film first start (from the index''s first pages) and on the next start (from Home''s saved page, the saved index behind it); durations in the log'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-180-first-row.ps1'
        Steps    = 'home'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_BIG_LIBRARY = '25000' }
        Themes   = @('Classic')
        Sizes    = 'none'
        Minutes  = 25
        Fails173 = 'a timing, not a pass or fail: 1.8 before 9a4ff917 waited for the whole read (11 to 15 s at 25,000 films)'
        Tier     = 'nightly'
    }
) }
