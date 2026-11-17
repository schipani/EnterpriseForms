<#
.SYNOPSIS
  E3-M2f-3G capability-refusal evidence: disposable PostgreSQL 18 verification gate. Development/Testing only.

.DESCRIPTION
  Builds this working tree's Authority and business (Release), the unmodified historical business builds 11e15ee and 0be5f52
  (git archive; each with the E3-M2f-2G BusinessAgent), and the harness (tools/experiments/e3-m2f3g-evidence-gate/HarnessG3, a copy in
  a temporary work directory, with the three standalone E3-M2e-2 support files linked unchanged). Starts ONE fresh disposable
  postgres:18.0 container (tmpfs data, loopback port, random name and credentials, autovacuum off, every statement logged with
  database and application name, lock waits logged) and ONE fresh disposable hashicorp/vault:1.20.4 dev container (the host's Vault
  Transit signer: transit mount, the repository's authority-transit policy, a token holding only that policy; closure §10 PD-15);
  creates the ordinary runtime role; comments the maintenance database with a per-run nonce; runs the harness; removes both containers
  and verifies their removal.

  The harness creates every database it uses inside that container (admitted by nonce and system identifier), runs this tree's REAL
  Authority host (deployment floor D = 2, Release B floor 3, Envelope v2 acceptance OFF) and the REAL recorder over the REAL store with
  the runtime identity, and checks durable state with its own read-only SQL oracle after every applicable scenario.

  Contract: docs/security/E3_M2F3_CONTRACT_CLOSURE.md; record docs/security/E3_M2F3G_VERIFICATION_RECORD.md. No Azure call, no
  persistent database, no Stage B, no data-key binding, no Envelope v2 acceptance. Disposable-container results do not establish
  Azure PostgreSQL behaviour or performance.

.EXAMPLE
  powershell -File scripts\dev\e3-m2f3g-evidence-pg-gate.ps1 -Evidence C:\out\m2f3g-run1 -RunLabel run-1
#>
param(
    [string]$WorkDir,
    [string]$Evidence,
    [int]$Port = 0,
    [int]$BigRows = 2000000,
    [string]$Groups = 'all',
    [int]$BoundaryMaxWaitMinutes = 61,
    [string]$RunLabel = 'run',
    [string]$LegacyBuildsRoot,
    [string]$HistoricalBusinessCommits = '11e15ee7d7c3d9e9be0685c2330ec006503bf1ec,0be5f522456acfa2102cf3e709ee9de1774a3d3a',
    [switch]$KeepWorkDir
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$gate = Join-Path $repo 'tools\experiments\e3-m2f3g-evidence-gate'
$e2Gate = Join-Path $repo 'tools\experiments\e3-m2e2-acceptance-gate\HarnessE2'
$capabilityGate = Join-Path $repo 'tools\experiments\e3-m2f2g-capability-gate'
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('e3-m2f3g-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
if (-not $Evidence) { $Evidence = Join-Path $WorkDir 'evidence' }
if ($Port -eq 0) { $Port = Get-Random -Minimum 55900 -Maximum 56300 }
New-Item -ItemType Directory -Force $WorkDir, $Evidence | Out-Null
Write-Output "work directory: $WorkDir"

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Write-Hashes([string]$folder, [string]$file) {
    Get-ChildItem $folder -Filter '*.dll' | Where-Object { $_.Name -like 'AuthorizationAuthority.*' -or $_.Name -like 'EnterpriseForms.*' } | Sort-Object Name | Get-FileHash -Algorithm SHA256 |
        ForEach-Object { "$($_.Hash.ToLowerInvariant()) *$(Split-Path -Leaf $_.Path)" } | Set-Content -Encoding ascii $file
}

function Get-ConfigEnablers {
    $hits = git -C $repo grep -n -i 'EnvelopeV2AcceptanceEnabled' -- '*.json' '*.yaml' '*.yml' '*.env' '*.bicep' '*.bicepparam' 2>$null
    return @($hits | Where-Object { $_ })
}

# A native command whose stderr must not become a terminating error (Windows PowerShell 5.1 with ErrorActionPreference Stop).
function Test-NativeQuiet([scriptblock]$probe) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $probe *> $null; return ($LASTEXITCODE -eq 0) } catch { return $false } finally { $ErrorActionPreference = $previous }
}

function Test-PortListening([int]$p) {
    return [bool](Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue)
}

# An unmodified historical BUSINESS build (src/backend/EnterpriseForms.Api, Release) from `git archive`.
function Get-FrozenBusinessBuild([string]$commit) {
    $short = $commit.Substring(0, 7)
    $label = "business-$short"
    $target = Join-Path $WorkDir $label
    if ($LegacyBuildsRoot -and (Test-Path (Join-Path $LegacyBuildsRoot "$label\EnterpriseForms.Infrastructure.dll"))) {
        Copy-Item (Join-Path $LegacyBuildsRoot $label) $target -Recurse -Force
    }
    else {
        $archive = Join-Path $WorkDir "src-$short.zip"
        $source = Join-Path $WorkDir "src-$short"
        Invoke-Checked 'git' @('-C', $repo, 'archive', '--format=zip', '-o', $archive, $commit)
        Expand-Archive -Path $archive -DestinationPath $source -Force
        Invoke-Checked 'dotnet' @('build', (Join-Path $source 'src\backend\EnterpriseForms.Api\EnterpriseForms.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
        Copy-Item (Join-Path $source 'src\backend\EnterpriseForms.Api\bin\Release\net10.0') $target -Recurse -Force
    }
    Write-Hashes $target (Join-Path $Evidence "$label-sha256.txt")
    "git archive $commit, Release build of src/backend/EnterpriseForms.Api (unmodified)" | Set-Content -Encoding ascii (Join-Path $Evidence "$label-origin.txt")
    return $target
}

# ---------------------------------------------------------------- 0. preflight
$enablersBefore = Get-ConfigEnablers
if ($enablersBefore.Count -gt 0) { throw "a repository configuration file mentions EnvelopeV2AcceptanceEnabled: $($enablersBefore -join '; ')" }
foreach ($image in 'postgres:18.0', 'hashicorp/vault:1.20.4') {
    if (-not (docker image inspect $image --format '{{.Id}}' 2>$null)) { throw "$image is not present locally (docker pull $image first)" }
}
if (Test-PortListening $Port) { throw "port $Port is already listening; refusing to start" }
$vaultPort = $Port + 1000
if (Test-PortListening $vaultPort) { throw "port $vaultPort is already listening; refusing to start" }
$dotnet = (Get-Command dotnet).Source
$containersBefore = @(docker ps -a --format '{{.Names}}' | Sort-Object)
$volumesBefore = @(docker volume ls -q)
(git -C $repo rev-parse HEAD) + ' plus the uncommitted working tree (git status --short below)' | Set-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
git -C $repo status --short | Add-Content -Encoding ascii (Join-Path $Evidence 'source.txt')

# ---------------------------------------------------------------- 1. builds
Invoke-Checked $dotnet @('build', (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
$new = Join-Path $WorkDir 'authority'
Copy-Item (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $new -Recurse -Force
Write-Hashes $new (Join-Path $Evidence 'authority-sha256.txt')
Invoke-Checked $dotnet @('build', (Join-Path $repo 'src\backend\EnterpriseForms.Api\EnterpriseForms.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
$business = Join-Path $WorkDir 'business'
Copy-Item (Join-Path $repo 'src\backend\EnterpriseForms.Api\bin\Release\net10.0') $business -Recurse -Force
Write-Hashes $business (Join-Path $Evidence 'business-sha256.txt')

# The harness binary set: this tree's Authority plus the business assemblies (first-party versions must agree; third-party: the higher).
$merged = Join-Path $WorkDir 'merged'
Copy-Item $new $merged -Recurse -Force
$mergeLog = New-Object System.Collections.Generic.List[string]
foreach ($dll in Get-ChildItem $business -Filter '*.dll') {
    if ($dll.Name -eq 'EnterpriseForms.Api.dll') { continue }
    $existing = Join-Path $merged $dll.Name
    if (Test-Path $existing) {
        $a = [System.Reflection.AssemblyName]::GetAssemblyName($existing).Version
        $b = [System.Reflection.AssemblyName]::GetAssemblyName($dll.FullName).Version
        if ($a -ne $b) {
            if ($dll.Name -like 'EnterpriseForms.*' -or $dll.Name -like 'AuthorizationAuthority.*') { throw "assembly version conflict for $($dll.Name): Authority $a, business $b" }
            # Third-party: the harness keeps the HIGHER version (a reference binds to an equal or higher version, never a lower one). The
            # Authority host and commands run from their own unmerged copies, so this affects only the harness process (in-process business
            # clients and the in-process recorder/store).
            if ($b -gt $a) { Copy-Item $dll.FullName $existing -Force; $mergeLog.Add("took the business $($dll.Name) $b (Authority has $a)") }
            else { $mergeLog.Add("kept the Authority's $($dll.Name) $a (business has $b)") }
        }
    }
    else {
        Copy-Item $dll.FullName $merged
        $mergeLog.Add("added $($dll.Name)")
    }
}
$mergeLog | Set-Content -Encoding ascii (Join-Path $Evidence 'merge.txt')

$hostCopy = Join-Path $WorkDir 'host'
Copy-Item $new $hostCopy -Recurse -Force
Remove-Item (Join-Path $hostCopy 'appsettings.Development.json') -Force -ErrorAction SilentlyContinue
if (Test-Path (Join-Path $hostCopy 'appsettings.Development.json')) { throw 'could not remove appsettings.Development.json from the host copy' }

$agents = New-Object System.Collections.Generic.List[string]
foreach ($commit in @($HistoricalBusinessCommits -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    $build = Get-FrozenBusinessBuild $commit
    $dir = Join-Path $WorkDir ('bagent-' + $commit.Substring(0, 7))
    Copy-Item (Join-Path $capabilityGate 'BusinessAgent') $dir -Recurse -Force
    Invoke-Checked $dotnet @('build', (Join-Path $dir 'BusinessAgent.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:BusinessBinaries=$build")
    $agents.Add($commit.Substring(0, 7) + '=' + (Join-Path $dir 'bin\Release\net10.0\BusinessAgent.exe'))
}

$harness = Join-Path $WorkDir 'harnessG3'
Copy-Item (Join-Path $gate 'HarnessG3') $harness -Recurse -Force
$linked = Join-Path $harness 'Linked'
New-Item -ItemType Directory -Force $linked | Out-Null
foreach ($f in 'Rows.cs', 'M1Core.cs', 'Isolation.cs') { Copy-Item (Join-Path $e2Gate $f) (Join-Path $linked $f) -Force }
Get-ChildItem $harness -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object FullName | Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash.ToLowerInvariant()) *$($_.Path.Substring($harness.Length + 1))" } | Set-Content -Encoding ascii (Join-Path $Evidence 'harness-sources-sha256.txt')
Invoke-Checked $dotnet @('build', (Join-Path $harness 'HarnessG3.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:M2f3gBinaries=$merged")
$exe = Join-Path $harness 'bin\Release\net10.0\M2f3gHarness.exe'

# ---------------------------------------------------------------- 2. one disposable PostgreSQL container
$name = 'm2f3g-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$vaultName = 'm2f3g-vault-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
if ($containersBefore -contains $name -or $containersBefore -contains $vaultName) { throw 'container name collision' }
$vaultRoot = 'm2f3g-root-' + [guid]::NewGuid().ToString('N')
$vaultAuthority = 'm2f3g-authority-' + [guid]::NewGuid().ToString('N')
$adminPassword = 'm2f3g-admin-' + [guid]::NewGuid().ToString('N')
$runtimePassword = 'm2f3g-runtime-' + [guid]::NewGuid().ToString('N')
$nonce = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N').Substring(0, 8)
$agentWork = Join-Path $WorkDir 'agent-calls'
New-Item -ItemType Directory -Force $agentWork | Out-Null
$code = 99
$started = $false
$vaultStarted = $false
try {
    docker run -d --rm --name $vaultName --cap-add IPC_LOCK -p "127.0.0.1:${vaultPort}:8200" hashicorp/vault:1.20.4 server -dev "-dev-root-token-id=$vaultRoot" '-dev-listen-address=0.0.0.0:8200' | Out-Null
    $vaultStarted = $true
    docker run -d --rm --name $name -e "POSTGRES_PASSWORD=$adminPassword" -e POSTGRES_DB=m2f3ggate -p "127.0.0.1:${Port}:5432" --tmpfs /var/lib/postgresql:rw postgres:18.0 `
        -c autovacuum=off -c log_statement=all -c log_lock_waits=on -c max_connections=400 -c shared_buffers=256MB -c 'log_line_prefix=%m [%p] %d %a ' | Out-Null
    $started = $true
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-NativeQuiet { docker exec $name pg_isready -U postgres -d m2f3ggate }) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    docker exec $name psql -U postgres -d m2f3ggate -v ON_ERROR_STOP=1 -qAt -c "CREATE ROLE authority_runtime LOGIN PASSWORD '$runtimePassword' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS" | Out-Null
    docker exec $name psql -U postgres -d m2f3ggate -v ON_ERROR_STOP=1 -qAt -c "COMMENT ON DATABASE m2f3ggate IS 'm2e2-nonce:$nonce'" | Out-Null
    $sysid = (docker exec $name psql -U postgres -d m2f3ggate -qAt -c 'SELECT system_identifier FROM pg_control_system()').Trim()
    docker exec $name psql -U postgres -d m2f3ggate -qAt -c 'SELECT version()' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-version.txt')
    docker image inspect postgres:18.0 --format '{{.Id}}' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-image.txt')
    docker image inspect hashicorp/vault:1.20.4 --format '{{.Id}}' | Set-Content -Encoding ascii (Join-Path $Evidence 'vault-image.txt')
    $vaultReady = $false
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-NativeQuiet { docker exec -e VAULT_ADDR=http://127.0.0.1:8200 $vaultName vault status }) { $vaultReady = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $vaultReady) { throw 'the disposable Vault did not become ready' }
    $vaultEnv = @('-e', 'VAULT_ADDR=http://127.0.0.1:8200', '-e', "VAULT_TOKEN=$vaultRoot")
    docker exec $vaultName mkdir -p /vault-config | Out-Null
    docker cp (Join-Path $repo 'infrastructure\docker\vault\authority-transit-policy.hcl') "${vaultName}:/vault-config/authority-transit-policy.hcl" | Out-Null
    if (-not (Test-NativeQuiet { docker exec @vaultEnv $vaultName vault secrets enable -path=transit transit })) { throw 'enabling the transit mount failed' }
    if (-not (Test-NativeQuiet { docker exec @vaultEnv $vaultName vault policy write authority-transit /vault-config/authority-transit-policy.hcl })) { throw 'writing the authority transit policy failed' }
    if (-not (Test-NativeQuiet { docker exec @vaultEnv $vaultName vault token create "-id=$vaultAuthority" -orphan -no-default-policy -policy=authority-transit -ttl=8h })) { throw 'creating the authority token failed' }

    $env:G3_AUTHORITY_DLL = Join-Path $new 'AuthorizationAuthority.Api.dll'
    $env:G3_HOSTBIN = $hostCopy
    $env:G3_DOTNET = $dotnet
    $env:G3_ADMIN = "Host=127.0.0.1;Port=$Port;Database=m2f3ggate;Username=postgres;Password=$adminPassword;Pooling=false;Timeout=30;Command Timeout=600"
    $env:G3_RUNTIME = "Host=127.0.0.1;Port=$Port;Database=m2f3ggate;Username=authority_runtime;Password=$runtimePassword;Timeout=30;Command Timeout=120"
    $env:G3_EVIDENCE = $Evidence
    $env:G3_PORT = "$Port"
    $env:G3_NONCE = $nonce
    $env:G3_SYSID = $sysid
    $env:G3_CONTAINER = $name
    $env:G3_BUSINESS_AGENTS = ($agents -join ';')
    $env:G3_AGENT_WORK = $agentWork
    $env:G3_GROUPS = $Groups
    $env:G3_BIG_ROWS = "$BigRows"
    $env:G3_BOUNDARY_MAX_WAIT_MINUTES = "$BoundaryMaxWaitMinutes"
    $env:G3_RUN_LABEL = $RunLabel
    $env:G3_VAULT_ADDRESS = "http://127.0.0.1:$vaultPort"
    $env:G3_VAULT_PORT = "$vaultPort"
    $env:G3_VAULT_TOKEN = $vaultAuthority
    & $exe *> (Join-Path $Evidence 'console.txt')
    $code = $LASTEXITCODE
}
finally {
    Get-ChildItem Env: | Where-Object { $_.Name -like 'G3_*' } | ForEach-Object { Remove-Item "Env:$($_.Name)" -ErrorAction SilentlyContinue }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    if ($started) { cmd /c "docker logs $name > `"$(Join-Path $WorkDir 'postgres-server.log')`" 2>&1" }
    docker rm -f -v $name 2>$null | Out-Null
    if ($vaultStarted) { docker rm -f -v $vaultName 2>$null | Out-Null } # -v: the Vault image declares anonymous volumes
    $ErrorActionPreference = $previous
}

# ---------------------------------------------------------------- 3. disposal, log classification, scrubbing, summary
$containersAfter = @(docker ps -a --format '{{.Names}}' | Sort-Object)
$removed = -not ($containersAfter -contains $name) -and -not ($containersAfter -contains $vaultName)
$listUnchanged = (Compare-Object $containersBefore $containersAfter | Measure-Object).Count -eq 0
$newVolumes = @(@(docker volume ls -q) | Where-Object { $volumesBefore -notcontains $_ })
$enablersAfter = Get-ConfigEnablers
$serverLog = Join-Path $WorkDir 'postgres-server.log'
$lines = if (Test-Path $serverLog) { Get-Content $serverLog } else { @() }
# Only the error, deadlock and lock-wait lines join the evidence (the full statement log is large); the harness saved its own traces and plans.
$lines | Where-Object { $_ -match 'ERROR:|FATAL:|deadlock|still waiting|acquired|STATEMENT:' } | Set-Content -Encoding utf8 (Join-Path $Evidence 'postgres-errors-and-lock-waits.log')
$deadlocks = @($lines | Select-String -SimpleMatch 'deadlock detected').Count
$denied = @($lines | Select-String -SimpleMatch 'permission denied').Count
if (Test-Path $agentWork) {
    $agentEvidence = Join-Path $Evidence 'agent-calls'
    New-Item -ItemType Directory -Force $agentEvidence | Out-Null
    Get-ChildItem $agentWork -Filter '*.out.json' | Copy-Item -Destination $agentEvidence
}

$secrets = @($adminPassword, $runtimePassword, $nonce, $vaultRoot, $vaultAuthority)
$paths = @($WorkDir.TrimEnd('\'), $repo, $env:USERPROFILE) | Where-Object { $_ }
foreach ($file in Get-ChildItem $Evidence -Recurse -File | Where-Object { $_.Extension -in '.txt', '.json', '.log' }) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    $original = $text
    foreach ($s in $secrets) { $text = $text.Replace($s, '<redacted>') }
    foreach ($p in $paths) {
        foreach ($form in @($p, $p.Replace('\', '\\'), $p.Replace('\', '/'))) {
            $text = [regex]::Replace($text, [regex]::Escape($form), '<local>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
    }
    if ($text -ne $original) { [System.IO.File]::WriteAllText($file.FullName, $text) }
}
$leaks = @(Get-ChildItem $Evidence -Recurse -File | Select-String -SimpleMatch -Pattern ($secrets + $paths) -ErrorAction SilentlyContinue).Count

$report = Join-Path $Evidence 'm2f3g-evidence.json'
$checks = if (Test-Path $report) { Get-Content $report -Raw | ConvertFrom-Json } else { $null }
$summary = [pscustomobject]@{
    RunLabel = $RunLabel
    Groups = $Groups
    ExitCode = $code
    Checks = if ($checks) { $checks.checksTotal } else { 0 }
    Failed = if ($checks) { $checks.checksFailed } else { -1 }
    DemonstrationsOutsidePassTotals = if ($checks) { $checks.demonstrationsOutsidePassTotals } else { -1 }
    NotExecuted = if ($checks) { @($checks.notExecuted).Count } else { -1 }
    ContainersRemoved = $removed
    ContainerListUnchanged = $listUnchanged
    NewDockerVolumes = $newVolumes.Count
    DeadlocksInServerLog = $deadlocks
    PermissionDeniedInServerLog = $denied
    ConfigEnablersBefore = $enablersBefore.Count
    ConfigEnablersAfter = $enablersAfter.Count
    EvidenceLeaksAfterScrub = $leaks
}
$summary | Format-List | Out-String | Write-Output
$summary | ConvertTo-Json | Set-Content -Encoding ascii (Join-Path $Evidence 'summary.json')
Get-Content (Join-Path $Evidence 'console.txt') | Write-Output
if (-not $KeepWorkDir) {
    if ((Resolve-Path $Evidence).Path.StartsWith((Resolve-Path $WorkDir).Path)) { Write-Output 'evidence is inside the work directory; keeping it' }
    else { Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($code -ne 0 -or $summary.Failed -ne 0 -or -not $removed -or -not $listUnchanged -or $newVolumes.Count -ne 0 -or $deadlocks -ne 0 -or $leaks -ne 0 -or $enablersAfter.Count -ne 0) {
    Write-Output 'E3-M2f-3G capability-refusal evidence PostgreSQL gate: FAILED'
    exit 1
}

Write-Output 'E3-M2f-3G capability-refusal evidence PostgreSQL gate: all checks passed'
exit 0
