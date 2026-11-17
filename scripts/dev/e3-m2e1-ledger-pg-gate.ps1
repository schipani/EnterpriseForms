<#
.SYNOPSIS
  E3-M2e-1 dark data-key ledger focused real-PostgreSQL gate. Development/Testing only.

.DESCRIPTION
  Builds this working tree's Authority (Release; ContentAcceptanceRules.CurrentVersion = 3) and, from `git archive`, two frozen
  Authority Release builds: the accepted signed E3-M2d commit (-Carv2Commit, default a1127a1; CurrentVersion = 2) and the accepted
  signed E3-M2c commit (-Carv1Commit, default 8915f77; CurrentVersion = 1). Builds the harness (tools/experiments/e3-m2e1-ledger-gate/
  HarnessE1, against this tree) and the legacy agent (LegacyAgent, once against each frozen build) as copies in a temporary work
  directory, starts ONE fresh disposable postgres:18.0 container (tmpfs data, loopback port, removed afterwards; no other stack, volume
  or database is touched), creates an ordinary runtime role, and runs the harness:

    - the ledger migration (no floor), its exact index and trigger renderings, runtime grants, P-1, P-2, CHECKs, global uniqueness;
    - the real EF store: one-transaction registration, the L-3 rollback, the cross-organization race, L-13 and its lock;
    - ContentService on real persistence with Envelope v2 acceptance OFF: DK-6 / P-4 refusals and incidents, the switch re-check,
      CARV 3 stamps, the DK-8 body, the guarded Down;
    - C-14: a seeded v2 head downgraded by the CARV-1 build on floor 1, refused on floor 2;
    - floor 3: refusals (skipped floor, older-marker session, in-flight version-2 operation, weakened ledger), activation under
      concurrent writers, the exact live definition, Release B RequiredFloor 3, the CARV-2 build refused, monotonicity, missing and
      lowered floors with reinstatement.

  Floors are installed ONLY in this disposable container. Envelope v2 acceptance is never enabled. Contract:
  docs/security/E3_M2E_CONTRACT_CLOSURE.md; record docs/security/E3_M2E1_IMPLEMENTATION_RECORD.md. Exit code 0 only if every check
  passed and the container was removed. Not Stage B, not a security-test stack run, no Azure call.

.EXAMPLE
  powershell -File scripts\dev\e3-m2e1-ledger-pg-gate.ps1 -Evidence C:\out\m2e1

.NOTES
  F-E1 (RB-CARV-1 amendment A-1, contract section 22.14): -Mode fe1 runs the focused floor-deadlock scenarios instead of the gate
  (FloorDeadlockScenarios.cs: deterministic interleavings, lock timeout, history/catalog/restore, -Iterations repeated raises under
  concurrent writers), each in its own database of the same container. -NewBinaries runs a prebuilt Authority Release folder instead
  of building this tree (used to run the same checks against the pre-A-1 build). Both modes start PostgreSQL with log_lock_waits and
  a log line prefix naming the process and application, and count `deadlock detected` lines in the server log.
