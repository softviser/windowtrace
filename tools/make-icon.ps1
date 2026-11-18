# Draws the WindowTrace mark and writes assets\app.ico. With -StoreDir it also writes the Microsoft Store
# tiles and listing art into that folder (msix\Assets and listing\assets), so every image comes from one drawing.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 -StoreDir ..\WindowTrace-store
param([string]$StoreDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Rgb([int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb(255, $r, $g, $b) }
$indigo = Rgb 67 56 202      # background, top left
$night  = Rgb 30 27 75       # background, bottom right
$amber  = Rgb 251 191 36     # the trace
$lilac  = Rgb 165 180 252    # title bar of the window

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return , $p
}

function New-Background([single]$x, [single]$y, [single]$w, [single]$h) {
    $rect = New-Object System.Drawing.RectangleF $x, $y, $w, $h
    return New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $indigo, $night, 45.0
}

# The mark on a 64-unit grid: a window, and an amber line tracing it back to the process that launched it.
function Draw-Mark($g, [single]$ox, [single]$oy, [single]$size, [bool]$plate = $true) {
    $s = $size / 64.0
    if ($plate) {
        $bg = New-Background $ox $oy $size $size
        $g.FillPath($bg, (New-RoundRect ($ox + 2 * $s) ($oy + 2 * $s) (60 * $s) (60 * $s) (14 * $s)))
    }

    $pen = New-Object System.Drawing.Pen $amber, (5 * $s)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($ox + 14 * $s), ($oy + 50 * $s)),
        (New-Object System.Drawing.PointF ($ox + 37 * $s), ($oy + 50 * $s)),
        (New-Object System.Drawing.PointF ($ox + 37 * $s), ($oy + 36 * $s)))
    $g.DrawLines($pen, $pts)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $amber), $ox + 7.5 * $s, $oy + 43.5 * $s, 13 * $s, 13 * $s)

    $win = New-RoundRect ($ox + 19 * $s) ($oy + 11 * $s) (36 * $s) (28 * $s) (4.5 * $s)
    $g.FillPath([System.Drawing.Brushes]::White, $win)
    $g.SetClip($win)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $lilac), $ox + 19 * $s, $oy + 11 * $s, 36 * $s, 8.5 * $s)
    $g.ResetClip()
}

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    return $bmp, $g
}

function Save-Png($bmp, $g, [string]$file) {
    $g.Dispose()
    New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# A tile: the mark centred on a transparent canvas, filling $fill of the shorter side.
function Save-Tile([int]$w, [int]$h, [double]$fill, [string]$file) {
    $bmp, $g = New-Canvas $w $h
    $size = [Math]::Min($w, $h) * $fill
    Draw-Mark $g (($w - $size) / 2) (($h - $size) / 2) $size
    Save-Png $bmp $g $file
}

# Listing art: full-bleed background, the mark without its plate, optionally the name and a tagline under it.
function Save-Art([int]$w, [int]$h, [double]$markFill, [double]$markCy, [string]$title, [string]$tagline, [string]$file) {
    $bmp, $g = New-Canvas $w $h
    $g.FillRectangle((New-Background 0 0 $w $h), 0, 0, $w, $h)
    $u = [Math]::Min($w, $h)
    $size = $u * $markFill
    Draw-Mark $g (($w - $size) / 2) ($h * $markCy - $size / 2) $size $false
    $center = New-Object System.Drawing.StringFormat
    $center.Alignment = 'Center'
    if ($title) {
        $font = New-Object System.Drawing.Font 'Segoe UI Semibold', ($u * 0.085), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawString($title, $font, [System.Drawing.Brushes]::White, ($w / 2), ($h * $markCy + $size * 0.5), $center)
    }
    if ($tagline) {
        $font = New-Object System.Drawing.Font 'Segoe UI', ($u * 0.04), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawString($tagline, $font, (New-Object System.Drawing.SolidBrush $lilac), ($w / 2), ($h * $markCy + $size * 0.5 + $u * 0.125), $center)
    }
    Save-Png $bmp $g $file
}

# app.ico: PNG-compressed entries, the sizes Explorer and the taskbar ask for
$root = Split-Path $PSScriptRoot
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($sz in $sizes) {
    $bmp, $g = New-Canvas $sz $sz
    Draw-Mark $g 0 0 $sz
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
New-Item -ItemType Directory -Force "$root\assets" | Out-Null
$bw = New-Object System.IO.BinaryWriter ([System.IO.File]::Create("$root\assets\app.ico"))
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($data in $images) { $bw.Write([byte[]]$data) }
$bw.Close()
Save-Tile 256 256 1 "$root\assets\icon-256.png"
Write-Host "Wrote $root\assets\app.ico and icon-256.png"

if ($StoreDir) {
    $store = (Resolve-Path $StoreDir).Path
    $a = "$store\msix\Assets"
    Save-Tile 44 44 1 "$a\Square44x44Logo.png"
    Save-Tile 88 88 1 "$a\Square44x44Logo.scale-200.png"
    Save-Tile 44 44 1 "$a\Square44x44Logo.targetsize-44_altform-unplated.png"
    Save-Tile 256 256 1 "$a\Square44x44Logo.targetsize-256_altform-unplated.png"
    Save-Tile 50 50 1 "$a\StoreLogo.png"
    Save-Tile 100 100 1 "$a\StoreLogo.scale-200.png"
    Save-Tile 150 150 0.62 "$a\Square150x150Logo.png"
    Save-Tile 300 300 0.62 "$a\Square150x150Logo.scale-200.png"
    Save-Tile 310 150 0.7 "$a\Wide310x150Logo.png"
    Save-Tile 620 300 0.7 "$a\Wide310x150Logo.scale-200.png"

    $l = "$store\listing\assets"
    $tag = 'See what opened that window'
    Save-Tile 300 300 1 "$l\store-logo-300x300.png"
    Save-Tile 150 150 1 "$l\tile-150x150.png"
    Save-Tile 71 71 1 "$l\tile-71x71.png"
    Save-Art 1080 1080 0.5 0.4 'WindowTrace' $tag "$l\box-art-1080x1080.png"
    Save-Art 2160 2160 0.5 0.4 'WindowTrace' $tag "$l\box-art-2160x2160.png"
    Save-Art 720 1080 0.62 0.38 'WindowTrace' $tag "$l\poster-720x1080.png"
    Save-Art 1440 2160 0.62 0.38 'WindowTrace' $tag "$l\poster-1440x2160.png"
    # the Store lays the product name over the hero image, so it carries no text
    Save-Art 1920 1080 0.5 0.5 '' '' "$l\hero-1920x1080.png"
    Save-Art 3840 2160 0.5 0.5 '' '' "$l\hero-3840x2160.png"
    Write-Host "Wrote Store tiles to $a and listing art to $l"
}
