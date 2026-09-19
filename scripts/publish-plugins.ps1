param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Output = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $root "builds\plugins\$Configuration"
}

$plugins = @(
    "MailServer.MimePlugin",
    "MailServer.GreylistingPlugin",
    "MailServer.TestPlugin"
)

foreach ($plugin in $plugins) {
    $project = Join-Path $root "plugins\$plugin\$plugin.csproj"
    $target = Join-Path $Output $plugin
    dotnet publish $project -c $Configuration -o $target
    if ($LASTEXITCODE -ne 0) {
        throw "Plugin publish failed: $plugin"
    }

    $manifestPath = Join-Path $target "plugin.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Plugin manifest is missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $manifest.system) {
        throw "Official plugin must declare system=true: $plugin"
    }
    $assemblyPath = Join-Path $target $manifest.entryAssembly
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Plugin entry assembly is missing: $assemblyPath"
    }
}

Write-Host "Plugin packages: $([System.IO.Path]::GetFullPath($Output))"
