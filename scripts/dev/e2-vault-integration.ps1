<#
.SYNOPSIS
  E-2 targeted local Docker Vault integration test (Development/Testing only).

.DESCRIPTION
  Starts a throwaway HashiCorp Vault 1.20.4 dev server bound to 127.0.0.1 (its own container, no compose project, no other stack
  touched), prepares it with the repository's own scripts (infrastructure/docker/vault/business-encryption-init.sh and
  provision-business-encryption-keys.sh, plus (E-4) provision-business-encryption-key-version.sh for a second organization, plus an Authority-style `transit` HMAC key for the isolation probes), runs only
  VaultTransitDockerIntegrationTests and (E-4) KeyActivationVaultIntegrationTests, and removes the container. Tokens are random per run and never written to disk.
  Not Stage B, not a security-test stack run.
#>
param(
    [int]$Port = 18201,
    [switch]$KeepContainer
)

# Native-command exit codes are checked explicitly; 'Stop' would turn expected stderr of the refusal probes into errors (PowerShell 5.1).
$ErrorActionPreference = 'Continue'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$name = 'enterpriseforms-e2-vault-it'
$root = 'e2-root-' + [guid]::NewGuid().ToString('N')
$business = 'e2-business-' + [guid]::NewGuid().ToString('N')
$organization = '019a5b2c-a000-7000-8000-00000000000a'
$activationOrganization = '019a5b2c-e400-7000-8000-0000000000e4'
$exe = Join-Path $repo 'tests\features\EnterpriseForms.Api.ContractTests\bin\Debug\net10.0\EnterpriseForms.Api.ContractTests.exe'

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
    docker exec @envArgs -e "BUSINESS_ENCRYPTION_VAULT_TOKEN=$business" $name sh /vault-config/business-encryption-init.sh
    if ($LASTEXITCODE -ne 0) { throw 'init script failed' }
    docker exec @envArgs -e "ORGANIZATION_ID=$organization" $name sh /vault-config/provision-business-encryption-keys.sh
    if ($LASTEXITCODE -ne 0) { throw 'provisioning script failed' }
    # Refusals of the scripts themselves.
    docker exec @envArgs -e "ORGANIZATION_ID=not-a-uuid" $name sh /vault-config/provision-business-encryption-keys.sh 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'provisioning accepted a non-UUID organization' }
    docker exec @envArgs -e "BUSINESS_ENCRYPTION_VAULT_TOKEN=$root" $name sh /vault-config/business-encryption-init.sh 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'init accepted the root token as the business token' }
    docker exec @envArgs -e "BUSINESS_ENCRYPTION_VAULT_TOKEN=$business" -e "BUSINESS_ENCRYPTION_MOUNT=transit" $name sh /vault-config/business-encryption-init.sh 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'init accepted the Authority mount' }

    # E-4: a second organization whose field-value key gets version 2 through the version-provisioning script (NOT activated).
    docker exec @envArgs -e "ORGANIZATION_ID=$activationOrganization" $name sh /vault-config/provision-business-encryption-keys.sh | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'provisioning of the activation organization failed' }
    $versionArgs = @('-e', "ORGANIZATION_ID=$activationOrganization", '-e', 'KEY_PURPOSE=field-value')
    docker exec @envArgs @versionArgs -e 'EXPECTED_LATEST_VERSION=1' $name sh /vault-config/provision-business-encryption-key-version.sh
    if ($LASTEXITCODE -ne 0) { throw 'version provisioning script failed' }
    # Its refusals: a repeated run (latest is now 2), an unknown purpose, a missing key, a non-UUID organization, a bad version.
    $refusals = @(
        @('-e', "ORGANIZATION_ID=$activationOrganization", '-e', 'KEY_PURPOSE=field-value', '-e', 'EXPECTED_LATEST_VERSION=1'),
        @('-e', "ORGANIZATION_ID=$activationOrganization", '-e', 'KEY_PURPOSE=signing', '-e', 'EXPECTED_LATEST_VERSION=1'),
        @('-e', "ORGANIZATION_ID=$([guid]::NewGuid())", '-e', 'KEY_PURPOSE=field-value', '-e', 'EXPECTED_LATEST_VERSION=1'),
        @('-e', 'ORGANIZATION_ID=not-a-uuid', '-e', 'KEY_PURPOSE=field-value', '-e', 'EXPECTED_LATEST_VERSION=1'),
        @('-e', "ORGANIZATION_ID=$activationOrganization", '-e', 'KEY_PURPOSE=field-value', '-e', 'EXPECTED_LATEST_VERSION=0')
    )
    foreach ($refusal in $refusals) {
        $reason = (docker exec @envArgs @refusal $name sh /vault-config/provision-business-encryption-key-version.sh 2>&1 | ForEach-Object { "$_" }) -join ' '
        if ($LASTEXITCODE -eq 0) { throw "version provisioning accepted: $($refusal -join ' ')" }
        Write-Output "  refused as expected: $($reason.Trim())"
    }
    Write-Output 'version provisioning script: provisioned v2 once; 5 refusals as expected'

    # An Authority-style HMAC key on the Authority's `transit` mount (what the business token must never reach).
    docker exec @envArgs $name vault secrets enable transit | Out-Null
    docker exec @envArgs $name vault write -f "transit/keys/org-$($organization.Replace('-', ''))-row-integrity" type=hmac key_size=32 exportable=false | Out-Null

    $env:E2_VAULT_ADDRESS = "http://127.0.0.1:$Port"
    $env:E2_VAULT_ROOT_TOKEN = $root
    $env:E2_VAULT_BUSINESS_TOKEN = $business
    $env:E4_VAULT_ACTIVATION_ORGANIZATION = $activationOrganization
    & $exe --filter-class 'EnterpriseForms.Api.ContractTests.VaultTransitDockerIntegrationTests' --filter-class 'EnterpriseForms.Api.ContractTests.KeyActivationVaultIntegrationTests'
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:E2_VAULT_ADDRESS, Env:E2_VAULT_ROOT_TOKEN, Env:E2_VAULT_BUSINESS_TOKEN, Env:E4_VAULT_ACTIVATION_ORGANIZATION -ErrorAction SilentlyContinue
    if (-not $KeepContainer) { docker rm -f $name | Out-Null }
}
exit $code
