<#
  构建安装包(Inno Setup 6,ADR-0031):先发布,再编译 installer/waio.iss → artifacts/installer/WAIO-Setup-<版本>.exe。
  用法:pwsh scripts/build-installer.ps1 [-SkipPublish]
  需要 Inno Setup 6:winget install --id JRSoftware.InnoSetup -e
#>
param([switch]$SkipPublish)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$iscc = @(
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "没找到 Inno Setup 6 的 ISCC.exe,先运行:winget install --id JRSoftware.InnoSetup -e" }

if (-not $SkipPublish) { & (Join-Path $PSScriptRoot "publish.ps1") }

$version = (dotnet msbuild (Join-Path $root "src\Waio.App\Waio.App.csproj") -getProperty:Version -nologo).Trim()
if (-not $version) { throw "读不到版本号(Directory.Build.props 的 Version)" }

Write-Host "==> 编译安装包 WAIO $version" -ForegroundColor Cyan
& $iscc /Qp "/DSourceDir=$(Join-Path $root 'artifacts\publish')" "/DAppVersion=$version" (Join-Path $root "installer\waio.iss")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "安装包:$(Join-Path $root "artifacts\installer\WAIO-Setup-$version.exe")" -ForegroundColor Green
