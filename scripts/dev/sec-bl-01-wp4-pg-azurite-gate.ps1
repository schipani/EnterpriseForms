<#
.SYNOPSIS
  SEC-BL-01 WP-3/WP-4 (C-155) and WP-4A (A-1) disposable PostgreSQL 18 + Azurite gate (Development/Testing only).

.DESCRIPTION
  Starts one throwaway postgres:18.0 container and two throwaway Azurite 3.9.0 blob instances (security-event archive; separately
  keyed journal-verification checkpoint store), all bound to 127.0.0.1, with credentials generated for this run and never written to
  disk. No compose project, persistent volume or other stack is touched. The tests create the Authority runtime role, apply the real
  Authority migrations and runtime grants, seed synthetic data only, and run the baseline and verifier as the runtime role in a
  server-enforced read-only snapshot. Runs JournalVerificationPostgresIntegrationTests and JournalBoundedListingAzuriteTests (WP-4A), then
  removes all three containers with their
  anonymous volumes. Not Stage B, not a security-test stack run, not production evidence.

.PARAMETER EvidenceDirectory
  Optional directory for the measurement JSON and the console log (local paths are not written into them by the tests).

.PARAMETER Class
  SEC-BL-01 WP-5b: test classes to run (default: the WP-4 gate classes).

.PARAMETER Measure
  SEC-BL-01 WP-5b (P2-D5): also run the opt-in D+/W2-S delivery measurement (SECBL01_DPLUS_MEASURE=1).

.PARAMETER MethodFilter
  SEC-BL-01 WP-5b: optional --filter-method pattern (for example '*WP5BDPlusDeliveryMeasurement').
#>
param(
    [int]$PostgresPort = 18320,
    [int]$ArchivePort = 18321,
    [int]$CheckpointPort = 18322,
    [int]$PerformanceRows = 20000,
    [int]$CheckpointCount = 2016,
    [string]$EvidenceDirectory,
    [switch]$KeepContainers,
    [string[]]$Class = @('AuthorizationAuthority.Tests.JournalVerificationPostgresIntegrationTests', 'AuthorizationAuthority.Tests.JournalBoundedListingAzuriteTests'),
    [switch]$Measure,
    [string]$MeasureBacklogs = '32,256,2048',
    [string[]]$MethodFilter,
    # WP-7A: a suffix for the container names and another test executable (a frozen copy of the build), so a long measurement can run
    # beside other gate runs without touching their containers or blocking builds. Defaults keep the previous behaviour.
    [string]$NameSuffix = '',
    [string]$TestExecutable
)

$ErrorActionPreference = 'Continue'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$names = @{ Postgres = "enterpriseforms-secbl01-wp4-postgres$NameSuffix"; Archive = "enterpriseforms-secbl01-wp4-archive$NameSuffix"; Checkpoint = "enterpriseforms-secbl01-wp4-checkpoint$NameSuffix" }
$azurite = 'mcr.microsoft.com/azure-storage/azurite:3.9.0'
$exe = if ($TestExecutable) { $TestExecutable } else { Join-Path $repo 'tests\authority\AuthorizationAuthority.Tests\bin\Debug\net10.0\AuthorizationAuthority.Tests.exe' }

function New-Secret([int]$bytes) {
    $buffer = New-Object byte[] $bytes
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    return [Convert]::ToBase64String($buffer)
}

function New-Password { return ((New-Secret 24) -replace '[^A-Za-z0-9]', 'x') }

function Wait-Log([string]$name, [string]$pattern) {
    for ($i = 0; $i -lt 120; $i++) {
        $logs = (docker logs $name 2>&1 | ForEach-Object { "$_" }) -join "`n"
        if ($logs -match $pattern) { return }
        Start-Sleep -Milliseconds 500
    }
    docker logs $name 2>&1 | Select-Object -Last 20 | Out-Host
    throw "container $name did not become ready"
}

if (-not (Test-Path $exe)) { throw "test executable not found (build tests/authority/AuthorizationAuthority.Tests first): $exe" }

