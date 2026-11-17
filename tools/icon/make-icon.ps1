<#
  Draws the Nixie icon with System.Drawing, from vector shapes only (no stock or copied art): a nixie tube (a glass tube with
  a domed top, on a dark socket) whose glowing "digit" is a play triangle, lit hot pink to orange like a real neon tube,
  on a deep maroon rounded tile with a pale rim (readable on a light and a dark taskbar). The glow is baked: stacked
  strokes, wide and faint to narrow and bright, with a pale core (docs\reports: "Seaglass rename and retro desert theme",
  icon notes). Writes:
    <Out>\Nixie-<size>.png  for 16, 20, 24, 32, 40, 48, 64, 128, 256
    <Out>\Nixie.ico         with all of those sizes (PNG-compressed entries)
    <Out>\Square44x44Logo*.png, StoreLogo*.png  the package manifest's logos (only with -Assets)
    <Preview>               a sheet of every size at 1x and enlarged, on a dark and a light strip (only with -Preview)
  Usage: make-icon.ps1 [-Out <folder>] [-Assets] [-Preview <file.png>]   (default Out: ui\FluentFin\Assets)
  Every size is drawn at its own resolution from the same shapes (4x supersampled). The small sizes are hand-tuned rather
  than shrunk: up to 32 px the tube fills more of the tile and the socket, mesh, pins and highlight are dropped, and no
  stroke is thinner than 1.5 px.
#>
param([string]$Out, [switch]$Assets, [string]$Preview)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $Out) { $Out = Join-Path $repo 'ui\FluentFin\Assets' }
New-Item -ItemType Directory -Force $Out | Out-Null

