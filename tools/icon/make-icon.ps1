<#
  Draws the Nixie icon with System.Drawing, from vector shapes only (no stock or copied art), and writes:
    <Out>\Nixie-<size>.png  for 16, 20, 24, 32, 40, 48, 64, 128, 256
    <Out>\Nixie.ico         with all of those sizes (PNG-compressed entries)
    <Out>\Square44x44Logo*.png, StoreLogo*.png  the package manifest's logos (only with -Assets)
    <Preview>               every size at 1x and enlarged, on a dark and a light strip (only with -Preview)
  Usage: make-icon.ps1 [-Out <folder>] [-Assets] [-Preview <file.png>]   (default Out: ui\FluentFin\Assets)

  The design (docs\reports: "Seaglass rename and retro desert theme", icon notes): a nixie tube whose glowing digit is a
  play triangle, hot pink to orange, on a deep maroon rounded tile.
  - 48 px and up: the tube (a glass dome with a thin rose edge and one soft highlight down its left side) standing in a
    dark cap, the digit drawn as a lit wire (a bright core over a pink-to-orange stroke) with a soft halo behind it.
  - 40 px: the same with a thicker wire.
  - 32, 24 and 20 px: hand-placed on the pixel grid: the tile, a one-pixel glass arch and a solid, lit triangle.
  - 16 px: the tile and the solid triangle only, with its edges on whole pixels.
  Every size is drawn 8x larger and box-filtered down, so an edge placed on a pixel boundary stays sharp.
#>
param([string]$Out, [switch]$Assets, [string]$Preview)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class IconBox {
  // the average of each factor x factor block (premultiplied, so transparent edges keep their colour)
  public static Bitmap Down(Bitmap big, int factor) {
    int w = big.Width / factor, h = big.Height / factor;
    var src = big.LockBits(new Rectangle(0, 0, big.Width, big.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var buf = new byte[src.Stride * big.Height]; Marshal.Copy(src.Scan0, buf, 0, buf.Length); big.UnlockBits(src);
    var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    var dst = outBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    var ob = new byte[dst.Stride * h];
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
      double a = 0, r = 0, g = 0, b = 0;
      for (int dy = 0; dy < factor; dy++) for (int dx = 0; dx < factor; dx++) {
        int i = (y * factor + dy) * src.Stride + (x * factor + dx) * 4;
        double al = buf[i + 3] / 255.0; a += al; b += buf[i] * al; g += buf[i + 1] * al; r += buf[i + 2] * al;
      }
      int o = y * dst.Stride + x * 4; double n = factor * factor;
      if (a > 0) { ob[o] = (byte)Math.Round(b / a); ob[o + 1] = (byte)Math.Round(g / a); ob[o + 2] = (byte)Math.Round(r / a); }
      ob[o + 3] = (byte)Math.Round(a / n * 255);
    }
    Marshal.Copy(ob, 0, dst.Scan0, ob.Length); outBmp.UnlockBits(dst);
    return outBmp;
  }
}
"@
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $Out) { $Out = Join-Path $repo 'ui\FluentFin\Assets' }
New-Item -ItemType Directory -Force $Out | Out-Null

