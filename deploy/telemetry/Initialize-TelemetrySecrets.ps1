<# Creates private pilot credentials and a development TLS certificate. Existing secrets are never overwritten. #>
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../../.telemetry/pilot'))
$ErrorActionPreference = 'Stop'
$resolvedDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $resolvedDirectory 'compose.env')) {
    throw 'Telemetry secrets already exist. Reuse them or select a new directory.'
}
New-Item -ItemType Directory -Path $resolvedDirectory -Force | Out-Null
if ($IsWindows) {
    $directoryAcl = Get-Acl -LiteralPath $resolvedDirectory
    $directoryAcl.SetAccessRuleProtection($true, $false)
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $directoryAcl.AddAccessRule($rule)
    Set-Acl -LiteralPath $resolvedDirectory -AclObject $directoryAcl
} else {
    & chmod 700 $resolvedDirectory
}
$objectSecret = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$gatewaySecret = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$bcrypt = $gatewaySecret | docker run --rm -i httpd:2.4-alpine htpasswd -niB automate
if ($LASTEXITCODE -ne 0) { throw 'Unable to generate gateway bcrypt credentials.' }
Set-Content -LiteralPath (Join-Path $resolvedDirectory 'htpasswd') -Value $bcrypt -Encoding ascii
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('localhost')
    $san.AddIpAddress([System.Net.IPAddress]::Loopback)
    $request.CertificateExtensions.Add($san.Build())
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(90))
    try {
        Set-Content -LiteralPath (Join-Path $resolvedDirectory 'tls.crt') -Value $certificate.ExportCertificatePem() -Encoding ascii
        Set-Content -LiteralPath (Join-Path $resolvedDirectory 'tls.key') -Value $rsa.ExportPkcs8PrivateKeyPem() -Encoding ascii
    } finally { $certificate.Dispose() }
} finally { $rsa.Dispose() }
$mountDirectory = $resolvedDirectory.Replace('\', '/')
$ingestionSecret = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
@("OBJECT_STORAGE_ACCESS_KEY=automate-pilot", "OBJECT_STORAGE_SECRET_KEY=$objectSecret", "TELEMETRY_SECRETS_DIR=$mountDirectory", "TELEMETRY_INGEST_TOKEN=$ingestionSecret") |
    Set-Content -LiteralPath (Join-Path $resolvedDirectory 'compose.env') -Encoding ascii
$authorization = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("automate:$gatewaySecret"))
@('TelemetryStorage__Backend=LokiMimir', 'TelemetryStorage__LokiUrl=https://localhost:9443/loki',
  'TelemetryStorage__DeliveryMode=DiskGateway', 'TelemetryStorage__GatewayUrl=http://localhost:9450',
  'TelemetryStorage__AllowInsecureDevelopment=true', "TelemetryStorage__GatewayToken=$ingestionSecret",
  'TelemetryStorage__MetricsWriteUrl=https://localhost:9443/mimir/otlp/v1/metrics',
  'TelemetryStorage__MetricsQueryUrl=https://localhost:9443/mimir/prometheus',
  "TelemetryStorage__CaCertificatePath=$mountDirectory/tls.crt", "TelemetryStorage__Authorization=Basic $authorization") |
    Set-Content -LiteralPath (Join-Path $resolvedDirectory 'automate.env') -Encoding ascii
if (!$IsWindows) { Get-ChildItem -LiteralPath $resolvedDirectory -File | ForEach-Object { & chmod 600 $_.FullName } }
Write-Output "Private pilot files created in $resolvedDirectory. Trust tls.crt on the AutoMate host before connecting; no TLS bypass is configured."
