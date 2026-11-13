# Makes the pictures inside the installer's window from the first rug (Pebbles) instead of Inno
# Setup's own: the tall picture on the welcome and finish pages, and the small one at the top
# of the other pages (the app's icon). Several sizes each, for screens with larger scaling.
#   powershell -ExecutionPolicy Bypass -File tools\make_wizard_images.ps1
param([string]$Pattern = "$PSScriptRoot\..\src\Sajjada\Assets\Patterns\pebbles.jpg",
      [string]$Icon = "$PSScriptRoot\..\src\Sajjada\Assets\Sajjada.ico",
      [string]$OutDir = "$PSScriptRoot\..\installer")
Add-Type -AssemblyName PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'

function Save-Bmp($visual, [int]$w, [int]$h, [string]$path) {
    $rt = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($w, $h, 96, 96, 'Pbgra32'); $rt.Render($visual)
    $rgb = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($rt, [System.Windows.Media.PixelFormats]::Bgr24, $null, 0)
    $e = New-Object System.Windows.Media.Imaging.BmpBitmapEncoder; $e.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rgb))
    $fs = [IO.File]::Create($path); $e.Save($fs); $fs.Close()
}

$rug = New-Object System.Windows.Media.Imaging.BitmapImage
$rug.BeginInit(); $rug.UriSource = [Uri](Resolve-Path $Pattern).Path; $rug.CacheOption = 'OnLoad'; $rug.EndInit()
$ico = [System.Windows.Media.Imaging.BitmapDecoder]::Create([Uri](Resolve-Path $Icon).Path, 'None', 'OnLoad')
$big = $ico.Frames | Sort-Object PixelWidth -Descending | Select-Object -First 1

# Tall picture: the rug's own weave filling the panel (its middle, upright), with a soft shade at the edge.
$tall = @(); foreach ($s in @(@(164, 314), @(246, 459), @(328, 604))) {
    $w = $s[0]; $h = $s[1]
    $dv = New-Object System.Windows.Media.DrawingVisual; $dc = $dv.RenderOpen()
    $brush = New-Object System.Windows.Media.ImageBrush($rug)
    $brush.Stretch = 'UniformToFill'
    $dc.DrawRectangle($brush, $null, (New-Object System.Windows.Rect(0, 0, $w, $h)))
    $shade = New-Object System.Windows.Media.LinearGradientBrush([System.Windows.Media.Color]::FromArgb(0, 0, 0, 0), [System.Windows.Media.Color]::FromArgb(70, 0, 0, 0), 0)
    $dc.DrawRectangle($shade, $null, (New-Object System.Windows.Rect(0, 0, $w, $h)))
    $dc.Close()
    $p = Join-Path $OutDir "wizard-$w.bmp"; Save-Bmp $dv $w $h $p; $tall += (Split-Path $p -Leaf)
}

# Small picture: the app's icon on white.
$small = @(); foreach ($s in 55, 83, 110) {
    $dv = New-Object System.Windows.Media.DrawingVisual; $dc = $dv.RenderOpen()
    $dc.DrawRectangle([System.Windows.Media.Brushes]::White, $null, (New-Object System.Windows.Rect(0, 0, $s, $s)))
    $dc.DrawImage($big, (New-Object System.Windows.Rect(0, 0, $s, $s)))
    $dc.Close()
    $p = Join-Path $OutDir "wizard-small-$s.bmp"; Save-Bmp $dv $s $s $p; $small += (Split-Path $p -Leaf)
}
"WizardImageFile=$($tall -join ',')"
"WizardSmallImageFile=$($small -join ',')"
