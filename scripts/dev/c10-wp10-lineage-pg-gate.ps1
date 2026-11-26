<#
.SYNOPSIS
  C10 M1-WP10 legacy lineage dry run: disposable real-PostgreSQL gate. Development/Testing only.

.DESCRIPTION
  Builds this working tree's Authority test project (Release), starts ONE fresh disposable postgres:18.0 container (tmpfs data,
  loopback port, random name and password, every statement logged with its application name; removed afterwards; no other stack,
  volume or database is touched) and runs LegacyLineageDryRunPostgresTests against it. That test creates its own database,
  migrates it, seeds synthetic legacy shares, runs the dry run as a SELECT-only role and as the administrator, and proves every
  Authority table is unchanged.

  The gate then reads the server's statement log and requires, for the dry run's application name
  (authority-legacy-lineage-dry-run): a REPEATABLE READ transaction, SET TRANSACTION READ ONLY, ROLLBACK, and no write, DDL,
  privilege, lock or COMMIT statement. Exit code 0 only if every check passed and the container was removed. Not Stage B, not a
  security-test stack run, no Azure call, no persistent database.

.EXAMPLE
  powershell -File scripts\dev\c10-wp10-lineage-pg-gate.ps1 -Evidence C:\out\c10-wp10-run1
#>
param(
    [string]$Evidence,
    [int]$Port = 0
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Evidence) { $Evidence = Join-Path ([System.IO.Path]::GetTempPath()) ('c10-wp10-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
if ($Port -eq 0) { $Port = Get-Random -Minimum 55500 -Maximum 55900 }
New-Item -ItemType Directory -Force $Evidence | Out-Null
Write-Output "evidence directory: $Evidence"

if (-not (docker image inspect postgres:18.0 --format '{{.Id}}' 2>$null)) { throw 'postgres:18.0 is not present locally (docker pull postgres:18.0 first)' }
$dotnet = (Get-Command dotnet).Source
$project = Join-Path $repo 'tests\authority\AuthorizationAuthority.Tests'
& $dotnet build $project -c Release -nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
(git -C $repo rev-parse HEAD) + ' plus the working tree (git status --short below)' | Set-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
git -C $repo status --short | Add-Content -Encoding ascii (Join-Path $Evidence 'source.txt')

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

    $env:C10_WP10_PG_ADMIN = "Host=127.0.0.1;Port=$Port;Database=c10wp10gate;Username=postgres;Password=$password"
    $testLog = Join-Path $Evidence 'tests.log'
    $ErrorActionPreference = 'Continue'
    & $dotnet test --project $project -c Release --no-build -- --minimum-expected-tests 1 --filter-class '*LegacyLineageDryRunPostgresTests' 2>&1 |
        ForEach-Object { "$_" } | Tee-Object -FilePath $testLog | Out-Host
    $testExit = $LASTEXITCODE
    Remove-Item Env:\C10_WP10_PG_ADMIN

    $serverLog = Join-Path $Evidence 'postgres.log'
    docker logs $name 2>&1 | ForEach-Object { "$_" } | Set-Content -Encoding utf8 $serverLog
    # Simple-protocol statements log as "statement:", extended-protocol ones as "execute <name>:".
    $statements = @(Get-Content $serverLog | ForEach-Object {
            if ($_ -match ' authority-legacy-lineage-dry-run LOG:\s+(?:statement|execute [^:]+): (.*)$') { $Matches[1] } })
    $statements | Set-Content -Encoding utf8 (Join-Path $Evidence 'dry-run-statements.txt')

    $failures = @()
    if ($testExit -ne 0) { $failures += "integration test failed (exit $testExit)" }
    if ((Select-String -Path $testLog -Pattern 'skipped: 0' -SimpleMatch -Quiet) -ne $true) { $failures += 'the integration test was skipped' }
    if ($statements.Count -eq 0) { $failures += 'no dry-run statements were logged' }
    foreach ($required in @('^\s*BEGIN( TRANSACTION)? ISOLATION LEVEL REPEATABLE READ', '^\s*SET TRANSACTION READ ONLY', '^\s*ROLLBACK')) {
        if (-not ($statements | Where-Object { $_ -match $required })) { $failures += "missing statement: $required" }
    }
    $writes = @($statements | Where-Object { $_ -match '^\s*(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|COPY|CREATE|ALTER|DROP|GRANT|REVOKE|LOCK|COMMIT|VACUUM|REINDEX|CLUSTER)\b' -or $_ -match '\bFOR\s+(UPDATE|SHARE|NO KEY UPDATE|KEY SHARE)\b' -or $_ -match 'pg_advisory' })
    if ($writes.Count -gt 0) { $failures += "write-capable statements from the dry run: $($writes.Count)"; $writes | Set-Content -Encoding utf8 (Join-Path $Evidence 'unexpected-statements.txt') }

    if ($failures.Count -eq 0) {
        Write-Output "PASS: $($statements.Count) dry-run statements, all read-only; the integration test passed."
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
