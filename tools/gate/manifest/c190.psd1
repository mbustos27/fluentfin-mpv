# The basics gate (Nixie 1.9 "Collections 2", the ui stream): the Collections page's Categories row and the Franchises row by
# relevance, and Settings' Categories switch, in the four themes at 1280 x 720, 1920 x 1080 and maximised (2560 x 1392). The
# pattern: tools\gate\README.md. The fake server's library is real-sized (FAKESERVER_BIG_LIBRARY=25000: its genres and decades
# make the categories, whatever the owner renames) with FAKESERVER_FRANCHISES190 (two franchises named alike, one started).
# Release: categories190 (what the user sees: the row, its Narrator names, the keyboard and a gamepad to its last tile, Back to
# its place, See all For you or A to Z, the Franchises row's order and the twins' years, the switch off and on); rows190: every input to
# the last tile of every row of the Collections page (the rows check, then-174-rows.ps1), the mouse's among them.
@{ Checks = @(
    @{
        Id       = 'categories190'
        Class    = 4
        Owner    = 'c190'
        What     = 'the Categories row above Franchises with a dozen tiles and See all, each tile named "<name>, made by Nixie, category, ..." and drawn whole; End and the D-pad reach its last tile on screen; a category opened and Back keeps the row''s place; See all opens For you (not A to Z), its order button to A to Z and back; the Franchises row opens on the franchise started, See all A to Z with the twins'' years; the Categories switch off takes the row away, on brings it back'
        Kind     = 'then'
        Then     = 'tools\ui-harness\offscreen\then-190-collections.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{ FAKESERVER_COLLECTIONS = '1'; FAKESERVER_FRANCHISES190 = '1'; FAKESERVER_BIG_LIBRARY = '25000' }
        Themes   = 'all'
        Sizes    = '1280x720,1920x1080,max'
        Minutes  = 30
        Fails173 = '1.8.1 (its revert check): no Categories row nor switch, and the Franchises row opens A to Z ("Harbor Watch" twice, no years)'
    }
    @{
        Id       = 'rows190'
        Class    = 4
        Owner    = 'c190'
        What     = 'every row of the Collections page, the Categories row and the Franchises row among them, reaches its last tile, in view, by keys, D-pad, tilt, wheel, drag and the hover arrows'
        Kind     = 'script'
        Script   = 'tools\gate\checks\4-rows.ps1'
        Args     = @{ Sizes = 'max,1280x720'; Pages = 'collections'; Depth = 'quick'; RowsPerPage = 0 }
        Env      = @{ FAKESERVER_FRANCHISES190 = '1'; FAKESERVER_BIG_LIBRARY = '25000'; RG_WAIT_ROW = 'Categories' }
        Themes   = 'all'
        Sizes    = 'none'
        Dev      = $true
        Minutes  = 40
        Fails173 = '1.8.1 has no Categories row: "Categories" is "no such row"'
        # 2026-11-26: 60/60 in Classic, Drive-in, Cinematic and Editorial at 1920x1080 and 1280x720 after the wheel fix (5d30aeef:
        # the page's ScrollView ignores the wheel); 58/60 before (the Franchises and Keywords rows at the page's end). Release
    }
) }
