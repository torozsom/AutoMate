<# Provisions a local operator's fixed-tenant data sources without rotating existing login credentials. #>
param(
    [Parameter(Mandatory)] [guid]$TenantId,
    [string]$SecretsDirectory = (Join-Path $PSScriptRoot '../../.telemetry/pilot')
)
$ErrorActionPreference = 'Stop'
if ($TenantId -eq [guid]::Empty) { throw 'Select the AutoMate project owner ID, not an empty tenant.' }
$resolvedDirectory = [System.IO.Path]::GetFullPath($SecretsDirectory)
$authorizationEntry = Get-Content -LiteralPath (Join-Path $resolvedDirectory 'automate.env') |
    Where-Object { $_.StartsWith('TelemetryStorage__Authorization=') } | Select-Object -First 1
if (!$authorizationEntry) { throw 'Initialize the telemetry gateway before provisioning Grafana.' }
$authorization = $authorizationEntry.Substring('TelemetryStorage__Authorization='.Length)
$certificate = [System.IO.File]::ReadAllText((Join-Path $resolvedDirectory 'tls.crt'))
$grafanaDirectory = Join-Path $resolvedDirectory 'grafana'
New-Item -ItemType Directory -Path $grafanaDirectory -Force | Out-Null
if ($IsWindows) {
    $acl = Get-Acl -LiteralPath $grafanaDirectory
    $acl.SetAccessRuleProtection($true, $false)
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
        $identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $grafanaDirectory -AclObject $acl
} else { & chmod 700 $grafanaDirectory }
foreach ($secretName in @('admin-password', 'encryption-key')) {
    $secretPath = Join-Path $grafanaDirectory $secretName
    if (!(Test-Path -LiteralPath $secretPath)) {
        $secret = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        [System.IO.File]::WriteAllText($secretPath, $secret)
    }
}
$sources = foreach ($definition in @(
    @{ Name = 'AutoMate Loki'; Uid = 'automate-loki'; Type = 'loki'; Url = 'https://gateway/loki'; Default = $false },
    @{ Name = 'AutoMate Mimir'; Uid = 'automate-mimir'; Type = 'prometheus'; Url = 'https://gateway/mimir/prometheus'; Default = $true }
)) {
    $settings = @{
        tlsAuthWithCACert = $true; tlsSkipVerify = $false; serverName = 'localhost'
        httpHeaderName1 = 'X-Scope-OrgID'; httpHeaderName2 = 'Authorization'
        timeout = '15'; manageAlerts = $false
    }
    if ($definition.Type -eq 'prometheus') {
        $settings.httpMethod = 'GET'; $settings.prometheusType = 'Mimir'
        $settings.prometheusVersion = '2.17.2'; $settings.timeInterval = '60s'
        $settings.disableRecordingRules = $true
    } else { $settings.maxLines = 500 }
    @{
        name = $definition.Name; uid = $definition.Uid; type = $definition.Type
        access = 'proxy'; url = $definition.Url; isDefault = $definition.Default; editable = $false
        jsonData = $settings
        secureJsonData = @{
            httpHeaderValue1 = $TenantId.ToString('N'); httpHeaderValue2 = $authorization; tlsCACert = $certificate
        }
    }
}
# JSON is also valid YAML; serialization avoids quoting credentials and PEM contents by hand.
$provisioning = @{ apiVersion = 1; datasources = @($sources) } | ConvertTo-Json -Depth 10
# Grafana expands dollar signs even in JSON/YAML values. Escape literal dollars before provisioning.
$provisioning = $provisioning.Replace('$', '$$')
[System.IO.File]::WriteAllText((Join-Path $grafanaDirectory 'datasources.yaml'), $provisioning)
# The host directory stays 0700; individual bind-mounted files must be readable by Grafana's unprivileged UID.
if (!$IsWindows) { Get-ChildItem -LiteralPath $grafanaDirectory -File | ForEach-Object { & chmod 644 $_.FullName } }
Write-Output "Local Grafana configured for tenant $($TenantId.ToString('N')). Login: admin. Password file: $grafanaDirectory/admin-password"
