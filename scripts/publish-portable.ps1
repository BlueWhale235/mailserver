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
        ".env.example",
        "mailserver.example.json",
        "README.md"
    )) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination (Join-Path $Target $file) -Force
    }

    Copy-Item -LiteralPath (Join-Path $root ".env.example") -Destination (Join-Path $Target ".env") -Force
    Copy-Item -LiteralPath (Join-Path $root "mailserver.example.json") -Destination (Join-Path $Target "mailserver.json") -Force

    foreach ($directory in @("certificates", "plugins", "security-assets", "wwwroot", "logs")) {
        New-Item -ItemType Directory -Path (Join-Path $Target $directory) -Force | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $root "certificates\README.md") -Destination (Join-Path $Target "certificates\README.md") -Force
    Copy-Item -LiteralPath (Join-Path $root "security-assets\README.md") -Destination (Join-Path $Target "security-assets\README.md") -Force
    Copy-Item -Path (Join-Path $root "src\MailServer.Api\wwwroot\*") -Destination (Join-Path $Target "wwwroot") -Recurse -Force

    $adminSource = Join-Path $root "src\MailServer.Admin\dist"
    $adminIndex = Join-Path $adminSource "index.html"
    if (-not (Test-Path -LiteralPath $adminIndex -PathType Leaf)) {
        throw "The Vue administration bundle was not produced at '$adminIndex'."
    }

    $adminTarget = Join-Path $Target "wwwroot\admin"
    New-Item -ItemType Directory -Path $adminTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $adminSource "*") -Destination $adminTarget -Recurse -Force
}

function Remove-ReferencedHostPublishFiles {
    param(
        [Parameter(Mandatory)][string]$Target,
        [switch]$SingleFile
    )

    Remove-Item -LiteralPath (Join-Path $Target "appsettings.json") -Force -ErrorAction SilentlyContinue
    foreach ($prefix in @("MailServer.Api", "MailServer.Smtp", "MailServer.Imap", "MailServer.Worker")) {
        foreach ($name in @(
            "$prefix.exe",
            "$prefix.deps.json",
            "$prefix.runtimeconfig.json",
            "$prefix.pdb"
        )) {
            Remove-Item -LiteralPath (Join-Path $Target $name) -Force -ErrorAction SilentlyContinue
        }

        Get-ChildItem -LiteralPath $Target -Filter "$prefix.staticwebassets.*.json" -File -ErrorAction SilentlyContinue |
            Remove-Item -Force

        if ($SingleFile) {
            Remove-Item -LiteralPath (Join-Path $Target "$prefix.dll") -Force -ErrorAction SilentlyContinue
        }
    }
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

    # Republish into a clean directory. Incremental publishes leave stale
    # bundles behind (for example a view that has since been deleted), and the
    # whole package is copied to production, so stale files ship silently.
    if (Test-Path -LiteralPath $target) {
        $resolvedRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\$Output"))
        $resolvedTarget = [System.IO.Path]::GetFullPath($target)
        if (-not $resolvedTarget.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean '$resolvedTarget' because it is outside '$resolvedRoot'."
        }
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $temporaryArtifacts = Join-Path ([System.IO.Path]::GetTempPath()) "mailserver-publish-$rid-$([guid]::NewGuid().ToString('N'))"
    $platformArgs = if ($rid.EndsWith("-x64", [StringComparison]::OrdinalIgnoreCase)) { @("-p:Platform=x64") } else { @() }
    try {
        dotnet publish $project `
            -c Release `
            -r $rid `
            --self-contained true `
            $platformArgs `
            -p:ArtifactsPath=$temporaryArtifacts `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:ValidateExecutableReferencesMatchSelfContained=false `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -o $target
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for '$rid' with exit code $LASTEXITCODE."
        }

        Get-ChildItem -LiteralPath $target -Filter *.pdb -File -ErrorAction SilentlyContinue |
            Remove-Item -Force
        Remove-ReferencedHostPublishFiles -Target $target -SingleFile
        Copy-DeploymentAssets -Target $target

        $executableName = if ($rid.StartsWith("win-")) { "mailserver.exe" } else { "mailserver" }
        $executablePath = Join-Path $target $executableName
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "Publish for '$rid' completed without expected executable '$executablePath'."
        }

        Write-Host "Deployment package: $([System.IO.Path]::GetFullPath($target))"
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
