# 生成应用图标:先画一张 1024 母版,再缩成多尺寸拼成 assets/app.ico
# (exe 内嵌图标、安装包、开始菜单快捷方式共用同一份)
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$outIco = Join-Path $root "assets\app.ico"
$outPng = Join-Path $root "assets\app.png"
New-Item -ItemType Directory -Force -Path (Split-Path $outIco) | Out-Null

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$master = 1024

function New-RoundedRect {
  param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $R * 2
  $p.AddArc($X, $Y, $d, $d, 180.0, 90.0) | Out-Null
  $p.AddArc($X + $W - $d, $Y, $d, $d, 270.0, 90.0) | Out-Null
  $p.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0.0, 90.0) | Out-Null
  $p.AddArc($X, $Y + $H - $d, $d, $d, 90.0, 90.0) | Out-Null
  $p.CloseFigure() | Out-Null
  return $p
}

$bmp = New-Object System.Drawing.Bitmap($master, $master, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

$pad = [single]($master * 0.035)
$side = [single]($master - 2 * $pad)
$rx = $pad; $ry = $pad; $rw = $side; $rh = $side
$body = New-RoundedRect -X $rx -Y $ry -W $rw -H $rh -R ($rw * [single]0.225)

$c = { param($a, $r, $gr, $b) [System.Drawing.Color]::FromArgb($a, $r, $gr, $b) }
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  ([System.Drawing.PointF]::new($rx, $ry)),
  ([System.Drawing.PointF]::new($rx + $rw, $ry + $rh)),
  (& $c 255 12 44 78), (& $c 255 44 143 214))
$blend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
$blend.Colors = [System.Drawing.Color[]]@((& $c 255 9 38 70), (& $c 255 24 96 160), (& $c 255 68 176 236))
$blend.Positions = [single[]]@(0.0, 0.55, 1.0)
$grad.InterpolationColors = $blend
$g.FillPath($grad, $body) | Out-Null

# 上半身一道淡高光,免得整块太闷
$hlRect = [System.Drawing.RectangleF]::new($rx, $ry, $rw, ($rh * [single]0.5))
$hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
  $hlRect, (& $c 44 255 255 255), (& $c 0 255 255 255), [single]90.0)
$g.SetClip($body)
$g.FillRectangle($hl, $hlRect) | Out-Null
$g.ResetClip()

# 三层"容器"横条,左边一个状态点
$barX = $rx + $rw * [single]0.20
$barW = $rw * [single]0.60
$barH = $rh * [single]0.105
$barGap = $rh * [single]0.155
$barY0 = $ry + $rh * [single]0.245
$alphas = @(236, 190, 132)
$dotColors = @((& $c 255 122 226 175), (& $c 255 255 196 92), (& $c 255 130 205 255))
for ($i = 0; $i -lt 3; $i++) {
  $y = $barY0 + $i * $barGap
  $bar = New-RoundedRect -X $barX -Y $y -W $barW -H $barH -R ($barH / [single]2)
  $brush = New-Object System.Drawing.SolidBrush((& $c $alphas[$i] 255 255 255))
  $g.FillPath($brush, $bar) | Out-Null
  $dr = $barH * [single]0.46
  $g.FillEllipse((New-Object System.Drawing.SolidBrush($dotColors[$i])),
                  ($barX + ($barH - $dr) / [single]2), ($y + ($barH - $dr) / [single]2), $dr, $dr) | Out-Null
  $g.FillEllipse((New-Object System.Drawing.SolidBrush((& $c 64 9 38 70))),
                  ($barX + $barW - $barH * [single]0.85), ($y + ($barH - $dr) / [single]2), $dr, $dr) | Out-Null
}
$grad.Dispose(); $hl.Dispose(); $g.Dispose()

# ---- 缩到各尺寸 ----
$pngBySize = @{}
foreach ($s in $sizes) {
  $small = New-Object System.Drawing.Bitmap($s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
  $sg = [System.Drawing.Graphics]::FromImage($small)
  $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
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


