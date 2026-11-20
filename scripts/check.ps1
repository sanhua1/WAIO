<#
  构建 + 全部测试。交付前必须全绿。
  用法:
    pwsh scripts/check.ps1                 # 目标框架用 Directory.Build.props 里的默认值(net10.0)
    pwsh scripts/check.ps1 -Tfm net8.0     # 本机只有 .NET 8 SDK 时临时验证
#>
param(
    [string]$Tfm = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$extra = @()
if ($Tfm) { $extra += "-p:WaioTfm=$Tfm" }

Push-Location $root
try {
    Write-Host "==> 截图识别文字的模型(已下载且哈希正确时跳过,ADR-0019)" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "fetch-ocr-models.ps1")

    Write-Host "==> 构建" -ForegroundColor Cyan
    dotnet build Waio.sln @extra --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "==> 测试(含架构测试)" -ForegroundColor Cyan
    dotnet test Waio.sln @extra --no-build --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "全部通过" -ForegroundColor Green
}
finally {
    Pop-Location
}
