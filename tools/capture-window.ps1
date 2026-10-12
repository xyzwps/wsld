param([string]$ProcessName = "WslcDesktop", [string]$OutFile = "capture.png")
Add-Type -AssemblyName System.Drawing
$src = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "win32-cap.cs")
Add-Type -TypeDefinition $src
[void][Win32Cap]::SetProcessDPIAware()  # 让窗口坐标与屏幕像素一致,否则截到的是缩放后的裁切区域
$pids = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
if ($pids.Count -eq 0) { Write-Error "process not running"; exit 1 }
$found = New-Object Collections.ArrayList
$cb = [Win32Cap+EnumWindowsProc]{ param($h, $l)
  $p = 0
  [void][Win32Cap]::GetWindowThreadProcessId($h, [ref]$p)
  if ($pids -contains [uint32]$p -and [Win32Cap]::IsWindowVisible($h)) { [void]$found.Add($h) }
  return $true
}
[void][Win32Cap]::EnumWindows($cb, [IntPtr]::Zero)
if ($found.Count -eq 0) { Write-Error "no visible window"; exit 1 }
# ToolTip 是"同进程的另一个顶层窗口"(dev-notes §21),按面积挑最大的那个才是主窗口。
$best = $null; $bestArea = 0; $bestRect = New-Object Win32Cap+RECT
foreach ($candidate in $found) {
  $r = New-Object Win32Cap+RECT
  [void][Win32Cap]::GetWindowRect($candidate, [ref]$r)
  $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
  if ($area -gt $bestArea) { $bestArea = $area; $best = $candidate; $bestRect = $r }
}
$h = $best
[void][Win32Cap]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 700
$rect = New-Object Win32Cap+RECT
# GetWindowRect 含一圈"看不见的边框"(阴影 + 调整大小热区),直接按它截会在四边混进背后的窗口。
# DWM 的扩展帧边界才是屏幕上真正看到的窗口范围;拿不到时才退回 GetWindowRect。
$frame = New-Object Win32Cap+RECT
$frameSize = [System.Runtime.InteropServices.Marshal]::SizeOf($frame)
if ([Win32Cap]::DwmGetWindowAttribute($h, [Win32Cap]::DWMWA_EXTENDED_FRAME_BOUNDS, [ref]$frame, $frameSize) -eq 0 -and ($frame.Right - $frame.Left) -gt 0) {
  $rect = $frame
} else {
  [void][Win32Cap]::GetWindowRect($h, [ref]$rect)
}
# DWM 边界外面还包着约 2px 的"不可见边框"(拖动调整大小的热区),不裁掉截图四边会混进背后的窗口。
if (($rect.Right - $rect.Left) -gt 8 -and ($rect.Bottom - $rect.Top) -gt 8) {
  $rect.Left += 2; $rect.Top += 2; $rect.Right -= 2; $rect.Bottom -= 2
}
$w = $rect.Right - $rect.Left
$ht = $rect.Bottom - $rect.Top
$bmp = New-Object Drawing.Bitmap $w, $ht
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object Drawing.Size($w, $ht)))
$bmp.Save($OutFile, [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Output ("captured {0}x{1} -> {2}" -f $w, $ht, $OutFile)
