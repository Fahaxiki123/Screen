#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$CompilerPath,
    # Folder that holds 正式版 and receives 安装包. Defaults to the parent of a
    # source folder named 开发工程; must match rebuild-and-restart.ps1 -ProductRoot.
    [string]$ProductRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if ($ProductRoot) {
    $productRoot = [IO.Path]::GetFullPath($ProductRoot).TrimEnd('\')
    if ([string]::Equals($productRoot, $repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $productRoot.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ProductRoot must be outside the source folder.'
    }
}
elseif ([IO.Path]::GetFileName($repositoryRoot) -ne '开发工程') {
    throw 'Run this script from the 开发工程\tools folder in the arranged Screen directory, or pass -ProductRoot <folder holding 正式版>.'
}
else {
    $productRoot = [IO.Path]::GetDirectoryName($repositoryRoot)
}
$publishDirectory = Join-Path $productRoot '正式版'
$installerSources = Join-Path $PSScriptRoot 'installer'
$runtimeDirectory = Join-Path $installerSources 'runtime\win-x64'
$noticeDirectory = Join-Path $installerSources 'third-party-notices'
$vcLicenseFile = Join-Path $noticeDirectory 'licenses\Microsoft-Visual-C-V14-2026-License.txt'
$iconPath = Join-Path $repositoryRoot 'src\Screen.App\Assets\Screen.ico'
$outputDirectory = Join-Path $productRoot '安装包'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$buildDirectory = Join-Path $artifactsDirectory ('.installer-build-' + [guid]::NewGuid().ToString('N'))
$payloadDirectory = Join-Path $buildDirectory 'payload'

function Assert-NoReparsePoints([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Required path does not exist: $Path" }
    $items = @((Get-Item -LiteralPath $Path -Force))
    if ($items[0].PSIsContainer) { $items += @(Get-ChildItem -LiteralPath $Path -Recurse -Force) }
    foreach ($item in $items) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse points are not allowed in packaging paths: $($item.FullName)"
        }
    }
}

function Assert-WorkspacePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $insideProject = $full.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    $isOfficialDirectory = [string]::Equals($full, $publishDirectory, [StringComparison]::OrdinalIgnoreCase)
    $isInstallerDirectory = [string]::Equals($full, $outputDirectory, [StringComparison]::OrdinalIgnoreCase)
    $isInstallerFile = [string]::Equals([IO.Path]::GetDirectoryName($full), $outputDirectory, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($full) -match '^Screen-\d+\.\d+\.\d+-win-x64-Setup\.exe$'
    if (-not ($insideProject -or $isOfficialDirectory -or $isInstallerDirectory -or $isInstallerFile)) {
        throw "Path is outside the project and the exact release targets: $full"
    }
    $ancestor = $full
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Packaging path has a reparse-point ancestor: $ancestor"
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}

function Remove-StagingDirectory {
    $resolved = [IO.Path]::GetFullPath($buildDirectory)
    Assert-WorkspacePath $resolved
    if ([IO.Path]::GetDirectoryName($resolved) -ne $artifactsDirectory -or
        [IO.Path]::GetFileName($resolved) -notmatch '^\.installer-build-[0-9a-f]{32}$') {
        throw "Refusing to remove an unexpected staging path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Assert-NoReparsePoints $resolved
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Copy-PayloadFile([string]$Source, [string]$RelativePath) {
    if ($RelativePath -match '[\r\n"$]' -or $RelativePath.StartsWith('..') -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Payload path cannot be represented safely in NSIS: $RelativePath"
    }
    $destination = Join-Path $payloadDirectory $RelativePath
    if (Test-Path -LiteralPath $destination) { throw "Duplicate payload path: $RelativePath" }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
    $candidates = @(
        $(if ($command) { $command.Source }),
        "$env:LOCALAPPDATA\Programs\NSIS\makensis.exe",
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
        "$env:ProgramFiles\NSIS\makensis.exe",
        (Join-Path $artifactsDirectory 'tooling\nsis-3.12\Bin\makensis.exe')
    )
    $CompilerPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'NSIS 3.12 is required. Pass -CompilerPath with the path to its makensis.exe. No application or compiler is installed by this script.'
}
$CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
$compilerVersion = (& $CompilerPath /VERSION | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -ne 'v3.12') { throw "Expected NSIS v3.12; found $compilerVersion" }

foreach ($path in @($publishDirectory, $runtimeDirectory, $noticeDirectory, $outputDirectory, $buildDirectory)) { Assert-WorkspacePath $path }
foreach ($path in @($publishDirectory, $runtimeDirectory, $noticeDirectory)) { Assert-NoReparsePoints $path }

$requiredFiles = @(
    'Screen.exe', 'Screen.dll', 'Screen.Core.dll', 'Screen.deps.json', 'Screen.runtimeconfig.json',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll', 'WindowsBase.dll',
    'RapidOcrNet.dll', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll', 'libSkiaSharp.dll',
    'ffmpeg\bin\ffmpeg.exe', 'models\v5\ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx',
    'models\v6\PP-OCRv6_det_small.onnx', 'models\v6\PP-OCRv6_rec_small.onnx',
    'models\v6\ppocrv6_small_dict.txt', 'THIRD-PARTY-NOTICES.txt'
)
foreach ($relative in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $relative) -PathType Leaf)) {
        throw "The official self-contained publish is incomplete: $relative. Run rebuild-and-restart.ps1 before packaging."
    }
}
$requiredVcFiles = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
foreach ($relative in $requiredVcFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $runtimeDirectory $relative) -PathType Leaf)) {
        throw "The app-local Microsoft Visual C++ runtime is incomplete: $relative"
    }
}
if (@(Get-ChildItem -LiteralPath $noticeDirectory -File -Recurse).Count -eq 0) { throw 'Third-party redistribution notices are missing.' }
if (-not (Test-Path -LiteralPath $vcLicenseFile -PathType Leaf)) { throw 'The Microsoft Visual C++ runtime license text is missing.' }
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) { throw "Application icon is missing: $iconPath" }
Assert-WorkspacePath $iconPath
Assert-NoReparsePoints $iconPath

