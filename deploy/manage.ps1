<# Starts and stops the three local Compose projects without merging their Docker identities. #>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'restart', 'status', 'pull', 'down')]
    [string]$Action = 'start',

    [switch]$Build,
    [switch]$Ide,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if ($Build -and $Action -notin @('start', 'restart')) {
    throw '-Build is supported only with start or restart.'
}

# Resolve paths from the script so invocation works from any working directory.
$deployRoot = $PSScriptRoot
$startupOrder = @('infrastructure', 'telemetry', 'app')
$shutdownOrder = @('app', 'telemetry', 'infrastructure')

function Invoke-StackCompose {
    <# Runs one folder's Compose command, stopping orchestration on the first failure. #>
    param([string]$Stack, [string[]]$ComposeArguments)

    $stackDirectory = Join-Path $deployRoot $Stack
    $arguments = @(
        'compose', '--project-directory', $stackDirectory,
        '--env-file', (Join-Path $stackDirectory '.env'),
        '--file', (Join-Path $stackDirectory 'compose.yaml')
    )
    $ideOverride = Join-Path $stackDirectory 'compose.ide.yaml'
    if ($Ide -and (Test-Path -LiteralPath $ideOverride)) {
        $arguments += @('--file', $ideOverride)
    }
    $arguments += @('--project-name', "automate-$Stack") + $ComposeArguments

    Write-Host "[$Stack] docker compose $($ComposeArguments -join ' ')"
    if ($DryRun) { return }
    & docker @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose failed for $Stack (exit code $LASTEXITCODE)."
    }
}

if ($Action -in @('stop', 'restart', 'down')) {
    $stopCommand = if ($Action -eq 'down') { 'down' } else { 'stop' }
    foreach ($stack in $shutdownOrder) {
        Invoke-StackCompose -Stack $stack -ComposeArguments @($stopCommand)
    }
}

if ($Action -in @('start', 'restart')) {
    # Stop a previously containerized Web so only the IDE runs deployment workers.
    if ($Ide) { Invoke-StackCompose -Stack 'app' -ComposeArguments @('stop', 'automate') }
    foreach ($stack in $startupOrder) {
        $upArguments = @('up', '-d')
        if ($Build -and $stack -eq 'app') { $upArguments += '--build' }
        Invoke-StackCompose -Stack $stack -ComposeArguments $upArguments
    }
} elseif ($Action -in @('status', 'pull')) {
    $composeArguments = if ($Action -eq 'status') { @('ps', '--all') } else { @('pull', '--ignore-buildable') }
    foreach ($stack in $startupOrder) {
        Invoke-StackCompose -Stack $stack -ComposeArguments $composeArguments
    }
}
