# 生成应用图标:读 assets/app-source.png(人工画的母版),缩成多尺寸拼成 assets/app.ico
# (exe 内嵌图标、安装包、开始菜单快捷方式共用同一份)
# 换了图标只需替换 app-source.png 再跑本脚本;母版至少 256 见方,否则小尺寸会糊。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File tools/make-icon.ps1
param(
  [string]$Source = ""
)
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$srcPng = if ($Source) { $Source } else { Join-Path $root "assets\app-source.png" }
$outIco = Join-Path $root "assets\app.ico"
$outPng = Join-Path $root "assets\app.png"
if (-not (Test-Path $srcPng)) { throw "找不到图标母版:$srcPng" }

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# ---- 读母版并摆成正方形(非方图居中留透明边,不裁不拉变形) ----
$srcMs = New-Object System.IO.MemoryStream (,[System.IO.File]::ReadAllBytes($srcPng))
$src = [System.Drawing.Bitmap]::FromStream($srcMs)
$master = [Math]::Max($src.Width, $src.Height)
Write-Output ("母版 {0}x{1} -> 正方形 {2}: {3}" -f $src.Width, $src.Height, $master, $srcPng)
if ($master -lt 256) { Write-Warning "母版不足 256 见方,256 档会放大失真" }

$bmp = New-Object System.Drawing.Bitmap($master, $master, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Transparent)
$g.DrawImage($src, ([System.Drawing.Rectangle]::new(
  [int](($master - $src.Width) / 2), [int](($master - $src.Height) / 2), $src.Width, $src.Height)))
$g.Dispose(); $src.Dispose(); $srcMs.Dispose()

# ---- 缩到各尺寸 ----
$pngBySize = @{}
foreach ($s in $sizes) {
  $small = New-Object System.Drawing.Bitmap($s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
  $sg = [System.Drawing.Graphics]::FromImage($small)
  $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  # 透明边要垫实,否则 HighQualityBicubic 会把透明像素的 RGB 混进边缘形成暗边
  $sg.Clear([System.Drawing.Color]::Transparent)
  $sg.DrawImage($bmp, ([System.Drawing.Rectangle]::new(0, 0, $s, $s))) | Out-Null
  $sg.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $small.Save($ms, ([System.Drawing.Imaging.ImageFormat]::Png))
  $pngBySize[$s] = $ms.ToArray()
  $ms.Dispose()
  if ($s -eq 256) { $small.Save($outPng, ([System.Drawing.Imaging.ImageFormat]::Png)) }
  $small.Dispose()
}
$bmp.Dispose()

# ---- 打包 .ico:小尺寸用 32bpp DIB,256 用 PNG ----
function Get-BmpPayload {
  param([int]$S, [byte[]]$Png)
  $ms = New-Object System.IO.MemoryStream(,$Png)
  $img = [System.Drawing.Bitmap]::FromStream($ms)
  $area = [System.Drawing.Rectangle]::new(0, 0, $S, $S)
  $data = $img.LockBits($area, ([System.Drawing.Imaging.ImageLockMode]::ReadOnly),
                        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
  $buf = New-Object byte[] ($S * $S * 4)
  [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
  $img.UnlockBits($data) | Out-Null
  $img.Dispose(); $ms.Dispose()

  $out = New-Object System.IO.MemoryStream
  $w = [BitConverter]::GetBytes([int32]$S)
  $out.Write([BitConverter]::GetBytes([int32]40), 0, 4)
  $out.Write($w, 0, 4)
  $out.Write([BitConverter]::GetBytes([int32]($S * 2)), 0, 4)
  $out.Write([BitConverter]::GetBytes([uint16]1), 0, 2)
  $out.Write([BitConverter]::GetBytes([uint16]32), 0, 2)
  $out.Write([BitConverter]::GetBytes([int32]0), 0, 4)
  $out.Write([BitConverter]::GetBytes([int32]($S * $S * 4)), 0, 4)
  $out.Write([BitConverter]::GetBytes([int32]96), 0, 4)
  $out.Write([BitConverter]::GetBytes([int32]96), 0, 4)
  0..1 | ForEach-Object { $out.Write([BitConverter]::GetBytes([int32]0), 0, 4) }
  for ($y = 0; $y -lt $S; $y++) {
    $out.Write($buf, (($S - 1 - $y) * $S * 4), $S * 4)
  }
  $maskStride = [int]([Math]::Ceiling($S / 32.0) * 4)
  $out.Write((New-Object byte[] ($maskStride * $S)), 0, ($maskStride * $S))
  return $out.ToArray()
}

$entries = @()
foreach ($s in $sizes) {
  if ($s -ge 256) { $entries += ,@($s, $pngBySize[$s]) }
  else { $entries += ,@($s, (Get-BmpPayload -S $s -Png $pngBySize[$s])) }
}

$fs = New-Object System.IO.FileStream($outIco, ([System.IO.FileMode]::Create))
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
  $s = $e[0]; $bytes = $e[1]
  $byte = [byte]($(if ($s -ge 256) { 0 } else { $s }))
  $bw.Write($byte); $bw.Write($byte); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$bytes.Length); $bw.Write([uint32]$offset)
  $offset += $bytes.Length
}
foreach ($e in $entries) { $bw.Write([byte[]]$e[1]) }
$bw.Flush(); $fs.Close()

Write-Output ("app.ico -> {0} ({1:N1} KB)" -f $outIco, ((Get-Item $outIco).Length / 1KB))
Write-Output ("app.png -> {0}" -f $outPng)
