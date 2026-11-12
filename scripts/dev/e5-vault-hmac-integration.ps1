<#
.SYNOPSIS
  E-5 targeted local Docker Vault integration test for Authority HMAC key activation (Development/Testing only).

.DESCRIPTION
  Starts a throwaway HashiCorp Vault 1.20.4 dev server bound to 127.0.0.1 (its own container, no compose project, no other stack
  touched). As the root operator it enables `transit`, creates two organization HMAC keys the way key custody does
  (non-exportable, no plaintext backup), writes the development stack's Authority onboarding policy (read/hmac/verify on org-*
  only) and creates the Authority's token with it. It then provisions version 2 of the first organization's key with
  infrastructure/docker/vault/provision-integrity-key-version.sh, checks that script's refusals, runs only
  IntegrityKeyActivationVaultIntegrationTests and removes the container. Tokens are random per run and never written to disk.
  Not Stage B, not a security-test stack run.
#>
param(
    [int]$Port = 18202,
    [switch]$KeepContainer
)

# Native-command exit codes are checked explicitly; 'Stop' would turn expected stderr of the refusal probes into errors (PowerShell 5.1).
$ErrorActionPreference = 'Continue'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$name = 'enterpriseforms-e5-vault-it'
$root = 'e5-root-' + [guid]::NewGuid().ToString('N')
$rotated = '019a5b2c-e500-7000-8000-0000000000e5'
$single = '019a5b2c-e501-7000-8000-0000000000e5'
$exe = Join-Path $repo 'tests\authority\AuthorizationAuthority.Tests\bin\Debug\net10.0\AuthorizationAuthority.Tests.exe'

function KeyName([string]$organization) { 'org-' + $organization.Replace('-', '') + '-row-integrity-v1' }

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

    $envArgs = @('-e', 'VAULT_ADDR=http://127.0.0.1:8200', '-e', "VAULT_TOKEN=$root")
    docker exec @envArgs $name vault secrets enable transit | Out-Null
    foreach ($organization in $rotated, $single) {
        docker exec @envArgs $name vault write "transit/keys/$(KeyName $organization)" type=hmac key_size=32 exportable=false allow_plaintext_backup=false | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "key creation failed for $organization" }
    }

    # The development stack's Authority onboarding policy (scripts/devstack/devstack.py AUTHORITY_ONBOARDING_POLICY_HCL).
    $policy = 'path "transit/keys/org-*" { capabilities = ["read"] }' + "`n" + 'path "transit/hmac/org-*" { capabilities = ["update"] }' + "`n" +
        'path "transit/verify/org-*" { capabilities = ["update"] }' + "`n" + 'path "auth/token/lookup-self" { capabilities = ["read"] }'
    # Written without a byte-order mark (PowerShell 5.1 pipes to native commands with one), then copied in.
    $policyFile = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'e5-authority-transit-' + [guid]::NewGuid().ToString('N') + '.hcl')
    [IO.File]::WriteAllText($policyFile, $policy + "`n", (New-Object System.Text.UTF8Encoding($false)))
    docker exec $name mkdir -p /vault-config | Out-Null
    docker cp $policyFile "${name}:/vault-config/authority-transit.hcl" | Out-Null
    Remove-Item $policyFile -ErrorAction SilentlyContinue
    docker exec @envArgs $name vault policy write authority-transit /vault-config/authority-transit.hcl | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'policy write failed' }
    $authority = (docker exec @envArgs $name vault token create -orphan -no-default-policy -policy=authority-transit -ttl=1h -field=token)
    if ($LASTEXITCODE -ne 0 -or -not $authority) { throw 'authority token creation failed' }

    docker exec $name mkdir -p /vault-config | Out-Null
    docker cp (Join-Path $repo 'infrastructure\docker\vault\provision-integrity-key-version.sh') "${name}:/vault-config/provision-integrity-key-version.sh" | Out-Null
    docker exec @envArgs -e "ORGANIZATION_ID=$rotated" -e 'EXPECTED_LATEST_VERSION=1' $name sh /vault-config/provision-integrity-key-version.sh
    if ($LASTEXITCODE -ne 0) { throw 'version provisioning script failed' }
    # Its refusals: a repeated run (latest is now 2), a missing key, a non-UUID organization, a bad expected version, the Authority's token.
    $refusals = @(
        @('-e', "ORGANIZATION_ID=$rotated", '-e', 'EXPECTED_LATEST_VERSION=1', '-e', "VAULT_TOKEN=$root"),
        @('-e', "ORGANIZATION_ID=$([guid]::NewGuid())", '-e', 'EXPECTED_LATEST_VERSION=1', '-e', "VAULT_TOKEN=$root"),
        @('-e', 'ORGANIZATION_ID=not-a-uuid', '-e', 'EXPECTED_LATEST_VERSION=1', '-e', "VAULT_TOKEN=$root"),
        @('-e', "ORGANIZATION_ID=$single", '-e', 'EXPECTED_LATEST_VERSION=0', '-e', "VAULT_TOKEN=$root"),
        @('-e', "ORGANIZATION_ID=$single", '-e', 'EXPECTED_LATEST_VERSION=1', '-e', "VAULT_TOKEN=$authority")
    )
    foreach ($refusal in $refusals) {
        $reason = (docker exec -e VAULT_ADDR=http://127.0.0.1:8200 @refusal $name sh /vault-config/provision-integrity-key-version.sh 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -and $_ -ne 'System.Management.Automation.RemoteException' }) -join ' '
        if ($LASTEXITCODE -eq 0) { throw "version provisioning accepted: $(($refusal | Where-Object { $_ -notlike 'VAULT_TOKEN=*' }) -join ' ')" }
        Write-Output "  refused as expected: $($reason.Trim())"
    }
    $latestSingle = docker exec @envArgs $name vault read -field=latest_version "transit/keys/$(KeyName $single)"
    if ($latestSingle -ne '1') { throw "the refused runs changed the single-version key (latest $latestSingle)" }
    Write-Output 'version provisioning script: provisioned v2 once; 5 refusals as expected; the single-version key is unchanged'

    $env:E5_VAULT_ADDRESS = "http://127.0.0.1:$Port"
    $env:E5_VAULT_ROOT_TOKEN = $root
    $env:E5_VAULT_AUTHORITY_TOKEN = $authority
    $env:E5_VAULT_ROTATED_ORGANIZATION = $rotated
    $env:E5_VAULT_SINGLE_VERSION_ORGANIZATION = $single
    & $exe --filter-class 'AuthorizationAuthority.Tests.IntegrityKeyActivationVaultIntegrationTests'
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:E5_VAULT_ADDRESS, Env:E5_VAULT_ROOT_TOKEN, Env:E5_VAULT_AUTHORITY_TOKEN, Env:E5_VAULT_ROTATED_ORGANIZATION, Env:E5_VAULT_SINGLE_VERSION_ORGANIZATION -ErrorAction SilentlyContinue
    if (-not $KeepContainer) { docker rm -f $name | Out-Null }
}
exit $code
