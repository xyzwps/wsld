# 清 Windows 图标缓存:资源管理器按"路径"记图标,同一个 exe 重新编译后常常还显示旧图
# (exe 里嵌的图标已经换了,屏幕上没换 —— 就是这里的事)。
# 会先结束 explorer 再删缓存,删完把它拉回来,桌面和任务栏会闪一下。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File tools/clean-iconcache.ps1 [-Yes]
param(
  [switch]$Yes
)
$ErrorActionPreference = 'Stop'

if (-not $Yes) {
  "将重启资源管理器以清图标缓存(桌面、任务栏会闪一下,开着的资源管理器窗口会关掉)。"
  $ans = Read-Host "回车继续,输入 n 取消"
  if ($ans -eq 'n' -or $ans -eq 'N') { "已取消"; exit 0 }
}

$patterns = @(
  (Join-Path $env:LOCALAPPDATA "IconCache.db"),
  (Join-Path $env:LOCALAPPDATA "Microsoft\Windows\Explorer\iconcache*")
)

# explorer 自己攥着这些文件,不停掉删不动
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1200

$victims = @()
foreach ($p in $patterns) {
  $victims += @(Get-Item $p -Force -ErrorAction SilentlyContinue)
}
foreach ($f in $victims) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue }

# 让 shell 重新读一遍新图标(缓存文件没了它会自己重建)
& ie4uinit.exe -show 2>$null | Out-Null
if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }

$left = @($victims | Where-Object { Test-Path $_.FullName })
"删掉 $($victims.Count - $left.Count) 个图标缓存文件,资源管理器已恢复。"
if ($left.Count -gt 0) {
  "这几个没删动(多半还被别的进程占着):"
  $left | ForEach-Object { "  $($_.FullName)" }
  "注销一次再登录最彻底。"
}
