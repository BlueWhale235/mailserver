param(
    [Parameter(Mandatory = $true)][string]$BaselineRoot,
    [int]$Rounds = 5
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$baselinePath = (Resolve-Path -LiteralPath $BaselineRoot).Path
$resultPath = Join-Path $projectRoot ('builds/performance/efficiency-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $resultPath | Out-Null
$previousBenchmarkSetting = $env:MAILSERVER_EFFICIENCY_BENCHMARK
try {
    $env:MAILSERVER_EFFICIENCY_BENCHMARK = '1'
    $inputs = @{ startedAt = (Get-Date).ToString('o'); baselineRoot = $baselinePath; optimizedRoot = $projectRoot;
        rounds = $Rounds; configuration = 'Release'; warmup = 1; samplesPerCase = 20;
        baselineCommit = (& git -C $baselinePath rev-parse HEAD); optimizedCommit = (& git -C $projectRoot rev-parse HEAD) }
    $inputs | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultPath 'parameters.json') -Encoding utf8
    & git -C $baselinePath diff --binary | Set-Content -LiteralPath (Join-Path $resultPath 'baseline-inputs.patch') -Encoding utf8
    & git -C $projectRoot diff --binary | Set-Content -LiteralPath (Join-Path $resultPath 'optimized-inputs.patch') -Encoding utf8
    foreach ($sourceRoot in @($baselinePath, $projectRoot)) {
        & dotnet build (Join-Path $sourceRoot 'tests/MailServer.Tests/MailServer.Tests.csproj') -c Release -p:SkipAdminBuild=true *> (Join-Path $resultPath ('build-' + $(if ($sourceRoot -eq $baselinePath) {'baseline'} else {'optimized'}) + '.log'))
        if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed; see result logs.' }
    }
    $records = @()
    for ($round = 1; $round -le $Rounds; $round++) {
        $variants = if ($round % 2 -eq 1) { @('baseline', 'optimized') } else { @('optimized', 'baseline') }
        foreach ($variant in $variants) {
            $sourceRoot = if ($variant -eq 'baseline') { $baselinePath } else { $projectRoot }
            $logPath = Join-Path $resultPath "$variant-round-$round.log"
            & dotnet test (Join-Path $sourceRoot 'tests/MailServer.Tests/MailServer.Tests.csproj') -c Release --no-build --filter FullyQualifiedName~EfficiencyBenchmarks --logger 'console;verbosity=detailed' *> $logPath
            if ($LASTEXITCODE -ne 0) { throw "Benchmark $variant round $round failed; see result logs." }
            foreach ($line in Get-Content -LiteralPath $logPath) {
                $position = $line.IndexOf('EFFICIENCY_BENCHMARK:')
                if ($position -lt 0) { continue }
                $record = $line.Substring($position + 'EFFICIENCY_BENCHMARK:'.Length) | ConvertFrom-Json
                $record | Add-Member -NotePropertyName variant -NotePropertyValue $variant
                $record | Add-Member -NotePropertyName round -NotePropertyValue $round
                $records += $record
            }
            Write-Output "Completed $variant round $round."
        }
    }
    $records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $resultPath 'results.json') -Encoding utf8
    Write-Output "Results: $resultPath"
}
finally {
    $env:MAILSERVER_EFFICIENCY_BENCHMARK = $previousBenchmarkSetting
}
