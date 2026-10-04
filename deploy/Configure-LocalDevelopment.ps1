<# Imports private local application and telemetry settings without printing credentials. Run with PowerShell 7. #>
param(
    [string]$ApplicationEnv = (Join-Path $PSScriptRoot 'development/.env'),
    [string]$TelemetryEnv = (Join-Path $PSScriptRoot '../.telemetry/pilot/automate.env')
)
$ErrorActionPreference = 'Stop'
$settings = @{}
foreach ($path in @($ApplicationEnv, $TelemetryEnv)) {
    foreach ($line in Get-Content -LiteralPath $path) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
        $separator = $line.IndexOf('=')
        if ($separator -lt 1) { throw 'Malformed local development environment entry.' }
        $settings[$line.Substring(0, $separator).Replace('__', ':')] = $line.Substring($separator + 1)
    }
}
foreach ($required in @('ConnectionStrings:DefaultConnection', 'ConnectionStrings:Redis',
    'Authentication:GitHub:ClientId', 'Authentication:GitHub:ClientSecret',
    'Authentication:Microsoft:ClientId', 'Authentication:Microsoft:ClientSecret',
    'TelemetryStorage:GatewayToken', 'TelemetryStorage:GatewayUrl')) {
    if ([string]::IsNullOrWhiteSpace([string]$settings[$required])) { throw "Local development is missing $required." }
}
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Web/Web.csproj'))
# Piping JSON keeps credentials out of process command-line arguments and normal console output.
$settings | ConvertTo-Json | dotnet user-secrets set --project $project
if ($LASTEXITCODE -ne 0) { throw 'Local development configuration import failed.' }
Write-Output 'Local application and disk-gateway settings imported. No credentials were printed.'
