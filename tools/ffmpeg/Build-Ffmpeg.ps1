#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$MsysRoot = (Join-Path $PSScriptRoot '..\..\artifacts\tooling\msys64'),
    [string]$FfmpegSource = (Join-Path $PSScriptRoot '..\..\artifacts\tooling\ffmpeg-src'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\tooling\ffmpeg-build\out'),
    [string]$FfmpegTag = 'n9.0.2',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$msysFull = [IO.Path]::GetFullPath($MsysRoot)
$bashPath = Join-Path $msysFull 'usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bashPath -PathType Leaf)) {
    throw "MSYS2 bash.exe was not found at $bashPath. Provide -MsysRoot pointing to an MSYS2 installation with mingw64 gcc, nasm, pkgconf, libx264, libvpl, amf-headers, and ffnvcodec-headers."
}

$srcFull = [IO.Path]::GetFullPath($FfmpegSource)
if (-not (Test-Path -LiteralPath (Join-Path $srcFull 'configure') -PathType Leaf)) {
    New-Item -ItemType Directory -Path (Split-Path $srcFull -Parent) -Force | Out-Null
    & git clone --depth 1 --branch $FfmpegTag https://github.com/FFmpeg/FFmpeg.git $srcFull
    if ($LASTEXITCODE -ne 0) { throw "Failed to clone FFmpeg tag $FfmpegTag." }
}

$outFull = [IO.Path]::GetFullPath($OutputDirectory)
$buildFull = Join-Path ([IO.Path]::GetDirectoryName($outFull)) 'work'
$scriptFull = Join-Path $PSScriptRoot 'build-ffmpeg.sh'

function Convert-ToMsysPath([string]$WindowsPath) {
    $full = [IO.Path]::GetFullPath($WindowsPath).Replace('\', '/')
    if ($full -match '^([A-Za-z]):/(.*)$') {
        return '/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2]
    }
    throw "Unsupported path format for MSYS2 conversion: $WindowsPath"
}

$msysScript = Convert-ToMsysPath $scriptFull
$msysSrc = Convert-ToMsysPath $srcFull
$msysBuild = Convert-ToMsysPath $buildFull
$msysOut = Convert-ToMsysPath $outFull

& $bashPath -lc "'$msysScript' '$msysSrc' '$msysBuild' '$msysOut'"
if ($LASTEXITCODE -ne 0) {
    throw "Minimal FFmpeg build failed with exit code $LASTEXITCODE."
}

$builtExe = Join-Path $outFull 'ffmpeg.exe'
if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) {
    throw "Expected output binary was not produced: $builtExe"
}

$manifest = Get-Content -LiteralPath (Join-Path $outFull 'build-manifest.txt') -Raw
$dllImports = @([regex]::Matches($manifest, 'DLL Name:\s*(\S+)') | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() })
$allowedDllPatterns = '^(kernel32|msvcrt|advapi32|user32|gdi32|ole32|oleaut32|shell32|shlwapi|ws2_32|bcrypt|crypt32|secur32|ncrypt|imm32|winmm|cfgmgr32|dxva2|d3d11|dxgi|version|setupapi|rpcrt4|api-ms-win-.*)\.dll$'
$unexpectedDlls = @($dllImports | Where-Object { $_ -notmatch $allowedDllPatterns })
if ($unexpectedDlls.Count -gt 0) {
    throw "Built ffmpeg.exe links non-OS runtime DLLs: $($unexpectedDlls -join ', ')"
}

if ($Install) {
    $destination = Join-Path $repoRoot 'src\Screen.App\ffmpeg\bin\ffmpeg.exe'
    Copy-Item -LiteralPath $builtExe -Destination $destination -Force
}

$sizeBytes = (Get-Item -LiteralPath $builtExe).Length
$sha256 = (Get-FileHash -LiteralPath $builtExe -Algorithm SHA256).Hash.ToLowerInvariant()
[pscustomobject]@{
    Binary = $builtExe
    SizeMB = [Math]::Round($sizeBytes / 1MB, 2)
    SizeBytes = $sizeBytes
    SHA256 = $sha256
    DllImports = $dllImports
    Installed = [bool]$Install
}
