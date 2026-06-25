param(
    [ValidateSet("win-x64", "linux-x64", "linux-arm64")]
    [string[]]$Runtime = @(),
    [string]$Output = "builds\publish",
    [switch]$RemoveBuildCache
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\MailServer.Cli\MailServer.Cli.csproj"
if ($Runtime.Count -eq 0) {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $Runtime = if ($IsWindows -or $env:OS -eq "Windows_NT") {
        @("win-$architecture")
    } elseif ($IsLinux) {
        @("linux-$architecture")
    } else {
        throw "Only Windows and Linux portable targets are supported."
    }
}

foreach ($rid in $Runtime) {
    $target = Join-Path $PSScriptRoot "..\$Output\$rid"
    $temporaryArtifacts = Join-Path ([System.IO.Path]::GetTempPath()) "mailserver-publish-$rid-$([guid]::NewGuid().ToString('N'))"
    try {
        dotnet publish $project `
            -c Release `
            -r $rid `
            --self-contained true `
            -p:ArtifactsPath=$temporaryArtifacts `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=None `
            -o $target
    }
    finally {
        if (Test-Path -LiteralPath $temporaryArtifacts) {
            Remove-Item -LiteralPath $temporaryArtifacts -Recurse -Force
        }
    }
}

if ($RemoveBuildCache) {
    $buildsRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\builds")).Path
    foreach ($name in @("bin", "obj")) {
        $target = Join-Path $buildsRoot $name
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
}
