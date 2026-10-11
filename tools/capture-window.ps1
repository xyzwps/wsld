param([string]$ProcessName = "WslcDesktop", [string]$OutFile = "capture.png")
Add-Type -AssemblyName System.Drawing
$src = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "win32-cap.cs")
Add-Type -TypeDefinition $src
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
$h = $found[0]
[void][Win32Cap]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 700
$rect = New-Object Win32Cap+RECT
[void][Win32Cap]::GetWindowRect($h, [ref]$rect)
$w = $rect.Right - $rect.Left
$ht = $rect.Bottom - $rect.Top
$bmp = New-Object Drawing.Bitmap $w, $ht
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object Drawing.Size($w, $ht)))
$bmp.Save($OutFile, [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Output ("captured {0}x{1} -> {2}" -f $w, $ht, $OutFile)