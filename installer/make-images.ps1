<#
  Draws the setup wizard's side and corner images from the app icon (installer\assets\*.bmp).
  Run it when the icon or the look changes; the .bmp files are committed so a normal build does not need it.
#>
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot 'assets'
New-Item -ItemType Directory -Force $out | Out-Null
$icon = [System.Drawing.Image]::FromFile((Join-Path $root 'src\OptiGame.App\Assets\app-256.png'))

function New-Canvas($w, $h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
    return @($bmp, $g)
}

# side image, 164 x 314 (scaled up by Inno on high DPI)
$bmp, $g = New-Canvas 164 314
$rect = New-Object System.Drawing.Rectangle 0, 0, 164, 314
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(24, 26, 38)), ([System.Drawing.Color]::FromArgb(40, 30, 78)), 90
$g.FillRectangle($grad, $rect)
$g.DrawImage($icon, 32, 52, 100, 100)
$font = New-Object System.Drawing.Font 'Segoe UI Semibold', 17
$small = New-Object System.Drawing.Font 'Segoe UI', 9
$white = [System.Drawing.Brushes]::White
$grey = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 175, 195))
$fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
$g.DrawString('OptiGame+', $font, $white, (New-Object System.Drawing.RectangleF 0, 164, 164, 30), $fmt)
$g.DrawString("Game settings you can`npreview and undo", $small, $grey, (New-Object System.Drawing.RectangleF 0, 198, 164, 40), $fmt)
$bmp.Save((Join-Path $out 'side.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose()

# corner image, 55 x 58
$bmp, $g = New-Canvas 55 58
$g.Clear([System.Drawing.Color]::White)
$g.DrawImage($icon, 4, 5, 47, 47)
$bmp.Save((Join-Path $out 'corner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose(); $icon.Dispose()
Get-ChildItem $out | ForEach-Object { "{0} {1} bytes" -f $_.Name, $_.Length }
