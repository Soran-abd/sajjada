# Draws the app icon: a small Ushak rug with fringes on a transparent background,
# saved as one .ico holding every size Windows asks for (PNG-compressed entries).
#   powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1
param(
    [string]$Pattern = "$PSScriptRoot\..\src\Sajjada\Assets\Patterns\usak.jpg",
    [string]$Out = "$PSScriptRoot\..\src\Sajjada\Assets\Sajjada.ico",
    [string]$PreviewDir = ""
)
Add-Type -AssemblyName System.Drawing
$texture = [System.Drawing.Image]::FromFile((Resolve-Path $Pattern))
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Fringe length and rug size: the rug keeps its 3:2 shape, centred.
    $fringe = [math]::Max(1, [math]::Round($s * 0.07))
    $w = $s - 2 * $fringe - [math]::Max(0, [math]::Round($s * 0.02))
    $h = [math]::Round($w / 1.5)
    $x = [math]::Round(($s - $w) / 2); $y = [math]::Round(($s - $h) / 2)

    # Soft shadow under the rug (only where there is room for it).
    if ($s -ge 32) {
        $off = [math]::Max(1, [math]::Round($s * 0.012))
        for ($i = 3; $i -ge 1; $i--) {
            $grow = $i * $s / 256.0
            $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(14, 0, 0, 0))
            $g.FillRectangle($brush, [single]($x - $grow), [single]($y + $off - $grow), [single]($w + 2 * $grow), [single]($h + 2 * $grow))
            $brush.Dispose()
        }
    }

    # Fringes: undyed wool strands along both short sides, dark enough to show on white too.
    $wool = [System.Drawing.Color]::FromArgb(255, 196, 172, 132)
    $step = if ($s -le 24) { 2 } else { [math]::Max(2, [math]::Round($s / 24)) }
    $pen = New-Object System.Drawing.Pen $wool, ([single][math]::Max(1, $s / 96.0))
    for ($yy = $y + 1; $yy -lt $y + $h - 1; $yy += $step) {
        $g.DrawLine($pen, [single]($x - $fringe), [single]$yy, [single]$x, [single]$yy)
        $g.DrawLine($pen, [single]($x + $w), [single]$yy, [single]($x + $w + $fringe), [single]$yy)
    }
    $pen.Dispose()

    # The rug itself: the pattern photo scaled into the rectangle.
    $g.DrawImage($texture, (New-Object System.Drawing.Rectangle $x, $y, $w, $h))

    # A faint dark edge so it stands out on light backgrounds.
    if ($s -ge 24) {
        $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 40, 20, 10)), 1
        $g.DrawRectangle($edge, $x, $y, $w - 1, $h - 1)
        $edge.Dispose()
    }
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewDir) { $bmp.Save((Join-Path $PreviewDir "icon-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    $pngs += , $ms.ToArray()
}
$texture.Dispose()

# ICO file: header, one directory entry per size, then the PNG data.
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"wrote $Out ($((Get-Item $Out).Length) bytes, sizes $($sizes -join ', '))"
