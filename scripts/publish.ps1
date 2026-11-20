<#
  发布 Release 版(自包含:目标机器不需要装 .NET 运行时;Windows App Runtime 也随程序带上)到 artifacts/publish。
  用法:pwsh scripts/publish.ps1
  注意:不要给 dotnet publish 传 -r。Waio.App 的 csproj 里已经写了 win-x64;命令行的 -r 会传给所有引用的库,
  WinUI 生成 .pri 时就会去 bin\Release\...\win-x64\ 下找库的 .pri 而找不到(PRI175 / PRI252)。
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\publish"

Push-Location $root
try {
    Write-Host "==> 截图识别文字的模型(ADR-0019)" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "fetch-ocr-models.ps1")

    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Write-Host "==> 发布 Release(自包含)到 $out" -ForegroundColor Cyan
    dotnet publish src\Waio.App\Waio.App.csproj -c Release -p:SelfContained=true -o $out --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not (Test-Path (Join-Path $out "Waio.exe"))) { throw "发布目录里没有 Waio.exe" }
}
finally {
    Pop-Location
}
