<#
.SYNOPSIS
  C10 D5 real-PostgreSQL gate for the expiry-aware share-only candidate query (Development/Testing only).
.DESCRIPTION
  Builds this working tree's Authority (Release), copies tools/experiments/c10-d5-share-only-gate/Harness to a temporary work
  directory and builds it against those binaries, starts ONE fresh disposable postgres:18.0 container (tmpfs data, loopback port,
  random name and password), applies every Authority migration, seeds synthetic shares and postings, and runs the real
  SearchQueryReader.ReadShareReadableItemsAsync with and without excluded (expired) shares. Removes the container afterwards.
  Never touches a persistent or shared database.
.EXAMPLE
  powershell -File scripts\dev\c10-d5-share-only-pg-gate.ps1 -Evidence C:\out\c10-d5-run1
#>
param(
    [string]$WorkDir,
    [string]$Evidence,
    [int]$Port = 0
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('c10-d5-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
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

Invoke-Checked $dotnet @('build', (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
$authority = Join-Path $WorkDir 'authority'
Copy-Item (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $authority -Recurse -Force
(git -C $repo rev-parse HEAD) + ' plus the working tree (git status --short below)' | Set-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
git -C $repo status --short | Add-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
$harness = Join-Path $WorkDir 'harness'
Copy-Item (Join-Path $repo 'tools\experiments\c10-d5-share-only-gate\Harness') $harness -Recurse -Force
Invoke-Checked $dotnet @('build', (Join-Path $harness 'Harness.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:C10D5Binaries=$authority")
$exe = Join-Path $harness 'bin\Release\net10.0\C10D5Harness.exe'

$name = 'c10-d5-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$password = 'c10-d5-admin-' + [guid]::NewGuid().ToString('N')
$code = 99
try {
    docker run -d --rm --name $name -e "POSTGRES_PASSWORD=$password" -e POSTGRES_DB=c10d5gate -p "127.0.0.1:${Port}:5432" --tmpfs /var/lib/postgresql:rw postgres:18.0 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run failed (exit $LASTEXITCODE); try another -Port" }
    for ($i = 0; $i -lt 60; $i++) {
        docker exec $name pg_isready -U postgres -d c10d5gate 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2

    $ErrorActionPreference = 'Continue'
    $admin = "Host=127.0.0.1;Port=$Port;Database=c10d5gate;Username=postgres;Password=$password"
    & $exe --admin $admin 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath (Join-Path $Evidence 'harness.log') | Out-Host
    $code = if ($LASTEXITCODE -eq 0) { 0 } else { 1 }
    Write-Output ($(if ($code -eq 0) { 'PASS' } else { 'FAIL' }) + ': C10 D5 share-only candidate gate')
} finally {
    $ErrorActionPreference = 'Continue'
    docker rm -f $name 2>&1 | Out-Null
    if (docker ps -a --format '{{.Names}}' | Select-String -SimpleMatch $name) { Write-Output "FAIL: container $name was not removed"; $code = 1 }
}

exit $code
