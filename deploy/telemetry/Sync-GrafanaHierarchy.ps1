<# Configures local operator navigation from read-only metadata; telemetry payloads stay in Loki/Mimir.
Run again after adding users or recreating the database. Existing projects/deployments update live. #>
param(
    [string]$SecretsDirectory = (Join-Path $PSScriptRoot '../../.telemetry/pilot'),
    [string]$PostgresContainer = 'automate-postgres',
    [string]$PostgresAddress = 'host.docker.internal:5432',
    [string]$GrafanaUrl = 'http://localhost:3000'
)
$ErrorActionPreference = 'Stop'
$secretsRoot = [IO.Path]::GetFullPath($SecretsDirectory)
# All SQL runs through stdin, including the generated password, never command-line arguments.
function Invoke-CatalogSql([string]$Sql) {
    $result = $Sql | & docker exec -i $PostgresContainer sh -c 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Atq -v ON_ERROR_STOP=1'
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the local Grafana metadata catalog.' }
    return $result
}
$owners = @(Invoke-CatalogSql 'SELECT id FROM public.users ORDER BY username, id;')
if (!$owners.Count) { throw 'Create an AutoMate user before configuring its Grafana hierarchy.' }
$ownerIds = @($owners | ForEach-Object { [guid]$_ })
$grafanaDirectory = Join-Path $secretsRoot 'grafana'
# Initialization establishes the private directory ACL before writing any additional secret.
& "$PSScriptRoot/Initialize-Grafana.ps1" -TenantId $ownerIds[0] -SecretsDirectory $secretsRoot | Out-Null
$passwordFile = Join-Path $grafanaDirectory 'catalog-password'
if (!(Test-Path -LiteralPath $passwordFile)) {
    [IO.File]::WriteAllText($passwordFile, [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
}
$catalogPassword = [IO.File]::ReadAllText($passwordFile).Trim()
if ($catalogPassword -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid generated catalog credential format.' }
$catalogSql = @'
DO $$ BEGIN
 IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'automate_grafana_catalog') THEN
   CREATE ROLE automate_grafana_catalog LOGIN;
 END IF;
END $$;
ALTER ROLE automate_grafana_catalog NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '__PASSWORD__';
ALTER ROLE automate_grafana_catalog SET default_transaction_read_only = on;
CREATE SCHEMA IF NOT EXISTS grafana_catalog;
REVOKE ALL ON SCHEMA grafana_catalog FROM PUBLIC;
CREATE OR REPLACE VIEW grafana_catalog.users AS
 SELECT replace(id::text, '-', '') AS user_id, username FROM public.users;
CREATE OR REPLACE VIEW grafana_catalog.projects AS
 SELECT replace(id::text, '-', '') AS project_id, replace(user_id::text, '-', '') AS user_id, name FROM public.applications;
CREATE OR REPLACE VIEW grafana_catalog.deployments AS
 SELECT replace(d.id::text, '-', '') AS deployment_id, replace(c.app_id::text, '-', '') AS project_id,
 replace(a.user_id::text, '-', '') AS user_id, d.created_at, d.status
 FROM public.deployments d JOIN public.cs_projects c ON c.id=d.cs_project_id JOIN public.applications a ON a.id=c.app_id;
GRANT USAGE ON SCHEMA grafana_catalog TO automate_grafana_catalog;
GRANT SELECT ON ALL TABLES IN SCHEMA grafana_catalog TO automate_grafana_catalog;
SELECT current_database();
'@
$database = @(Invoke-CatalogSql $catalogSql.Replace('__PASSWORD__', $catalogPassword))[-1]
$catalogSource = @{
    name = 'AutoMate catalog'; uid = 'automate-catalog'; type = 'grafana-postgresql-datasource'
    access = 'proxy'; url = $PostgresAddress; user = 'automate_grafana_catalog'; editable = $false
    jsonData = @{ database = $database; sslmode = 'disable'; postgresVersion = 1600; maxOpenConns = 3; maxIdleConns = 1 }
    secureJsonData = @{ password = $catalogPassword }
}
& "$PSScriptRoot/Initialize-Grafana.ps1" -TenantId $ownerIds[0] -AdditionalTenantIds $ownerIds -CatalogDataSource $catalogSource -SecretsDirectory $secretsRoot | Out-Null
# Reload provisioning without restarting the application, collectors, or storage services.
$adminPassword = [IO.File]::ReadAllText((Join-Path $grafanaDirectory 'admin-password')).Trim()
$headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('admin:' + $adminPassword)) }
foreach ($kind in @('datasources', 'dashboards')) {
    Invoke-RestMethod -Method Post -Uri "$GrafanaUrl/api/admin/provisioning/$kind/reload" -Headers $headers | Out-Null
}
Write-Output "Grafana hierarchy configured for $($ownerIds.Count) user(s). Open $GrafanaUrl/d/automate-hierarchy."
