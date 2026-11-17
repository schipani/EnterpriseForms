<#
.SYNOPSIS
  E3-M2d CARV 2 / floor 2 focused real-PostgreSQL gate. Development/Testing only.

.DESCRIPTION
  Builds this working tree's Authority (Release; ContentAcceptanceRules.CurrentVersion = 2) and, from `git archive` of the accepted
  signed E3-M2c commit (-V1Commit, default 8915f77; CurrentVersion = 1), a version-1 Authority Release build. Builds the harness
  (tools/experiments/e3-m2d-carv2-gate/HarnessD, against this tree) and the version-1 agent (V1Agent, against the version-1 build)
  as copies in a temporary work directory, starts ONE fresh disposable postgres:18.0 container (tmpfs data, loopback port, removed
  afterwards; no other stack, volume or database is touched), creates an ordinary runtime role, and runs the harness:

    - floor-1 compatibility of the version-2 build (Release B required floor 1 and 2), the cross-build version-1 / version-2
      Prepare / Commit refusals, historical committed-operation replay, Envelope v2 still dark on the real persistence path;
    - floor 2: refusals (skipped floor, unrecognized floor, --migrate combination, older-marker session, in-flight version-1
      operation, held lock), activation under concurrent writers, the exact live definition, monotonicity, the database floor
      against the version-1 build;
    - missing, weakened, substituted, duplicated and unrecorded floors under Release B, with recovery.

  Floor 2 is installed ONLY in this disposable container (owner decision D-3). Contract: docs/security/E3_M2D_IMPLEMENTATION_RECORD.md.
  Exit code 0 only if every check passed and the container was removed. Not Stage B, not a security-test stack run, no Azure call.

.EXAMPLE
  powershell -File scripts\dev\e3-m2d-carv2-pg-gate.ps1 -Evidence C:\out\m2d
#>
param(
    [string]$WorkDir,
    [string]$Evidence,
    [string]$V1Commit = '8915f77c10299c08b27e36d40b7a2388d1e38d0f',
    [int]$Port = 55470
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$gate = Join-Path $repo 'tools\experiments\e3-m2d-carv2-gate'
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('e3-m2d-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
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

if (-not (docker image inspect postgres:18.0 --format '{{.Id}}' 2>$null)) { throw 'postgres:18.0 is not present locally (docker pull postgres:18.0 first)' }

# 1. The build under test (this working tree).
Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
$new = Join-Path $WorkDir 'new'
Copy-Item (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $new -Recurse -Force
Write-Hashes $new (Join-Path $Evidence 'new-sha256.txt')
(git -C $repo rev-parse HEAD) + ' plus the uncommitted working tree (git status --short below)' | Set-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
git -C $repo status --short | Add-Content -Encoding ascii (Join-Path $Evidence 'source.txt')

# 2. The version-1 build: git archive of the accepted signed E3-M2c commit, Release build in the work directory.
$short = $V1Commit.Substring(0, 7)
$archive = Join-Path $WorkDir "src-$short.zip"
$source = Join-Path $WorkDir "src-$short"
Invoke-Checked 'git' @('-C', $repo, 'archive', '--format=zip', '-o', $archive, $V1Commit)
Expand-Archive -Path $archive -DestinationPath $source -Force
Invoke-Checked 'dotnet' @('build', (Join-Path $source 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
$v1 = Join-Path $WorkDir "v1-$short"
Copy-Item (Join-Path $source 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $v1 -Recurse -Force
Write-Hashes $v1 (Join-Path $Evidence 'v1-sha256.txt')
"git archive $V1Commit (accepted signed E3-M2c commit; ContentAcceptanceRules.CurrentVersion = 1), Release build in the work directory; release builds are not byte-reproducible, so no binary hash is claimed as provenance" |
    Set-Content -Encoding ascii (Join-Path $Evidence 'v1-origin.txt')

# 3. The harness and the version-1 agent (copies outside the repository).
$harness = Join-Path $WorkDir 'harnessD'
Copy-Item (Join-Path $gate 'HarnessD') $harness -Recurse -Force
Invoke-Checked 'dotnet' @('build', (Join-Path $harness 'HarnessD.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:M2dBinaries=$new")
$exe = Join-Path $harness 'bin\Release\net10.0\M2dHarness.exe'
$agent = Join-Path $WorkDir 'v1agent'
Copy-Item (Join-Path $gate 'V1Agent') $agent -Recurse -Force
Invoke-Checked 'dotnet' @('build', (Join-Path $agent 'V1Agent.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:V1Binaries=$v1")
$agentExe = Join-Path $agent 'bin\Release\net10.0\V1Agent.exe'

# 4. One disposable PostgreSQL container.
$name = 'm2d-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$adminPassword = 'm2d-admin-' + [guid]::NewGuid().ToString('N')
$runtimePassword = 'm2d-runtime-' + [guid]::NewGuid().ToString('N')
$code = 99
try {
    docker run -d --rm --name $name -e "POSTGRES_PASSWORD=$adminPassword" -e POSTGRES_DB=m2dgate -p "127.0.0.1:${Port}:5432" --tmpfs /var/lib/postgresql:rw postgres:18.0 | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        docker exec $name pg_isready -U postgres -d m2dgate 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    docker exec $name psql -U postgres -d m2dgate -v ON_ERROR_STOP=1 -qAt -c "CREATE ROLE authority_runtime LOGIN PASSWORD '$runtimePassword' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS" | Out-Null
    docker exec $name psql -U postgres -d m2dgate -qAt -c 'SELECT version()' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-version.txt')
    docker image inspect postgres:18.0 --format '{{.Id}}' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-image.txt')
    $env:M2D_NEWBIN = Join-Path $new 'AuthorizationAuthority.Api.dll'
    $env:M2D_V1AGENT = $agentExe
    $env:M2D_ADMIN = "Host=127.0.0.1;Port=$Port;Database=m2dgate;Username=postgres;Password=$adminPassword;Pooling=false;Timeout=30;Command Timeout=120"
    $env:M2D_RUNTIME = "Host=127.0.0.1;Port=$Port;Database=m2dgate;Username=authority_runtime;Password=$runtimePassword;Timeout=30;Command Timeout=120"
    $env:M2D_EVIDENCE = $Evidence
    & $exe *> (Join-Path $Evidence 'console.txt')
    $code = $LASTEXITCODE
}
finally {
    docker rm -f $name 2>$null | Out-Null
    Remove-Item Env:M2D_NEWBIN, Env:M2D_V1AGENT, Env:M2D_ADMIN, Env:M2D_RUNTIME, Env:M2D_EVIDENCE -ErrorAction SilentlyContinue
}

# The version-1 call files hold staged canonical test bytes only (no secret); the connection strings never reach them.
$removed = [string]::IsNullOrEmpty((docker ps -a --filter "name=$name" --format '{{.Names}}'))
$report = Join-Path $Evidence 'm2d-evidence.json'
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
    Write-Output 'E3-M2d CARV 2 / floor 2 PostgreSQL gate: FAILED'
    exit 1
}

Write-Output 'E3-M2d CARV 2 / floor 2 PostgreSQL gate: all checks passed'
exit 0
