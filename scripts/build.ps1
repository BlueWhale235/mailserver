param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Test
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
dotnet build (Join-Path $root "MailServer.slnx") -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

if ($Test) {
    dotnet test (Join-Path $root "MailServer.slnx") -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Build output: $(Join-Path $root "builds")"
