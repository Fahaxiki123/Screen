$ErrorActionPreference = 'Stop'
$testRoot = Join-Path $PSScriptRoot ('.publish-flow-fixtures-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $testRoot) { throw "Remove the previous test fixtures before repeating: $testRoot" }
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'rebuild-and-restart.ps1'
$required = @('Screen.exe', 'Screen.dll', 'Screen.Core.dll', 'Screen.deps.json', 'Screen.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll', 'wpfgfx_cor3.dll', 'onnxruntime.dll', 'libSkiaSharp.dll', 'models\v6\PP-OCRv6_det_small.onnx', 'models\v6\PP-OCRv6_rec_small.onnx', 'models\v6\ppocrv6_small_dict.txt', 'ffmpeg\bin\ffmpeg.exe')

# Child script scopes inherit these fakes; no application is built or launched.
function dotnet {
    if ($args -notcontains 'true') { throw 'Self-contained publication was not requested.' }
    $outputPath = $args[[Array]::IndexOf($args, '-o') + 1]
    foreach ($relativePath in $required) {
        if ($global:ScreenPublishTestMode -eq 'missing-runtime' -and $relativePath -eq 'wpfgfx_cor3.dll') { continue }
        $file = Join-Path $outputPath $relativePath
        $null = New-Item -ItemType Directory -Path (Split-Path $file) -Force
        [IO.File]::WriteAllText($file, 'new')
    }
    $global:LASTEXITCODE = if ($global:ScreenPublishTestMode -eq 'publish-failure') { 1 } else { 0 }
}

function Get-Process {
    param($Name, $ErrorAction)
    if ($global:ScreenPublishTestMode -eq 'running-project') {
        [pscustomobject]@{ Id = 99999; Path = (Join-Path $global:ScreenPublishTestFixture 'src\Screen.App\bin\Screen.exe'); HasExited = $false }
    }
    elseif ($global:ScreenPublishTestMode -eq 'running-official') {
        [pscustomobject]@{ Id = 99999; Path = (Join-Path (Split-Path $global:ScreenPublishTestFixture -Parent) '正式版\Screen.exe'); HasExited = $false }
    }
    elseif ($global:ScreenPublishTestMode -eq 'external-screen') {
        [pscustomobject]@{ Id = 99999; Path = 'C:\OtherProduct\Screen.exe'; HasExited = $false }
    }
    elseif ($global:ScreenPublishTestMode -eq 'startup-no-ready' -and $global:ScreenPublishTestLaunched) {
        [pscustomobject]@{ Id = 99999; Path = (Join-Path (Split-Path $global:ScreenPublishTestFixture -Parent) '正式版\Screen.exe'); HasExited = $false }
    }
}

function Move-Item {
    param($LiteralPath, $Destination)
    if ($global:ScreenPublishTestMode -eq 'promotion-failure' -and $LiteralPath.EndsWith('.Screen-staging')) { throw 'Injected promotion failure.' }
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
}

function Start-Process {
    param($FilePath, $WorkingDirectory, $ArgumentList, $WindowStyle, [switch]$PassThru)
    if ($global:ScreenPublishTestMode -eq 'launch-failure') { throw 'Injected launch failure.' }
    if ($global:ScreenPublishTestMode -eq 'startup-no-ready') {
        $global:ScreenPublishTestLaunched = $true
        $process = [pscustomobject]@{ Id = 99999; HasExited = $false; ExitCode = 0 }
        $process | Add-Member ScriptMethod WaitForExit { param($milliseconds) [Threading.Thread]::Sleep($milliseconds); return $false }
        return $process
    }
    throw 'Unexpected process launch during publication fixture test.'
}

function Assert-Test($Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

foreach ($modeValue in @('success', 'publish-failure', 'missing-runtime', 'promotion-failure', 'running-project', 'running-official', 'external-screen', 'recover-interrupted', 'launch-failure', 'startup-no-ready')) {
    $global:ScreenPublishTestMode = $modeValue
    $global:ScreenPublishTestLaunched = $false
    # Recreate the final directory topology entirely beneath this temporary root.
    # A copied script resolves its parent release directory within this fixture.
    $fixtureProductRoot = Join-Path $testRoot $modeValue
    $global:ScreenPublishTestFixture = Join-Path $fixtureProductRoot '开发工程'
    $official = Join-Path $fixtureProductRoot '正式版'
    $staging = Join-Path $global:ScreenPublishTestFixture 'artifacts\publish\.Screen-staging'
    $rollback = Join-Path $global:ScreenPublishTestFixture 'artifacts\publish\.Screen-rollback'
    $null = New-Item -ItemType Directory -Path $official -Force
    $null = New-Item -ItemType Directory -Path (Split-Path $staging -Parent) -Force
    Copy-Item -LiteralPath $source -Destination (Join-Path $global:ScreenPublishTestFixture 'rebuild-and-restart.ps1')
    # Isolate the fixture from any real app owning the session-wide mutex.
    $fixtureScript = Join-Path $global:ScreenPublishTestFixture 'rebuild-and-restart.ps1'
    $fixtureContent = [IO.File]::ReadAllText($fixtureScript).Replace('if (!$NoRestart) { Assert-SingleInstanceAvailable }', '# This fixture simulates an available session mutex.')
    [IO.File]::WriteAllText($fixtureScript, $fixtureContent)
    [IO.File]::WriteAllText((Join-Path $official 'old.txt'), 'old')
    if ($modeValue -eq 'recover-interrupted') { Microsoft.PowerShell.Management\Move-Item -LiteralPath $official -Destination $rollback }
    $failure = $null
    try {
        if ($modeValue -in @('launch-failure', 'startup-no-ready')) { & (Join-Path $global:ScreenPublishTestFixture 'rebuild-and-restart.ps1') *> $null }
        else { & (Join-Path $global:ScreenPublishTestFixture 'rebuild-and-restart.ps1') -NoRestart *> $null }
    }
    catch { $failure = $_.Exception.Message }
    if ($modeValue -in @('success', 'external-screen', 'recover-interrupted')) {
        Assert-Test ($null -eq $failure) "Unexpected failure in ${modeValue}: $failure"
        Assert-Test ((Get-Content -LiteralPath (Join-Path $official 'Screen.exe')) -eq 'new') "$modeValue did not promote new publication"
        Assert-Test (!(Test-Path -LiteralPath (Join-Path $official 'old.txt'))) "$modeValue left stale files"
        Assert-Test (!(Test-Path -LiteralPath $rollback)) "$modeValue left rollback"
        Assert-Test (!(Test-Path -LiteralPath $staging)) "$modeValue left staging"
    }
    elseif ($modeValue -eq 'startup-no-ready') {
        Assert-Test ($failure -like '*did not report successful initialization*') "No-ready test failed unexpectedly: $failure"
        Assert-Test ((Get-Content -LiteralPath (Join-Path $rollback 'old.txt')) -eq 'old') 'No-ready failure lost rollback'
        Assert-Test ((Get-Content -LiteralPath (Join-Path $official 'Screen.exe')) -eq 'new') 'No-ready failure modified the running app files'
        Assert-Test ($failure -like '*no process was stopped*') 'No-ready failure did not explain retained publications'
    }
    else {
        Assert-Test ($null -ne $failure) "$modeValue unexpectedly succeeded"
        Assert-Test ((Get-Content -LiteralPath (Join-Path $official 'old.txt')) -eq 'old') "$modeValue lost previous publication"
        Assert-Test (!(Test-Path -LiteralPath (Join-Path $official 'Screen.exe'))) "$modeValue mixed new files into previous publication"
        if ($modeValue -notin @('running-project', 'running-official')) { Assert-Test (!(Test-Path -LiteralPath $staging)) "$modeValue left staging" }
        if ($modeValue -eq 'launch-failure') { Assert-Test ($failure -like '*Injected launch failure*') "Launch test did not reach injected failure: $failure" }
    }
    "PASS $modeValue"
}

$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
$readyFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-ApplicationReady' }, $true)
Invoke-Expression $readyFunction.Extent.Text
$readyPid = Get-Random -Minimum 100000000 -Maximum 200000000
$readyEvent = [Threading.EventWaitHandle]::new($true, [Threading.EventResetMode]::ManualReset, "Local\Screen.NativeScreenshot.Ready.$readyPid")
try {
    $fakeProcess = [pscustomobject]@{ Id = $readyPid; HasExited = $false; ExitCode = 0; StableWait = 0 }
    $fakeProcess | Add-Member ScriptMethod WaitForExit { param($milliseconds) $this.StableWait = $milliseconds; return $false }
    Wait-ApplicationReady $fakeProcess
    Assert-Test ($fakeProcess.StableWait -eq 2000) 'Ready signal skipped stability verification'
    'PASS ready-signal-and-stability'
}
finally { $readyEvent.Dispose() }
$fakeProcess = [pscustomobject]@{ Id = $readyPid; HasExited = $true; ExitCode = -1 }
$failure = $null
try { Wait-ApplicationReady $fakeProcess } catch { $failure = $_.Exception.Message }
Assert-Test ($failure -like '*exited during startup*') 'Early process exit was not detected'
'PASS startup-early-exit'

# Reaching this point means every check passed. Failures above retain their
# fixture directory for diagnosis instead of deleting the evidence in finally.
$expectedParent = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$resolvedTestRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
if ((Split-Path $expectedParent -Leaf) -ne 'tools' -or
    (Split-Path $resolvedTestRoot -Parent) -ne $expectedParent -or
    (Split-Path $resolvedTestRoot -Leaf) -notmatch '^\.publish-flow-fixtures-[0-9a-f]{32}$') {
    throw "Refusing to clean an unexpected fixture path: $resolvedTestRoot"
}
$ancestor = $resolvedTestRoot
while ($ancestor) {
    $item = Get-Item -LiteralPath $ancestor -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Fixture cleanup refused a junction or symbolic link: $ancestor"
    }
    $ancestor = Split-Path $ancestor -Parent
}
$links = @(Get-ChildItem -LiteralPath $resolvedTestRoot -Force -Recurse |
    Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
if ($links.Count -ne 0) {
    throw "Fixture cleanup refused a junction or symbolic link: $($links[0].FullName)"
}
Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
'All publication-flow checks passed; temporary fixtures were removed.'
