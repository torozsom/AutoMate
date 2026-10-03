<# Imports the generated pilot settings into Web user secrets so IDE and dotnet-run starts use the same configuration. #>
param([string]$SecretsDirectory = (Join-Path $PSScriptRoot '../../.telemetry/pilot'))
$ErrorActionPreference = 'Stop'
$settings = @{}
$envPath = Join-Path ([System.IO.Path]::GetFullPath($SecretsDirectory)) 'automate.env'
foreach ($line in Get-Content -LiteralPath $envPath) {
    if (!$line.StartsWith('TelemetryStorage__')) { continue }
    $separator = $line.IndexOf('=')
    if ($separator -lt 0) { throw 'Malformed telemetry configuration entry.' }
    $settings[$line.Substring(0, $separator).Replace('__', ':')] = $line.Substring($separator + 1)
}
foreach ($required in @('Backend', 'LokiUrl', 'MetricsWriteUrl', 'MetricsQueryUrl', 'Authorization')) {
    if (!$settings['TelemetryStorage:' + $required]) { throw "Pilot configuration is missing $required." }
}
$webProject = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Web/Web.csproj'))
$certificatePath = Join-Path ([System.IO.Path]::GetFullPath($SecretsDirectory)) 'tls.crt'
if (!(Test-Path -LiteralPath $certificatePath)) { throw 'The pilot public TLS certificate is missing.' }
$settings['TelemetryStorage:CaCertificatePath'] = $certificatePath
# JSON on stdin avoids putting the private authorization value into command-line arguments or console output.
$settings | ConvertTo-Json | dotnet user-secrets set --project $webProject
if ($LASTEXITCODE -ne 0) { throw 'Unable to configure Web user secrets.' }
Write-Output 'Complete pilot telemetry settings saved for Web development. Restart AutoMate to apply them.'