function C([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function Alpha([System.Drawing.Color]$c, [int]$a) { [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B) }
# the palette: the report's desert-dusk swatches, a cleaner orange for the neon, a near-white warm core
$TileTop = C 255 0x6E 0x12 0x36      # deep maroon, a little lighter at the top
$TileBottom = C 255 0x3A 0x05 0x19
$TileEdge = C 255 0x23 0x02 0x0E     # the outer edge (separates the tile from a light taskbar)
$TileLip = C 255 0xB0 0x3A 0x62      # a thin lit lip inside the edge (separates it from a dark one)
$Pink = C 255 0xFF 0x4F 0x7E         # hot pink
$Orange = C 255 0xFF 0x9A 0x3C
$Core = C 255 0xFF 0xF1 0xE4
$Glass = C 255 0xF6 0xB4 0xB0        # the tube's edge
$CapTop = C 255 0x4A 0x10 0x24
$CapBottom = C 255 0x14 0x02 0x09

function P([double]$x, [double]$y) { New-Object System.Drawing.PointF ([single]$x), ([single]$y) }
function RectF([double]$x, [double]$y, [double]$w, [double]$h) { New-Object System.Drawing.RectangleF ([single]$x), ([single]$y), ([single]$w), ([single]$h) }
function New-Pen($brushOrColor, [double]$width) {
    $p = if ($brushOrColor -is [System.Drawing.Color]) { New-Object System.Drawing.Pen $brushOrColor, ([single]$width) } else { New-Object System.Drawing.Pen $brushOrColor, ([single]$width) }
    $p.LineJoin = 'Round'; $p.StartCap = 'Round'; $p.EndCap = 'Round'
    return $p
}
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
# the tube's glass: a half circle on top of straight sides, open at the bottom (the cap covers it)
function New-Dome([double]$x, [double]$top, [double]$w, [double]$bottom) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddLine([single]$x, [single]$bottom, [single]$x, [single]($top + $w / 2))
    $path.AddArc([single]$x, [single]$top, [single]$w, [single]$w, 180, 180)
    $path.AddLine([single]($x + $w), [single]($top + $w / 2), [single]($x + $w), [single]$bottom)
    return $path
}
function New-Triangle([double[]]$pts) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon([System.Drawing.PointF[]]@((P $pts[0] $pts[1]), (P $pts[2] $pts[3]), (P $pts[4] $pts[5])))
    return $path
}
function Neon([System.Drawing.RectangleF]$box, [int]$alpha = 255) {
    New-Object System.Drawing.Drawing2D.LinearGradientBrush $box, (Alpha $Pink $alpha), (Alpha $Orange $alpha), 90.0
}
# a soft round glow: a radial gradient from the centre colour to nothing
function Fill-Halo($g, [double]$cx, [double]$cy, [double]$rx, [double]$ry, [System.Drawing.Color]$color) {
    $e = New-Object System.Drawing.Drawing2D.GraphicsPath
    $e.AddEllipse([single]($cx - $rx), [single]($cy - $ry), [single](2 * $rx), [single](2 * $ry))
    $b = New-Object System.Drawing.Drawing2D.PathGradientBrush $e
    $b.CenterColor = $color
    $b.SurroundColors = @((Alpha $color 0))
    $g.FillPath($b, $e)
}
# the tile: maroon, lighter at the top, a dark outer edge and a thin lit lip just inside it
function Draw-Tile($g, [double]$x, [double]$y, [double]$w, [double]$r, [double]$edge, [double]$lip) {
    $tile = New-RoundRect $x $y $w $w $r
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush (RectF $x $y $w $w), $TileTop, $TileBottom, 90.0), $tile)
    if ($lip -gt 0) {
        $inner = New-RoundRect ($x + $edge + $lip / 2) ($y + $edge + $lip / 2) ($w - 2 * $edge - $lip) ($w - 2 * $edge - $lip) ([Math]::Max(0.5, $r - $edge - $lip / 2))
        $lipBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (RectF $x $y $w $w), (Alpha $TileLip 150), (Alpha $TileLip 40), 90.0
        $g.DrawPath((New-Pen $lipBrush $lip), $inner)
    }
    $outer = New-RoundRect ($x + $edge / 2) ($y + $edge / 2) ($w - $edge) ($w - $edge) ($r - $edge / 2)
    $g.DrawPath((New-Pen (Alpha $TileEdge 230) $edge), $outer)
}

# --- 48 px and up: the full drawing on a 256-unit grid -------------------------------------------------------------------
function Draw-Large($g, [double]$u, [int]$size) {
    $px = 256.0 / $size * $u   # one final pixel, in canvas units
    Draw-Tile $g (12 * $u) (12 * $u) (232 * $u) (44 * $u) ([Math]::Max(2.5 * $u, $px)) ([Math]::Max(2.5 * $u, $px))
    $tx = 68; $tw = 120; $ttop = 30; $tbottom = 200
    $dome = New-Dome ($tx * $u) ($ttop * $u) ($tw * $u) ($tbottom * $u)
    $domeFill = New-Object System.Drawing.Drawing2D.GraphicsPath; $domeFill.AddPath($dome, $false); $domeFill.CloseFigure()
    # inside the glass: a little darker than the tile, then the digit's light
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 90 0x12 0x00 0x08)), $domeFill)
    $g.SetClip($domeFill)
    Fill-Halo $g (130 * $u) (122 * $u) (62 * $u) (66 * $u) (C 95 0xFF 0x5E 0x6A)
    $g.ResetClip()
    # the digit: a play triangle as a lit wire, rounded corners, a pale core
    $tri = New-Triangle @((104 * $u), (86 * $u), (104 * $u), (158 * $u), (164 * $u), (122 * $u))
    $box = $tri.GetBounds(); $box.Inflate([single](8 * $u), [single](8 * $u))
    $wire = if ($size -ge 128) { 9 } elseif ($size -ge 64) { 12 } else { 15 }
    $g.DrawPath((New-Pen (Neon $box 60) ([Math]::Max(($wire + 14) * $u, 4 * $px))), $tri)
    $g.DrawPath((New-Pen (Neon $box 110) ([Math]::Max(($wire + 6) * $u, 3 * $px))), $tri)
    $g.DrawPath((New-Pen (Neon $box) ([Math]::Max($wire * $u, 2 * $px))), $tri)
    $g.DrawPath((New-Pen (Alpha $Core 235) ([Math]::Max($wire * 0.38 * $u, 0.8 * $px))), $tri)
    # the glass edge, brighter on the left where the light catches it
    $edgeBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (RectF ($tx * $u) 0 ($tw * $u) 1), (Alpha $Glass 230), (Alpha $Glass 120), 0.0
    $g.DrawPath((New-Pen $edgeBrush ([Math]::Max(4.5 * $u, 1.2 * $px))), $dome)
    if ($size -ge 64) {
        # one soft highlight inside the glass: down the left side and round into the dome, fading toward the bottom
        $inset = 12; $hx = $tx + $inset; $hw = $tw - 2 * $inset
        $hl = New-Object System.Drawing.Drawing2D.GraphicsPath
        $hl.AddLine([single]($hx * $u), [single](($tbottom - 46) * $u), [single]($hx * $u), [single](($ttop + $inset + $hw / 2) * $u))
        $hl.AddArc([single]($hx * $u), [single](($ttop + $inset) * $u), [single]($hw * $u), [single]($hw * $u), 180, 62)
        $hlBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (RectF 0 (($ttop + $inset) * $u) 1 (($tbottom - 46 - $ttop - $inset) * $u)), (C 120 255 255 255), (C 0 255 255 255), 90.0
        $hlBrush.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY   # no repeat of the bright end past the fade
        $g.DrawPath((New-Pen $hlBrush (5.5 * $u)), $hl)
    }
    # the cap the tube stands in: a maroon band a little wider than the glass, lit along its top edge
    $capRect = RectF (($tx - 6) * $u) (($tbottom - 4) * $u) (($tw + 12) * $u) (22 * $u)
    $cap = New-RoundRect $capRect.X $capRect.Y $capRect.Width $capRect.Height (6 * $u)
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $capRect, (C 255 0x7A 0x22 0x44), (C 255 0x2A 0x05 0x14), 90.0), $cap)
    $g.DrawLine((New-Pen (Alpha $Glass 200) ([Math]::Max(2.5 * $u, $px))), (P (($tx - 1) * $u) (($tbottom - 2.5) * $u)), (P (($tx + $tw + 1) * $u) (($tbottom - 2.5) * $u)))
}

