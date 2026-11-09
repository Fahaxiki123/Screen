[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run this script with powershell.exe -NoProfile -STA -File tools\generate-app-icon.ps1.'
}

Add-Type -AssemblyName PresentationCore, WindowsBase
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$assetsPath = Join-Path $repositoryRoot 'src\Screen.App\Assets'
$svgPath = Join-Path $assetsPath 'Screen.svg'
[xml]$svg = Get-Content -LiteralPath $svgPath -Raw
$invariant = [Globalization.CultureInfo]::InvariantCulture

function ConvertTo-Number([string]$Value) {
    [double]::Parse($Value, $invariant)
}

function New-SvgBrush([string]$Value, [double]$Opacity = 1) {
    if ($Value -eq 'none') { return $null }
    if ($Value -eq 'url(#blue)') {
        $brush = New-Object Windows.Media.LinearGradientBrush
        $brush.StartPoint = New-Object Windows.Point(0, 0)
        $brush.EndPoint = New-Object Windows.Point(1, 1)
        foreach ($stop in $svg.svg.defs.linearGradient.stop) {
            $color = [Windows.Media.ColorConverter]::ConvertFromString($stop.'stop-color')
            $brush.GradientStops.Add((New-Object Windows.Media.GradientStop($color, (ConvertTo-Number $stop.offset))))
        }
    }
    else {
        $brush = New-Object Windows.Media.SolidColorBrush([Windows.Media.ColorConverter]::ConvertFromString($Value))
    }
    $brush.Opacity = $Opacity
    $brush.Freeze()
    return $brush
}

function New-IconPng([int]$Size) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    try {
        $scale = $Size / 512.0
        $context.PushTransform((New-Object Windows.Media.ScaleTransform($scale, $scale)))
        foreach ($shape in $svg.svg.ChildNodes) {
            if ($shape.LocalName -notin @('rect', 'path')) { continue }
            $fill = New-SvgBrush $shape.fill
            $pen = $null
            if ($shape.HasAttribute('stroke')) {
                $opacity = 1.0
                if ($shape.HasAttribute('stroke-opacity')) { $opacity = ConvertTo-Number $shape.'stroke-opacity' }
                $pen = New-Object Windows.Media.Pen((New-SvgBrush $shape.stroke $opacity), (ConvertTo-Number $shape.'stroke-width'))
                $pen.StartLineCap = [Windows.Media.PenLineCap]::Round
                $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
                $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
                $pen.Freeze()
            }
            if ($shape.LocalName -eq 'rect') {
                $rect = New-Object Windows.Rect((ConvertTo-Number $shape.x), (ConvertTo-Number $shape.y), (ConvertTo-Number $shape.width), (ConvertTo-Number $shape.height))
                $radius = ConvertTo-Number $shape.rx
                $context.DrawRoundedRectangle($fill, $pen, $rect, $radius, $radius)
            }
            else {
                $geometry = [Windows.Media.Geometry]::Parse($shape.d)
                $context.DrawGeometry($fill, $pen, $geometry)
            }
        }
        $context.Pop()
    }
    finally { $context.Close() }

    $rendered = New-Object Windows.Media.Imaging.RenderTargetBitmap($Size, $Size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $rendered.Render($visual)
    $rendered.Freeze()
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rendered))
    $stream = New-Object IO.MemoryStream
    try {
        $encoder.Save($stream)
        return ,$stream.ToArray()
    }
    finally { $stream.Dispose() }
}

[IO.File]::WriteAllBytes((Join-Path $assetsPath 'Screen.png'), (New-IconPng 512))
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) { ,(New-IconPng $size) }
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
Write-Output "Generated Screen.ico ($($sizes -join ', ') px) and Screen.png (512 px) from Screen.svg."
