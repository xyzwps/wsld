# 清理编译产物:dotnet clean -> 删各项目的 bin\ 和 obj\ -> 删 artifacts\(发布目录与旧 setup.exe)
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File tools/clean.ps1 [-NoDotnetClean]
param(
  [switch]$NoDotnetClean
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# 应用还开着的话 bin 里的 exe 被占住,删到一半会报错;让它先去 just kill
$running = @(Get-Process WslcDesktop -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
  Write-Error "有 $($running.Count) 个 WslcDesktop 正在运行,文件会被占住。先跑 just kill。"
  exit 1
}

if (-not $NoDotnetClean) {
  dotnet clean (Join-Path $root "WslcDesktop.sln") --nologo
  if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet clean 失败(退出码 $LASTEXITCODE)"
    exit 1
  }
}

# 只清项目目录旁边的 bin/obj,artifacts 里发布出来的副本单独按目录删
$removed = @()
Get-ChildItem $root -Recurse -Filter *.csproj |
  Where-Object { $_.FullName -notmatch '\\(bin|obj|artifacts)\\' } |
  ForEach-Object {
    foreach ($name in 'bin', 'obj') {
      $p = Join-Path $_.DirectoryName $name
      if (Test-Path $p) { Remove-Item -Recurse -Force $p; $removed += $p }
    }
  }

$artifacts = Join-Path $root "artifacts"
if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts; $removed += $artifacts }

"已删除 {0} 个目录:" -f $removed.Count
$removed | ForEach-Object { "  " + $_.Replace("$root\", "") }
if ($removed.Count -eq 0) { "  (没什么可删的)" }
"下次 just dev / just build 会重新编译。"