#>
param(
    [string]$WorkDir,
    [string]$Evidence,
    [string]$Carv2Commit = 'a1127a109b5596e65cd3ac7f66a4ae4b35df2eb7',
    [string]$Carv1Commit = '8915f77c10299c08b27e36d40b7a2388d1e38d0f',
    [string]$LegacyBuildsRoot,
    [int]$Port = 55480,
    [ValidateSet('gate', 'fe1')]
    [string]$Mode = 'gate',
    [string]$NewBinaries,
    [int]$Iterations = 10
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$gate = Join-Path $repo 'tools\experiments\e3-m2e1-ledger-gate'
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('e3-m2e1-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
if (-not $Evidence) { $Evidence = Join-Path $WorkDir 'evidence' }
New-Item -ItemType Directory -Force $WorkDir, $Evidence | Out-Null
Write-Output "work directory: $WorkDir"

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    # Out-Host: native output must not become part of a function's return value.
    & $exe @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Write-Hashes([string]$folder, [string]$file) {
    Get-ChildItem $folder -Filter '*.dll' | Where-Object { $_.Name -like 'AuthorizationAuthority.*' -or $_.Name -eq 'EnterpriseForms.Integrity.dll' } | Sort-Object Name |
        Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant()) *$(Split-Path -Leaf $_.Path)" } | Set-Content -Encoding ascii $file
}

function Get-FrozenBuild([string]$commit, [string]$label) {
    $short = $commit.Substring(0, 7)
    $target = Join-Path $WorkDir "$label-$short"
    if ($LegacyBuildsRoot -and (Test-Path (Join-Path $LegacyBuildsRoot "$label-$short\AuthorizationAuthority.Api.dll"))) {
        Copy-Item (Join-Path $LegacyBuildsRoot "$label-$short") $target -Recurse -Force
    }
    else {
        $archive = Join-Path $WorkDir "src-$short.zip"
        $source = Join-Path $WorkDir "src-$short"
        Invoke-Checked 'git' @('-C', $repo, 'archive', '--format=zip', '-o', $archive, $commit)
        Expand-Archive -Path $archive -DestinationPath $source -Force
        Invoke-Checked 'dotnet' @('build', (Join-Path $source 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
        Copy-Item (Join-Path $source 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $target -Recurse -Force
    }

    Write-Hashes $target (Join-Path $Evidence "$label-sha256.txt")
    "git archive $commit, Release build; release builds are not byte-reproducible, so no binary hash is claimed as provenance" | Set-Content -Encoding ascii (Join-Path $Evidence "$label-origin.txt")
    return $target
}

if (-not (docker image inspect postgres:18.0 --format '{{.Id}}' 2>$null)) { throw 'postgres:18.0 is not present locally (docker pull postgres:18.0 first)' }

# 1. The build under test (this working tree, or -NewBinaries).
$new = Join-Path $WorkDir 'new'
if ($NewBinaries) {
    Copy-Item $NewBinaries $new -Recurse -Force
    'prebuilt Authority Release folder (-NewBinaries); the record names its source' | Set-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
}
else {
    Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
    Copy-Item (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $new -Recurse -Force
    (git -C $repo rev-parse HEAD) + ' plus the uncommitted working tree (git status --short below)' | Set-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
    git -C $repo status --short | Add-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
}
Write-Hashes $new (Join-Path $Evidence 'new-sha256.txt')

# 2. The frozen builds.
$carv2 = Get-FrozenBuild $Carv2Commit 'carv2'
$carv1 = Get-FrozenBuild $Carv1Commit 'carv1'

# 3. The harness and the two legacy agents (copies outside the repository).
$harness = Join-Path $WorkDir 'harnessE1'
Copy-Item (Join-Path $gate 'HarnessE1') $harness -Recurse -Force
Invoke-Checked 'dotnet' @('build', (Join-Path $harness 'HarnessE1.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:M2eBinaries=$new")
$exe = Join-Path $harness 'bin\Release\net10.0\M2e1Harness.exe'
$agents = @{}
foreach ($pair in @(@('carv1', $carv1), @('carv2', $carv2))) {
    $dir = Join-Path $WorkDir ("agent-" + $pair[0])
    Copy-Item (Join-Path $gate 'LegacyAgent') $dir -Recurse -Force
    Invoke-Checked 'dotnet' @('build', (Join-Path $dir 'LegacyAgent.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:LegacyBinaries=$($pair[1])")
    $agents[$pair[0]] = Join-Path $dir 'bin\Release\net10.0\LegacyAgent.exe'
}

# 4. One disposable PostgreSQL container.
$name = 'm2e1-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$adminPassword = 'm2e1-admin-' + [guid]::NewGuid().ToString('N')
$runtimePassword = 'm2e1-runtime-' + [guid]::NewGuid().ToString('N')
$code = 99
try {
    docker run -d --rm --name $name -e "POSTGRES_PASSWORD=$adminPassword" -e POSTGRES_DB=m2e1gate -p "127.0.0.1:${Port}:5432" --tmpfs /var/lib/postgresql:rw postgres:18.0 -c log_lock_waits=on -c 'log_line_prefix=%m [%p] %a ' | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        docker exec $name pg_isready -U postgres -d m2e1gate 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    docker exec $name psql -U postgres -d m2e1gate -v ON_ERROR_STOP=1 -qAt -c "CREATE ROLE authority_runtime LOGIN PASSWORD '$runtimePassword' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS" | Out-Null
    docker exec $name psql -U postgres -d m2e1gate -qAt -c 'SELECT version()' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-version.txt')
    docker image inspect postgres:18.0 --format '{{.Id}}' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-image.txt')
    $env:M2E_NEWBIN = Join-Path $new 'AuthorizationAuthority.Api.dll'
    $env:M2E_CARV1AGENT = $agents['carv1']
    $env:M2E_CARV2AGENT = $agents['carv2']
    $env:M2E_ADMIN = "Host=127.0.0.1;Port=$Port;Database=m2e1gate;Username=postgres;Password=$adminPassword;Pooling=false;Timeout=30;Command Timeout=120"
    $env:M2E_RUNTIME = "Host=127.0.0.1;Port=$Port;Database=m2e1gate;Username=authority_runtime;Password=$runtimePassword;Timeout=30;Command Timeout=120"
    $env:M2E_EVIDENCE = $Evidence
    $env:M2E_MODE = $Mode
    $env:M2E_FE1_ITERATIONS = "$Iterations"
    & $exe *> (Join-Path $Evidence 'console.txt')
    $code = $LASTEXITCODE
}
finally {
    # The server log (errors only by default: deadlocks, lock timeouts, refused statements) is kept as evidence; it holds no credential.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue' # docker writes the server log to stderr
    cmd /c "docker logs $name > `"$(Join-Path $Evidence 'postgres-server.log')`" 2>&1"
    $ErrorActionPreference = $previous
    docker rm -f $name 2>$null | Out-Null
    Remove-Item Env:M2E_NEWBIN, Env:M2E_CARV1AGENT, Env:M2E_CARV2AGENT, Env:M2E_ADMIN, Env:M2E_RUNTIME, Env:M2E_EVIDENCE, Env:M2E_MODE, Env:M2E_FE1_ITERATIONS -ErrorAction SilentlyContinue
}

# The legacy call files hold staged canonical test bytes only (no secret); the connection strings never reach them.
$removed = [string]::IsNullOrEmpty((docker ps -a --filter "name=$name" --format '{{.Names}}'))
$report = Join-Path $Evidence $(if ($Mode -eq 'fe1') { 'fe1-evidence.json' } else { 'm2e1-evidence.json' })
$serverLog = Join-Path $Evidence 'postgres-server.log'
$deadlockLines = if (Test-Path $serverLog) { @(Select-String -Path $serverLog -Pattern 'deadlock detected' -SimpleMatch).Count } else { -1 }
$checks = if (Test-Path $report) { Get-Content $report -Raw | ConvertFrom-Json } else { $null }
$summary = [pscustomobject]@{
    ExitCode = $code
    Checks = if ($checks) { $checks.checksTotal } else { 0 }
    Failed = if ($checks) { $checks.checksFailed } else { -1 }
    ContainerRemoved = $removed
    Mode = $Mode
    DeadlockLinesInServerLog = $deadlockLines
}
$summary | Format-Table -AutoSize | Out-String | Write-Output
$summary | ConvertTo-Json | Set-Content -Encoding ascii (Join-Path $Evidence 'summary.json')
Get-Content (Join-Path $Evidence 'console.txt') | Write-Output
if ($code -ne 0 -or $summary.Failed -ne 0 -or -not $removed) {
    Write-Output "E3-M2e-1 dark data-key ledger PostgreSQL gate ($Mode): FAILED"
    exit 1
}

Write-Output "E3-M2e-1 dark data-key ledger PostgreSQL gate ($Mode): all checks passed"
exit 0
