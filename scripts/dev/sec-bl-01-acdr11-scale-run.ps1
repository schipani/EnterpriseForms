<#
.SYNOPSIS
  SEC-BL-01 WP-7A (C-160) AC-DR-11 scale measurement on a disposable PostgreSQL 18 + Azurite stack (Development/Testing only).

.DESCRIPTION
  Runs JournalVerificationPostgresIntegrationTests.MeasureAcDr11ScaleOrganization through sec-bl-01-wp4-pg-azurite-gate.ps1 (fresh containers,
  per-run credentials, removed afterwards) with SECBL01_SCALE_ROWS target-organization rows (the other four organizations hold a quarter of
  that in total, OI-DR-6), and samples `docker stats` of the three gate containers every minute into docker-stats.log. Writes a run manifest
  with the SHA-256 of the executed assemblies. Synthetic data only; no Azure resource; not Stage B; not production evidence.
#>
param(
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [Parameter(Mandatory = $true)][long]$Rows,
    [int]$Concurrency = 32,
    [int]$Chunk = 10000,
    [int]$PostgresPort = 18330,
    [int]$ArchivePort = 18331,
    [int]$CheckpointPort = 18332
)

$ErrorActionPreference = 'Continue'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$built = Join-Path $repo 'tests\authority\AuthorizationAuthority.Tests\bin\Debug\net10.0'
New-Item -ItemType Directory -Force $EvidenceDirectory | Out-Null
$EvidenceDirectory = (Resolve-Path $EvidenceDirectory).Path
# A frozen copy of the build, so the repository can be rebuilt while this (long) run executes; the manifest hashes the executed copy.
$bin = Join-Path ([IO.Path]::GetTempPath()) ('secbl01-acdr11-bin-' + [Guid]::NewGuid().ToString('N'))
robocopy $built $bin /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if (-not $bin.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'the frozen copy must be under the temporary directory' }
$assemblies = 'AuthorizationAuthority.Tests.dll', 'AuthorizationAuthority.Application.dll', 'AuthorizationAuthority.Infrastructure.dll', 'AuthorizationAuthority.Domain.dll'
$hashes = [ordered]@{}
foreach ($a in $assemblies) { $hashes[$a] = (Get-FileHash -Algorithm SHA256 (Join-Path $bin $a)).Hash.ToLowerInvariant() }
$env:SECBL01_SCALE_ROWS = "$Rows"
$env:SECBL01_SCALE_CONCURRENCY = "$Concurrency"
$env:SECBL01_SCALE_CHUNK = "$Chunk"
$statsLog = Join-Path $EvidenceDirectory 'docker-stats.log'
$sampler = Start-Job -ScriptBlock {
    param($log)
    while ($true) {
        $line = (Get-Date).ToUniversalTime().ToString('o') + ' ' + ((docker stats --no-stream --format '{{.Name}} cpu={{.CPUPerc}} mem={{.MemUsage}} io={{.BlockIO}}' 2>$null | Where-Object { $_ -like 'enterpriseforms-secbl01-wp4-*-acdr11 *' }) -join ' | ')
        Add-Content -Path $log -Value $line
        Start-Sleep -Seconds 60
    }
} -ArgumentList $statsLog
$started = (Get-Date).ToUniversalTime().ToString('o')
& (Join-Path $PSScriptRoot 'sec-bl-01-wp4-pg-azurite-gate.ps1') -Class 'AuthorizationAuthority.Tests.JournalVerificationPostgresIntegrationTests' -MethodFilter '*MeasureAcDr11ScaleOrganization*' `
    -EvidenceDirectory $EvidenceDirectory -NameSuffix '-acdr11' -PostgresPort $PostgresPort -ArchivePort $ArchivePort -CheckpointPort $CheckpointPort `
    -TestExecutable (Join-Path $bin 'AuthorizationAuthority.Tests.exe') *> (Join-Path $EvidenceDirectory 'gate-host.log')
$code = $LASTEXITCODE
$ended = (Get-Date).ToUniversalTime().ToString('o')
Stop-Job $sampler | Out-Null
Remove-Job $sampler -Force | Out-Null
Remove-Item -Recurse -Force $bin -ErrorAction SilentlyContinue
$summary = [ordered]@{
    command = "scripts/dev/sec-bl-01-acdr11-scale-run.ps1 -Rows $Rows -Concurrency $Concurrency -Chunk $Chunk -EvidenceDirectory <dir>"
    started_utc = $started
    ended_utc = $ended
    exit_code = $code
    assembly_sha256 = $hashes
    stack = 'postgres:18.0 + two azurite:3.9.0 (archive; separately keyed journal-verification store), 127.0.0.1, per-run credentials, removed afterwards'
}
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'run-summary.json'), ($summary | ConvertTo-Json -Depth 4).Replace("`r`n", "`n") + "`n")
"exit $code"