# the palette (the report's desert-dusk swatches, plus the neon's orange)
function C([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
$Maroon = C 255 0x50 0x07 0x23      # deep maroon, the tile
$MaroonDeep = C 255 0x2E 0x03 0x14  # the tile's edges
$MaroonLift = C 255 0x74 0x12 0x36  # the tile's centre, behind the tube
$HotPink = C 255 0xF9 0x69 0x85
$Orange = C 255 0xFF 0x8C 0x42
$Peach = C 255 0xF7 0xAA 0x9C       # the rim and the glass edge
$Core = C 255 0xFF 0xE8 0xD6        # the hottest part of the glow

function P([double]$x, [double]$y) { New-Object System.Drawing.PointF ([single]$x), ([single]$y) }
function New-Pen([System.Drawing.Brush]$brush, [double]$width) {
    $p = New-Object System.Drawing.Pen $brush, ([single]$width)
    $p.LineJoin = 'Round'; $p.StartCap = 'Round'; $p.EndCap = 'Round'
    return $p
}
# a rounded rectangle
function New-RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single](2 * $r)
    $path.AddArc([single]$x, [single]$y, $d, $d, 180, 90)
    $path.AddArc([single]($x + $w - 2 * $r), [single]$y, $d, $d, 270, 90)
    $path.AddArc([single]($x + $w - 2 * $r), [single]($y + $h - 2 * $r), $d, $d, 0, 90)
    $path.AddArc([single]$x, [single]($y + $h - 2 * $r), $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}
# the tube: a semicircle on top, straight sides, small rounded corners at the bottom
function New-Tube([double]$x, [double]$top, [double]$w, [double]$bottom, [double]$foot) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([single]$x, [single]$top, [single]$w, [single]$w, 180, 180)
    $path.AddLine([single]($x + $w), [single]($top + $w / 2), [single]($x + $w), [single]($bottom - $foot))
    $path.AddArc([single]($x + $w - 2 * $foot), [single]($bottom - 2 * $foot), [single](2 * $foot), [single](2 * $foot), 0, 90)
    $path.AddArc([single]$x, [single]($bottom - 2 * $foot), [single](2 * $foot), [single](2 * $foot), 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Icon([int]$size) {
    $ss = 4; $n = $size * $ss
    $u = $n / 256.0              # one unit of the 256-unit design
    $px = [double]$ss            # one pixel of the final image
    function W([double]$units, [double]$minPx) { [Math]::Max($units * $u, $minPx * $px) }
    $small = $size -le 32
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    # --- the tile: deep maroon, lifted behind the tube, a pale rim
    $inset = if ($small) { [Math]::Max(0.5 * $px, 6 * $u) } else { 8 * $u }
    $tile = New-RoundRect $inset $inset ($n - 2 * $inset) ($n - 2 * $inset) (52 * $u)
    $tileFill = New-Object System.Drawing.Drawing2D.PathGradientBrush $tile
    $tileFill.CenterPoint = P ($n / 2) ($n * 0.45)
    $tileFill.CenterColor = $MaroonLift
    $tileFill.SurroundColors = @($MaroonDeep)
    $tileFill.FocusScales = New-Object System.Drawing.PointF 0.35, 0.35
    $g.FillPath($tileFill, $tile)
    $rimWidth = W 5 1
    $g.DrawPath((New-Pen (New-Object System.Drawing.SolidBrush (C 170 $Peach.R $Peach.G $Peach.B)) $rimWidth), $tile)

    # --- the tube's geometry: bigger in the tile at small sizes, with no socket
    if ($small) { $tx = 50; $tw = 156; $ttop = 24; $tbottom = 226; $foot = 26 }
    else { $tx = 70; $tw = 116; $ttop = 30; $tbottom = 196; $foot = 14 }
    $tube = New-Tube ($tx * $u) ($ttop * $u) ($tw * $u) ($tbottom * $u) ($foot * $u)

    # the warm light the digit throws on the inside of the glass
    $g.SetClip($tube)
    $halo = New-Object System.Drawing.Drawing2D.GraphicsPath
    $hr = $tw * 0.62
    $hc = P (($tx + $tw / 2) * $u) ((($ttop + $tbottom) / 2 + 6) * $u)
    $halo.AddEllipse([single]($hc.X - $hr * $u), [single]($hc.Y - $hr * $u), [single](2 * $hr * $u), [single](2 * $hr * $u))
    $haloFill = New-Object System.Drawing.Drawing2D.PathGradientBrush $halo
    $haloFill.CenterColor = C 150 0xFF 0x6E 0x5A
    $haloFill.SurroundColors = @((C 0 0xFF 0x6E 0x5A))
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 70 0x14 0x00 0x08)), $tube)  # the dark gas behind the glow
    $g.FillPath($haloFill, $halo)

    if (-not $small) {
        # the anode mesh behind the digit: a fine diagonal grid, faint
        if ($size -ge 64) {
            $mesh = New-Pen (New-Object System.Drawing.SolidBrush (C 34 0xFF 0xC8 0xB4)) (W 1.6 0.6)
            for ($k = -256; $k -le 256; $k += 14) {
                $g.DrawLine($mesh, (P (($k) * $u) (0)), (P (($k + 256) * $u) (256 * $u)))
                $g.DrawLine($mesh, (P (($k + 256) * $u) (0)), (P (($k) * $u) (256 * $u)))
            }
        }
    }
    $g.ResetClip()

    # --- the digit: a play triangle as a neon tube, glow baked as stacked strokes, then a pale core
    if ($small) { $tri = @((P 92 76), (P 92 180), (P 178 128)) } else { $tri = @((P 104 84), (P 104 164), (P 170 124)) }
    $pts = [System.Drawing.PointF[]]@($tri | ForEach-Object { P ($_.X * $u) ($_.Y * $u) })
    $digit = New-Object System.Drawing.Drawing2D.GraphicsPath
    $digit.AddPolygon($pts)
    # hot pink at the top of the digit to orange at its foot (the gradient spans the digit, not the tube, so both show)
    $db = $digit.GetBounds()
    $bounds = New-Object System.Drawing.RectangleF ($db.X - 2 * $u), ($db.Y - 2 * $u), ($db.Width + 4 * $u), ($db.Height + 4 * $u)
    function Neon([int]$alpha) { New-Object System.Drawing.Drawing2D.LinearGradientBrush $bounds, (C $alpha $HotPink.R $HotPink.G $HotPink.B), (C $alpha $Orange.R $Orange.G $Orange.B), 75.0 }
    if ($small) {
        # at 16 to 32 px a filled triangle reads; the glow is one soft ring around it
        $g.DrawPath((New-Pen (Neon 80) (W 22 2.5)), $digit)
        $g.FillPath((Neon 255), $digit)
        $g.DrawPath((New-Pen (Neon 255) (W 8 1.5)), $digit)
    } else {
        foreach ($layer in @(@(34, 34), @(24, 56), @(16, 90), @(11, 130))) {
            $g.DrawPath((New-Pen (Neon $layer[1]) (W $layer[0] ($layer[0] / 5))), $digit)
        }
        $g.FillPath((Neon 80), $digit)
        $g.DrawPath((New-Pen (Neon 255) (W 7 1.5)), $digit)
        $g.DrawPath((New-Pen (New-Object System.Drawing.SolidBrush $Core) (W 2.6 0.75)), $digit)
    }

    # --- the glass: a pale edge and, from 48 px, a highlight down the left side
    $g.DrawPath((New-Pen (New-Object System.Drawing.SolidBrush (C 190 $Peach.R $Peach.G $Peach.B)) (W 4.5 1.5)), $tube)
    if (-not $small) {
        $hl = New-RoundRect (($tx + 12) * $u) (($ttop + 34) * $u) (9 * $u) (($tbottom - $ttop - 60) * $u) (4.5 * $u)
        $hlRect = New-Object System.Drawing.RectangleF ([single](($tx + 12) * $u)), ([single](($ttop + 30) * $u)), ([single](9 * $u)), ([single](($tbottom - $ttop - 50) * $u))
        $hlFill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $hlRect, (C 120 255 255 255), (C 0 255 255 255), 90.0
        $g.FillPath($hlFill, $hl)
        # a short glint on the dome
        $g.DrawArc((New-Pen (New-Object System.Drawing.SolidBrush (C 150 255 255 255)) (W 3 0.75)), [single](($tx + 14) * $u), [single](($ttop + 14) * $u), [single](($tw - 28) * $u), [single](($tw - 28) * $u), 200, 40)

        # --- the socket the tube stands in, and its pins
        $socket = New-RoundRect (($tx - 6) * $u) (($tbottom - 4) * $u) (($tw + 12) * $u) (24 * $u) (6 * $u)
        $socketRect = New-Object System.Drawing.RectangleF ([single](($tx - 6) * $u)), ([single](($tbottom - 4) * $u)), ([single](($tw + 12) * $u)), ([single](24 * $u))
        $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $socketRect, (C 255 0x5A 0x14 0x2C), (C 255 0x1A 0x02 0x0C), 90.0), $socket)
        $g.DrawLine((New-Pen (New-Object System.Drawing.SolidBrush (C 140 $Peach.R $Peach.G $Peach.B)) (W 2.5 0.75)), (P (($tx - 2) * $u) (($tbottom - 3) * $u)), (P (($tx + $tw + 2) * $u) (($tbottom - 3) * $u)))
        if ($size -ge 64) {
            $pin = New-Pen (New-Object System.Drawing.SolidBrush (C 200 0xC9 0x8B 0x7E)) (W 4 1)
            foreach ($xPin in @(98, 118, 138, 158)) { $g.DrawLine($pin, (P ($xPin * $u) (($tbottom + 20) * $u)), (P ($xPin * $u) (($tbottom + 30) * $u))) }
        }
    }
    $g.Dispose()

    $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = 'HighQualityBicubic'; $g2.PixelOffsetMode = 'HighQuality'; $g2.SmoothingMode = 'AntiAlias'
    $g2.DrawImage($bmp, 0, 0, $size, $size); $g2.Dispose(); $bmp.Dispose()
    return $out
}
function Get-PngBytes($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return ,$ms.ToArray() }

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$png = @{}
foreach ($s in $sizes) {
    $b = New-Icon $s; $png[$s] = Get-PngBytes $b; $b.Dispose()
    [System.IO.File]::WriteAllBytes((Join-Path $Out "Nixie-$s.png"), $png[$s])
}
# the .ico: header, directory, then the PNG images
$ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$png[$s].Length); $bw.Write([uint32]$offset)
    $offset += $png[$s].Length
}
foreach ($s in $sizes) { $bw.Write($png[$s]) }
$bw.Flush(); [System.IO.File]::WriteAllBytes((Join-Path $Out 'Nixie.ico'), $ms.ToArray())

