[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$assetsPath = Join-Path $repositoryRoot 'src\Screen.App\Assets'
$sourcePngPath = Join-Path $assetsPath 'Screen.png'
if (-not (Test-Path -LiteralPath $sourcePngPath -PathType Leaf)) {
    throw "Missing master icon artwork: $sourcePngPath"
}

function New-ResizedIconPng([System.Drawing.Bitmap]$Source, [int]$Size) {
    # 小尺寸（16-24 px）适当收紧透明外边距，使托盘图标在 Windows 任务栏中更饱满清晰。
    $cropInset = if ($Size -le 24) { 18 } else { 0 }
    $srcRect = New-Object System.Drawing.Rectangle(
        $cropInset,
        $cropInset,
        ($Source.Width - 2 * $cropInset),
        ($Source.Height - 2 * $cropInset)
    )
    $canvas = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($canvas)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $dstRect = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
            $graphics.DrawImage($Source, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally {
            $graphics.Dispose()
        }

        $stream = New-Object IO.MemoryStream
        try {
            $canvas.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $canvas.Dispose()
    }
}

$sourceBitmap = [System.Drawing.Bitmap]::FromFile($sourcePngPath)
try {
    $sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
    $frames = foreach ($size in $sizes) { ,(New-ResizedIconPng $sourceBitmap $size) }
}
finally {
    $sourceBitmap.Dispose()
}

$icoPath = Join-Path $assetsPath 'Screen.ico'
$icoStream = [IO.File]::Create($icoPath)
$writer = New-Object IO.BinaryWriter($icoStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = $sizes[$index] % 256
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally {
    $writer.Dispose()
    $icoStream.Dispose()
}

Write-Output "Generated Screen.ico ($($sizes -join ', ') px) from Screen.png."
