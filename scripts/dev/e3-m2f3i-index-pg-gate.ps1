<#
.SYNOPSIS
  E3-M2f-3i capability-refusal evidence index: disposable real-PostgreSQL gate. Development/Testing only.

.DESCRIPTION
  Builds this working tree's Authority (Release) and the harness (tools/experiments/e3-m2f3i-index-gate/HarnessI, a copy in a
  temporary work directory), starts ONE fresh disposable postgres:18.0 container (tmpfs data, loopback port, random name and
  credentials, autovacuum off, every statement logged with database and application name; removed afterwards; no other stack,
  volume or database is touched), creates an ordinary runtime role and an unrelated login, and runs the harness. The harness runs the
  command under test as a separate process of the built Authority:

      dotnet AuthorizationAuthority.Api.dll --provision-capability-refusal-evidence-index [--verify-only]

  against throwaway databases copied from one migrated template: fresh creation and idempotency, independent catalogue verification,
  predicate semantics, --verify-only, an existing invalid index and its manual recovery, same-name wrong definitions, concurrent
  invocations, a build in progress in another session, an interrupted build, insufficient privileges, both lock-timeout phases, no
  migration-history or organization-row interaction (statement trace and held locks), a large unrelated audit history with query
  plans of the approved lookup (no statistics, analyzed, dead entries, vacuumed, generic prepared plan), no data or privilege change,
  and a concurrent build under live synthetic writers.

  Contract: docs/security/E3_M2F3_CONTRACT_CLOSURE.md section 5A; record docs/security/E3_M2F3I_IMPLEMENTATION_RECORD.md. Exit code 0
  only if every check passed and the container was removed. Not Stage B, not a security-test stack run, no Azure call, no persistent
  database. Planner behaviour on this container does not establish the plan on Azure PostgreSQL.

.EXAMPLE
  powershell -File scripts\dev\e3-m2f3i-index-pg-gate.ps1 -Evidence C:\out\m2f3i-run1
