<#
.SYNOPSIS
  E3-M2e-2 controlled Envelope v2 acceptance verification gate. Development/Testing only. TEST-ONLY.

.DESCRIPTION
  Owner authorization (2026-11-16, readiness decisions D-1..D-12): Envelope v2 acceptance may be enabled only inside newly created,
  identity-verified, disposable Development/Testing processes and databases. This script:

    1. refuses to start if any repository configuration file enables Content:EnvelopeV2AcceptanceEnabled, or if the required images are
       missing; records the docker container list;
    2. builds this tree's Authority (Release) and business API (Release) and MERGES their binaries for the harness, refusing two different
       versions of one assembly; builds frozen Authority Release builds of a1127a1 (CARV 2) and 8915f77 (CARV 1) from `git archive`;
       prepares a host copy of this tree's Authority output WITHOUT appsettings.Development.json (no default can point at a persistent
       stack);
    3. starts ONE fresh postgres:18.0 container (--rm, tmpfs, 127.0.0.1 only, random name, random free port, random passwords) and ONE
       fresh hashicorp/vault:1.20.4 dev container (--rm, 127.0.0.1 only, random name, port and tokens), comments the databases with a
       per-run nonce, records the cluster's system identifier from inside the container, provisions two Vault organizations with the
       repository's own Vault scripts;
    4. runs the harness (tools/experiments/e3-m2e2-acceptance-gate/HarnessE2) with only its own M2E2_* inputs; the harness scrubs inherited
       settings, admits only nonce-verified databases on the run's port, and enables acceptance only on in-process switch objects;
    5. ALWAYS (finally): saves the PostgreSQL server log, removes both containers, removes its environment variables, and verifies that the
       container list equals the one recorded before the run;
    6. classifies the server log (deadlocks, lock timeouts, expected and unexpected permission errors), scrubs local paths and secrets from
       the evidence, and fails unless every counted check passed, both containers were removed, no unexpected permission error and no
       deadlock occurred.

  Not Stage B, not a security-test or developer stack run, no Azure call, no persistent database. Contract:
  docs/security/E3_M2E_CONTRACT_CLOSURE.md §6.2; record docs/security/E3_M2E2_VERIFICATION_RECORD.md.

.PARAMETER Groups
  Harness groups to run (comma list of F,P,N,C,X,D,R,H or 'all'). Setup and acceptance activation always run. Used by focused mutation runs.

.PARAMETER DbMutationApply
  Enforcement-removal runs (database mode) only: SQL that weakens one database control in this run's disposable database after acceptance
  is activated; -DbMutationRestore restores it before teardown (the harness verifies the catalog digest and the runtime grants).

.PARAMETER NewBinaries
  A prebuilt Authority Release folder to test instead of building this tree (enforcement-removal runs build a mutated scratch copy).

.PARAMETER HistoricalBusinessCommits
  E3-M2f-2G (groups G, GC, GR, GH; docs/security/E3_M2F2G_VERIFICATION_RECORD.md): business builds exported with `git archive` and built
  (Release, src/backend/EnterpriseForms.Api) unmodified; each gets its own BusinessAgent. Default: 11e15ee (before E3-M2b-2) and 0be5f52
  (E3-M2e-2, no capability header). This tree's business build is always added as "current".

.PARAMETER BaselineAuthorityCommit
  E3-M2f-2G: the Authority build without E3-M2f-2 enforcement (default 0be5f52) for the expected-gap demonstrations (AuthorityAgent).

.EXAMPLE
  powershell -File scripts\dev\e3-m2e2-acceptance-pg-gate.ps1 -Evidence C:\out\m2e2-run1
