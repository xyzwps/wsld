# 安装包自检:静默安装 -> 启动 -> 关闭 -> 静默卸载 -> 检查残留
# 用法: just verify-installer
#       或 powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-installer.ps1
# 加 -KeepInstalled 装完不卸载,方便手工点一遍界面。
# 装的是"仅当前用户",不需要管理员权限,也不会碰系统目录。

[CmdletBinding()]
param(
  [string]$Setup,                       # 默认取 artifacts\installer 里最新的 setup.exe
  [string]$AppDir,                      # 默认 %LOCALAPPDATA%\Programs\WSLC Desktop
  [switch]$KeepInstalled
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Setup) {
  $Setup = (Get-ChildItem (Join-Path $root 'artifacts\installer') -Filter '*.exe' |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Setup -or -not (Test-Path $Setup)) { throw '找不到安装包,先跑 just package' }
if (-not $AppDir) { $AppDir = Join-Path $env:LOCALAPPDATA 'Programs\WSLC Desktop' }

$log = Join-Path $env:TEMP ('wslc-setup-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
  if ($ok) { Write-Host "  PASS  $name $detail" -ForegroundColor Green }
  else { $script:fail++; Write-Host "  FAIL  $name $detail" -ForegroundColor Red }
}

Write-Host "安装包: $Setup"
Write-Host ("大小  : {0:N1} MB`n" -f ((Get-Item $Setup).Length / 1MB))

Stop-Process -Name WslcDesktop -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$installArgs = @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$log")
$p = Start-Process -FilePath $Setup -PassThru -Wait -ArgumentList $installArgs
Check '静默安装退出码' ($p.ExitCode -eq 0) "(exit=$($p.ExitCode))"

$exe = Join-Path $AppDir 'WslcDesktop.exe'
Check '主程序已落盘' (Test-Path $exe) $exe
$files = @(Get-ChildItem $AppDir -Recurse -File -ErrorAction SilentlyContinue)
Check '文件数量合理' ($files.Count -gt 300) "($($files.Count) 个)"
Check 'CRT 运行库随包' (Test-Path (Join-Path $AppDir 'vcruntime140.dll'))
Check '卸载程序已注册' (Test-Path (Join-Path $AppDir 'unins000.exe'))

$ap = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
Check '安装后能启动' (-not $ap.HasExited) $(if ($ap.HasExited) { "(退出码 $($ap.ExitCode))" } else { "(pid $($ap.Id))" })
Stop-Process -Id $ap.Id -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

if ($KeepInstalled) {
  Write-Host "`n按 -KeepInstalled 保留安装,界面点完后再手动卸载:" -ForegroundColor Yellow
  Write-Host ("  & `"{0}\unins000.exe`" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART" -f $AppDir)
} else {
  $unins = Join-Path $AppDir 'unins000.exe'
  if (Test-Path $unins) {
    $uninstallArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    $u = Start-Process -FilePath $unins -PassThru -Wait -ArgumentList $uninstallArgs
    Check '静默卸载退出码' ($u.ExitCode -eq 0) "(exit=$($u.ExitCode))"
  } else {
    Check '静默卸载退出码' $false '(unins000.exe 不存在)'
  }
  Start-Sleep -Seconds 3
  Check '安装目录已清空' (-not (Test-Path $AppDir))
  $sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WSLC Desktop.lnk'
  Check '开始菜单快捷方式已移除' (-not (Test-Path $sm))
  $arp = @(Get-ChildItem 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
           Where-Object { $_.PSChildName -like '*7498251D*' })
  Check '控制面板条目已移除' ($arp.Count -eq 0)
}

Write-Host ''
if ($fail) { Write-Host "$fail 项未通过(安装日志:$log)" -ForegroundColor Red; exit 1 }
Write-Host '全部通过' -ForegroundColor Green