#>
param(
    [string]$WorkDir,
    [string]$Evidence,
    [int]$Port = 0,
    [int]$BigRows = 2000000,
    [switch]$KeepWorkDir
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$gate = Join-Path $repo 'tools\experiments\e3-m2f3i-index-gate'
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('e3-m2f3i-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
if (-not $Evidence) { $Evidence = Join-Path $WorkDir 'evidence' }
if ($Port -eq 0) { $Port = Get-Random -Minimum 55500 -Maximum 55900 }
New-Item -ItemType Directory -Force $WorkDir, $Evidence | Out-Null
Write-Output "work directory: $WorkDir"

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not (docker image inspect postgres:18.0 --format '{{.Id}}' 2>$null)) { throw 'postgres:18.0 is not present locally (docker pull postgres:18.0 first)' }
$dotnet = (Get-Command dotnet).Source
$volumesBefore = @(docker volume ls -q)

# 1. The build under test (this working tree) and the harness (a copy outside the repository).
Invoke-Checked $dotnet @('build', (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
$new = Join-Path $WorkDir 'authority'
Copy-Item (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $new -Recurse -Force
(git -C $repo rev-parse HEAD) + ' plus the uncommitted working tree (git status --short below)' | Set-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
git -C $repo status --short | Add-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
Get-ChildItem $new -Filter 'AuthorizationAuthority.*.dll' | Sort-Object Name | Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash.ToLowerInvariant()) *$(Split-Path -Leaf $_.Path)" } | Set-Content -Encoding ascii (Join-Path $Evidence 'authority-sha256.txt')
$harness = Join-Path $WorkDir 'harnessI'
Copy-Item (Join-Path $gate 'HarnessI') $harness -Recurse -Force
Invoke-Checked $dotnet @('build', (Join-Path $harness 'HarnessI.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:M2f3iBinaries=$new")
$exe = Join-Path $harness 'bin\Release\net10.0\M2f3iHarness.exe'

# 2. One disposable PostgreSQL container.
$name = 'm2f3i-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$adminPassword = 'm2f3i-admin-' + [guid]::NewGuid().ToString('N')
$runtimePassword = 'm2f3i-runtime-' + [guid]::NewGuid().ToString('N')
$outsiderPassword = 'm2f3i-outsider-' + [guid]::NewGuid().ToString('N')
$code = 99
try {
    docker run -d --rm --name $name -e "POSTGRES_PASSWORD=$adminPassword" -e POSTGRES_DB=m2f3igate -p "127.0.0.1:${Port}:5432" --tmpfs /var/lib/postgresql:rw postgres:18.0 `
        -c autovacuum=off -c log_statement=all -c log_lock_waits=on -c 'log_line_prefix=%m [%p] %d %a ' | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        docker exec $name pg_isready -U postgres -d m2f3igate 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    docker exec $name psql -U postgres -d m2f3igate -v ON_ERROR_STOP=1 -qAt -c "CREATE ROLE authority_runtime LOGIN PASSWORD '$runtimePassword' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS" | Out-Null
    docker exec $name psql -U postgres -d m2f3igate -v ON_ERROR_STOP=1 -qAt -c "CREATE ROLE gate_outsider LOGIN PASSWORD '$outsiderPassword' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS" | Out-Null
    docker exec $name psql -U postgres -d m2f3igate -qAt -c 'SELECT version()' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-version.txt')
    docker image inspect postgres:18.0 --format '{{.Id}}' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-image.txt')
    $env:M3I_AUTHORITY_DLL = Join-Path $new 'AuthorizationAuthority.Api.dll'
    $env:M3I_DOTNET = $dotnet
    $env:M3I_ADMIN = "Host=127.0.0.1;Port=$Port;Database=m2f3igate;Username=postgres;Password=$adminPassword;Pooling=false;Timeout=30;Command Timeout=300"
    $env:M3I_RUNTIME_USER = 'authority_runtime'
    $env:M3I_RUNTIME_PASSWORD = $runtimePassword
    $env:M3I_OUTSIDER_USER = 'gate_outsider'
    $env:M3I_OUTSIDER_PASSWORD = $outsiderPassword
    $env:M3I_CONTAINER = $name
    $env:M3I_EVIDENCE = $Evidence
    $env:M3I_BIG_ROWS = "$BigRows"
    & $exe *> (Join-Path $Evidence 'console.txt')
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:M3I_AUTHORITY_DLL, Env:M3I_DOTNET, Env:M3I_ADMIN, Env:M3I_RUNTIME_USER, Env:M3I_RUNTIME_PASSWORD, Env:M3I_OUTSIDER_USER, Env:M3I_OUTSIDER_PASSWORD, Env:M3I_CONTAINER, Env:M3I_EVIDENCE, Env:M3I_BIG_ROWS -ErrorAction SilentlyContinue
    # Only the error and lock-wait lines of the server log are kept (the full statement log of the seeding is large); no credential appears in them.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $log = cmd /c "docker logs $name 2>&1"
    $ErrorActionPreference = $previous
    $log | Where-Object { $_ -match 'ERROR:|FATAL:|deadlock|still waiting|acquired' } | Set-Content -Encoding utf8 (Join-Path $Evidence 'postgres-errors-and-lock-waits.log')
    docker rm -f $name 2>$null | Out-Null
}

# 3. Disposal and summary. Paths are scrubbed from the evidence (the work directory is temporary).
$removed = [string]::IsNullOrEmpty((docker ps -a --filter "name=$name" --format '{{.Names}}'))
$volumesAfter = @(docker volume ls -q)
$newVolumes = @($volumesAfter | Where-Object { $volumesBefore -notcontains $_ })
foreach ($file in Get-ChildItem $Evidence -File -Recurse) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    $scrubbed = $text.Replace($WorkDir, '<workdir>').Replace($repo, '<repo>').Replace($adminPassword, '<admin-password>').Replace($runtimePassword, '<runtime-password>').Replace($outsiderPassword, '<outsider-password>')
    if ($scrubbed -ne $text) { [System.IO.File]::WriteAllText($file.FullName, $scrubbed) }
}
$report = Join-Path $Evidence 'm2f3i-evidence.json'
$checks = if (Test-Path $report) { Get-Content $report -Raw | ConvertFrom-Json } else { $null }
$summary = [pscustomobject]@{
    ExitCode = $code
    Checks = if ($checks) { $checks.checksTotal } else { 0 }
    Failed = if ($checks) { $checks.checksFailed } else { -1 }
    ContainerRemoved = $removed
    NewDockerVolumes = $newVolumes.Count
    DeadlockLines = @(Select-String -Path (Join-Path $Evidence 'postgres-errors-and-lock-waits.log') -Pattern 'deadlock detected' -SimpleMatch).Count
}
$summary | Format-Table -AutoSize | Out-String | Write-Output
$summary | ConvertTo-Json | Set-Content -Encoding ascii (Join-Path $Evidence 'summary.json')
Get-Content (Join-Path $Evidence 'console.txt') | Write-Output
if (-not $KeepWorkDir) {
    if ((Resolve-Path $Evidence).Path.StartsWith((Resolve-Path $WorkDir).Path)) { Write-Output 'evidence is inside the work directory; keeping it' }
    else { Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($code -ne 0 -or $summary.Failed -ne 0 -or -not $removed -or $newVolumes.Count -ne 0) {
    Write-Output 'E3-M2f-3i capability-refusal evidence index PostgreSQL gate: FAILED'
    exit 1
}

Write-Output 'E3-M2f-3i capability-refusal evidence index PostgreSQL gate: all checks passed'
exit 0
