<#
.SYNOPSIS
    从 Unicode Unihan 数据库生成启动台用的汉字拼音表(ADR-0023)。

.DESCRIPTION
    读取 Unihan_Readings.txt 的 kMandarin 字段(每个字最常用的普通话读音;有两个值时第一个是大陆读音),
    去掉声调(ü 写成 v),按读音分组写成:
        读音<TAB>这个读音的全部汉字
    只收 CJK 基本区和扩展 A 区(U+3400–U+9FFF),和启动台判定"是不是汉字"的范围一致。
    多音字的其他读音不在这里,见 Waio.Feature.Launcher/Domain/Pinyin.cs 的补充表。

    Unihan 是下载物(不可信数据),放在 references/data/unihan/;本脚本只把它当文本读,不执行任何东西。

.EXAMPLE
    # 先下载并解压(只需要 Unihan_Readings.txt):
    #   https://www.unicode.org/Public/UCD/latest/ucd/Unihan.zip → references/data/unihan/
    pwsh -NoProfile -File scripts/build-pinyin-table.ps1 -Source references/data/unihan/Unihan_Readings.txt
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [string] $Output = (Join-Path $PSScriptRoot '..\src\Features\Waio.Feature.Launcher\Assets\pinyin.txt')
)

$ErrorActionPreference = 'Stop'

function Remove-Tone([string] $reading) {
    $d = $reading.Normalize([Text.NormalizationForm]::FormD).Replace("u$([char]0x308)", 'v')
    $sb = [Text.StringBuilder]::new()
    foreach ($c in $d.ToCharArray()) {
        if ([Globalization.CharUnicodeInfo]::GetUnicodeCategory($c) -ne 'NonSpacingMark') { [void]$sb.Append($c) }
    }
    return $sb.ToString().ToLowerInvariant()
}

$header = Get-Content -LiteralPath $Source -TotalCount 40 | Where-Object { $_ -match '^# (Unicode Version|Date|©)' }
$groups = [ordered]@{}
$count = 0
foreach ($line in [IO.File]::ReadLines((Resolve-Path -LiteralPath $Source))) {
    if (-not $line.StartsWith('U+')) { continue }
    $parts = $line.Split("`t")
    if ($parts.Count -lt 3 -or $parts[1] -ne 'kMandarin') { continue }
    $code = [Convert]::ToInt32($parts[0].Substring(2), 16)
    if ($code -lt 0x3400 -or $code -gt 0x9FFF) { continue }
    $reading = Remove-Tone ($parts[2].Split(' ')[0])
    if ($reading -notmatch '^[a-z]+$') { throw "读音无法处理:$line" }
    if (-not $groups.Contains($reading)) { $groups[$reading] = [Text.StringBuilder]::new() }
    [void]$groups[$reading].Append([char]$code)
    $count++
}

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# 汉字拼音表(启动台的拼音搜索用,ADR-0023)。由 scripts/build-pinyin-table.ps1 生成,不要手改。')
$lines.Add('# 来源:Unicode Unihan 数据库 Unihan_Readings.txt 的 kMandarin(第一个读音),去掉声调,ü 写成 v。')
foreach ($h in $header) { $lines.Add($h) }
$lines.Add('# Unicode 数据按 Unicode License v3 使用:https://www.unicode.org/license.txt')
$lines.Add('# 格式:读音<TAB>汉字…')
foreach ($key in ($groups.Keys | Sort-Object)) { $lines.Add("$key`t$($groups[$key])") }

$full = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full)) | Out-Null
[IO.File]::WriteAllLines($full, $lines, [Text.UTF8Encoding]::new($false))
Write-Host "写入 ${full}:$count 个字,$($groups.Count) 个读音"
