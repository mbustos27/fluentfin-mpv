<#
  Draws the installer's two pictures in Nixie's dusk style with System.Drawing (no stock art), using the icon's nixie tube
  (tools\icon\make-icon.ps1), and writes 24-bit BMPs, which is what Windows Installer shows:
    <Out>\Banner.bmp  493x58   the strip across the top of the middle pages (black text goes over its left side, so that
                               side stays light)
    <Out>\Dialog.bmp  493x312  the welcome and finish pages: a dusk panel on the left 164 px (deep maroon at the top to
                               crimson at the horizon, a striped half sun, thin scanlines), the page on the right
  Usage: make-installer-art.ps1 -Out <folder>
#>
param([Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null
$iconTemp = Join-Path $Out 'icon'
& (Join-Path $PSScriptRoot '..\..\icon\make-icon.ps1') -Out $iconTemp | Out-Null
$tube = [System.Drawing.Image]::FromFile((Join-Path $iconTemp 'Nixie-256.png'))
function C([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    return @($bmp, $g)
}
$Page = C 255 0xFF 0xF8 0xF5      # the page: a warm white
$Hairline = C 255 0xF2 0xD4 0xCE

# a striped half sun on the horizon: bands of the sunset swatches, cut by gaps that widen towards the horizon
function Add-Sun($g, [single]$cx, [single]$horizon, [single]$r) {
    $sun = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sun.AddPie($cx - $r, $horizon - $r, 2 * $r, 2 * $r, 180, 180)
    $rect = New-Object System.Drawing.RectangleF ($cx - $r), ($horizon - $r), (2 * $r), $r
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (C 255 0xF5 0xA8 0xA5), (C 255 0xF9 0x69 0x85), 90.0
    $g.FillPath($fill, $sun)
    $gap = New-Object System.Drawing.SolidBrush (C 255 0x8E 0x14 0x3A)
    $y = $horizon - $r * 0.42; $h = 2.0
    while ($y -lt $horizon) { $g.FillRectangle($gap, $cx - $r - 1, $y, 2 * $r + 2, $h); $y += 9; $h += 1.2 }
}

# --- Dialog.bmp
$bmp, $g = New-Canvas 493 312
$g.Clear($Page)
$panel = New-Object System.Drawing.Rectangle 0, 0, 164, 312
$sky = New-Object System.Drawing.Drawing2D.LinearGradientBrush $panel, (C 255 0x3A 0x04 0x18), (C 255 0xBC 0x1E 0x47), 90.0
$blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
$blend.Colors = @((C 255 0x2E 0x03 0x14), (C 255 0x50 0x07 0x23), (C 255 0xBC 0x1E 0x47))
$blend.Positions = @(0.0, 0.55, 1.0)
$sky.InterpolationColors = $blend
$g.FillRectangle($sky, $panel)
Add-Sun $g 82 268 64
# the horizon: a near-black hill line
$hill = New-Object System.Drawing.Drawing2D.GraphicsPath
$hill.AddLines([System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 0, 270), (New-Object System.Drawing.PointF 40, 262), (New-Object System.Drawing.PointF 70, 268),
    (New-Object System.Drawing.PointF 120, 258), (New-Object System.Drawing.PointF 164, 266), (New-Object System.Drawing.PointF 164, 312), (New-Object System.Drawing.PointF 0, 312)))
$g.FillPath((New-Object System.Drawing.SolidBrush (C 255 0x24 0x02 0x10)), $hill)
# faint scanlines over the sky (3 px apart, peach, never green)
$scan = New-Object System.Drawing.Pen (C 18 0xF7 0xAA 0x9C), 1
for ($y = 1; $y -lt 258; $y += 3) { $g.DrawLine($scan, 0, $y, 164, $y) }
$g.DrawImage($tube, 22, 52, 120, 120)
# a quiet hairline between the panel and the page
$g.DrawLine((New-Object System.Drawing.Pen $Hairline, 1), 164, 0, 164, 312)
$g.Dispose(); $bmp.Save((Join-Path $Out 'Dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()

# --- Banner.bmp
$bmp, $g = New-Canvas 493 58
$g.Clear($Page)
$strip = New-Object System.Drawing.Rectangle 0, 0, 493, 58
$gr2 = New-Object System.Drawing.Drawing2D.LinearGradientBrush $strip, $Page, (C 255 0xF9 0xD9 0xD2), 0.0
$g.FillRectangle($gr2, $strip)
# a small sun behind the tube, then the tube
Add-Sun $g 452 58 34
$g.DrawImage($tube, 428, 5, 48, 48)
$g.DrawLine((New-Object System.Drawing.Pen $Hairline, 1), 0, 57, 493, 57)
$g.Dispose(); $bmp.Save((Join-Path $Out 'Banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
$tube.Dispose()
Write-Host "wrote $Out\Banner.bmp and $Out\Dialog.bmp"
