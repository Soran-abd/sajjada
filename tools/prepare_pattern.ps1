# Turns a top-down rug image into a pattern texture for the app: crops it to 3:2 from the
# centre, scales it to 1440x960, saves it as JPEG in src\Sajjada\Assets\Patterns, and prints
# a fringe colour taken from the rug's own light wool (the brightest tenth of its pixels).
#   powershell -ExecutionPolicy Bypass -File tools\prepare_pattern.ps1 -Source art\tabriz.png -Id tabriz
param(
    [Parameter(Mandatory)] [string]$Source,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9]+$')] [string]$Id,
    [int]$Quality = 90,
    # Scale a nearly-3:2 image to fit instead of cropping it, so a woven border is not cut off.
    [switch]$Stretch
)
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile((Resolve-Path $Source))

# Centre crop to exactly 3:2 (or the whole image with -Stretch).
$w = $img.Width; $h = $img.Height
if ($Stretch) { $cw = $w; $ch = $h }
elseif ($w / $h -gt 1.5) { $cw = [int]($h * 1.5); $ch = $h } else { $cw = $w; $ch = [int]($w / 1.5) }
$crop = New-Object System.Drawing.Rectangle ([int](($w - $cw) / 2)), ([int](($h - $ch) / 2)), $cw, $ch

$out = New-Object System.Drawing.Bitmap 1440, 960, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($out)
$g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'HighQuality'
$g.DrawImage($img, (New-Object System.Drawing.Rectangle 0, 0, 1440, 960), $crop, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose(); $img.Dispose()

$dest = Join-Path $PSScriptRoot "..\src\Sajjada\Assets\Patterns\$Id.jpg"
$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
$params = New-Object System.Drawing.Imaging.EncoderParameters 1
$params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), ([long]$Quality)
$out.Save($dest, $codec, $params)

# Fringe colour: the average of the brightest tenth of a coarse sample of pixels.
$samples = New-Object System.Collections.Generic.List[object]
for ($y = 0; $y -lt 960; $y += 8) { for ($x = 0; $x -lt 1440; $x += 8) {
    $c = $out.GetPixel($x, $y); $samples.Add([pscustomobject]@{ L = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B; C = $c })
} }
$out.Dispose()
$top = $samples | Sort-Object L -Descending | Select-Object -First ([int]($samples.Count / 10))
$r = [int](($top | ForEach-Object { $_.C.R } | Measure-Object -Average).Average)
$gr = [int](($top | ForEach-Object { $_.C.G } | Measure-Object -Average).Average)
$b = [int](($top | ForEach-Object { $_.C.B } | Measure-Object -Average).Average)
"{0}: {1:N0} KB  fringe 0x{2:X2}, 0x{3:X2}, 0x{4:X2}" -f $Id, ((Get-Item $dest).Length / 1KB), $r, $gr, $b
