<#
.SYNOPSIS
Runs workspace projection migrations and PostgreSQL regressions in an owned disposable Docker container.
.DESCRIPTION
Requires an already cached PostgreSQL 17 image and Docker. Publishes only a random loopback port,
keeps database files in tmpfs, restores process environment and removes only its labeled container.
Never reads application secrets or connects to an existing database. No LLM request is made.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$verificationId = [Guid]::NewGuid().ToString('N')
$containerName = "automate-ui-verification-$verificationId"
$containerId = $null
$previousConnection = [Environment]::GetEnvironmentVariable('AUTOMATE_AI_TEST_DB', 'Process')
$previousPassword = [Environment]::GetEnvironmentVariable('POSTGRES_PASSWORD', 'Process')

try {
    & docker image inspect postgres:17-alpine *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Cache postgres:17-alpine before running isolated verification.' }
    $password = [Guid]::NewGuid().ToString('N')
    [Environment]::SetEnvironmentVariable('POSTGRES_PASSWORD', $password, 'Process')
    $containerId = & docker run --detach --pull never --name $containerName `
        --label "automate.verification.id=$verificationId" --publish '127.0.0.1::5432' `
        --tmpfs '/var/lib/postgresql/data:rw' --env POSTGRES_PASSWORD `
        --env 'POSTGRES_DB=automate_ai_verification' postgres:17-alpine
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the isolated PostgreSQL verification container.' }
    $containerId = $containerId.Trim()
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        & docker exec $containerId pg_isready --username postgres --dbname automate_ai_verification *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'The isolated verification database did not become ready.' }
    $mapping = & docker port $containerId '5432/tcp'
    if ($LASTEXITCODE -ne 0 -or $mapping -notmatch '^127\.0\.0\.1:(\d+)$') {
        throw 'Verification requires a single loopback-only PostgreSQL port.'
    }
    $port = $Matches[1]
    [Environment]::SetEnvironmentVariable('AUTOMATE_AI_TEST_DB',
        "Host=127.0.0.1;Port=$port;Database=automate_ai_verification;Username=postgres;Password=$password;Include Error Detail=false", 'Process')
    Write-Host 'Running workspace SQL projections on disposable PostgreSQL.'
    & dotnet test (Join-Path $repository 'Infrastructure.Tests/Infrastructure.Tests.csproj') --no-restore `
        --filter 'FullyQualifiedName~WorkspaceQueryTests'
    if ($LASTEXITCODE -ne 0) { throw 'Isolated workspace PostgreSQL verification failed.' }
}
finally {
    [Environment]::SetEnvironmentVariable('AUTOMATE_AI_TEST_DB', $previousConnection, 'Process')
    [Environment]::SetEnvironmentVariable('POSTGRES_PASSWORD', $previousPassword, 'Process')
    if ($containerId -and $containerId -match '^[a-f0-9]{64}$') {
        $inspection = (& docker inspect $containerId | ConvertFrom-Json)[0]
        $label = $inspection.Config.Labels.'automate.verification.id'
        if ($LASTEXITCODE -ne 0 -or $label -ne $verificationId) {
            throw 'Container ownership could not be verified; cleanup was refused.'
        }
        & docker rm --force --volumes $containerId *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Could not remove the owned verification container.' }
        Write-Host 'Removed the owned disposable PostgreSQL container and its temporary data.'
    }
}
