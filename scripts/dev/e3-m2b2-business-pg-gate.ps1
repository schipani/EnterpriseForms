<#
.SYNOPSIS
  E3-M2b-2 business real-PostgreSQL gate (signed schema 2 groundwork). Development/Testing only.

.DESCRIPTION
  Re-runnable gate for the BusinessDb migration 20261115125042_EnvelopeV2SignedColumns and the business required-migration guard
  (docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §23). On ONE fresh disposable postgres:18.0 container (tmpfs data, loopback
  port, removed afterwards; no other stack, volume or database is touched) it runs, in order:

    pre      this tree's harness: baseline at the previous migration; guard, real Worker startup guard and real API monitor
             refuse the unmigrated database; the new EF model cannot read it; migration atomicity (a violating row) and the
             lock timeout (an open transaction) both leave nothing applied.
    (pg_dump of the pre-migration database)
    up       the real migration applied by EF; definitions, CHECK matrix, EF round trip and clone persistence, runtime-role limits.
    legacy   a pre-E3-M2b-2 business build (git archive of -LegacyCommit, built in the work directory) writes v1 rows through its
             own EF model and reports its canonical bytes.
    (pg_dump of the migrated database)
    post     legacy rows verified and byte-compared; guarded Down refused with v2 rows, then run; re-apply.
    restore  both dumps restored into new databases (pg_restore) and checked.

  The harness sources live in tools/experiments/e3-m2b2-business-gate and are built as copies in the work directory. Exit code 0
  only if every check of every phase passed. Not Stage B, not a security-test stack run, no Azure call.

.EXAMPLE
  powershell -File scripts\dev\e3-m2b2-business-pg-gate.ps1 -Evidence C:\out\m2b2
