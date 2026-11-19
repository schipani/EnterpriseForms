<#
.SYNOPSIS
  SEC-BL-01 WP-7 (C-159) PostgreSQL 18 + Azurite run of record (Development/Testing only).

.DESCRIPTION
  One run of record: a fresh disposable stack started by sec-bl-01-wp4-pg-azurite-gate.ps1 (new containers, new per-run credentials, removed
  afterwards), every journal-verification PostgreSQL/Azurite class, and a run manifest: the exact command, UTC start and end, exit code, the
  SHA-256 of the test and product assemblies executed, and the console log (scrubbed by the gate script). The in-class 20k-row measurement
  runs at a functional size here (-PerformanceRows); performance is measured separately. Not Stage B, not production evidence.
#>
param(
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [int]$PerformanceRows = 2000,
    [int]$CheckpointCount = 100
)

$ErrorActionPreference = 'Continue'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$bin = Join-Path $repo 'tests\authority\AuthorizationAuthority.Tests\bin\Debug\net10.0'
New-Item -ItemType Directory -Force $EvidenceDirectory | Out-Null
$EvidenceDirectory = (Resolve-Path $EvidenceDirectory).Path
$classes = @('AuthorizationAuthority.Tests.JournalVerificationPostgresIntegrationTests', 'AuthorizationAuthority.Tests.JournalBoundedListingAzuriteTests',
    'AuthorizationAuthority.Tests.JournalVerificationAzuriteIntegrationTests')
$assemblies = 'AuthorizationAuthority.Tests.dll', 'AuthorizationAuthority.Application.dll', 'AuthorizationAuthority.Infrastructure.dll', 'AuthorizationAuthority.Domain.dll'
$hashes = [ordered]@{}
foreach ($a in $assemblies) { $hashes[$a] = (Get-FileHash -Algorithm SHA256 (Join-Path $bin $a)).Hash.ToLowerInvariant() }
$started = (Get-Date).ToUniversalTime().ToString('o')
& (Join-Path $PSScriptRoot 'sec-bl-01-wp4-pg-azurite-gate.ps1') -Class $classes -PerformanceRows $PerformanceRows -CheckpointCount $CheckpointCount -EvidenceDirectory $EvidenceDirectory *> (Join-Path $EvidenceDirectory 'gate-host.log')
$code = $LASTEXITCODE
$ended = (Get-Date).ToUniversalTime().ToString('o')
$console = Get-Content (Join-Path $EvidenceDirectory 'console.txt') -Raw
$summary = [ordered]@{
    run = Split-Path $EvidenceDirectory -Leaf
    command = "scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1 -Class $($classes -join ',') -PerformanceRows $PerformanceRows -CheckpointCount $CheckpointCount -EvidenceDirectory <dir>"
    started_utc = $started
    ended_utc = $ended
    exit_code = $code
    total = [int]([regex]::Match($console, '(?m)^\s+total: (\d+)').Groups[1].Value)
    succeeded = [int]([regex]::Match($console, '(?m)^\s+succeeded: (\d+)').Groups[1].Value)
    failed = [int]([regex]::Match($console, '(?m)^\s+failed: (\d+)').Groups[1].Value)
    skipped = [int]([regex]::Match($console, '(?m)^\s+skipped: (\d+)').Groups[1].Value)
    failed_tests = @([regex]::Matches($console, '(?m)^failed (\S+)') | ForEach-Object { $_.Groups[1].Value })
    assembly_sha256 = $hashes
    console_sha256 = (Get-FileHash -Algorithm SHA256 (Join-Path $EvidenceDirectory 'console.txt')).Hash.ToLowerInvariant()
    stack = 'postgres:18.0 + two azurite:3.9.0 (archive; separately keyed journal-verification store), 127.0.0.1, per-run credentials, removed afterwards'
}
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'run-summary.json'), (($summary | ConvertTo-Json -Depth 4) -replace "`r`n", "`n") + "`n")
Write-Host "[wp7] $($summary.run): exit $code, $($summary.succeeded)/$($summary.total) succeeded, $($summary.failed) failed, $($summary.skipped) skipped"
exit $code
