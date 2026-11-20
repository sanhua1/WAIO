<#
.SYNOPSIS
  从 design/icon 的 SVG 生成多尺寸 .ico(程序图标 + 托盘图标共用一个文件)。

.DESCRIPTION
  1. 用无头 Edge 把 SVG 按 1024×1024、透明背景渲染成 PNG(不弹窗口,使用独立的临时配置目录);
  2. 用 System.Drawing 高质量缩放到各尺寸;
  3. 打包成 PNG 压缩的 .ico(Windows Vista 起支持)。

  小尺寸(16/20/24,托盘和资源管理器小图标)用 -Small,更大的用 -Large。
  默认两者都是完整图标(托盘和任务栏图标一致);
  想让小尺寸用简化版,传 -Small design/icon/tray-color.svg。
  Windows 按需要的尺寸从 .ico 里挑最接近的一张,托盘随 DPI 取 16/20/24。

.EXAMPLE
  pwsh scripts/build-icon.ps1
#>
param(
    [string]$Large = "$PSScriptRoot/../design/icon/waio-plate.svg",
    [string]$Small = "$PSScriptRoot/../design/icon/waio-plate.svg",
    [string]$Output = "$PSScriptRoot/../src/Waio.App/Assets/waio.ico",
    [int[]]$SmallSizes = @(16, 20, 24),
    [int[]]$LargeSizes = @(32, 40, 48, 64, 96, 128, 256)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw '找不到 Microsoft Edge,无法渲染 SVG' }

$work = Join-Path ([IO.Path]::GetTempPath()) "waio-icon-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $work | Out-Null

function Render-Svg([string]$svg, [string]$png) {
    $svgUri = [Uri]::new((Resolve-Path $svg).Path).AbsoluteUri
    $html = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($png) + '.html')
    Set-Content $html -Encoding utf8 -Value @"
<!doctype html><style>html,body{margin:0;background:transparent}img{display:block;width:1024px;height:1024px}</style><img src="$svgUri">
"@
    & $edge --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 `
        --user-data-dir="$(Join-Path $work 'edge')" --window-size=1024,1024 `
        --screenshot="$png" ([Uri]::new($html).AbsoluteUri) 2>$null | Out-Null
    for ($i = 0; $i -lt 50 -and -not (Test-Path $png); $i++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path $png)) { throw "渲染失败:$svg" }
}

function Resize-Png([string]$source, [int]$size) {
    $src = [Drawing.Image]::FromFile($source)
    try {
        $bmp = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.DrawImage($src, 0, 0, $size, $size)
        $g.Dispose()
        $ms = [IO.MemoryStream]::new()
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        return , $ms.ToArray()
    }
    finally { $src.Dispose() }
}

try {
    $largePng = Join-Path $work 'large.png'
    $smallPng = Join-Path $work 'small.png'
    Render-Svg $Large $largePng
    Render-Svg $Small $smallPng

    $images = [Collections.Generic.List[object]]::new()
    foreach ($s in $SmallSizes) { $images.Add(@{ Size = $s; Data = (Resize-Png $smallPng $s) }) }
    foreach ($s in $LargeSizes) { $images.Add(@{ Size = $s; Data = (Resize-Png $largePng $s) }) }

    # ICO:6 字节头 + 每张 16 字节目录项 + PNG 数据
    $out = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($out)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }   # 256 在目录项里记为 0
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$img.Data.Length); $w.Write([uint32]$offset)
        $offset += $img.Data.Length
    }
    foreach ($img in $images) { $w.Write([byte[]]$img.Data) }
    $w.Flush()

    New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
    [IO.File]::WriteAllBytes($Output, $out.ToArray())
    Write-Host "已生成 $Output($($images.Count) 个尺寸:$(($images | ForEach-Object Size) -join ', '))"

    # 顺便导出 1024 的 PNG,方便查看
    Copy-Item $largePng (Join-Path (Split-Path $Large) 'waio-1024.png') -Force
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
