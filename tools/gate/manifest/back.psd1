# The basics gate (Nixie 1.7.5), back's checks: class 4 (Back), at real shape (the fake server's FAKESERVER_COLLECTIONS,
# FAKESERVER_LONG_TITLE, FAKESERVER_BIG_SERIES=24x13 and FAKESERVER_SEARCH=sunny, as back-gate.ps1 sets them). The pattern:
# tools\gate\README.md. Release: every cell of back-gate.ps1 (then-174-back.ps1) in the four themes, each page's cell at
# 1280 x 720 and maximised (the cells the window's size has no part in, change, refresh and signout, once). Nightly (the
# coordinator, 2026-11-24: "the rest nightly"): back's other two checks, the Collections arrival under load and the leak count
# over 50 Backs. Run time measured 2026-11-24: 6 min per theme at one size; 11.4 min at the gate's two sizes (Classic).
@{ Checks = @(
    @{
        Id       = 'back'
        Class    = 4
        Owner    = 'back'
        What     = 'Back to a page just left (a collection, playlist, Made by Nixie, library, season, series, film, search results, a person''s titles, Collections, Home) shows it as it was within 1.2 s with every answer held 2.5 s: its card on screen with the keyboard, the scroll kept, no loading state; Forward the same; two libraries and two searches each keep their own page (title and results); a film marked watched shows so after Back; R still asks the server; sign-out lets every kept page go; 1.8.1: the Back key with the search box''s list of suggestions open goes Back, no list left'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-174-back.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_COLLECTIONS = '1'; FAKESERVER_LONG_TITLE = '1'; FAKESERVER_BIG_SERIES = '24x13'; FAKESERVER_SEARCH = 'sunny'
                      BK_PAGES = 'collection,forward,playlist,nixie,library,season,series,item,search,person,collections,home,samelibrary,samesearch,searchlist,change,refresh,signout'
                      BK_DELAY_MS = '2500'; BK_LIMIT_MS = '1200' }
        Themes   = 'all'
        Sizes    = 'gate'
        Dev      = $true   # not tried on a release package yet
        Minutes  = 30
        Fails173 = '1.7.3 and 1.7.4: Back loads every page again (the owner''s 1.7.4 report: the collection loads again, a loading state, the place lost); on the 1.7.5 branch samelibrary failed before 755a6338 (Movies'' entry showed the Shows page) and samesearch before 1f6df4e4 (the Back key did nothing from the search box); searchlist fails on 1.8.0 (the list held the Back key)'
    }
    @{
        Id       = 'back-arrival'
        Class    = 4
        Owner    = 'back'
        What     = 'the Collections page opened from the Libraries page''s Made by Nixie tile gives the keyboard to its first Nixie tile, on screen, every time (7 tries at 1920 x 1080 and 1100 x 900)'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-175-arrival.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_COLLECTIONS = '1'; FAKESERVER_COLLIDE = '1' }
        Themes   = @('DriveIn', 'Cinematic')   # where 1.6.1 and 1.7.3 saw the keyboard stay on Back
        Sizes    = 'none'   # it sizes the window itself (AR_WIDTHS)
        Dev      = $true
        Minutes  = 20
        Fails173 = 'not reproduced on 1.7.4 either (28 of 28 took the tile at once under load, 2026-11-24): a load check kept nightly'
        Tier     = 'nightly'
    }
    @{
        Id       = 'back-leak'
        Class    = 4
        Owner    = 'back'
        What     = '50 times an episode opened from a series page and Back: at most 2 series pages alive (the app''s own count, NIXIE_HARNESS_LEAKS)'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-175-leak.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ NIXIE_HARNESS_LEAKS = '1'; FAKESERVER_BIG_SERIES = '24x13' }
        Themes   = @('Classic', 'DriveIn')
        Sizes    = 'none'
        Dev      = $true   # a release build never reads NIXIE_HARNESS_LEAKS
        Minutes  = 20
        Fails173 = 'the 1.7.5 branch before 85ad666d (the review''s Major 3): 51 series pages alive after 50 Backs'
        Tier     = 'nightly'
    }
    @{
        Id       = 'back-leak-collections'
        Class    = 4
        Owner    = 'back'
        What     = '30 times a collection opened from the Collections page (kept whole for Back since 1.8.1) and Back: one Collections page alive and at most 2 collection pages (the app''s own count, NIXIE_HARNESS_LEAKS); the private bytes a round said'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-181-leakcolls.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ NIXIE_HARNESS_LEAKS = '1'; FAKESERVER_COLLECTIONS = '1' }
        Themes   = @('Classic', 'DriveIn')
        Sizes    = 'none'
        Dev      = $true   # a release build never reads NIXIE_HARNESS_LEAKS
        Minutes  = 15
        Fails173 = 'new in 1.8.1 (the back stream''s review): no build kept the Collections page whole before 87b11835'
        Tier     = 'nightly'
    }
) }