if ($Assets) {
    # the manifest's logos: the same names as the files they replace
    $map = [ordered]@{
        'Square44x44Logo.altform-lightunplated_targetsize-24.png' = 24; 'Square44x44Logo.altform-lightunplated_targetsize-32.png' = 32
        'Square44x44Logo.altform-unplated_targetsize-24.png' = 24; 'Square44x44Logo.altform-unplated_targetsize-32.png' = 32
        'Square44x44Logo.scale-125.png' = 55; 'Square44x44Logo.scale-150.png' = 66
        'Square44x44Logo.targetsize-24.png' = 24; 'Square44x44Logo.targetsize-32.png' = 32
        'StoreLogo.scale-125.png' = 63; 'StoreLogo.scale-150.png' = 75
    }
    foreach ($k in $map.Keys) { $b = New-Icon $map[$k]; [System.IO.File]::WriteAllBytes((Join-Path $Out $k), (Get-PngBytes $b)); $b.Dispose() }
}

if ($Preview) {
    # every size at 1x and at 4x (nearest neighbour, to judge the pixels), on a dark and on a light taskbar colour
    # (up to 64 px enlarged 4x; 128 and 256 at 1x only)
    $zoom = 4; $pad = 16
    function Column([int]$s) { [Math]::Max($(if ($s -le 64) { $s * $zoom } else { $s }), 64) }
    $width = $pad + ($sizes | ForEach-Object { (Column $_) + $pad } | Measure-Object -Sum).Sum
    $h = $pad + 256 + $pad + 256 + $pad
    $sheet = New-Object System.Drawing.Bitmap ([int]$width), ([int](2 * $h)), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
    $y = 0
    foreach ($bg in @((C 255 0x20 0x20 0x20), (C 255 0xF3 0xF3 0xF3))) {
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $bg), 0, $y, $sheet.Width, $h)
        $x = $pad
        foreach ($s in $sizes) {
            $img = [System.Drawing.Image]::FromStream((New-Object System.IO.MemoryStream (, $png[$s])))
            $g.DrawImage($img, (New-Object System.Drawing.Rectangle $x, ($y + $pad), $s, $s))
            if ($s -le 64) { $g.DrawImage($img, (New-Object System.Drawing.Rectangle $x, ($y + 2 * $pad + 256), ($s * $zoom), ($s * $zoom))) }
            $img.Dispose()
            $x += (Column $s) + $pad
        }
        $y += $h
    }
    $g.Dispose()
    New-Item -ItemType Directory -Force (Split-Path $Preview) | Out-Null
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png); $sheet.Dispose()
    Write-Host "wrote the preview $Preview"
}
Write-Host "wrote $Out\Nixie.ico and Nixie-<size>.png"