#>
param(
    [string]$WorkDir,
    [string]$Evidence,
    [string]$Carv2Commit = 'a1127a109b5596e65cd3ac7f66a4ae4b35df2eb7',
    [string]$Carv1Commit = '8915f77c10299c08b27e36d40b7a2388d1e38d0f',
    [string]$LegacyBuildsRoot,
    [string]$BusinessBinaries,
    [string]$NewBinaries,
    [string]$Groups = 'all',
    [string]$Label = 'run',
    [string]$DbMutationApply,
    [string]$DbMutationRestore,
    [string]$HistoricalBusinessCommits = '11e15ee7d7c3d9e9be0685c2330ec006503bf1ec,0be5f522456acfa2102cf3e709ee9de1774a3d3a',
    [string]$BaselineAuthorityCommit = '0be5f522456acfa2102cf3e709ee9de1774a3d3a'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$gate = Join-Path $repo 'tools\experiments\e3-m2e2-acceptance-gate'
$legacySource = Join-Path $repo 'tools\experiments\e3-m2e1-ledger-gate\LegacyAgent'
$capabilityGate = Join-Path $repo 'tools\experiments\e3-m2f2g-capability-gate'
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('e3-m2e2-gate-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
if (-not $Evidence) { $Evidence = Join-Path $WorkDir 'evidence' }
New-Item -ItemType Directory -Force $WorkDir, $Evidence | Out-Null
# E3-M2f-2G: canonical Windows paths, so the evidence scrub below matches every rendering of the work directory (a caller may pass '/').
$WorkDir = [System.IO.Path]::GetFullPath($WorkDir).TrimEnd('')
$Evidence = [System.IO.Path]::GetFullPath($Evidence).TrimEnd('')
Write-Output "work directory: $WorkDir"

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Write-Hashes([string]$folder, [string]$file) {
    Get-ChildItem $folder -Filter '*.dll' | Where-Object { $_.Name -like 'AuthorizationAuthority.*' -or $_.Name -like 'EnterpriseForms.*' } | Sort-Object Name |
        Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant()) *$(Split-Path -Leaf $_.Path)" } | Set-Content -Encoding ascii $file
}

# Windows PowerShell 5.1 turns native stderr into an error record; readiness probes write expected stderr while a container starts.
function Test-NativeQuiet([scriptblock]$probe) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $probe *> $null; return ($LASTEXITCODE -eq 0) } catch { return $false } finally { $ErrorActionPreference = $previous }
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    return $port
}

function Test-PortListening([int]$port) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try { $async = $client.BeginConnect('127.0.0.1', $port, $null, $null); $ok = $async.AsyncWaitHandle.WaitOne(300) -and $client.Connected; return $ok }
    catch { return $false } finally { $client.Close() }
}

function New-UuidV7 {
    $bytes = New-Object byte[] 16
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $ms = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    for ($i = 0; $i -lt 6; $i++) { $bytes[$i] = [byte](($ms -shr (8 * (5 - $i))) -band 0xff) }
    $bytes[6] = [byte](($bytes[6] -band 0x0f) -bor 0x70)
    $bytes[8] = [byte](($bytes[8] -band 0x3f) -bor 0x80)
    $hex = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    return "$($hex.Substring(0,8))-$($hex.Substring(8,4))-$($hex.Substring(12,4))-$($hex.Substring(16,4))-$($hex.Substring(20,12))"
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
    "git archive $commit, Release build" | Set-Content -Encoding ascii (Join-Path $Evidence "$label-origin.txt")
    return $target
}

# E3-M2f-2G: an unmodified historical BUSINESS build (src/backend/EnterpriseForms.Api, Release) from `git archive`.
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
        if (-not (Test-Path (Join-Path $source 'src'))) {
            Invoke-Checked 'git' @('-C', $repo, 'archive', '--format=zip', '-o', $archive, $commit)
            Expand-Archive -Path $archive -DestinationPath $source -Force
        }
        Invoke-Checked 'dotnet' @('build', (Join-Path $source 'src\backend\EnterpriseForms.Api\EnterpriseForms.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
        Copy-Item (Join-Path $source 'src\backend\EnterpriseForms.Api\bin\Release\net10.0') $target -Recurse -Force
    }
    Write-Hashes $target (Join-Path $Evidence "$label-sha256.txt")
    "git archive $commit, Release build of src/backend/EnterpriseForms.Api (unmodified)" | Set-Content -Encoding ascii (Join-Path $Evidence "$label-origin.txt")
    return $target
}

