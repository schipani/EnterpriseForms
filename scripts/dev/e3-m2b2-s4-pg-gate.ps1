<#
.SYNOPSIS
  E3-M2b-2 R-M2b2-11 focused real-PostgreSQL gate for the Authority S-4 schema/envelope tie. Development/Testing only.

.DESCRIPTION
  Builds this working tree's Authority (Release) and the S-4 harness (tools/experiments/e3-m2b2-s4-gate, built as a copy in a
  temporary work directory), starts one fresh disposable postgres:18.0 container (tmpfs data, loopback port, removed afterwards;
  no other stack, volume or database is touched), creates an ordinary runtime role, and runs the harness:

    - this build's own --migrate and --apply-content-acceptance-floor 1 (migration identity, separate processes);
    - a Release B ContentService + EF AuthorityStore of this build, in-process, connected as the runtime role;
    - S-4 at Prepare and at Commit (field and document rows, both mismatched pairs), with no authoritative state advancing,
      Commit replay, positive controls, schema 2 / envelope 2 still refused, and the CARV and floor fences still active.

  Contract: docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md §23.15. Exit code 0 only if every check passed and the container was
  removed. Not Stage B, not a security-test stack run, no Azure call.

.EXAMPLE
  powershell -File scripts\dev\e3-m2b2-s4-pg-gate.ps1 -Evidence C:\out\s4
#>
param(
    [string]$WorkDir,
    [string]$Evidence,
    [int]$Port = 55460
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$gate = Join-Path $repo 'tools\experiments\e3-m2b2-s4-gate'
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('e3-m2b2-s4-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
if (-not $Evidence) { $Evidence = Join-Path $WorkDir 'evidence' }
New-Item -ItemType Directory -Force $WorkDir, $Evidence | Out-Null
Write-Output "work directory: $WorkDir"

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    # Out-Host: native output must not become part of a function's return value.
    & $exe @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not (docker image inspect postgres:18.0 --format '{{.Id}}' 2>$null)) { throw 'postgres:18.0 is not present locally (docker pull postgres:18.0 first)' }

# 1. The build under test (this working tree).
Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
$new = Join-Path $WorkDir 'new'
Copy-Item (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $new -Recurse -Force
Get-ChildItem $new -Filter '*.dll' | Where-Object { $_.Name -like 'AuthorizationAuthority.*' -or $_.Name -eq 'EnterpriseForms.Integrity.dll' } | Sort-Object Name |
    Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant()) *$(Split-Path -Leaf $_.Path)" } | Set-Content -Encoding ascii (Join-Path $Evidence 'new-sha256.txt')
(git -C $repo rev-parse HEAD) + ' plus the uncommitted working tree (git status --short below)' | Set-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
git -C $repo status --short | Add-Content -Encoding ascii (Join-Path $Evidence 'source.txt')

# 2. The harness (a copy outside the repository).
$harness = Join-Path $WorkDir 'harnessS4'
Copy-Item (Join-Path $gate 'HarnessS4') $harness -Recurse -Force
Invoke-Checked 'dotnet' @('build', (Join-Path $harness 'HarnessS4.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:S4Binaries=$new")
$exe = Join-Path $harness 'bin\Release\net10.0\S4Harness.exe'

# 3. One disposable PostgreSQL container.
$name = 's4-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$adminPassword = 's4-admin-' + [guid]::NewGuid().ToString('N')
$runtimePassword = 's4-runtime-' + [guid]::NewGuid().ToString('N')
$code = 99
try {
    docker run -d --rm --name $name -e "POSTGRES_PASSWORD=$adminPassword" -e POSTGRES_DB=s4gate -p "127.0.0.1:${Port}:5432" --tmpfs /var/lib/postgresql:rw postgres:18.0 | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        docker exec $name pg_isready -U postgres -d s4gate 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    docker exec $name psql -U postgres -d s4gate -v ON_ERROR_STOP=1 -qAt -c "CREATE ROLE authority_runtime LOGIN PASSWORD '$runtimePassword' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS" | Out-Null
    docker exec $name psql -U postgres -d s4gate -qAt -c 'SELECT version()' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-version.txt')
    docker image inspect postgres:18.0 --format '{{.Id}}' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-image.txt')
    $env:S4_NEWBIN = Join-Path $new 'AuthorizationAuthority.Api.dll'
    $env:S4_ADMIN = "Host=127.0.0.1;Port=$Port;Database=s4gate;Username=postgres;Password=$adminPassword;Pooling=false;Timeout=30;Command Timeout=120"
    $env:S4_RUNTIME = "Host=127.0.0.1;Port=$Port;Database=s4gate;Username=authority_runtime;Password=$runtimePassword;Timeout=30;Command Timeout=120"
    $env:S4_EVIDENCE = $Evidence
    & $exe *> (Join-Path $Evidence 'console.txt')
    $code = $LASTEXITCODE
}
finally {
    docker rm -f $name 2>$null | Out-Null
    Remove-Item Env:S4_NEWBIN, Env:S4_ADMIN, Env:S4_RUNTIME, Env:S4_EVIDENCE -ErrorAction SilentlyContinue
}

$removed = [string]::IsNullOrEmpty((docker ps -a --filter "name=$name" --format '{{.Names}}'))
$report = Join-Path $Evidence 's4-evidence.json'
$checks = if (Test-Path $report) { Get-Content $report -Raw | ConvertFrom-Json } else { $null }
$summary = [pscustomobject]@{
    ExitCode = $code
    Checks = if ($checks) { $checks.checksTotal } else { 0 }
    Failed = if ($checks) { $checks.checksFailed } else { -1 }
    ContainerRemoved = $removed
}
$summary | Format-Table -AutoSize | Out-String | Write-Output
$summary | ConvertTo-Json | Set-Content -Encoding ascii (Join-Path $Evidence 'summary.json')
Get-Content (Join-Path $Evidence 'console.txt') | Write-Output
if ($code -ne 0 -or $summary.Failed -ne 0 -or -not $removed) {
    Write-Output 'E3-M2b-2 S-4 PostgreSQL gate: FAILED'
    exit 1
}

Write-Output 'E3-M2b-2 S-4 PostgreSQL gate: all checks passed'
exit 0
