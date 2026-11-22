# Generates Assets\app.ico (multi-size, PNG-compressed) for OptiGame+.
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'src\OptiGame.App\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-Master([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    # rounded square with diagonal accent gradient
    $m = [int]($s * 0.04); $r = [int]($s * 0.22); $d = $r * 2; $w = $s - 2 * $m
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $d, $d, 180, 90); $path.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
    $path.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90); $path.AddArc($m, $m + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0, 120, 212)), ([System.Drawing.Color]::FromArgb(255, 124, 77, 255)), 45.0
    $g.FillPath($grad, $path)
    # clearing ring
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ($s * 0.045)
    $c = $s * 0.5; $rr = $s * 0.34
    $g.DrawEllipse($pen, $c - $rr, $c - $rr, 2 * $rr, 2 * $rr)
    # lightning bolt
    $pts = @(
        (New-Object System.Drawing.PointF ($s * 0.56), ($s * 0.16)),
        (New-Object System.Drawing.PointF ($s * 0.28), ($s * 0.55)),
        (New-Object System.Drawing.PointF ($s * 0.47), ($s * 0.55)),
        (New-Object System.Drawing.PointF ($s * 0.41), ($s * 0.84)),
        (New-Object System.Drawing.PointF ($s * 0.72), ($s * 0.42)),
        (New-Object System.Drawing.PointF ($s * 0.52), ($s * 0.42))
    )
    $g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    return $bmp
}

$master = New-Master 256
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
    $b = New-Object System.Drawing.Bitmap $sz, $sz, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($master, 0, 0, $sz, $sz); $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , ($ms.ToArray()); $b.Dispose(); $ms.Dispose()
}
$master.Save((Join-Path $outDir 'app-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)

$fs = [System.IO.File]::Create((Join-Path $outDir 'app.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close(); $fs.Close()
Write-Host "Wrote $(Join-Path $outDir 'app.ico')"
