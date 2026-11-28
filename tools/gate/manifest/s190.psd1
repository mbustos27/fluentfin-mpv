# The basics gate (Nixie 1.9, the scroll stream; release blocker): the owner on the 1.9 test copy at 10:55 PM, "scrolling the
# collections page takes a longgg time", on a library of 190,524 titles (152,800 films, 37,700 shows) with thin metadata. The
# pattern: tools\gate\README.md. The fake server at that shape (FAKESERVER_BIG_LIBRARY=152800, FAKESERVER_BIG_SHOWS=37700: 190,992
# titles in the index) with FAKESERVER_COLLECTIONS and FAKESERVER_FRANCHISES190; the page measured once its first read and the
# people step after it are in (offscreen\then-190-scroll.ps1), timed in frames by the dev build's frame clock (HarnessFrames:
# Dev), at 1280 x 720 and maximised, in the four themes.
# Release: scroll190, a full scroll of the Collections page top to bottom and back by the wheel (a notch every 40 ms), Page Down
# and Page Up, the keys (Down, Up) and a gamepad's D-pad.
# Nightly: scroll190-nav, the same page's See all Categories grid by the wheel, and a notch after a quick spin cut short by a key
# or by a tile opened and Back. Not release: posted wheel input just after a page change sets off WinUI's known fail-fast in
# Microsoft.UI.Input.dll (+0x4e49f, FAST_FAIL_FATAL_APP_EXIT; 1.7.5's, docs\KNOWN-ISSUES.md), on 1.9's b2 build as on this
# branch: 3 of 9 such runs on 2026-11-27, none in the 19 runs without one. The page scrolls the same either way.
@{ Checks = @(
    @{
        Id       = 'scroll190'
        Class    = 4
        Owner    = 's190'
        What     = 'the Collections page at 190,000 titles scrolls top to bottom and back: the wheel reaches its end in the notches its height asks at 96 px a notch (no later than 1 s after the last it needed), Page Down and Page Up move it most of a view a press to its end and back, Down and the D-pad reach its last row with the tile on screen, Up and the D-pad its first, and each full scroll has at most 2 s of frames over 50 ms'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-190-scroll.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_BIG_LIBRARY = '152800'; FAKESERVER_BIG_SHOWS = '37700'; FAKESERVER_COLLECTIONS = '1'; FAKESERVER_FRANCHISES190 = '1'
            SC_PARTS = 'wheel,pgdn,keys,dpad'; SC_BUDGET_MS = '2000' }
        Themes   = 'all'
        Sizes    = 'gate'
        Dev      = $true   # the frame clock is the pointer hook's (a release package never listens)
        Minutes  = 30
        Fails173 = '1.9''s b2 build (a278808a, Classic, 2026-11-26): 27 quick notches moved the page 421 of its 1,992 px (21 %; 17.5 % at 1280 x 720; ScrollView.ScrollBy restarted at every notch), and Page Down moved it 0 px; the first test build and 1.8.1 (the ScrollView''s own wheel, 48 px a notch): 1,296 px (65 %)'
    }
    @{
        Id       = 'scroll190-nav'
        Class    = 4
        Owner    = 's190'
        What     = 'the Collections page at 190,000 titles: See all Categories'' grid scrolls by the wheel to its end and back within the frame budget, and after a quick spin cut short by Down or by a tile opened and Back, one notch moves the page one notch from where that left it'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-190-scroll.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_BIG_LIBRARY = '152800'; FAKESERVER_BIG_SHOWS = '37700'; FAKESERVER_COLLECTIONS = '1'; FAKESERVER_FRANCHISES190 = '1'
            SC_PARTS = 'seeall,mixed'; SC_BUDGET_MS = '2000' }
        Themes   = 'all'
        Sizes    = 'gate'
        Tier     = 'nightly'
        Dev      = $true
        Minutes  = 30
        Fails173 = '1.9''s b2 build (a278808a, Classic, max, 2026-11-27): the wheel took See all''s grid 31.9 % of the way (9 notches), and back only to 3.2 %'
    }
) }
