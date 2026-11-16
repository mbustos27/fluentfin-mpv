<#
  Draws the Seaglass icon (a frosted sea-glass pebble: a rounded, slightly irregular shape with an aqua-to-teal
  gradient, a soft white highlight and a white play triangle) with System.Drawing, and writes:
    <Out>\Seaglass-<size>.png  for 16, 24, 32, 48, 64, 128, 256
    <Out>\Seaglass.ico         with all of those sizes (PNG-compressed entries)
    <Out>\Square44x44Logo*.png, StoreLogo*.png  the package manifest's logos (only with -Assets)
  Usage: make-icon.ps1 [-Out <folder>] [-Assets]   (default Out: ui\FluentFin\Assets)
  Every size is drawn at its own resolution from the same shape (4x supersampled), so small sizes stay crisp.
#>
param([string]$Out, [switch]$Assets)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $Out) { $Out = Join-Path $repo 'ui\FluentFin\Assets' }
New-Item -ItemType Directory -Force $Out | Out-Null

function New-Pebble([int]$size) {
    $ss = 4; $n = $size * $ss
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    # an irregular rounded blob: a closed curve through twelve points, slightly off a circle
    $c = $n / 2.0
    $radii = 0.46, 0.485, 0.47, 0.495, 0.465, 0.485, 0.455, 0.49, 0.47, 0.495, 0.46, 0.48
    $pts = @()
    for ($i = 0; $i -lt 12; $i++) {
        $a = [Math]::PI * 2 * $i / 12 - [Math]::PI / 2 + 0.12
        $pts += New-Object System.Drawing.PointF ([single]($c + [Math]::Cos($a) * $n * $radii[$i])), ([single]($c + [Math]::Sin($a) * $n * $radii[$i]))
    }
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddClosedCurve($pts, 0.5)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $n, $n
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 170, 240, 228)), ([System.Drawing.Color]::FromArgb(255, 22, 138, 146)), 55.0
    $g.FillPath($grad, $path)
    # frosted rim: a thin lighter edge
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), ([single]([Math]::Max(1, $n * 0.012)))
    $g.DrawPath($pen, $path)
    # soft white highlight, upper left, clipped to the pebble
    $g.SetClip($path)
    $hl = New-Object System.Drawing.Drawing2D.GraphicsPath
    $hl.AddEllipse([single]($n * 0.14), [single]($n * 0.09), [single]($n * 0.5), [single]($n * 0.3))
    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush $hl
    $pgb.CenterColor = [System.Drawing.Color]::FromArgb(210, 255, 255, 255)
    $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $m = New-Object System.Drawing.Drawing2D.Matrix; $m.RotateAt(-28, (New-Object System.Drawing.PointF ([single]($n * 0.39)), ([single]($n * 0.24))))
    $g.Transform = $m
    $g.FillPath($pgb, $hl)
    $g.ResetTransform(); $g.ResetClip()
    # white play triangle, optically centred (a little right of centre)
    $t = $n * 0.21
    $tri = @(
        (New-Object System.Drawing.PointF ([single]($c - $t * 0.55)), ([single]($c - $t * 0.95))),
        (New-Object System.Drawing.PointF ([single]($c - $t * 0.55)), ([single]($c + $t * 0.95))),
        (New-Object System.Drawing.PointF ([single]($c + $t * 1.05)), ([single]$c)))
    $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(50, 0, 60, 70))
    $g.TranslateTransform(0, [single]($n * 0.012)); $g.FillPolygon($shadow, $tri); $g.ResetTransform()
    $g.FillPolygon([System.Drawing.Brushes]::White, $tri)
    $g.Dispose()
    $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = 'HighQualityBicubic'; $g2.PixelOffsetMode = 'HighQuality'; $g2.SmoothingMode = 'AntiAlias'
    $g2.DrawImage($bmp, 0, 0, $size, $size); $g2.Dispose(); $bmp.Dispose()
    return $out
}
function Get-PngBytes($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return ,$ms.ToArray() }

$sizes = 16, 24, 32, 48, 64, 128, 256
$png = @{}
foreach ($s in $sizes) {
    $b = New-Pebble $s; $png[$s] = Get-PngBytes $b; $b.Dispose()
    [System.IO.File]::WriteAllBytes((Join-Path $Out "Seaglass-$s.png"), $png[$s])
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
$bw.Flush(); [System.IO.File]::WriteAllBytes((Join-Path $Out 'Seaglass.ico'), $ms.ToArray())

if ($Assets) {
    # the manifest's logos: the same names as the files they replace
    $map = [ordered]@{
        'Square44x44Logo.altform-lightunplated_targetsize-24.png' = 24; 'Square44x44Logo.altform-lightunplated_targetsize-32.png' = 32
        'Square44x44Logo.altform-unplated_targetsize-24.png' = 24; 'Square44x44Logo.altform-unplated_targetsize-32.png' = 32
        'Square44x44Logo.scale-125.png' = 55; 'Square44x44Logo.scale-150.png' = 66
        'Square44x44Logo.targetsize-24.png' = 24; 'Square44x44Logo.targetsize-32.png' = 32
        'StoreLogo.scale-125.png' = 63; 'StoreLogo.scale-150.png' = 75
    }
    foreach ($k in $map.Keys) { $b = New-Pebble $map[$k]; [System.IO.File]::WriteAllBytes((Join-Path $Out $k), (Get-PngBytes $b)); $b.Dispose() }
}
Write-Host "wrote $Out\Seaglass.ico and Seaglass-<size>.png"