function Get-ConfigEnablers {
    $hits = git -C $repo grep -n -i 'EnvelopeV2AcceptanceEnabled' -- '*.json' '*.yaml' '*.yml' '*.env' '*.bicep' '*.bicepparam' 2>$null
    return @($hits | Where-Object { $_ })
}

# ---------------------------------------------------------------- 0. preflight
$enablersBefore = Get-ConfigEnablers
if ($enablersBefore.Count -gt 0) { throw "a repository configuration file mentions EnvelopeV2AcceptanceEnabled: $($enablersBefore -join '; ')" }
foreach ($image in 'postgres:18.0', 'hashicorp/vault:1.20.4') {
    if (-not (docker image inspect $image --format '{{.Id}}' 2>$null)) { throw "$image is not present locally (docker pull $image first)" }
}
$containersBefore = @(docker ps -a --format '{{.Names}}' | Sort-Object)
(git -C $repo rev-parse HEAD) + ' plus the working tree below' | Set-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
git -C $repo status --short | Add-Content -Encoding ascii (Join-Path $Evidence 'source.txt')

# ---------------------------------------------------------------- 1. builds
$new = Join-Path $WorkDir 'new'
if ($NewBinaries) {
    Copy-Item $NewBinaries $new -Recurse -Force
    'prebuilt Authority Release folder (-NewBinaries; an enforcement-removal scratch build)' | Add-Content -Encoding ascii (Join-Path $Evidence 'source.txt')
}
else {
    Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
    Copy-Item (Join-Path $repo 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $new -Recurse -Force
}
Write-Hashes $new (Join-Path $Evidence 'new-sha256.txt')

$business = Join-Path $WorkDir 'business'
if ($BusinessBinaries) { Copy-Item $BusinessBinaries $business -Recurse -Force }
else {
    Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'src\backend\EnterpriseForms.Api\EnterpriseForms.Api.csproj'), '-c', 'Release', '-nologo', '-v', 'q')
    Copy-Item (Join-Path $repo 'src\backend\EnterpriseForms.Api\bin\Release\net10.0') $business -Recurse -Force
}
Write-Hashes $business (Join-Path $Evidence 'business-sha256.txt')

# The harness binary set: this tree's Authority, plus the business assemblies the Authority output lacks. A first-party assembly
# (EnterpriseForms.*, AuthorizationAuthority.*) present in both must have the same assembly version, otherwise the merge is refused.
# For a third-party assembly in both, the Authority's copy is kept and a version difference is recorded (merge.txt): the in-process
# Authority must run with its own dependencies; a business code path that needed the other version would fail loudly at load time.
$merged = Join-Path $WorkDir 'merged'
Copy-Item $new $merged -Recurse -Force
$mergeLog = New-Object System.Collections.Generic.List[string]
foreach ($dll in Get-ChildItem $business -Filter '*.dll') {
    if ($dll.Name -in @('EnterpriseForms.Api.dll')) { continue }
    $existing = Join-Path $merged $dll.Name
    if (Test-Path $existing) {
        $a = [System.Reflection.AssemblyName]::GetAssemblyName($existing).Version
        $b = [System.Reflection.AssemblyName]::GetAssemblyName($dll.FullName).Version
        if ($a -ne $b) {
            if ($dll.Name -like 'EnterpriseForms.*' -or $dll.Name -like 'AuthorizationAuthority.*') { throw "assembly version conflict for $($dll.Name): Authority $a, business $b" }
            $mergeLog.Add("kept the Authority's $($dll.Name) $a (business has $b)")
        }
    }
    else {
        Copy-Item $dll.FullName $merged
        $mergeLog.Add("added $($dll.Name)")
    }
}
$mergeLog | Set-Content -Encoding ascii (Join-Path $Evidence 'merge.txt')

