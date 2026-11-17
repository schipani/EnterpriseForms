<#
.SYNOPSIS
  E3-M2c targeted local Docker Vault check of the dark Envelope v2 readers (Development/Testing only).

.DESCRIPTION
  Starts a throwaway HashiCorp Vault 1.20.4 dev server bound to 127.0.0.1 (its own container, no compose project, no other stack
  touched), prepares it with the repository's own scripts (infrastructure/docker/vault/business-encryption-init.sh,
  provision-business-encryption-keys.sh for two organizations, and provision-business-encryption-key-version.sh to give the second
  organization's field-value key a version 2), runs only EnvelopeV2ReaderVaultIntegrationTests, and removes the container. Tokens are
  random per run and never written to disk. No persistent database, no Azure, not Stage B, not a security-test stack run.
#>
param(
    [int]$Port = 18202,
    [switch]$KeepContainer
)

# Native-command exit codes are checked explicitly (PowerShell 5.1).
$ErrorActionPreference = 'Continue'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$name = 'enterpriseforms-e3m2c-vault-it'
$root = 'e3m2c-root-' + [guid]::NewGuid().ToString('N')
$business = 'e3m2c-business-' + [guid]::NewGuid().ToString('N')
$organization = '019a5b2c-a000-7000-8000-00000000000a'
$versionedOrganization = '019a5b2c-e400-7000-8000-0000000000e4'
$exe = Join-Path $repo 'tests\features\EnterpriseForms.Api.ContractTests\bin\Debug\net10.0\EnterpriseForms.Api.ContractTests.exe'
$code = 1

docker rm -f $name 2>$null | Out-Null
docker run -d --name $name --cap-add IPC_LOCK -p "127.0.0.1:${Port}:8200" hashicorp/vault:1.20.4 server -dev "-dev-root-token-id=$root" "-dev-listen-address=0.0.0.0:8200" | Out-Null
try {
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        docker exec -e VAULT_ADDR=http://127.0.0.1:8200 $name vault status *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { docker logs $name 2>&1 | Select-Object -Last 20; throw 'Vault did not become ready' }

    docker exec $name mkdir -p /vault-config | Out-Null
    foreach ($file in 'business-encryption-init.sh', 'business-encryption-policy.hcl', 'provision-business-encryption-keys.sh', 'provision-business-encryption-key-version.sh') {
        docker cp (Join-Path $repo "infrastructure\docker\vault\$file") "${name}:/vault-config/$file" | Out-Null
    }

    $envArgs = @('-e', 'VAULT_ADDR=http://127.0.0.1:8200', '-e', "VAULT_TOKEN=$root")
    docker exec @envArgs -e "BUSINESS_ENCRYPTION_VAULT_TOKEN=$business" $name sh /vault-config/business-encryption-init.sh | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'init script failed' }
    foreach ($org in $organization, $versionedOrganization) {
        docker exec @envArgs -e "ORGANIZATION_ID=$org" $name sh /vault-config/provision-business-encryption-keys.sh | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "provisioning failed for $org" }
    }
    docker exec @envArgs -e "ORGANIZATION_ID=$versionedOrganization" -e 'KEY_PURPOSE=field-value' -e 'EXPECTED_LATEST_VERSION=1' $name sh /vault-config/provision-business-encryption-key-version.sh | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'version provisioning script failed' }
    Write-Output 'Vault ready: two organizations provisioned; second organization field-value key at versions 1 and 2'

    $env:E2_VAULT_ADDRESS = "http://127.0.0.1:$Port"
    $env:E2_VAULT_BUSINESS_TOKEN = $business
    $env:E4_VAULT_ACTIVATION_ORGANIZATION = $versionedOrganization
    & $exe --filter-class 'EnterpriseForms.Api.ContractTests.EnvelopeV2ReaderVaultIntegrationTests'
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:E2_VAULT_ADDRESS, Env:E2_VAULT_BUSINESS_TOKEN, Env:E4_VAULT_ACTIVATION_ORGANIZATION -ErrorAction SilentlyContinue
    if (-not $KeepContainer) { docker rm -f $name | Out-Null }
}
exit $code
