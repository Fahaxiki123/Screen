$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    dotnet restore Screen.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    dotnet build Screen.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test Screen.slnx -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    dotnet format Screen.slnx whitespace --verify-no-changes --no-restore --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Whitespace verification failed.' }
}
finally {
    Pop-Location
}
