# WSLC Desktop · 一键出安装包
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1
#
# 三步:1) dotnet publish 出"自包含"版本(.NET 运行时 + Windows App SDK 都带在文件夹里,
# 客户机不用装 .NET);2) 补齐 VC++ 运行库 DLL;3) 用 Inno Setup 编译成一个 setup.exe。
# 产物在 artifacts\installer\WSLC-Desktop-Setup-<版本>-x64.exe,旁边有个 .sha256 校验文件。
#
# 常用参数:
#   -Version 1.2.3        覆盖版本号(默认读 csproj 里的 <Version>)
#   -InstallerOnly        跳过发布,直接拿现有 artifacts\publish 打包
#   -Publisher "某某公司"        在"应用和功能"里显示厂商名(默认不写)
#   -Runtime framework-dependent   出"依赖 .NET 运行时"的小包(客户机得自己装 .NET 9 Desktop)
#   -NoInstaller          只发布,不打安装包

[CmdletBinding()]
param(
  [string]$Version,
  [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
  [ValidateSet('self-contained', 'framework-dependent')][string]$Runtime = 'self-contained',
  [string]$InnoSetup,          # ISCC.exe 路径,不填自动找
  [string]$Publisher,          # 可选:写进"应用和功能"里的厂商名
  [switch]$InstallerOnly,
  [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\WslcDesktop\WslcDesktop.csproj'
$iss  = Join-Path $root 'installer\WslcDesktop.iss'
$publishDir = Join-Path $root 'artifacts\publish'
$installerDir = Join-Path $root 'artifacts\installer'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ---- 版本号:参数 > csproj <Version> ----
if (-not $Version) {
  [xml]$csproj = Get-Content $proj -Raw
  $Version = @($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } |
               Where-Object { $_ }) | Select-Object -First 1
  if (-not $Version) { $Version = '0.0.0' }
}
$numParts = @($Version.Split('-')[0].Split('.'))
while ($numParts.Count -lt 4) { $numParts += '0' }
$VersionNumber = ($numParts[0..3] -join '.')
Step "版本 $Version (setup.exe 文件版本 $VersionNumber) · $Configuration · $Runtime"

# ---- 1. 发布 ----
if (-not $InstallerOnly) {
  Step 'dotnet publish'
  if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
  $selfContained = if ($Runtime -eq 'self-contained') { 'true' } else { 'false' }
  & dotnet publish $proj -c $Configuration -r win-x64 --self-contained $selfContained `
      -o $publishDir --nologo -v m
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败(exit $LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $publishDir 'WslcDesktop.exe'))) {
  throw "$publishDir 里没有 WslcDesktop.exe —— 先跑一次不带 -InstallerOnly 的完整流程"
}

# ---- 2. VC++ 运行库(Windows App SDK 的原生 DLL 要用它) ----
# 自包含发布只带 .NET,不带 vcruntime/msvcp;干净的客户机会在启动时黑屏报错,
# 所以把这几个可再分布 DLL 直接放进应用目录(私有部署,不写系统目录、不装全局)。
$crtNames = 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'vccorlib140.dll'
$crtSources = @(
  (Join-Path $root 'installer\prereq\crt'),
  'C:\Windows\System32'
) + @(Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT' `
        -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
foreach ($dll in $crtNames) {
  $target = Join-Path $publishDir $dll
  if (Test-Path $target) { continue }
  $src = $null
  foreach ($s in $crtSources) {
    $candidate = Join-Path $s $dll
    if (Test-Path $candidate) { $src = $candidate; break }
  }
  if ($src) { Copy-Item $src $target }
  else { Write-Warning "没找到 $dll,安装包将依赖客户机自行安装 VC++ 2015-2022 运行库" }
}

if ($NoInstaller) { Step "发布目录:$publishDir"; return }

# ---- 3. Inno Setup 编译安装包 ----
function Find-Iscc([string]$Hint) {
  $list = @()
  if ($Hint) { $list += $Hint }
  $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
  if ($cmd) { $list += $cmd.Source }
  $list += "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
  $list += "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
  $list += "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
  $list += "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe"
  foreach ($c in $list) { if ($c -and (Test-Path $c)) { return (Resolve-Path $c).Path } }
  return $null
}

$iscc = Find-Iscc $InnoSetup
if (-not $iscc) {
  throw '没找到 Inno Setup 6 的 ISCC.exe。装一下:winget install JRSoftware.InnoSetup(或指定 -InnoSetup 路径)'
}
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null
Step "编译安装包($iscc)"
$defines = @("/DMyAppVersion=$Version", "/DMyAppVersionNumber=$VersionNumber",
             "/DPublishDir=$publishDir", "/DOutputDir=$installerDir")
if ($Publisher) { $defines += "/DMyAppPublisher=$Publisher" }
& $iscc @defines $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC 失败(exit $LASTEXITCODE)" }

# ---- 结果 ----
$setup = Get-ChildItem $installerDir -Filter '*.exe' |
         Where-Object { $_.Name -like "WSLC-Desktop-Setup-$Version-x64*" } |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw 'ISCC 报告成功,但没找到产物 exe' }
# SHA256 自己算(不依赖 Get-FileHash,PowerShell 5.1 各台机器表现不一致)
$fs = [IO.File]::OpenRead($setup.FullName)
try {
  $sha = [Security.Cryptography.SHA256]::Create()
  $hash = (($sha.ComputeHash($fs) | ForEach-Object { $_.ToString('x2') }) -join '')
} finally { $fs.Dispose() }
Set-Content -Path "$($setup.FullName).sha256" -Value "$($hash.ToLower())  $($setup.Name)" `
            -Encoding ascii
Step '完成'
Write-Host ("  安装包 : {0}" -f $setup.FullName)
Write-Host ("  大小   : {0:N1} MB" -f ($setup.Length / 1MB))
Write-Host ("  SHA256 : {0}" -f $hash.ToLower())
Write-Host ("  校验文件: {0}.sha256" -f $setup.FullName)
