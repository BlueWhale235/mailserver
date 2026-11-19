#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$cacheDir = Join-Path $root '.cache'
$toolsDir = Join-Path $cacheDir 'windows-tools'
$publishDir = Join-Path $root 'builds\windows-app'
$instanceDir = Join-Path $root 'data\windows-local'
$configPath = Join-Path $instanceDir 'mailserver.json'
$loopback = '127.0.0.1'
$adminPort = 8080
$smtpPort = 2525
$imapPort = 1143
$adminUser = 'admin'
$initialPassword = 'Aa123456789000'

function Get-WindowsArchitecture {
    $cpu = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    switch ($cpu) {
        'AMD64' { 'x64' }
        'ARM64' { 'arm64' }
        default { throw "A 64-bit Windows installation is required to download the build tools ($cpu)." }
    }
}

function Install-Zip([string]$Uri, [string]$ExpectedHash, [string]$Algorithm, [string]$Destination) {
    [void][IO.Directory]::CreateDirectory($toolsDir)
    $zip = Join-Path $toolsDir ([IO.Path]::GetFileName(([uri]$Uri).AbsolutePath))
    if (-not (Test-Path -LiteralPath $zip) -or (Get-FileHash -LiteralPath $zip -Algorithm $Algorithm).Hash -ne $ExpectedHash) {
        $curl = Get-Command curl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($Algorithm -eq 'SHA512' -and $curl) {
            & $curl.Source --fail --location --progress-bar --connect-timeout 30 --speed-limit 1024 --speed-time 120 `
                --continue-at - --output $zip $Uri
            if ($LASTEXITCODE -ne 0) { throw "Download failed (exit code $LASTEXITCODE). Run the launcher again to retry: $Uri" }
        } else {
            Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $zip -TimeoutSec 600
        }
    }
    if ((Get-FileHash -LiteralPath $zip -Algorithm $Algorithm).Hash -ne $ExpectedHash) {
        [IO.File]::Delete($zip)
        throw "The download failed its $Algorithm check: $Uri"
    }
    Expand-Archive -LiteralPath $zip -DestinationPath $Destination -Force
    [IO.File]::Delete($zip)
}

function Invoke-DotNet {
    & $script:dotnet @args
    if ($LASTEXITCODE -ne 0) { throw ".NET command failed (exit code $LASTEXITCODE)." }
}

function Get-CompatibleDotNet([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    try {
        $version = & $Path --version 2>$null
        if ($LASTEXITCODE -eq 0 -and $version -match '^\d+\.\d+\.\d+') { return $Path }
    } catch {
        # An installed host may have no SDK matching global.json.
    }
}

function Get-CompatibleNode([string]$Directory) {
    $node = Join-Path $Directory 'node.exe'
    if (-not (Test-Path -LiteralPath $node -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $Directory 'npm.cmd') -PathType Leaf)) { return }
    try {
        $version = & $node --version 2>$null
        if ($LASTEXITCODE -eq 0 -and $version -match '^v24\.') { return $Directory }
    } catch {
        # Try the next installation.
    }
}

function Assert-PortAvailable([string]$Address, [int]$Port) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Parse($Address), $Port)
    try {
        $listener.ExclusiveAddressUse = $true
        $listener.Start()
    } catch {
        throw "Cannot listen on ${Address}:$Port. Close the program using it or change the port in the local configuration."
    } finally {
        $listener.Stop()
    }
}

Push-Location -LiteralPath $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $cacheDir 'dotnet-home'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = 'true'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:NUGET_PACKAGES = Join-Path $cacheDir 'nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $cacheDir 'nuget-http'
    $env:NUGET_PLUGINS_CACHE_PATH = Join-Path $cacheDir 'nuget-plugins'
    $env:NUGET_SCRATCH = Join-Path $cacheDir 'nuget-scratch'
    $env:npm_config_cache = Join-Path $cacheDir 'npm'

    $sdkVersion = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
    $sdkDir = Join-Path $toolsDir 'dotnet'
    $sdkLocal = Join-Path $sdkDir 'dotnet.exe'
    $dotnetCandidates = @(
        (Get-Command dotnet.exe -CommandType Application -All -ErrorAction SilentlyContinue).Source
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
        $sdkLocal
    )
    $dotnet = $null
    foreach ($candidate in $dotnetCandidates | Where-Object { $_ } | Select-Object -Unique) {
        $dotnet = Get-CompatibleDotNet $candidate
        if ($dotnet) { break }
    }
    if (-not $dotnet) {
        $architecture = Get-WindowsArchitecture
        $channel = ([version]$sdkVersion).ToString(2)
        $manifest = Invoke-RestMethod -UseBasicParsing -TimeoutSec 45 `
            -Uri "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/$channel/releases.json"
        $sdk = $manifest.releases | ForEach-Object { $_.sdks } |
            Where-Object { $_.version -eq $sdkVersion } | Select-Object -First 1
        $download = $sdk.files | Where-Object { $_.rid -eq "win-$architecture" -and $_.url -like '*.zip' } |
            Select-Object -First 1
        if (-not $download -or $download.hash -notmatch '^[a-f0-9]{128}$') {
            throw "Could not locate the official .NET SDK $sdkVersion download for Windows $architecture."
        }
        Write-Host "Downloading .NET SDK $sdkVersion from Microsoft..."
        Install-Zip $download.url $download.hash SHA512 $sdkDir
        $dotnet = Get-CompatibleDotNet $sdkLocal
        if (-not $dotnet) { throw "Could not install the .NET SDK required by global.json ($sdkVersion)." }
    }
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:Path = "$env:DOTNET_ROOT;$env:Path"
    Write-Host "Using .NET SDK $(& $dotnet --version)."

    $nodeCandidates = @(
        Get-Command node.exe -CommandType Application -All -ErrorAction SilentlyContinue |
            ForEach-Object { Split-Path -Parent $_.Source }
        Join-Path $env:ProgramFiles 'nodejs'
        if (Test-Path -LiteralPath $toolsDir -PathType Container) {
            Get-ChildItem -LiteralPath $toolsDir -Directory -Filter 'node-v24.*-win-*' |
                ForEach-Object { $_.FullName }
        }
    )
    $nodeDir = $null
    foreach ($candidate in $nodeCandidates | Select-Object -Unique) {
        $nodeDir = Get-CompatibleNode $candidate
        if ($nodeDir) { break }
    }
    if (-not $nodeDir) {
        $architecture = Get-WindowsArchitecture
        Write-Host 'Downloading Node.js 24 from nodejs.org...'
        $index = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 45 -Uri 'https://nodejs.org/dist/latest-v24.x/SHASUMS256.txt').Content
        $release = [regex]::Match($index, "(?m)^(?<hash>[a-f0-9]{64})\s+(?<zip>node-v24\.\d+\.\d+-win-$architecture\.zip)\r?$")
        if (-not $release.Success) { throw "Could not locate the official Node.js 24 download for Windows $architecture." }
        $zipName = $release.Groups['zip'].Value
        $directoryName = [IO.Path]::GetFileNameWithoutExtension($zipName)
        $nodeVersion = ($directoryName -split '-')[1]
        Install-Zip "https://nodejs.org/dist/$nodeVersion/$zipName" $release.Groups['hash'].Value SHA256 $toolsDir
        $nodeDir = Get-CompatibleNode (Join-Path $toolsDir $directoryName)
        if (-not $nodeDir) { throw 'Could not install Node.js 24 and npm.' }
    }
    $env:Path = "$nodeDir;$env:Path"
    Write-Host "Using Node.js $(& (Join-Path $nodeDir 'node.exe') --version)."

    $firstRun = -not (Test-Path -LiteralPath $configPath -PathType Leaf)
    if ($firstRun) {
        foreach ($port in @($adminPort, $smtpPort, $imapPort)) { Assert-PortAvailable $loopback $port }
    } else {
        $settings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        if ($settings.AdminServer.Http.Enabled) {
            Assert-PortAvailable $settings.AdminServer.BindAddress $settings.AdminServer.Http.Port
        }
        if ($settings.AdminServer.Https.Enabled) {
            Assert-PortAvailable $settings.AdminServer.BindAddress $settings.AdminServer.Https.Port
        }
        foreach ($endpoint in @($settings.Smtp.Endpoints) + @($settings.Imap.Endpoints)) {
            Assert-PortAvailable $endpoint.Address $endpoint.Port
        }
    }

    Write-Host 'Building MailServer and its web admin in Release mode...'
    Invoke-DotNet publish 'src/MailServer.Cli/MailServer.Cli.csproj' -c Release -o $publishDir
    $app = Join-Path $publishDir 'mailserver.dll'
    Push-Location -LiteralPath $publishDir
    try {
        if ($firstRun) {
            Write-Host 'Initializing the local SQLite database and administrator...'
            $pending = Join-Path $instanceDir 'mailserver.initializing.json'
            Invoke-DotNet $app init --config $pending --non-interactive --provider Sqlite --seed-demo `
                --connection 'Data Source=mailserver.db' --bind $loopback --http-port "$adminPort" `
                --admin $adminUser --admin-password $initialPassword
            $settings = Get-Content -LiteralPath $pending -Raw | ConvertFrom-Json
            $settings.Smtp.Endpoints = @([pscustomobject]@{
                Address = $loopback; Port = $smtpPort; Security = 'StartTls'; RequireAuthentication = $false
            })
            $settings.Imap.Endpoints = @([pscustomobject]@{
                Address = $loopback; Port = $imapPort; Security = 'StartTls'
            })
            [IO.File]::WriteAllText($pending, ($settings | ConvertTo-Json -Depth 20))
            Move-Item -LiteralPath $pending -Destination $configPath
            Write-Host "First login: $adminUser / $initialPassword"
        } else {
            Invoke-DotNet $app db update --config $configPath
        }
        if ($settings.AdminServer.Http.Enabled) {
            Write-Host ("Admin: http://{0}:{1}/admin/" -f $settings.AdminServer.BindAddress, $settings.AdminServer.Http.Port)
        }
        Write-Host "Configuration: $configPath"
        Write-Host 'Press Ctrl+C to stop MailServer.'
        Invoke-DotNet $app run all --config $configPath
    } finally {
        Pop-Location
    }
} catch {
    Write-Host "MailServer could not start: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    Pop-Location
}