$carv2 = Get-FrozenBuild $Carv2Commit 'carv2'
$carv1 = Get-FrozenBuild $Carv1Commit 'carv1'

$hostCopy = Join-Path $WorkDir 'host'
Copy-Item $new $hostCopy -Recurse -Force
Remove-Item (Join-Path $hostCopy 'appsettings.Development.json') -Force -ErrorAction SilentlyContinue
if (Test-Path (Join-Path $hostCopy 'appsettings.Development.json')) { throw 'could not remove appsettings.Development.json from the host copy' }

$harness = Join-Path $WorkDir 'harnessE2'
Copy-Item (Join-Path $gate 'HarnessE2') $harness -Recurse -Force
Invoke-Checked 'dotnet' @('build', (Join-Path $harness 'HarnessE2.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:M2e2Binaries=$merged")
$exe = Join-Path $harness 'bin\Release\net10.0\M2e2Harness.exe'
# E3-M2f-2G agents: one BusinessAgent per business build, one AuthorityAgent per Authority build (each built against that build alone).
$capabilityGroups = $Groups -eq 'all' -or @($Groups -split ',' | Where-Object { $_.Trim() -like 'G*' }).Count -gt 0
$businessAgents = New-Object System.Collections.Generic.List[string]
$authorityAgents = New-Object System.Collections.Generic.List[string]
if ($capabilityGroups) {
    $businessBuilds = [ordered]@{ current = $business }
    foreach ($commit in @($HistoricalBusinessCommits -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) { $businessBuilds[$commit.Substring(0, 7)] = Get-FrozenBusinessBuild $commit }
    $baseline = Get-FrozenBuild $BaselineAuthorityCommit 'base'
    foreach ($entry in $businessBuilds.GetEnumerator()) {
        $dir = Join-Path $WorkDir ('bagent-' + $entry.Key)
        Copy-Item (Join-Path $capabilityGate 'BusinessAgent') $dir -Recurse -Force
        Invoke-Checked 'dotnet' @('build', (Join-Path $dir 'BusinessAgent.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:BusinessBinaries=$($entry.Value)")
        $businessAgents.Add("$($entry.Key)=" + (Join-Path $dir 'bin\Release\net10.0\BusinessAgent.exe'))
    }
    foreach ($pair in @(@('current', $new), @($BaselineAuthorityCommit.Substring(0, 7), $baseline))) {
        $dir = Join-Path $WorkDir ('aagent-' + $pair[0])
        Copy-Item (Join-Path $capabilityGate 'AuthorityAgent') $dir -Recurse -Force
        Invoke-Checked 'dotnet' @('build', (Join-Path $dir 'AuthorityAgent.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:AuthorityBinaries=$($pair[1])")
        $authorityAgents.Add("$($pair[0])=" + (Join-Path $dir 'bin\Release\net10.0\AuthorityAgent.exe'))
    }
}

$agents = @{}
foreach ($pair in @(@('carv1', $carv1), @('carv2', $carv2))) {
    $dir = Join-Path $WorkDir ("agent-" + $pair[0])
    Copy-Item $legacySource $dir -Recurse -Force
    Invoke-Checked 'dotnet' @('build', (Join-Path $dir 'LegacyAgent.csproj'), '-c', 'Release', '-nologo', '-v', 'q', "-p:LegacyBinaries=$($pair[1])")
    $agents[$pair[0]] = Join-Path $dir 'bin\Release\net10.0\LegacyAgent.exe'
}

# ---------------------------------------------------------------- 2. disposable containers
$nonce = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N').Substring(0, 8)
$tag = $nonce.Substring(0, 8)
$pgName = "m2e2-gate-$tag"
$vaultName = "m2e2-vault-$tag"
$port = Get-FreePort
$vaultPort = Get-FreePort
while ($vaultPort -eq $port) { $vaultPort = Get-FreePort }
foreach ($p in $port, $vaultPort) { if (Test-PortListening $p) { throw "port $p is already listening; refusing to start" } }
if ($containersBefore -contains $pgName -or $containersBefore -contains $vaultName) { throw 'container name collision' }
$adminPassword = 'm2e2-admin-' + [guid]::NewGuid().ToString('N')
$runtimePassword = 'm2e2-runtime-' + [guid]::NewGuid().ToString('N')
$vaultRoot = 'm2e2-root-' + [guid]::NewGuid().ToString('N')
$vaultBusiness = 'm2e2-business-' + [guid]::NewGuid().ToString('N')
$vaultAuthority = 'm2f2g-authority-' + [guid]::NewGuid().ToString('N')
$vaultOrgs = @((New-UuidV7), (New-UuidV7))
$code = 99
$started = @()
try {
    docker run -d --rm --name $pgName -e "POSTGRES_PASSWORD=$adminPassword" -e POSTGRES_DB=m2e2gate -p "127.0.0.1:${port}:5432" --tmpfs /var/lib/postgresql:rw postgres:18.0 -c log_lock_waits=on -c 'log_line_prefix=%m [%p] app=%a ' | Out-Null
    $started += $pgName
    docker run -d --rm --name $vaultName --cap-add IPC_LOCK -p "127.0.0.1:${vaultPort}:8200" hashicorp/vault:1.20.4 server -dev "-dev-root-token-id=$vaultRoot" '-dev-listen-address=0.0.0.0:8200' | Out-Null
    $started += $vaultName
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-NativeQuiet { docker exec $pgName pg_isready -U postgres -d m2e2gate }) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
    $psql = @('exec', $pgName, 'psql', '-U', 'postgres', '-d', 'm2e2gate', '-v', 'ON_ERROR_STOP=1', '-qAt', '-c')
    docker @psql "CREATE ROLE authority_runtime LOGIN PASSWORD '$runtimePassword' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS" | Out-Null
    docker @psql "COMMENT ON DATABASE m2e2gate IS 'm2e2-nonce:$nonce'" | Out-Null
    docker @psql "COMMENT ON DATABASE postgres IS 'm2e2-nonce:$nonce'" | Out-Null
    $systemIdentifier = (docker @psql 'SELECT system_identifier FROM pg_control_system()').Trim()
    docker @psql 'SELECT version()' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-version.txt')
    docker image inspect postgres:18.0 --format '{{.Id}}' | Set-Content -Encoding ascii (Join-Path $Evidence 'postgres-image.txt')
    docker image inspect hashicorp/vault:1.20.4 --format '{{.Id}}' | Set-Content -Encoding ascii (Join-Path $Evidence 'vault-image.txt')

    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        if (Test-NativeQuiet { docker exec -e VAULT_ADDR=http://127.0.0.1:8200 $vaultName vault status }) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Vault did not become ready' }
    docker exec $vaultName mkdir -p /vault-config | Out-Null
    foreach ($file in 'business-encryption-init.sh', 'business-encryption-policy.hcl', 'provision-business-encryption-keys.sh') {
        docker cp (Join-Path $repo "infrastructure\docker\vault\$file") "${vaultName}:/vault-config/$file" | Out-Null
    }
    $envArgs = @('-e', 'VAULT_ADDR=http://127.0.0.1:8200', '-e', "VAULT_TOKEN=$vaultRoot")
    if (-not (Test-NativeQuiet { docker exec @envArgs -e "BUSINESS_ENCRYPTION_VAULT_TOKEN=$vaultBusiness" $vaultName sh /vault-config/business-encryption-init.sh })) { throw 'Vault init script failed' }
    foreach ($org in $vaultOrgs) {
        if (-not (Test-NativeQuiet { docker exec @envArgs -e "ORGANIZATION_ID=$org" $vaultName sh /vault-config/provision-business-encryption-keys.sh })) { throw "Vault provisioning failed for $org" }
    }
    # E3-M2f-2G host group: the Authority's own Transit mount and a token holding ONLY the repository's authority-transit-policy.hcl.
    docker cp (Join-Path $repo 'infrastructure\docker\vault\authority-transit-policy.hcl') "${vaultName}:/vault-config/authority-transit-policy.hcl" | Out-Null
    if (-not (Test-NativeQuiet { docker exec @envArgs $vaultName vault secrets enable -path=transit transit })) { throw 'enabling the authority transit mount failed' }
    if (-not (Test-NativeQuiet { docker exec @envArgs $vaultName vault policy write authority-transit /vault-config/authority-transit-policy.hcl })) { throw 'writing the authority transit policy failed' }
    if (-not (Test-NativeQuiet { docker exec @envArgs $vaultName vault token create "-id=$vaultAuthority" -orphan -no-default-policy -policy=authority-transit -ttl=6h })) { throw 'creating the authority token failed' }

    $env:M2E2_NEWBIN = Join-Path $new 'AuthorizationAuthority.Api.dll'
    $env:M2E2_HOSTBIN = $hostCopy
    $env:M2E2_CARV1AGENT = $agents['carv1']
    $env:M2E2_CARV2AGENT = $agents['carv2']
    $env:M2E2_ADMIN = "Host=127.0.0.1;Port=$port;Database=m2e2gate;Username=postgres;Password=$adminPassword;Pooling=false;Timeout=30;Command Timeout=180"
    $env:M2E2_RUNTIME = "Host=127.0.0.1;Port=$port;Database=m2e2gate;Username=authority_runtime;Password=$runtimePassword;Timeout=30;Command Timeout=180"
    $env:M2E2_EVIDENCE = $Evidence
    $env:M2E2_PORT = "$port"
    $env:M2E2_NONCE = $nonce
    $env:M2E2_SYSID = $systemIdentifier
    $env:M2E2_VAULT_ADDRESS = "http://127.0.0.1:$vaultPort"
    $env:M2E2_VAULT_TOKEN = $vaultBusiness
    $env:M2E2_VAULT_PORT = "$vaultPort"
    $env:M2E2_VAULT_ORGS = ($vaultOrgs -join ',')
    $env:M2E2_GROUPS = $Groups
    $env:M2F2G_BUSINESS_AGENTS = ($businessAgents -join ';')
    $env:M2F2G_AUTHORITY_AGENTS = ($authorityAgents -join ';')
    $env:M2F2G_VAULT_AUTHORITY_TOKEN = $vaultAuthority
    $env:M2F2G_AGENT_WORK = (Join-Path $WorkDir 'agent-calls')
    New-Item -ItemType Directory -Force $env:M2F2G_AGENT_WORK | Out-Null
    if ($DbMutationApply) { $env:M2E2_DBMUT_APPLY = $DbMutationApply; $env:M2E2_DBMUT_RESTORE = $DbMutationRestore }
    & $exe *> (Join-Path $Evidence 'console.txt')
    $code = $LASTEXITCODE
}
finally {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    if ($started -contains $pgName) { cmd /c "docker logs $pgName > `"$(Join-Path $Evidence 'postgres-server.log')`" 2>&1" }
    foreach ($c in $started) { docker rm -f $c 2>$null | Out-Null }
    $ErrorActionPreference = $previous
    Get-ChildItem Env: | Where-Object { $_.Name -like 'M2E2_*' -or $_.Name -like 'M2F2G_*' } | ForEach-Object { Remove-Item "Env:$($_.Name)" -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- 3. cleanup verification, log classification, scrubbing
$containersAfter = @(docker ps -a --format '{{.Names}}' | Sort-Object)
$removed = ($started | Where-Object { $containersAfter -contains $_ }).Count -eq 0
$listUnchanged = (Compare-Object $containersBefore $containersAfter | Measure-Object).Count -eq 0
$enablersAfter = Get-ConfigEnablers

$serverLog = Join-Path $Evidence 'postgres-server.log'
$lines = if (Test-Path $serverLog) { Get-Content $serverLog } else { @() }
$deadlocks = @($lines | Select-String -SimpleMatch 'deadlock detected').Count
$lockWaits = @($lines | Select-String -SimpleMatch 'still waiting for').Count
$lockTimeouts = @($lines | Select-String -SimpleMatch 'canceling statement due to lock timeout').Count
$denied = @($lines | Select-String -SimpleMatch 'permission denied')
$expectedDenied = @($denied | Where-Object { $_.Line -match 'app=m2e2-expected-42501' }).Count
$unexpectedDenied = @($denied | Where-Object { $_.Line -notmatch 'app=m2e2-expected-42501' })
$unexpectedDenied | ForEach-Object { $_.Line } | Set-Content -Encoding ascii (Join-Path $Evidence 'unexpected-permission-denied.txt')

# E3-M2f-2G: the agents' OUTPUTS (results, refusals, the capability headers each build sent) join the evidence; their inputs (test-only
# credentials, the run's seal key) stay in the work directory.
$agentWork = Join-Path $WorkDir 'agent-calls'
if (Test-Path $agentWork) {
    $agentEvidence = Join-Path $Evidence 'agent-calls'
    New-Item -ItemType Directory -Force $agentEvidence | Out-Null
    Get-ChildItem $agentWork -Filter '*.out.json' | Copy-Item -Destination $agentEvidence
}

# Scrub: local paths, user profile, passwords and tokens never stay in the evidence.
$secrets = @($adminPassword, $runtimePassword, $vaultRoot, $vaultBusiness, $vaultAuthority)
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

$report = Join-Path $Evidence 'm2e2-evidence.json'
$checks = if (Test-Path $report) { Get-Content $report -Raw | ConvertFrom-Json } else { $null }
$summary = [pscustomobject]@{
    Label = $Label
    Groups = $Groups
    ExitCode = $code
    Checks = if ($checks) { $checks.checksTotal } else { 0 }
    Failed = if ($checks) { $checks.checksFailed } else { -1 }
    ExpectedGapDemonstrations = if ($checks) { $checks.expectedGapDemonstrations } else { -1 }
    M2e2RegressionChecks = if ($checks) { $checks.m2e2Regression.total } else { 0 }
    M2e2RegressionFailed = if ($checks) { $checks.m2e2Regression.failed } else { -1 }
    M2f2gChecks = if ($checks) { $checks.m2f2g.total } else { 0 }
    M2f2gFailed = if ($checks) { $checks.m2f2g.failed } else { -1 }
    M2f2gByKind = if ($checks) { ($checks.m2f2g.byKind.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value.passed)/$($_.Value.total)" }) -join '; ' } else { '' }
    M2f2gGapDemonstrations = if ($checks) { ($checks.m2f2g.gapDemonstrations.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value.asExpected)/$($_.Value.total)" }) -join '; ' } else { '' }
    ContainersRemoved = $removed
    ContainerListUnchanged = $listUnchanged
    DeadlocksInServerLog = $deadlocks
    LockTimeoutsInServerLog = $lockTimeouts
    LockWaitsLoggedInServerLog = $lockWaits
    ExpectedPermissionDenied = $expectedDenied
    UnexpectedPermissionDenied = $unexpectedDenied.Count
    ConfigEnablersBefore = $enablersBefore.Count
    ConfigEnablersAfter = $enablersAfter.Count
    EvidenceLeaksAfterScrub = $leaks
}
$summary | Format-List | Out-String | Write-Output
$summary | ConvertTo-Json | Set-Content -Encoding ascii (Join-Path $Evidence 'summary.json')
Get-Content (Join-Path $Evidence 'console.txt') -ErrorAction SilentlyContinue | Write-Output
if ($code -ne 0 -or $summary.Failed -ne 0 -or -not $removed -or -not $listUnchanged -or $deadlocks -ne 0 -or $unexpectedDenied.Count -ne 0 -or $enablersAfter.Count -ne 0 -or $leaks -ne 0) {
    Write-Output "E3-M2e-2 acceptance verification gate ($Label, groups $Groups): FAILED"
    exit 1
}
Write-Output "E3-M2e-2 acceptance verification gate ($Label, groups $Groups): all counted checks passed"
exit 0
