# Nixie 1.8 (BH-180-M2, the 1.8 bug hunt's probe O): every Made by Nixie kind switched off in Settings > Collections (run
# with -Setting CollectionsKindsOff=<every kind, as turning each switch off writes them>). Home shows no Made by Nixie row,
# the Collections page shows no Made by Nixie tile and says every kind is off, and the index asks for no people at all
# (More from and the Spotlight off: no people step). Run with `-Script home -NoA11y -Then offscreen\then-180-off.ps1`
# (NIXIE_LAYOUT the theme). Dot-sourced by drive.ps1: every name here starts with o180. ASCII only.
$o180Tag = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
function O180-Lines([string]$Pattern) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
            Get-Content $_.FullName -Encoding utf8 -ErrorAction SilentlyContinue | Where-Object { $_ -match $Pattern }
        })
}
$o180ButtonCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)

# Home: the index read and Home made (its line said), then its end in view
$o180Deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $o180Deadline -and (O180-Lines 'Library index: \d+ titles in \d+ libraries').Count -eq 0) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 4
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); Start-Sleep -Milliseconds 1500
Shot "$o180Tag-off-home-end"
$o180Kickers = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Text' -and $_.Cached.Name -match '(?i)^made by nixie' } catch { $false } })
Check "off180 ($o180Tag): with every kind off, Home shows no Made by Nixie row" ($o180Kickers.Count -eq 0) "$($o180Kickers.Count) 'Made by Nixie' texts"

# the Collections page
Open-Nav 'Collections'
Start-Sleep -Seconds 8
$o180Tiles = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $o180ButtonCond) | Where-Object { try { $_.Current.Name -like '*, made by Nixie, *' } catch { $false } } | ForEach-Object { $_.Current.Name })
Shot "$o180Tag-off-collections"
Check "off180 ($o180Tag): the Collections page shows no Made by Nixie tile (no This week)" ($o180Tiles.Count -eq 0 -and -not (Find-Ui -Name 'This week' -OffscreenToo)) "$($o180Tiles.Count) tiles"
$o180Said = Find-Ui -Name 'Every kind of Made by Nixie collection is turned off in Settings > Collections.' -OffscreenToo
Check "off180 ($o180Tag): the page says every kind is off" ([bool]$o180Said) ''

# the people: none asked (no people step), however long the page stays
Start-Sleep -Seconds 6
$o180People = @(O180-Lines 'people pass|people are in|people by id|people page')
Check "off180 ($o180Tag): the index asks for no people" ($o180People.Count -eq 0) "$($o180People.Count) people lines"
$o180Reqs = @(Get-Requests 0 | Where-Object { "$($_.query.fields)" -match '(?i)people' })
Check "off180 ($o180Tag): no request asks for people" ($o180Reqs.Count -eq 0) "$($o180Reqs.Count) requests"
