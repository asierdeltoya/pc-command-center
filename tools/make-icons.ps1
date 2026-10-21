param([string]$OutDir)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Draws the mark (monitor frame + pulse line) in white into a square of size $s at offset.
function Draw-Mark($g, [float]$s, [float]$ox, [float]$oy, [bool]$simple) {
    $w = [Math]::Max(1.5, $s * 0.075)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    if (-not $simple) {
        $frame = New-RoundedPath ($ox + $s * 0.14) ($oy + $s * 0.2) ($s * 0.72) ($s * 0.5) ($s * 0.09)
        $g.DrawPath($pen, $frame)
        $g.DrawLine($pen, $ox + $s * 0.38, $oy + $s * 0.82, $ox + $s * 0.62, $oy + $s * 0.82)
    }
    $pts = @(
        [System.Drawing.PointF]::new($ox + $s * 0.24, $oy + $s * 0.46),
        [System.Drawing.PointF]::new($ox + $s * 0.38, $oy + $s * 0.46),
        [System.Drawing.PointF]::new($ox + $s * 0.45, $oy + $s * 0.32),
        [System.Drawing.PointF]::new($ox + $s * 0.54, $oy + $s * 0.6),
        [System.Drawing.PointF]::new($ox + $s * 0.61, $oy + $s * 0.42),
        [System.Drawing.PointF]::new($ox + $s * 0.76, $oy + $s * 0.42)
    )
    if ($simple) { $pts = $pts | ForEach-Object { [System.Drawing.PointF]::new($_.X, $_.Y + $s * 0.05) } }
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
    $pen.Dispose()
}

function New-Tile([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $m = [Math]::Max(0.5, $size * 0.02)
    $rect = New-Object System.Drawing.RectangleF $m, $m, ($size - 2 * $m), ($size - 2 * $m)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0x6E, 0x7F, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0x9B, 0x5C, 0xF6)), 45
    $path = New-RoundedPath $m $m ($size - 2 * $m) ($size - 2 * $m) ($size * 0.22)
    $g.FillPath($brush, $path)
    Draw-Mark $g $size 0 0 ($size -le 24)
    $g.Dispose()
    return $bmp
}

New-Item -ItemType Directory -Force $OutDir | Out-Null

# logo.png: white mark on transparent background (shown over gradient tiles in the UI)
$logo = New-Object System.Drawing.Bitmap 128, 128, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($logo)
$g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
Draw-Mark $g 128 0 0 $false
$g.Dispose()
$logo.Save((Join-Path $OutDir 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# app.ico: PNG-compressed entries for every standard size
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = New-Tile $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create((Join-Path $OutDir 'app.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $pngs[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close()

# 256px preview for the installer wizard
$big = New-Tile 256
$big.Save((Join-Path $OutDir 'icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()
Write-Output "Icons written to $OutDir"
