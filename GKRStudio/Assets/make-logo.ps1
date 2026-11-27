# Builds GKR Studio app icons from the source artwork: logo.png (1024, rounded tile, transparent corners) and app.ico.
param([string]$Source, [string]$AssetsDir)
Add-Type -AssemblyName System.Drawing
$src = [Drawing.Bitmap]::FromFile($Source)
# the rounded tile inside the artwork (measured): x 422, y 421, 1204 px, corner radius 242
$tx = 422; $ty = 421; $ts = 1204; $tr = 242

function RoundedTile([int]$size) {
  $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
  $r = $tr * $size / $ts; $d = 2 * $r
  $path = New-Object Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($size - $d, 0, $d, $d, 270, 90)
  $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90); $path.AddArc(0, $size - $d, $d, $d, 90, 90); $path.CloseFigure()
  $brush = New-Object Drawing.TextureBrush($src, (New-Object Drawing.Rectangle $tx, $ty, $ts, $ts))
  $brush.ScaleTransform($size / $ts, $size / $ts)
  $g.FillPath($brush, $path)
  $g.Dispose(); $brush.Dispose(); $path.Dispose()
  $bmp
}

$logo = RoundedTile 1024
$tmp = [IO.Path]::GetTempFileName(); $logo.Save($tmp, [Drawing.Imaging.ImageFormat]::Png); Copy-Item $tmp (Join-Path $AssetsDir 'logo.png') -Force; Remove-Item $tmp

# .ico with PNG-compressed entries
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
  $b = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($b); $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
  $g.DrawImage($logo, 0, 0, $s, $s); $g.Dispose()
  $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
  , $ms.ToArray()
}
$fs = [IO.File]::Create((Join-Path $AssetsDir 'app.ico')); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $pngs[$i].Length
  $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
  $offset += $len
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p, 0, $p.Length) }
$bw.Close()
$logo.Dispose(); $src.Dispose()
"ok"
