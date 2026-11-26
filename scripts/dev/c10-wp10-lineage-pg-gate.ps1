<#
.SYNOPSIS
  C10 M1-WP10 legacy lineage dry run: disposable real-PostgreSQL gate. Development/Testing only.

.DESCRIPTION
  Builds this working tree's Authority (Release) and the harness (tools/experiments/c10-wp10-lineage-gate/Harness, a copy in a
  temporary work directory), starts ONE fresh disposable postgres:18.0 container (tmpfs data, loopback port, random name and
  password, every statement logged with its application name; removed afterwards; no other stack, volume or database is touched)
  and runs the harness. The harness creates its own database, migrates it, seeds synthetic legacy shares and runs the command under
  test as a separate process of the built Authority:

      dotnet AuthorizationAuthority.Api.dll --legacy-lineage-dry-run --organization <uuid> --output <new file>

  as a SELECT-only role and as the administrator, checks classifications, refusals, redaction and refusal exit codes, and proves
  every Authority table is unchanged. The gate then reads the server's statement log and requires, for the dry run's application
  name (authority-legacy-lineage-dry-run): a REPEATABLE READ transaction, SET TRANSACTION READ ONLY, ROLLBACK, and no write, DDL,
  privilege, lock or COMMIT statement. Exit code 0 only if every check passed and the container was removed. Not Stage B, not a
  security-test stack run, no Azure call, no persistent database.

.EXAMPLE
  powershell -File scripts\dev\c10-wp10-lineage-pg-gate.ps1 -Evidence C:\out\c10-wp10-run1
#>
param(
    [string]$WorkDir,
    [string]$Evidence,
    [int]$Port = 0
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('c10-wp10-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
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

# 1. The build under test (this working tree) and the harness (a copy outside the repository).
Invoke-Checked $dotnet @('build', (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
$authority = Join-Path $WorkDir 'authority'
Copy-Item (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $authority -Recurse -Force
(git -C $repo rev-parse HEAD) + ' plus the working tree (git status --short below)' | Set-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
git -C $repo status --short | Add-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
Get-ChildItem $authority -Filter 'AuthorizationAuthority.*.dll' | Sort-Object Name | Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash.ToLowerInvariant()) *$(Split-Path -Leaf $_.Path)" } | Set-Content -Encoding ascii (Join-Path $Evidence 'authority-sha256.txt')
$harness = Join-Path $WorkDir 'harness'
Copy-Item (Join-Path $repo 'tools\experiments\c10-wp10-lineage-gate\Harness') $harness -Recurse -Force
Invoke-Checked $dotnet @('build', (Join-Path $harness 'Harness.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:C10Wp10Binaries=$authority")
$exe = Join-Path $harness 'bin\Release\net10.0\C10Wp10Harness.exe'

# 2. One disposable PostgreSQL container.
$name = 'c10-wp10-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$password = 'c10-wp10-admin-' + [guid]::NewGuid().ToString('N')
$code = 99
try {
    docker run -d --rm --name $name -e "POSTGRES_PASSWORD=$password" -e POSTGRES_DB=c10wp10gate -p "127.0.0.1:${Port}:5432" --tmpfs /var/lib/postgresql:rw postgres:18.0 `
        -c log_statement=all -c 'log_line_prefix=%m [%p] %d %a ' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run failed (exit $LASTEXITCODE); try another -Port" }
    for ($i = 0; $i -lt 60; $i++) {
        docker exec $name pg_isready -U postgres -d c10wp10gate 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2

    # 3. The harness, then the server's statement log.
    $ErrorActionPreference = 'Continue'
    $admin = "Host=127.0.0.1;Port=$Port;Database=c10wp10gate;Username=postgres;Password=$password"
    & $exe --admin $admin --authority $authority --evidence $Evidence 2>&1 | ForEach-Object { "$_" } |
        Tee-Object -FilePath (Join-Path $Evidence 'harness.log') | Out-Host
    $harnessExit = $LASTEXITCODE

    $serverLog = Join-Path $Evidence 'postgres.log'
    docker logs $name 2>&1 | ForEach-Object { "$_" } | Set-Content -Encoding utf8 $serverLog
    # Simple-protocol statements log as "statement:", extended-protocol ones as "execute <name>:".
    $statements = @(Get-Content $serverLog | ForEach-Object {
            if ($_ -match ' authority-legacy-lineage-dry-run LOG:\s+(?:statement|execute [^:]+): (.*)$') { $Matches[1] } })
    $statements | Set-Content -Encoding utf8 (Join-Path $Evidence 'dry-run-statements.txt')

    $failures = @()
    if ($harnessExit -ne 0) { $failures += "harness failed (exit $harnessExit)" }
    if ($statements.Count -eq 0) { $failures += 'no dry-run statements were logged' }
    foreach ($required in @('^\s*BEGIN( TRANSACTION)? ISOLATION LEVEL REPEATABLE READ', '^\s*SET TRANSACTION READ ONLY', '^\s*ROLLBACK')) {
        if (-not ($statements | Where-Object { $_ -match $required })) { $failures += "missing statement: $required" }
    }
    $writes = @($statements | Where-Object { $_ -match '^\s*(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|COPY|CREATE|ALTER|DROP|GRANT|REVOKE|LOCK|COMMIT|VACUUM|REINDEX|CLUSTER)\b' -or $_ -match '\bFOR\s+(UPDATE|SHARE|NO KEY UPDATE|KEY SHARE)\b' -or $_ -match 'pg_advisory' })
    if ($writes.Count -gt 0) { $failures += "write-capable statements from the dry run: $($writes.Count)"; $writes | Set-Content -Encoding utf8 (Join-Path $Evidence 'unexpected-statements.txt') }

    if ($failures.Count -eq 0) {
        Write-Output "PASS: harness passed; $($statements.Count) dry-run statements, all read-only."
        $code = 0
    } else {
        $failures | ForEach-Object { Write-Output "FAIL: $_" }
        $code = 1
    }
} finally {
    $ErrorActionPreference = 'Continue'
    docker rm -f $name 2>&1 | Out-Null
    if (docker ps -a --format '{{.Names}}' | Select-String -SimpleMatch $name) { Write-Output "FAIL: container $name was not removed"; $code = 1 }
}

exit $code
