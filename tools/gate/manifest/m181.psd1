# The basics gate: stream minors' checks of Nixie 1.8.1 (the fix release). The pattern: tools\gate\README.md. A gap the 1.7.5
# bug hunt found (BH-175-q4): no harness or gate run took the library index's read of a change (the fake server ignored
# MinDateLastSaved); and BH-175-m3's change the count hides, read as every title.
@{ Checks = @(
    @{
        Id       = 'index-change'
        Class    = 8
        Owner    = 'm181'
        What     = 'a film added to Movies on the server is read at the next start as the titles saved since the last read (not every title), and the index has it; a film removed from Movies while one is added to a mixed library the index skips (the count moves by 0) is read as every title, and the removed film is out of the index Made by Nixie is made of'
        Kind     = 'then'
        Then     = 'tools\gate\checks\8-index-change.ps1'
        Steps    = 'wait:1'
        SignIn   = 'quickconnect'
        Settings = @()
        Env      = @{}
        Themes   = @('Classic')   # the index is the same in every theme
        Sizes    = 'none'
        Dev      = $false
        Minutes  = 10
        Fails173 = '1.8.0 (d666e075) with this fake server: the removed-mixed part reads the change as the titles saved since and keeps the removed film in the index (2 of its checks); the 1.8.0 fake server answered every title saved since ("1 titles added and 468 changed"), which the added part refuses'
    }
) }
