param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Test
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
dotnet build (Join-Path $root "MailServer.slnx") -c $Configuration
if ($Test) {
    dotnet test (Join-Path $root "MailServer.slnx") -c $Configuration --no-build
}

Write-Host "Build output: $(Join-Path $root "builds")"
