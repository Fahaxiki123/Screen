param(
    [switch]$NoRestart
)

# Finish captures/recordings and exit Screen from its tray before replacement.
# The running app is never killed: an old build has no safe idle/exit protocol.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if ([IO.Path]::GetFileName($projectRoot) -ne '开发工程') {
    throw 'Run this script from the 开发工程 folder in the arranged Screen directory.'
}
$productRoot = [IO.Path]::GetDirectoryName($projectRoot)
$publishRoot = Join-Path $projectRoot 'artifacts\publish'
$publishDir = Join-Path $productRoot '正式版'
$candidateDir = Join-Path $publishRoot '.Screen-staging'
$backupDir = Join-Path $publishRoot '.Screen-rollback'
$appExe = Join-Path $publishDir 'Screen.exe'
$lockPath = Join-Path $publishRoot '.publish.lock'

function Assert-PublishPath([string]$Path, [switch]$InspectChildren) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $allowed = @($publishDir, $candidateDir, $backupDir, $lockPath)
    $insideProject = $fullPath.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)
    $isOfficial = [string]::Equals($fullPath, $publishDir, [StringComparison]::OrdinalIgnoreCase)
    if ($fullPath -notin $allowed -or (!$insideProject -and !$isOfficial)) {
        throw "Refusing to change a path outside the publication targets: $fullPath"
    }

    # Reject junctions/symlinks, including ancestors, before moving or deleting.
    $ancestor = $fullPath
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Publication path contains a junction or symbolic link: $ancestor"
            }
        }
        $ancestor = Split-Path -Path $ancestor -Parent
    }
    if ($InspectChildren -and (Test-Path -LiteralPath $fullPath)) {
        $links = @(Get-ChildItem -LiteralPath $fullPath -Force -Recurse |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if ($links.Count -ne 0) {
            throw "Publication contains a junction or symbolic link: $($links[0].FullName)"
        }
    }
}

function Remove-PublicationDirectory([string]$Path) {
    Assert-PublishPath $Path -InspectChildren
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Move-PublicationDirectory([string]$Source, [string]$Destination) {
    Assert-PublishPath $Source -InspectChildren
    Assert-PublishPath $Destination
    if (Test-Path -LiteralPath $Destination) {
        throw "The publication destination already exists: $Destination"
    }
    Move-Item -LiteralPath $Source -Destination $Destination
}

function Assert-ProjectAppExited {
    $running = @(Get-Process -Name Screen -ErrorAction SilentlyContinue)
    foreach ($process in $running) {
        try {
            $executablePath = $process.Path
        }
        catch {
            throw "Cannot inspect Screen process $($process.Id); publication was not replaced."
        }
        if (!$executablePath) {
            if ($process.HasExited) { continue }
            throw "Cannot inspect Screen process $($process.Id); publication was not replaced."
        }
        if ($executablePath.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $executablePath.StartsWith($publishDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Screen is running from $executablePath (PID $($process.Id)). Finish or save any capture/recording, exit from the tray, and run this script again. No process was stopped."
        }
    }
}

function Assert-SingleInstanceAvailable {
    try {
        $existingMutex = [Threading.Mutex]::OpenExisting('Local\Screen.NativeScreenshot.SingleInstance.v1')
    }
    catch [Threading.WaitHandleCannotBeOpenedException] {
        return
    }
    $existingMutex.Dispose()
    throw 'Another Screen instance owns this session. Exit it from the tray before restarting the official publication. No process was stopped.'
}

function Assert-PublicationComplete([string]$Path) {
    $required = @(
        'Screen.exe', 'Screen.dll', 'Screen.Core.dll',
        'Screen.deps.json', 'Screen.runtimeconfig.json',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
        'PresentationFramework.dll', 'wpfgfx_cor3.dll',
        'onnxruntime.dll', 'libSkiaSharp.dll',
        'models\v6\PP-OCRv6_det_small.onnx',
        'models\v6\PP-OCRv6_rec_small.onnx',
        'models\v6\ppocrv6_small_dict.txt',
        'ffmpeg\bin\ffmpeg.exe'
    )
    foreach ($relativePath in $required) {
        $file = Join-Path $Path $relativePath
        if (!(Test-Path -LiteralPath $file -PathType Leaf) -or
            (Get-Item -LiteralPath $file).Length -eq 0) {
            throw "Publication is incomplete: $relativePath is missing or empty."
        }
    }
}

function Wait-ApplicationReady($Process) {
    $readyEventName = "Local\Screen.NativeScreenshot.Ready.$($Process.Id)"
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "The updated app exited during startup (exit code $($Process.ExitCode))."
        }
        $readyEvent = $null
        try {
            $readyEvent = [Threading.EventWaitHandle]::OpenExisting($readyEventName)
            if ($readyEvent.WaitOne(0)) {
                if ($Process.WaitForExit(2000)) {
                    throw "The updated app exited after initialization (exit code $($Process.ExitCode))."
                }
                return
            }
        }
        catch [Threading.WaitHandleCannotBeOpenedException] {
            # App creates this event only after the tray and request listener are ready.
        }
        finally {
            if ($null -ne $readyEvent) { $readyEvent.Dispose() }
        }
        $null = $Process.WaitForExit(200)
    }
    throw 'The updated app did not report successful initialization within 15 seconds.'
}

