<#
  把当前代码同步到本机已安装的 WAIO(C:\Program Files\WAIO)并重新启动。调试通过后运行(ADR-0031)。
  步骤:发布 Release → 结束所有 Waio.exe(含 bin\Debug 里的开发实例)→ 镜像复制(保留卸载程序)→ 以托盘方式启动。
  需要管理员权限(Program Files 只有管理员能写)。要先用安装包装过一次(否则没有卸载项、没有开机自启任务)。
  用法:pwsh scripts/deploy-local.ps1 [-SkipPublish]
#>
param([switch]$SkipPublish)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root "artifacts\publish"
$target = Join-Path $env:ProgramFiles "WAIO"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "需要管理员权限(要写 $target)"
}
if (-not (Test-Path (Join-Path $target "unins000.exe"))) {
    throw "$target 还没用安装包装过。先运行 scripts/build-installer.ps1,再运行 artifacts\installer 里的安装包"
}

if (-not $SkipPublish) { & (Join-Path $PSScriptRoot "publish.ps1") }

Write-Host "==> 结束正在运行的 WAIO" -ForegroundColor Cyan
Get-Process Waio -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  结束 PID $($_.Id) $($_.Path)"
    Stop-Process -Id $_.Id -Force -Confirm:$false
    $_.WaitForExit(5000) | Out-Null
}

Write-Host "==> 同步到 $target" -ForegroundColor Cyan
# /MIR 会删掉目标里多出来的文件:排除安装包写的卸载程序
robocopy $source $target /MIR /XF "unins*.exe" "unins*.dat" "unins*.msg" /NFL /NDL /NJH /NP /R:3 /W:1
if ($LASTEXITCODE -ge 8) { throw "robocopy 失败,退出码 $LASTEXITCODE" }

Write-Host "==> 启动(托盘)" -ForegroundColor Cyan
Start-Process (Join-Path $target "Waio.exe") -ArgumentList "--autostart"
Write-Host "已同步并启动 $target\Waio.exe" -ForegroundColor Green
