<#
  下载截图识别文字用的 PP-OCRv5 模型(ADR-0019),校验 SHA256 后放进截图功能的 Assets/Ocr(不进 git)。
  已经存在且哈希正确的文件跳过,所以可以反复运行;check.ps1 构建前会调用它。
  来源:ModelScope RapidAI/RapidOCR(PaddleOCR 官方模型转成的 ONNX,Apache-2.0)。HuggingFace 在本机网络下连不上。
  下载物视为不可信数据:先下到一个新的临时空目录,哈希对上才移过去;不执行任何下载的内容。
#>
param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) "src/Features/Waio.Feature.Screenshot/Assets/Ocr")
)

$ErrorActionPreference = "Stop"
$base = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/master"
$files = @(
    @{ Name = "det.onnx"; Path = "onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx"; Sha256 = "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae" },
    @{ Name = "rec.onnx"; Path = "onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx"; Sha256 = "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5" },
    @{ Name = "dict.txt"; Path = "paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt"; Sha256 = "d1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b" }
)

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$staging = Join-Path ([IO.Path]::GetTempPath()) ("waio-ocr-" + [Guid]::NewGuid().ToString("N"))
try {
    foreach ($file in $files) {
        $target = Join-Path $Destination $file.Name
        if ((Test-Path -LiteralPath $target) -and (Get-Sha256 $target) -eq $file.Sha256) {
            continue
        }

        New-Item -ItemType Directory -Force -Path $staging | Out-Null
        $download = Join-Path $staging $file.Name
        Write-Host "下载 $($file.Path)"
        Invoke-WebRequest -Uri "$base/$($file.Path)" -OutFile $download -UseBasicParsing -TimeoutSec 300
        $actual = Get-Sha256 $download
        if ($actual -ne $file.Sha256) {
            throw "$($file.Name) 的 SHA256 不符:期望 $($file.Sha256),实际 $actual"
        }

        Move-Item -Force -LiteralPath $download -Destination $target
    }

    Write-Host "OCR 模型就绪:$Destination"
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -Recurse -Force -LiteralPath $staging }
}