Assert-PublishPath $lockPath
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
try {
    $publishLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
}
catch {
    throw 'Another publication is running, or the publication lock cannot be opened.'
}

$promoted = $false
$previousMoved = $false
try {
    if (Test-Path -LiteralPath $backupDir) {
        if (Test-Path -LiteralPath $publishDir) {
            throw "An interrupted update left both official and rollback folders. Verify the official app before removing $backupDir; neither copy was changed."
        }
        Assert-ProjectAppExited
        Move-PublicationDirectory $backupDir $publishDir
        Write-Host 'Recovered the previous official publication after an interrupted update.'
    }

    # A staging directory is disposable only when no project executable is using it.
    if (Test-Path -LiteralPath $candidateDir) {
        Assert-ProjectAppExited
        Remove-PublicationDirectory $candidateDir
    }
    Assert-PublishPath $candidateDir
    Write-Host '1/4 Building and checking the new self-contained publication...'
    dotnet publish (Join-Path $projectRoot 'src\Screen.App\Screen.App.csproj') -c Release -r win-x64 --self-contained true -o $candidateDir -v:q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. The official publication was not changed.' }
    Assert-PublicationComplete $candidateDir

    Write-Host '2/4 Checking that Screen has exited safely...'
    Assert-ProjectAppExited
    if (!$NoRestart) { Assert-SingleInstanceAvailable }

    Write-Host '3/4 Replacing the official publication as a complete directory...'
    if (Test-Path -LiteralPath $publishDir) {
        Move-PublicationDirectory $publishDir $backupDir
        $previousMoved = $true
    }
    try {
        Move-PublicationDirectory $candidateDir $publishDir
        $promoted = $true
        if (!$NoRestart) {
            Write-Host '4/4 Starting the official publication...'
            # --startup also prevents a racing second launch from requesting a capture.
            $started = Start-Process -FilePath $appExe -WorkingDirectory $publishDir -ArgumentList '--restarted', '--startup' -WindowStyle Hidden -PassThru
            Wait-ApplicationReady $started
        }
        else {
            Write-Host '4/4 Publication ready; restart skipped by -NoRestart.'
        }
    }
    catch {
        $updateFailure = $_
        # Never move files out from underneath an app that remains running.
        try {
            Assert-ProjectAppExited
        }
        catch {
            throw "$($updateFailure.Exception.Message) Automatic rollback was paused because a Screen process remains running. Close any startup error dialog and exit Screen, then restore $backupDir if present. Both publications were retained; no process was stopped."
        }
        if ($promoted) { Remove-PublicationDirectory $publishDir }
        if ($previousMoved) {
            Move-PublicationDirectory $backupDir $publishDir
            $previousMoved = $false
            Write-Warning 'Update failed; the previous official publication has been restored.'
        }
        throw $updateFailure
    }

    # Only remove rollback after a successful replacement (and startup, unless skipped).
    if ($previousMoved) {
        try {
            Remove-PublicationDirectory $backupDir
        }
        catch {
            Write-Warning "The official app was updated, but temporary rollback cleanup failed: $($_.Exception.Message)"
        }
    }
    Write-Host "Official publication: $appExe"
}
finally {
    try {
        if (Test-Path -LiteralPath $candidateDir) {
            # Do not delete staging if it was launched independently during this run.
            Assert-ProjectAppExited
            Remove-PublicationDirectory $candidateDir
        }
    }
    catch {
        Write-Warning "Temporary staging was retained: $($_.Exception.Message)"
    }
    $publishLock.Dispose()
}