# --- 32, 24, 20 and 16 px: placed on the pixel grid (coordinates in final pixels) ----------------------------------------
$Small = @{
    #     tile radius, arch (x, top, width, bottom) or $null, triangle (x1 y1 x2 y2 x3 y3), halo radius
    32 = @{ R = 7;   Arch = @(8.5, 4.5, 15, 26);  Tri = @(12, 11, 12, 22, 20.5, 16.5);  Halo = 7 }
    24 = @{ R = 5;   Arch = @(6.5, 3.5, 11, 19.5); Tri = @(9, 8, 9, 17, 16, 12.5);       Halo = 5 }
    20 = @{ R = 4.5; Arch = $null;                Tri = @(7, 4, 7, 16, 16.5, 10);     Halo = 0 }
    16 = @{ R = 3.5; Arch = $null;                Tri = @(5, 3, 5, 13, 13.5, 8);      Halo = 0 }
}
function Draw-Small($g, [double]$u, [int]$size) {
    $s = $Small[$size]
    Draw-Tile $g 0 0 ($size * $u) ($s.R * $u) $u $(if ($size -ge 24) { $u } else { 0 })
    $t = $s.Tri
    if ($s.Halo -gt 0) {
        # a faint glow in the glass only from 24 px; below that it only blurs the triangle's edges
        Fill-Halo $g ((($t[0] + $t[4]) / 2) * $u) ($t[5] * $u) ($s.Halo * $u) ($s.Halo * $u) (C 90 0xFF 0x5E 0x6A)
    }
    if ($s.Arch) {
        $a = $s.Arch
        $g.DrawPath((New-Pen (Alpha $Glass 175) $u), (New-Dome ($a[0] * $u) ($a[1] * $u) ($a[2] * $u) ($a[3] * $u)))
        # the cap: a lit top row and a dark one, on whole pixels, one pixel wider than the glass on each side
        $capX = [Math]::Floor($a[0] - 1); $capW = [Math]::Ceiling($a[0] + $a[2] + 1) - $capX
        $g.FillRectangle((New-Object System.Drawing.SolidBrush (Alpha $Glass 205)), [single]($capX * $u), [single]($a[3] * $u), [single]($capW * $u), [single]$u)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $CapBottom), [single]($capX * $u), [single](($a[3] + 1) * $u), [single]($capW * $u), [single]$u)
    }
    $tri = New-Triangle @(($t[0] * $u), ($t[1] * $u), ($t[2] * $u), ($t[3] * $u), ($t[4] * $u), ($t[5] * $u))
    $g.FillPath((Neon $tri.GetBounds()), $tri)
    if ($size -ge 24) {
        # the lit wire: one bright column down the triangle's straight left edge, crisp on the pixel grid
        $g.FillRectangle((New-Object System.Drawing.SolidBrush (Alpha $Core 200)), [single]($t[0] * $u), [single](($t[1] + 1.5) * $u), [single]$u, [single](($t[3] - $t[1] - 3) * $u))
    }
}

function New-Icon([int]$size) {
    $ss = 8; $n = $size * $ss
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'Half'; $g.Clear([System.Drawing.Color]::Transparent)
    if ($Small.ContainsKey($size)) { Draw-Small $g ([double]$ss) $size }
    else { Draw-Large $g ($n / 256.0) $size }
    $g.Dispose()
    $out = [IconBox]::Down($bmp, $ss)
    $bmp.Dispose()
    return $out
}
# a size the table does not hand-tune (the package logos at 55, 63, 66, 75 px) is drawn as the large design
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
    # every size at 1x and, up to 64 px, at 4x (nearest neighbour, to judge the pixels), on a dark and a light strip
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
