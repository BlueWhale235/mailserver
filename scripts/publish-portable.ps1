param(
    [ValidateSet("win-x64", "linux-x64", "linux-arm64")]
    [string[]]$Runtime = @(),
    [string]$Output = "builds\publish",
    [switch]$RemoveBuildCache
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $PSScriptRoot "..\src\MailServer.Cli\MailServer.Cli.csproj"

function Copy-DeploymentAssets {
    param([Parameter(Mandatory)][string]$Target)

    foreach ($file in @(
        "docker-compose.yml",
        "Dockerfile",
        ".env.example",
        "mailserver.example.json",
        "README.md",
        "global.json",
        "Directory.Build.props",
        "Directory.Packages.props",
        "MailServer.slnx"
    )) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination (Join-Path $Target $file) -Force
    }

    Copy-Item -LiteralPath (Join-Path $root ".env.example") -Destination (Join-Path $Target ".env") -Force
    Copy-Item -LiteralPath (Join-Path $root "mailserver.example.json") -Destination (Join-Path $Target "mailserver.json") -Force

    foreach ($directory in @("certificates", "plugins", "scripts")) {
        New-Item -ItemType Directory -Path (Join-Path $Target $directory) -Force | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $root "certificates\README.md") -Destination (Join-Path $Target "certificates\README.md") -Force
    Copy-Item -LiteralPath (Join-Path $root "plugins\.gitkeep") -Destination (Join-Path $Target "plugins\.gitkeep") -Force
    Copy-Item -Path (Join-Path $root "scripts\*.ps1"), (Join-Path $root "scripts\*.sh") -Destination (Join-Path $Target "scripts") -Force

    # Dockerfile builds the individual services from source, so include its
    # complete build context in every deployment package.
    $sourceTarget = Join-Path $Target "src"
    New-Item -ItemType Directory -Path $sourceTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $root "src\*") -Destination $sourceTarget -Recurse -Force
}

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
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $temporaryArtifacts = Join-Path ([System.IO.Path]::GetTempPath()) "mailserver-publish-$rid-$([guid]::NewGuid().ToString('N'))"
    try {
        dotnet publish $project `
            -c Release `
            -r $rid `
            --self-contained true `
            -p:ArtifactsPath=$temporaryArtifacts `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:ValidateExecutableReferencesMatchSelfContained=false `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -o $target

        Get-ChildItem -LiteralPath $target -Filter *.pdb -File -ErrorAction SilentlyContinue |
            Remove-Item -Force
        Copy-DeploymentAssets -Target $target
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
