# The basics gate: g2's checks (classes 2 and 3: servers and every menu). Read by tools\gate\run-gate.ps1; the pattern is
# tools\gate\README.md. Every check needs the dev build's pointer hook (a click off-screen), so Dev = $true.
@{
    Checks = @(
        @{
            Id       = 'servers'
            Class    = 2
            Owner    = 'g2'
            What     = 'add a second server, switch (a click, Enter, gamepad A), remove one through its More options (click, Enter, Space, gamepad A, the menu key; Escape; a live resize), remove the one in use, re-add it, restart: the page, the list and the saved file each time'
            Kind     = 'then'
            Then     = 'tools\gate\checks\2-servers.ps1'
            Steps    = 'wait:1'
            SignIn   = 'quickconnect'
            Settings = @()
            Env      = @{}
            Themes   = 'all'
            Sizes    = '1280x720,max'
            Dev      = $true
            Minutes  = 15
            Fails173 = 'run on the 1.7.3 build (Classic, 2026-11-24): 47 of 52; Remove kept the removed server''s row (174-signin), Remove server on the one in use stayed signed in and its question didn''t say so (BS-m2), the restart after re-adding forgot the Quick Connect sign-in'
        }
        @{
            Id       = 'servermenu'
            Class    = 2
            Owner    = 'g2'
            What     = '1.7.4''s before-174-servermenu as it is: an offline saved server''s More options by click, Enter, Space, gamepad A, the menu key opens only Remove, the page stays, Escape closes it, Remove removes it'
            Kind     = 'script'
            Script   = 'tools\gate\checks\2-servermenu.ps1'
            Args     = @{}
            Themes   = 'all'
            Sizes    = 'none'
            Dev      = $true
            Minutes  = 10
            Fails173 = 'Remove after the sign-in page had been shown left the removed row (174-signin)'
        }
        @{
            Id       = 'menus'
            Class    = 3
            Owner    = 'g2'
            What     = 'every card kind''s menu (the menu key; More options by click, Enter, Space, gamepad A), the item pages'' More options, the episode rows, Sort and View, the Account menu: only its items, nothing else happens, Escape closes it with the keyboard back; each item does what it says; an administrator''s items only for one'
            Kind     = 'then'
            Then     = 'tools\gate\checks\3-menus.ps1'
            Steps    = 'wait:1'
            SignIn   = 'quickconnect'
            Settings = @()
            Env      = @{ FAKESERVER_COLLECTIONS = '1' }   # a collection, for its Order menu
            Themes   = 'all'
            Sizes    = '1280x720,max'
            Dev      = $true
            Minutes  = 25
            Fails173 = 'no 1.7.4 fix lived in these menus (that was the server''s More options: servers, servermenu), so 1.7.3 is expected to pass but for the open bug it finds on both: a series'' and a season''s More options shown to a user who isn''t an administrator, opening nothing'
        }
        @{
            Id       = 'continue-menu'
            Class    = 3
            Owner    = 'g2'
            What     = 'a Continue watching card''s menu: Remove from Continue Watching takes it out with Undo, the keyboard stays in the row, the server''s resume point cleared and put back (then-continue.ps1 as it is)'
            Kind     = 'then'
            Then     = 'tools\ui-harness\offscreen\then-continue.ps1'
            Steps    = 'home'
            SignIn   = 'quickconnect'
            Settings = @()
            Env      = @{}
            Themes   = @('Classic')
            Sizes    = 'none'
            Dev      = $false
            Minutes  = 10
            Fails173 = 'none expected: it guards Seaglass 1.1.4''s Remove from Continue Watching'
        }
    )
}