$project = [xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\Screen.App\Screen.App.csproj') -Raw)
$appVersion = [string]($project.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Unsupported release version: $appVersion" }
$appFileVersion = "$appVersion.0"
foreach ($relative in @('Screen.exe', 'Screen.dll')) {
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDirectory $relative))
    if ([version]$versionInfo.FileVersion -ne [version]$appFileVersion) {
        throw "Published $relative version $($versionInfo.FileVersion) does not match project $appVersion. Re-publish before packaging."
    }
}
$runtime = Get-Content -LiteralPath (Join-Path $publishDirectory 'Screen.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtime.runtimeOptions.PSObject.Properties['includedFrameworks']) { throw 'The official release is not self-contained.' }
$frameworkNames = @($runtime.runtimeOptions.includedFrameworks.name)
if ('Microsoft.NETCore.App' -notin $frameworkNames -or 'Microsoft.WindowsDesktop.App' -notin $frameworkNames) {
    throw 'The official release does not contain both .NET and Windows Desktop runtime declarations.'
}
$deps = Get-Content -LiteralPath (Join-Path $publishDirectory 'Screen.deps.json') -Raw | ConvertFrom-Json
if (-not $deps.runtimeTarget.name.EndsWith('/win-x64') -or -not $deps.libraries.PSObject.Properties["Screen/$appVersion"]) {
    throw 'Published dependency metadata does not describe this Screen version for win-x64.'
}
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead((Join-Path $publishDirectory 'Screen.exe')))
try {
    $reader.BaseStream.Position = 0x3c
    $peOffset = $reader.ReadInt32()
    $reader.BaseStream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Screen.exe is not an AMD64 PE executable.' }
} finally { $reader.Dispose() }

