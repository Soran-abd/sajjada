# Turns a top-down photo of a leather desk pad into a texture for the app. The pad fills the
# photo but its corners are rounded, with the photo's light background showing outside them.
# This measures that corner curve (along each corner's diagonal, where the background ends)
# and prints it as a share of the width for Patterns.cs: the app cuts the corners away itself
# when it loads the picture, and rounds the shadow the same way. The picture is then made like
# any rug's (prepare_pattern.ps1: 1440x960 JPEG in src\Sajjada\Assets\Patterns).
#   powershell -ExecutionPolicy Bypass -File tools\prepare_leather.ps1 -Source art\cognac.png -Id cognac
param(
    [Parameter(Mandatory)] [string]$Source,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9]+$')] [string]$Id
)
Add-Type -AssemblyName System.Drawing
$src = New-Object System.Drawing.Bitmap (Resolve-Path $Source).Path
$width = $src.Width; $height = $src.Height

function Differs($a, $b) { [math]::Max([math]::Max([math]::Abs($a.R - $b.R), [math]::Abs($a.G - $b.G)), [math]::Abs($a.B - $b.B)) -gt 45 }
$corners = @(@(0, 0, 1, 1), @(($width - 1), 0, -1, 1), @(0, ($height - 1), 1, -1), @(($width - 1), ($height - 1), -1, -1))
$radii = foreach ($c in $corners) {
    $bg = $src.GetPixel($c[0], $c[1]); $k = 0
    while ($k -lt 200 -and -not (Differs $src.GetPixel($c[0] + $c[2] * $k, $c[1] + $c[3] * $k) $bg)) { $k++ }
    # On a corner of radius r, the curve crosses the diagonal r(1 - 1/sqrt 2) in from each edge.
    $k / (1 - 1 / [math]::Sqrt(2))
}
$src.Dispose()
# The largest: a shadow in one corner can end the walk early, and too round a cut only trims a
# sliver of leather, while too tight a one leaves the background showing.
$radius = ($radii | Measure-Object -Maximum).Maximum

& (Join-Path $PSScriptRoot prepare_pattern.ps1) -Source $Source -Id $Id | Out-Null
"{0}: corners {1}  radius {2:N4} of the width  ->  Leather({2:N3})" -f $Id, (($radii | ForEach-Object { '{0:N0}' -f $_ }) -join '/'), ($radius / $width)