foreach ($name in $names.Values) { docker rm -f -v $name 2>$null | Out-Null }
$adminPassword = New-Password
$runtimePassword = New-Password
$archiveKey = New-Secret 64
$checkpointKey = New-Secret 64
$exitCode = 1
try {
    docker run -d --name $names.Postgres -p "127.0.0.1:${PostgresPort}:5432" -e POSTGRES_USER=efadmin -e "POSTGRES_PASSWORD=$adminPassword" -e POSTGRES_DB=authority postgres:18.0 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'docker run postgres failed' }
    foreach ($pair in @(@($names.Archive, $ArchivePort, 'efarchiveit', $archiveKey), @($names.Checkpoint, $CheckpointPort, 'efcheckpointit', $checkpointKey))) {
        docker run -d --name $pair[0] -p "127.0.0.1:$($pair[1]):10000" -e "AZURITE_ACCOUNTS=$($pair[2]):$($pair[3])" $azurite `
            azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck --loose --location /data | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "docker run $($pair[0]) failed" }
    }

    Wait-Log $names.Archive 'Azurite Blob service (successfully listens|is successfully listening)'
    Wait-Log $names.Checkpoint 'Azurite Blob service (successfully listens|is successfully listening)'
    $ready = $false
    for ($i = 0; $i -lt 120; $i++) {
        docker exec $names.Postgres pg_isready -U efadmin -d authority -h 127.0.0.1 *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'postgres did not become ready' }

    $env:SECBL01_PG_ADMIN = "Host=127.0.0.1;Port=$PostgresPort;Database=authority;Username=efadmin;Password=$adminPassword;Timeout=15;Command Timeout=300"
    $env:SECBL01_PG_RUNTIME_ROLE = 'authority_runtime'
    $env:SECBL01_PG_RUNTIME_PASSWORD = $runtimePassword
    $env:SECBL01_PG_RUNTIME = "Host=127.0.0.1;Port=$PostgresPort;Database=authority;Username=authority_runtime;Password=$runtimePassword;Timeout=15"
    $env:SECBL01_ARCHIVE_BLOB_URI = "http://127.0.0.1:$ArchivePort/efarchiveit"
    $env:SECBL01_ARCHIVE_ACCOUNT = 'efarchiveit'
    $env:SECBL01_ARCHIVE_KEY = $archiveKey
    $env:SECBL01_CHECKPOINT_BLOB_URI = "http://127.0.0.1:$CheckpointPort/efcheckpointit"
    $env:SECBL01_CHECKPOINT_ACCOUNT = 'efcheckpointit'
    $env:SECBL01_CHECKPOINT_KEY = $checkpointKey
    $env:SECBL01_PERF_ROWS = "$PerformanceRows"
    $env:SECBL01_CHECKPOINT_COUNT = "$CheckpointCount"
    if ($EvidenceDirectory) {
        New-Item -ItemType Directory -Force $EvidenceDirectory | Out-Null
        $EvidenceDirectory = (Resolve-Path $EvidenceDirectory).Path # the test process runs in its own directory
        $env:SECBL01_EVIDENCE_DIR = $EvidenceDirectory
    }

    if ($Measure) {
        $env:SECBL01_DPLUS_MEASURE = '1'
        $env:SECBL01_DPLUS_BACKLOGS = $MeasureBacklogs
    }

    $filters = @()
    foreach ($c in $Class) { $filters += @('--filter-class', $c) }
    foreach ($m in $MethodFilter) { if ($m) { $filters += @('--filter-method', $m) } }
    Write-Host "[wp4] $($Class -join ', ')"
    $output = & $exe @filters --output Detailed 2>&1 | ForEach-Object { "$_" }
    $exitCode = $LASTEXITCODE
    $output | Out-Host
    if ($EvidenceDirectory) {
        $scrubbed = $output | ForEach-Object { $_.Replace("$repo", '<repo>').Replace($env:USERPROFILE, '<user-profile>') }
        [System.IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'console.txt'), (($scrubbed -join "`n") + "`n"))
    }
}
finally {
    foreach ($variable in 'SECBL01_PG_ADMIN', 'SECBL01_PG_RUNTIME_ROLE', 'SECBL01_PG_RUNTIME_PASSWORD', 'SECBL01_PG_RUNTIME', 'SECBL01_ARCHIVE_BLOB_URI', 'SECBL01_ARCHIVE_ACCOUNT',
        'SECBL01_ARCHIVE_KEY', 'SECBL01_CHECKPOINT_BLOB_URI', 'SECBL01_CHECKPOINT_ACCOUNT', 'SECBL01_CHECKPOINT_KEY', 'SECBL01_PERF_ROWS', 'SECBL01_CHECKPOINT_COUNT', 'SECBL01_EVIDENCE_DIR',
        'SECBL01_DPLUS_MEASURE', 'SECBL01_DPLUS_BACKLOGS') {
        Remove-Item "Env:$variable" -ErrorAction SilentlyContinue
    }
    if (-not $KeepContainers) {
        foreach ($name in $names.Values) { docker rm -f -v $name 2>$null | Out-Null }
        Write-Host '[wp4] containers and their anonymous volumes removed'
    }
}

Write-Host "[wp4] exit code $exitCode"
exit $exitCode
