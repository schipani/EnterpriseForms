<#
.SYNOPSIS
  SEC-BL-01 WP-2 (C-155) targeted Azurite integration test (Development/Testing only).

.DESCRIPTION
  Starts two throwaway Azurite 3.9.0 blob instances bound to 127.0.0.1 (own containers, no compose project, no other stack touched):
  one plays the security-event archive, the other the separately keyed journal-verification checkpoint store. Each gets its own
  random account key generated for this run (never written to disk) through AZURITE_ACCOUNTS, so neither uses the publicly known
  development key. Runs JournalVerificationAzuriteIntegrationTests (phase before-restart), restarts both containers, runs
  JournalVerificationAzuritePersistenceTests (phase after-restart), and removes both containers with their anonymous volumes.

  Separate Azurite instances are configuration separation only: one Development administrator controls both. This is not
  independence and not production evidence (docs/security/SEC_BL_01_DR_CONTRACT_CLOSURE.md section 7.4). Not Stage B, not a
  security-test stack run.
#>
param(
    [int]$ArchivePort = 18310,
    [int]$CheckpointPort = 18311,
    [switch]$KeepContainers
)

# Native-command exit codes are checked explicitly (PowerShell 5.1 turns native stderr into errors under 'Stop').
$ErrorActionPreference = 'Continue'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$image = 'mcr.microsoft.com/azure-storage/azurite:3.9.0'
$archiveName = 'enterpriseforms-secbl01-wp2-archive'
$checkpointName = 'enterpriseforms-secbl01-wp2-checkpoint'
$archiveAccount = 'efarchiveit'
$checkpointAccount = 'efcheckpointit'
$exe = Join-Path $repo 'tests\authority\AuthorizationAuthority.Tests\bin\Debug\net10.0\AuthorizationAuthority.Tests.exe'

function New-AccountKey {
    $bytes = New-Object byte[] 64
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return [Convert]::ToBase64String($bytes)
}

function Wait-Azurite([string]$name) {
    for ($i = 0; $i -lt 60; $i++) {
        $logs = (docker logs $name 2>&1 | ForEach-Object { "$_" }) -join "`n"
        if ($logs -match 'Azurite Blob service (successfully listens|is successfully listening)') { return }
        Start-Sleep -Milliseconds 500
    }
    docker logs $name 2>&1 | Select-Object -Last 20 | Out-Host
    throw "Azurite container $name did not become ready"
}

function Start-Azurite([string]$name, [int]$port, [string]$account, [string]$key) {
    docker run -d --name $name -p "127.0.0.1:${port}:10000" -e "AZURITE_ACCOUNTS=${account}:${key}" $image `
        azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck --loose --location /data | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run failed for $name" }
}

if (-not (Test-Path $exe)) { throw "test executable not found (build tests/authority/AuthorizationAuthority.Tests first): $exe" }

foreach ($name in $archiveName, $checkpointName) { docker rm -f -v $name 2>$null | Out-Null }
$archiveKey = New-AccountKey
$checkpointKey = New-AccountKey
$exitCodes = @{}
try {
    Start-Azurite $archiveName $ArchivePort $archiveAccount $archiveKey
    Start-Azurite $checkpointName $CheckpointPort $checkpointAccount $checkpointKey
    Wait-Azurite $archiveName
    Wait-Azurite $checkpointName

    $env:SECBL01_ARCHIVE_BLOB_URI = "http://127.0.0.1:$ArchivePort/$archiveAccount"
    $env:SECBL01_ARCHIVE_ACCOUNT = $archiveAccount
    $env:SECBL01_ARCHIVE_KEY = $archiveKey
    $env:SECBL01_CHECKPOINT_BLOB_URI = "http://127.0.0.1:$CheckpointPort/$checkpointAccount"
    $env:SECBL01_CHECKPOINT_ACCOUNT = $checkpointAccount
    $env:SECBL01_CHECKPOINT_KEY = $checkpointKey

    Write-Host '[wp2] phase before-restart: JournalVerificationAzuriteIntegrationTests'
    $env:SECBL01_PHASE = 'before-restart'
    & $exe --filter-class 'AuthorizationAuthority.Tests.JournalVerificationAzuriteIntegrationTests' | Out-Host
    $exitCodes['before-restart'] = $LASTEXITCODE

    Write-Host '[wp2] restarting both Azurite containers (same containers, same anonymous /data volumes)'
    docker restart $archiveName $checkpointName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'docker restart failed' }
    Wait-Azurite $archiveName
    Wait-Azurite $checkpointName

    Write-Host '[wp2] phase after-restart: JournalVerificationAzuritePersistenceTests'
    $env:SECBL01_PHASE = 'after-restart'
    & $exe --filter-class 'AuthorizationAuthority.Tests.JournalVerificationAzuritePersistenceTests' | Out-Host
    $exitCodes['after-restart'] = $LASTEXITCODE
}
finally {
    foreach ($variable in 'SECBL01_ARCHIVE_BLOB_URI', 'SECBL01_ARCHIVE_ACCOUNT', 'SECBL01_ARCHIVE_KEY', 'SECBL01_CHECKPOINT_BLOB_URI', 'SECBL01_CHECKPOINT_ACCOUNT', 'SECBL01_CHECKPOINT_KEY', 'SECBL01_PHASE') {
        Remove-Item "Env:$variable" -ErrorAction SilentlyContinue
    }
    if (-not $KeepContainers) {
        foreach ($name in $archiveName, $checkpointName) { docker rm -f -v $name 2>$null | Out-Null }
        Write-Host '[wp2] containers and their anonymous volumes removed'
    }
}

Write-Host ("[wp2] exit codes: before-restart={0} after-restart={1}" -f $exitCodes['before-restart'], $exitCodes['after-restart'])
if ($exitCodes['before-restart'] -ne 0 -or $exitCodes['after-restart'] -ne 0) { exit 1 }
exit 0
