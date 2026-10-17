# 册子编号与测试用例的双向对账。
# 规矩本体见 docs/development.md 的「规格册怎么写」。
# 默认:册子里有号但没实现、编号格式/归属有问题 = 红;只在 -Strict 时才把"有号无实现"也当成失败。

param([switch]$Strict)

$ErrorActionPreference = 'Stop'
# 控制台默认跟着系统代码页(GBK),不显式设成 UTF-8 的话中文结论会花。
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }
$root = Split-Path -Parent $PSScriptRoot
$specDir = Join-Path $root 'docs/specs'
$testDir = Join-Path $root 'tests/wsld.Tests'
$indexDoc = Join-Path $root 'docs/development.md'

# ---------- 能力索引:前缀 -> 册子文件 ----------
$registry = @{}
foreach ($line in Get-Content -LiteralPath $indexDoc -Encoding UTF8) {
    if ($line -match '^\|\s*([A-Z][A-Z0-9]*)\s*\|[^|]*\|\s*`([A-Za-z0-9._-]+\.md)`') {
        $registry[$Matches[2]] = $Matches[1]
    }
}
if ($registry.Count -eq 0) {
    Write-Host "没在 $indexDoc 里读到能力索引表(前缀 | 能力 | 册子 那一栏)" -ForegroundColor Red
    exit 1
}

$problems = @()
$warnings = @()
$scenarios = @{}   # id -> 出处
$titles = @{}      # id -> 标题

# ---------- 读册子 ----------
foreach ($book in (Get-ChildItem -LiteralPath $specDir -Filter '*.md' | Sort-Object Name)) {
    $want = $registry[$book.Name]
    if (-not $want) {
        $problems += "$($book.Name): 这本册子没登记进能力索引"
        continue
    }

    $lines = Get-Content -LiteralPath $book.FullName -Encoding UTF8
    $requirement = ''
    $lastNumber = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if ($line -match '^###\s+Requirement:\s*(.+)$') {
            $requirement = $Matches[1].Trim()
            $lastNumber = -1
            continue
        }
        if ($line -notmatch '^####\s+Scenario:\s*(.*)$') { continue }

        $head = $Matches[1].Trim()
        if ($head -notmatch '^([A-Z][A-Z0-9]*)-(\d{1,3})\s+(\S.*)$') {
            $problems += "$($book.Name):「$head」不是「前缀-三位号 + 标题」的样子"
            continue
        }
        $prefix = $Matches[1]
        $number = [int]$Matches[2]
        $id = '{0}-{1:d3}' -f $prefix, $number

        if ($prefix -ne $want) {
            $problems += "$($book.Name) 里出现了 $id,但这本册子登记的前缀是 $want"
        }
        if ($titles.ContainsKey($id)) {
            $problems += "$id 重号: $($titles[$id]) 与 $($book.Name):「$($Matches[3])」"
            continue
        }
        if ($number -le $lastNumber) {
            $problems += "$($book.Name) 的「$requirement」里 $id 没按号升序(上一条是 $lastNumber)"
        }
        $lastNumber = $number

        $bodyLines = 0
        for ($j = $i + 1; $j -lt $lines.Count; $j++) {
            if ($lines[$j] -match '^#{1,4}\s') { break }
            if ($lines[$j].Trim() -ne '') { $bodyLines++ }
        }
        if ($bodyLines -gt 5) {
            $problems += "$($book.Name) 的 $id 正文 $bodyLines 行,超过 5 行这条界"
        }

        $titles[$id] = "$($book.Name):「$($Matches[3])」"
        $scenarios[$id] = "$($book.Name) / $requirement"
    }
}

foreach ($name in $registry.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $specDir $name))) {
        $problems += "能力索引里登了 $name,但 docs/specs/$name 不存在"
    }
}

# ---------- 读测试 ----------
$cases = @{}   # id -> 挂它的用例
foreach ($file in (Get-ChildItem -LiteralPath $testDir -Recurse -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object Name)) {
    $lineNo = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName -Encoding UTF8) {
        $lineNo++
        foreach ($m in [regex]::Matches($line, 'Trait\(\s*"case"\s*,\s*"([A-Z][A-Z0-9]*-\d{3})"')) {
            $id = $m.Groups[1].Value
            if (-not $cases.ContainsKey($id)) { $cases[$id] = @() }
            $cases[$id] += "$($file.Name):$lineNo"
        }
    }
}

# ---------- 双向差集 ----------
$missing = @()
foreach ($id in ($scenarios.Keys | Sort-Object)) {
    if (-not $cases.ContainsKey($id)) { $missing += $id }
}
$private = @()
foreach ($id in ($cases.Keys | Sort-Object)) {
    if (-not $scenarios.ContainsKey($id)) {
        $private += "$id 挂在 $($cases[$id] -join ', '),册子里没有这一条"
    }
}

Write-Host "册子:$($scenarios.Count) 条 Scenario / 测试挂了号:$($cases.Count) 个"
if ($missing.Count -gt 0) {
    $text = "册里有号、测试没挂($($missing.Count) 条):$($missing -join ' ')"
    if ($Strict) { $problems += $text } else { $warnings += $text }
}
$problems += $private

foreach ($w in $warnings) { Write-Host "提醒:$w" -ForegroundColor Yellow }
foreach ($p in $problems) { Write-Host "问题:$p" -ForegroundColor Red }

if ($problems.Count -gt 0) {
    Write-Host "对账没过:$($problems.Count) 个问题" -ForegroundColor Red
    exit 1
}
Write-Host "对账过了。" -ForegroundColor Green