#>
param(
    [string]$LegacyCommit = '11e15ee7d7c3d9e9be0685c2330ec006503bf1ec',
    [string]$WorkDir,
    [string]$Evidence,
    [int]$Port = 55450
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$gate = Join-Path $repo 'tools\experiments\e3-m2b2-business-gate'
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('e3-m2b2-business-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
if (-not $Evidence) { $Evidence = Join-Path $WorkDir 'evidence' }
New-Item -ItemType Directory -Force $WorkDir, $Evidence | Out-Null
Write-Output "work directory: $WorkDir"

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Build-Copy([string]$source, [string]$target, [string]$binaries) {
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $source '*') $target -Recurse
    Invoke-Checked 'dotnet' @('build', (Get-ChildItem $target -Filter '*.csproj').FullName, '-c', 'Release', '-nologo', '-v', 'q', "-p:GateBinaries=$binaries")
    Join-Path $target 'bin\Release\net10.0'
}

if (-not (docker image inspect postgres:18.0 --format '{{.Id}}' 2>$null)) { throw 'postgres:18.0 is not present locally (docker pull postgres:18.0 first)' }

# 1. The build under test (this working tree): the Business API Release output plus the Worker assembly.
Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'src\backend\EnterpriseForms.Api\EnterpriseForms.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'src\backend\EnterpriseForms.Worker\EnterpriseForms.Worker.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
$new = Join-Path $WorkDir 'new'
Copy-Item (Join-Path $repo 'src\backend\EnterpriseForms.Api\bin\Release\net10.0') $new -Recurse -Force
Copy-Item (Join-Path $repo 'src\backend\EnterpriseForms.Worker\bin\Release\net10.0\EnterpriseForms.Worker.dll') $new -Force

# 2. The legacy (pre-E3-M2b-2) business build.
$short = $LegacyCommit.Substring(0, 7)
$archive = Join-Path $WorkDir "src-$short.zip"
$source = Join-Path $WorkDir "src-$short"
Invoke-Checked 'git' @('-C', $repo, 'archive', '--format=zip', '-o', $archive, $LegacyCommit)
Expand-Archive -Path $archive -DestinationPath $source -Force
Invoke-Checked 'dotnet' @('build', (Join-Path $source 'src\backend\EnterpriseForms.Api\EnterpriseForms.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
$legacy = Join-Path $WorkDir "legacy-$short"
Copy-Item (Join-Path $source 'src\backend\EnterpriseForms.Api\bin\Release\net10.0') $legacy -Recurse -Force
"git archive $LegacyCommit, Release build in the work directory" | Set-Content -Encoding ascii (Join-Path $Evidence 'legacy-origin.txt')

# 3. Harness builds (copies outside the repository).
$harness = Build-Copy (Join-Path $gate 'GateHarness') (Join-Path $WorkDir 'harness') $new
$writer = Build-Copy (Join-Path $gate 'LegacyWriter') (Join-Path $WorkDir 'writer') $legacy

# 4. One disposable PostgreSQL container for the whole sequence.
$name = 'm2b2-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 6)
$adminPassword = 'gate-admin-' + [guid]::NewGuid().ToString('N')
$runtimePassword = 'gate-runtime-' + [guid]::NewGuid().ToString('N')
$phases = @()
function Invoke-Phase([string]$phase) {
    $lines = & (Join-Path $harness 'BusinessGateHarness.exe') $phase 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    [System.IO.File]::WriteAllLines((Join-Path $Evidence "console-$phase.txt"), [string[]]@($lines), (New-Object System.Text.UTF8Encoding($false)))
    $report = Join-Path $Evidence "gate-$phase.json"
    $checks = if (Test-Path $report) { Get-Content $report -Raw | ConvertFrom-Json } else { $null }
    [pscustomobject]@{ Phase = $phase; ExitCode = $code; Checks = if ($checks) { $checks.checksTotal } else { 0 }; Failed = if ($checks) { $checks.checksFailed } else { -1 } }
}

try {
    docker run -d --rm --name $name -e "POSTGRES_PASSWORD=$adminPassword" -e POSTGRES_DB=gate -p "127.0.0.1:${Port}:5432" --tmpfs /var/lib/postgresql:rw postgres:18.0 | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        docker exec $name pg_isready -U postgres -d gate 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    docker exec $name psql -U postgres -d gate -v ON_ERROR_STOP=1 -qAt -c "CREATE ROLE business_api_runtime LOGIN PASSWORD '$runtimePassword' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS" | Out-Null
    docker exec $name psql -U postgres -d gate -qAt -c 'SELECT version()' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-version.txt')
    docker image inspect postgres:18.0 --format '{{.Id}}' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-image.txt')
    $env:GATE_ADMIN = "Host=127.0.0.1;Port=$Port;Database=gate;Username=postgres;Password=$adminPassword;Pooling=false;Timeout=30;Command Timeout=120"
    $env:GATE_RUNTIME = "Host=127.0.0.1;Port=$Port;Database=gate;Username=business_api_runtime;Password=$runtimePassword;Pooling=false;Timeout=30;Command Timeout=120"
    $env:GATE_EVIDENCE = $Evidence

    $phases += Invoke-Phase 'pre'
    docker exec $name sh -c 'pg_dump -U postgres -Fc -d gate -f /tmp/pre.dump' | Out-Host
    $phases += Invoke-Phase 'up'

    $env:GATE_LEGACY_OUTPUT = Join-Path $Evidence 'legacy-writer.json'
    $env:GATE_LEGACY_LABEL = "pre-E3-M2b-2 business Release build $short (git archive)"
    $lines = & (Join-Path $writer 'BusinessLegacyWriter.exe') 2>&1 | ForEach-Object { "$_" }
    $legacyCode = $LASTEXITCODE
    [System.IO.File]::WriteAllLines((Join-Path $Evidence 'console-legacy.txt'), [string[]]@($lines), (New-Object System.Text.UTF8Encoding($false)))
    $phases += [pscustomobject]@{ Phase = 'legacy'; ExitCode = $legacyCode; Checks = 1; Failed = if ($legacyCode -eq 0) { 0 } else { 1 } }

    docker exec $name sh -c 'pg_dump -U postgres -Fc -d gate -f /tmp/up.dump' | Out-Host
    $phases += Invoke-Phase 'post'
    foreach ($target in 'restored_pre', 'restored_up') {
        docker exec $name psql -U postgres -d gate -v ON_ERROR_STOP=1 -qAt -c "CREATE DATABASE $target" | Out-Null
        $dump = if ($target -eq 'restored_pre') { '/tmp/pre.dump' } else { '/tmp/up.dump' }
        # The gate's envelope fixtures have no parent rows (written with FKs disabled by the replica session; this migration does
        # not change any FK), so the FK CONSTRAINT entries are left out of the restore list. CHECK constraints are part of the
        # table definitions and apply while the data is loaded, so the restore itself re-checks every restored row.
        docker exec $name sh -c "pg_restore -l $dump | grep -v 'FK CONSTRAINT' > /tmp/$target.list && pg_restore -U postgres --exit-on-error -L /tmp/$target.list -d $target $dump" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "pg_restore into $target failed" }
    }
    $phases += Invoke-Phase 'restore'
}
finally {
    docker rm -f $name 2>$null | Out-Null
    Remove-Item Env:GATE_ADMIN, Env:GATE_RUNTIME, Env:GATE_EVIDENCE, Env:GATE_LEGACY_OUTPUT, Env:GATE_LEGACY_LABEL -ErrorAction SilentlyContinue
}

$removed = [string]::IsNullOrEmpty((docker ps -a --filter "name=$name" --format '{{.Names}}'))
$phases | Format-Table -AutoSize | Out-String | Write-Output
[pscustomobject]@{ Phases = $phases; ContainerRemoved = $removed; LegacyCommit = $LegacyCommit } | ConvertTo-Json -Depth 4 | Set-Content -Encoding ascii (Join-Path $Evidence 'summary.json')
if (-not $removed -or $phases.Count -ne 5 -or ($phases | Where-Object { $_.ExitCode -ne 0 -or $_.Failed -ne 0 })) {
    Write-Output 'E3-M2b-2 business PostgreSQL gate: FAILED'
    exit 1
}

Write-Output 'E3-M2b-2 business PostgreSQL gate: all phases passed'
exit 0
