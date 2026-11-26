# The basics gate (Nixie 1.8.1), stream mem's checks: class 4 (Back), BH-175-M2. A page come back to by Back (its view model
# kept, PageKeep) cost 6 to 15 MB a visit that never came back: its cards stayed alive with their whole trees. Two causes, each
# with its check (tools\ui-harness\offscreen\then-175-leak.ps1, LK_CASE and LK_DRIVE; the pattern: tools\gate\README.md):
#   leak-memory      by keys: x:Bind's tracker held a card's toggle buttons while it heard the kept item (every user)
#   leak-memory-uia  read by UI Automation each round, as Narrator or any assistive tool reads the page: a card's peer held
#                    its card while UI Automation held the peer
# Both bound Nixie's private bytes: at most 3 MB a round from round 5 to 20 (the app's full collection at each navigation,
# NIXIE_HARNESS_LEAKS, which a release build never reads: Dev).
# And the review's find (in 1.8.0, since 1.6), class 4: collection-place, a Collection page's cards say their place to
# Narrator ("2 of 24") on the first visit and after Back (tools\ui-harness\offscreen\then-181-place.ps1).
@{ Checks = @(
    @{
        Id       = 'leak-memory'
        Class    = 4
        Owner    = 'mem'
        What     = 'a film''s page come back to by Back 20 times (its first More like this card, Enter and Back by keys): each round there and back to the card, and Nixie''s private bytes grow at most 3 MB a round after the fifth'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-175-leak.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ NIXIE_HARNESS_LEAKS = '1'; LK_CASE = 'film'; LK_DRIVE = 'keys'; LK_ROUNDS = '20'; LK_MB_FROM = '5'; LK_MB_PER_ROUND = '3' }
        Themes   = @('Classic', 'DriveIn')
        Sizes    = 'none'
        Dev      = $true   # a release build never reads NIXIE_HARNESS_LEAKS
        Minutes  = 20
        Fails173 = '1.8.0 (a Debug build of 19b91d64, Classic, 2026-11-25): private bytes 402 MB at round 5, 487 at round 20, 5.67 MB a round (the card''s x:Bind tracker held its toggles while it heard the kept item); this branch 0.64'
    }
    @{
        Id       = 'leak-memory-uia'
        Class    = 4
        Owner    = 'mem'
        What     = 'a Collection page of 24 films come back to by Back 20 times while UI Automation reads its cards each round (as Narrator does): each round there and back to the card, and Nixie''s private bytes grow at most 3 MB a round after the fifth'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-175-leak.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ NIXIE_HARNESS_LEAKS = '1'; FAKESERVER_COLLECTIONS = '1'; FAKESERVER_LONG_TITLE = '1'; LK_CASE = 'collection'; LK_DRIVE = 'uia'; LK_ROUNDS = '20'; LK_MB_FROM = '5'; LK_MB_PER_ROUND = '3' }
        Themes   = @('Classic', 'Cinematic')   # Editorial's poster opens its details in place on Enter (no navigation by keys alone)
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 25
        Fails173 = '1.8.0 (a Debug build of 19b91d64, Classic, 2026-11-25): private bytes 417 MB at round 5, 590 at round 20, 11.5 MB a round (each Collection page''s 24 cards kept by their peers while UI Automation held the peers); this branch 0.95'
    }
    @{
        Id       = 'collection-place'
        Class    = 4
        Owner    = 'mem'
        What     = 'a Collection page of 24 films: every card says its place to Narrator ("2 of 24", the number its name leads with), on the first visit and after a film opened from it and Back, and the card given the keyboard says 2 of 24'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-181-place.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_COLLECTIONS = '1'; FAKESERVER_LONG_TITLE = '1' }
        Themes   = @('Classic', 'Editorial')   # the shared card's peer and Editorial's poster's
        Sizes    = 'none'
        Dev      = $false
        Minutes  = 10
        Fails173 = '1.8.0 (a Debug build of 19b91d64, Classic, 2026-11-25): cards 2 to 24 said no place (-1 of -1) on the first visit, 2 to 22 after Back, the card given the keyboard -1 of -1; this branch: every card its own, in Classic and Editorial'
    }
) }
