# Draws the LANDock icon (a laptop-blue tile with three small monitors) and writes landock.ico (PNG frames, 16-256 px).
# Run once when the artwork changes:  powershell -File make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot 'landock.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-Frame([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $u = $s / 100.0
  # rounded tile
  $r = 22 * $u
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($s - $r, 0, $r, $r, 270, 90)
  $path.AddArc($s - $r, $s - $r, $r, $r, 0, 90); $path.AddArc(0, $s - $r, $r, $r, 90, 90); $path.CloseFigure()
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), ([System.Drawing.Color]::FromArgb(255, 31, 111, 235)), ([System.Drawing.Color]::FromArgb(255, 20, 52, 120))
  $g.FillPath($bg, $path)
  $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 170, 205, 255))
  # three monitors: two small left/middle, one wide on the right (the "TV")
  function Mon($x, $y, $w, $h, $brush) {
    $g.FillRectangle($brush, [single]($x * $u), [single]($y * $u), [single]($w * $u), [single]($h * $u))
    $g.FillRectangle($brush, [single](($x + $w / 2 - 4) * $u), [single](($y + $h) * $u), [single](8 * $u), [single](5 * $u))
  }
  Mon 12 34 22 17 $white
  Mon 38 34 22 17 $white
  Mon 64 26 26 22 $soft
  # a laptop base under them
  $g.FillRectangle($white, [single](10 * $u), [single](66 * $u), [single](80 * $u), [single](5 * $u))
  $g.FillRectangle($soft, [single](30 * $u), [single](71 * $u), [single](40 * $u), [single](3 * $u))
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  return , $ms.ToArray()
}

$frames = foreach ($s in $sizes) { , (New-Frame $s) }
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $bytes = $frames[$i]
  $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s })); $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$bytes.Length); $bw.Write([uint32]$offset)
  $offset += $bytes.Length
}
foreach ($b in $frames) { $bw.Write($b) }
$bw.Dispose(); $fs.Dispose()
Write-Host "wrote $out"
