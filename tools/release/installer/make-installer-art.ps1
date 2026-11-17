<#
  Draws the installer's two pictures in Seaglass's sea-glass style with System.Drawing (no stock art), using the icon's pebble
  (tools\icon\make-icon.ps1), and writes 24-bit BMPs, which is what Windows Installer shows:
    <Out>\Banner.bmp  493x58   the strip across the top of the middle pages (text goes over its left side)
    <Out>\Dialog.bmp  493x312  the welcome and finish pages: a sea-glass panel on the left 164 px, the page on the right
  Usage: make-installer-art.ps1 -Out <folder>
#>
param([Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null
$iconTemp = Join-Path $Out 'icon'
& (Join-Path $PSScriptRoot '..\..\icon\make-icon.ps1') -Out $iconTemp | Out-Null
$pebble = [System.Drawing.Image]::FromFile((Join-Path $iconTemp 'Seaglass-256.png'))
function C([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    return @($bmp, $g)
}
# soft frosted discs (sea glass shards): a translucent fill and a lighter rim
function Add-Disc($g, [single]$x, [single]$y, [single]$d, [int]$alpha, [System.Drawing.Color]$tint) {
    $b = New-Object System.Drawing.SolidBrush (C $alpha $tint.R $tint.G $tint.B)
    $g.FillEllipse($b, $x, $y, $d, $d)
    $p = New-Object System.Drawing.Pen (C ([Math]::Min(255, $alpha + 40)) 255 255 255), 1
    $g.DrawEllipse($p, $x, $y, $d, $d)
}

# --- Dialog.bmp
$bmp, $g = New-Canvas 493 312
$g.Clear((C 255 247 252 251))
$panel = New-Object System.Drawing.Rectangle 0, 0, 164, 312
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $panel, (C 255 170 240 228), (C 255 22 138 146), 70.0
$g.FillRectangle($grad, $panel)
$aqua = C 255 255 255 255
Add-Disc $g -30 20 110 40 $aqua; Add-Disc $g 90 70 70 34 $aqua; Add-Disc $g 10 230 90 30 $aqua; Add-Disc $g 100 255 40 40 $aqua
$g.DrawImage($pebble, 22, 70, 120, 120)
# a quiet hairline between the panel and the page, and two faint shards on the page
$g.DrawLine((New-Object System.Drawing.Pen (C 255 205 232 230), 1), 164, 0, 164, 312)
Add-Disc $g 400 250 120 26 (C 255 120 205 200); Add-Disc $g 440 205 40 30 (C 255 120 205 200)
$g.Dispose(); $bmp.Save((Join-Path $Out 'Dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()

# --- Banner.bmp
$bmp, $g = New-Canvas 493 58
$g.Clear((C 255 247 252 251))
$strip = New-Object System.Drawing.Rectangle 0, 0, 493, 58
$gr2 = New-Object System.Drawing.Drawing2D.LinearGradientBrush $strip, (C 255 247 252 251), (C 255 196 236 230), 0.0
$g.FillRectangle($gr2, $strip)
Add-Disc $g 330 -20 70 30 (C 255 120 205 200); Add-Disc $g 380 20 30 34 (C 255 120 205 200)
$g.DrawImage($pebble, 428, 5, 48, 48)
$g.DrawLine((New-Object System.Drawing.Pen (C 255 205 232 230), 1), 0, 57, 493, 57)
$g.Dispose(); $bmp.Save((Join-Path $Out 'Banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
$pebble.Dispose()
Write-Host "wrote $Out\Banner.bmp and $Out\Dialog.bmp"
