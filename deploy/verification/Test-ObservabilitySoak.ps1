<#
.SYNOPSIS
Runs a bounded local observability regression soak without touching running deployments or live providers.
.DESCRIPTION
Repeats real SDK/OTLP loopback, temporary spool, fake-time metric burst, collector interruption and analysis-worker
scenarios for the selected duration. It does not establish live Azure/provider capacity or production CPU equivalence.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 120)][int]$DurationMinutes = 20,
    [string]$OutputDirectory = '.tmp/observability-soak',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifactDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
if (-not $artifactDirectory.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
{
    throw 'The artifact directory must be inside this workspace.'
}
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
Push-Location $repositoryRoot
try
{
    if (-not $NoBuild)
    {
        & dotnet build AutoMate.slnx --configuration Release --verbosity minimal
        if ($LASTEXITCODE -ne 0)
        {
            throw 'Build failed before soak.'
        }
    }
    # Snapshot the test output so ordinary builds cannot replace assemblies during this long-running verification.
    $runtimeDirectory = Join-Path $artifactDirectory ('runtime-' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())
    New-Item -ItemType Directory -Path $runtimeDirectory | Out-Null
    Copy-Item -Path (Join-Path $repositoryRoot 'Test/bin/Release/net10.0/*') -Destination $runtimeDirectory -Recurse
    $filter = 'FullyQualifiedName~OperationalTelemetryTests|FullyQualifiedName~DockerMetricDeliveryTests|FullyQualifiedName~DiskTelemetrySpoolTests|FullyQualifiedName~AzureContainerAppLiveLogsTests|FullyQualifiedName~AnalysisWorkerConcurrencyTests'
    $started = [DateTimeOffset]::UtcNow
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $iterations = 0
    do
    {
        $iterations++
        $logPath = Join-Path $artifactDirectory "iteration-$iterations.log"
        & dotnet vstest (Join-Path $runtimeDirectory 'Test.dll') "--TestCaseFilter:$filter" '--logger:console;verbosity=minimal' *> $logPath
        $testExit = $LASTEXITCODE
        $status = [ordered]@{
            StartedUtc = $started.ToString('O')
            ObservedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            DurationMinutes = $DurationMinutes
            ElapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2)
            Iterations = $iterations
            LastExitCode = $testExit
            Complete = ($watch.Elapsed.TotalMinutes -ge $DurationMinutes)
            Scope = 'Synthetic providers, real SDK/loopback and temporary disk; no live deployment interruption.'
        }
        $status | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactDirectory 'status.json') -Encoding utf8
        if ($testExit -ne 0)
        {
            Get-Content -LiteralPath $logPath -Tail 60
            throw "Observability soak failed on iteration $iterations."
        }
        Write-Output "Passed iteration $iterations; elapsed $([Math]::Round($watch.Elapsed.TotalMinutes, 1) ) minutes."
    } while ($watch.Elapsed.TotalMinutes -lt $DurationMinutes)
}
finally
{
    Pop-Location
}