$installerName = "Screen-$appVersion-win-x64-Setup.exe"
$stagedInstaller = Join-Path $buildDirectory $installerName
$outputInstaller = Join-Path $outputDirectory $installerName
$excludedFiles = [Collections.Generic.List[string]]::new()
try {
    New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
    foreach ($file in @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Force | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($publishDirectory, $file.FullName)
        if ($file.Extension -in @('.pdb', '.lib', '.log', '.tmp', '.dmp', '.bak') -or
            $relative -match '(?i)(^|\\)(docs?|tests?|logs?|\.git|\.svn)(\\|$)' -or
            $file.Name -match '(?i)(\.Tests\.|testhost|testadapter|\.coverage|^(settings|credentials|preferences)\.json$)') {
            $excludedFiles.Add($relative)
            continue
        }
        $rootBinary = $relative -notmatch '\\' -and $file.Extension -in @('.exe', '.dll')
        $satellite = $relative -match '^[a-z]{2}(-[A-Za-z]{2,4})?\\[^\\]+\.resources\.dll$'
        $appMetadata = $relative -in @('Screen.deps.json', 'Screen.runtimeconfig.json', 'THIRD-PARTY-NOTICES.txt')
        $featureData = $relative -in $requiredFiles -and ($relative.StartsWith('models\') -or $relative -eq 'ffmpeg\bin\ffmpeg.exe')
        $license = $relative.StartsWith('licenses\', [StringComparison]::OrdinalIgnoreCase) -and $file.Extension -in @('', '.txt', '.md', '.html')
        if (-not ($rootBinary -or $satellite -or $appMetadata -or $featureData -or $license)) {
            throw "Unexpected publish file; refusing to package possible personal or development data: $relative"
        }
        Copy-PayloadFile $file.FullName $relative
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $runtimeDirectory -File)) {
        if ($file.Extension -ne '.dll') { throw "Unexpected file in app-local runtime: $($file.Name)" }
        Copy-PayloadFile $file.FullName $file.Name
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $noticeDirectory -Recurse -File)) {
        if ($file.Extension -notin @('', '.txt', '.md', '.html')) { throw "Unexpected notice file: $($file.Name)" }
        Copy-PayloadFile $file.FullName (Join-Path 'licenses' ([IO.Path]::GetRelativePath($noticeDirectory, $file.FullName)))
    }

    $allFiles = @(Get-ChildItem -LiteralPath $payloadDirectory -Recurse -File | Sort-Object FullName)
    $relativeFiles = @($allFiles | ForEach-Object { [IO.Path]::GetRelativePath($payloadDirectory, $_.FullName) })
    $include = [Collections.Generic.List[string]]::new()
    $include.Add('; Generated by tools/build-installer.ps1; exact allowlisted payload.')
    $include.Add('!macro InstallPayload')
    foreach ($relative in $relativeFiles) {
        $directory = [IO.Path]::GetDirectoryName($relative)
        $include.Add(('  SetOutPath "$INSTDIR\{0}"' -f $directory))
        $include.Add(('  File "${{PAYLOAD_DIR}}\{0}"' -f $relative))
        $include.Add('  IfErrors install_failed')
    }
    $include.Add('!macroend')
    $include.Add('!macro CheckPayloadLocks Prefix')
    foreach ($relative in $relativeFiles) {
        $include.Add(('  Push "$INSTDIR\{0}"' -f $relative))
        $include.Add('  Call ${Prefix}CheckFileUnlocked')
    }
    $include.Add('!macroend')
    $include.Add('!macro RemovePayload')
    foreach ($relative in $relativeFiles) {
        $include.Add(('  ${{If}} ${{FileExists}} "$INSTDIR\{0}"' -f $relative))
        $include.Add(('    Delete "$INSTDIR\{0}"' -f $relative))
        $include.Add('    IfErrors uninstall_failed')
        $include.Add('  ${EndIf}')
    }
    foreach ($directory in @(Get-ChildItem -LiteralPath $payloadDirectory -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending)) {
        $include.Add(('  RMDir "$INSTDIR\{0}"' -f [IO.Path]::GetRelativePath($payloadDirectory, $directory.FullName)))
    }
    $include.Add('!macroend')
    $payloadInclude = Join-Path $buildDirectory 'Payload.nsh'
    [IO.File]::WriteAllLines($payloadInclude, $include, [Text.UTF8Encoding]::new($true))
    $installedSize = [int][Math]::Ceiling(($allFiles | Measure-Object -Property Length -Sum).Sum / 1024)
    & $CompilerPath /V3 /WX /INPUTCHARSET UTF8 "/DAPP_VERSION=$appVersion" "/DAPP_FILE_VERSION=$appFileVersion" `
        "/DPAYLOAD_DIR=$payloadDirectory" "/DPAYLOAD_INCLUDE=$payloadInclude" "/DOUTPUT_FILE=$stagedInstaller" `
        "/DINSTALLED_SIZE=$installedSize" "/DVC_LICENSE_FILE=$vcLicenseFile" "/DICON_FILE=$iconPath" (Join-Path $installerSources 'Screen.nsi')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $stagedInstaller -PathType Leaf)) {
        throw "NSIS compilation failed (exit $LASTEXITCODE). The previous installer is preserved."
    }
    $hash = (Get-FileHash -LiteralPath $stagedInstaller -Algorithm SHA256).Hash
    Assert-WorkspacePath $outputDirectory
    Assert-WorkspacePath $outputInstaller
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $outputInstaller) {
        Assert-NoReparsePoints $outputInstaller
        [IO.File]::Move($stagedInstaller, $outputInstaller, $true)
    } else {
        Move-Item -LiteralPath $stagedInstaller -Destination $outputInstaller
    }
    # Remove only older generated installers after the replacement is complete.
    foreach ($old in @(Get-ChildItem -LiteralPath $outputDirectory -File)) {
        if ($old.Name -match '^Screen-\d+\.\d+\.\d+-win-x64-Setup\.exe$' -and $old.FullName -ne $outputInstaller) {
            Assert-WorkspacePath $old.FullName
            Assert-NoReparsePoints $old.FullName
            Remove-Item -LiteralPath $old.FullName -Force
        }
    }
    [pscustomobject]@{
        Installer = $outputInstaller
        Version = $appVersion
        Compiler = $compilerVersion
        PayloadFiles = $allFiles.Count
        ExcludedFiles = $excludedFiles.ToArray()
        SHA256 = $hash
        DigitallySigned = $false
    }
} finally {
    Remove-StagingDirectory
}
