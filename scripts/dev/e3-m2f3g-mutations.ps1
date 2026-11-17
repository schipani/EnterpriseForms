<#
.SYNOPSIS
  E3-M2f-3G PostgreSQL-backed enforcement-removal (mutation) runs for the capability-refusal evidence recorder. Development/Testing only.

.DESCRIPTION
  For each mutation: verify the anchor occurs exactly once, apply ONE edit to this working tree's product source, run the E3-M2f-3G gate
  (scripts/dev/e3-m2f3g-evidence-pg-gate.ps1) on the groups that exercise it (fresh disposable container each time), classify the result,
  restore the exact original bytes and verify them by SHA-256. After all mutations the restored tree is run once more on the union of the
  groups (restored baseline). A mutation is DETECTED when the gate reports at least one failed check; SURVIVED when every check passed;
  INCONCLUSIVE when the gate did not produce a report. Equivalent or unexecuted mutations are reported as such, never as detected.

.EXAMPLE
  powershell -File scripts\dev\e3-m2f3g-mutations.ps1 -Out C:\out\m2f3g-mutations -LegacyBuildsRoot C:\temp\g3-explore-work
#>
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$LegacyBuildsRoot,
    [string[]]$Only,
    [int]$BigRows = 200000,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$gateScript = Join-Path $PSScriptRoot 'e3-m2f3g-evidence-pg-gate.ps1'
New-Item -ItemType Directory -Force $Out | Out-Null
$index = 'src\authority\AuthorizationAuthority.Infrastructure\Persistence\CapabilityRefusalEvidenceIndex.cs'
$store = 'src\authority\AuthorizationAuthority.Infrastructure\Persistence\AuthorityStore.cs'
$recorder = 'src\authority\AuthorizationAuthority.Application\Features\Content\CapabilityRefusalEvidenceRecorder.cs'
$lookup = 'src\authority\AuthorizationAuthority.Application\Features\Content\CapabilityRefusalEvidenceLookup.cs'

$mutations = @(
    @{ Id = 'PM-01'; What = 't_db from transaction_timestamp() instead of clock_timestamp() after the lock (M2F3-71)'; File = $index; Groups = 'T'
       Anchor = 'public const string DatabaseClockSql = "SELECT pg_catalog.clock_timestamp()";'; Replace = 'public const string DatabaseClockSql = "SELECT pg_catalog.transaction_timestamp()";' },
    @{ Id = 'PM-02'; What = 'no T2 lock-wait bound (lock_timeout 0 = wait indefinitely; only the 2 s budget remains)'; File = $index; Groups = 'T'
       Anchor = 'public const string EvidenceLockTimeoutSql = "SET LOCAL lock_timeout = ''1s''";'; Replace = 'public const string EvidenceLockTimeoutSql = "SET LOCAL lock_timeout = 0";' },
    @{ Id = 'PM-03'; What = 'no T2 statement bound (statement_timeout 0)'; File = $index; Groups = 'T'
       Anchor = 'public const string EvidenceStatementTimeoutSql = "SET LOCAL statement_timeout = ''500ms''";'; Replace = 'public const string EvidenceStatementTimeoutSql = "SET LOCAL statement_timeout = 0";' },
    @{ Id = 'PM-04'; What = 'lookup not restricted to evidence entries (event-kind predicate dropped: IX-B no longer matches; unrelated audit entries read)'; File = $index; Groups = 'L'
       Anchor = '+ "'' AND journal_event_kind = ''" + CapabilityRefusalEvidence.EventKind + "'' ORDER BY journal_stream_revision DESC LIMIT 8";'; Replace = '+ "'' ORDER BY journal_stream_revision DESC LIMIT 8";' },
    @{ Id = 'PM-05'; What = 'catalogue check bypassed (IX-B always reported usable)'; File = $store; Groups = 'L'
       Anchor = '(await CapabilityRefusalEvidenceIndex.InspectAsync(EvidenceConnection(), cancellationToken).ConfigureAwait(false)).State == CapabilityRefusalEvidenceIndexState.ValidMatching;'
       Replace = '(await CapabilityRefusalEvidenceIndex.InspectAsync(EvidenceConnection(), cancellationToken).ConfigureAwait(false)).State != (CapabilityRefusalEvidenceIndexState)(-1);' },
    @{ Id = 'PM-06'; What = 'T2 reads the organization row without the row lock (no FOR UPDATE, untracked)'; File = $recorder; Groups = 'T'
       Anchor = 'var locked = await scope.LockOrganizationAsync(key.OrganizationId, token).ConfigureAwait(false);'; Replace = 'var locked = await scope.ReadOrganizationAsync(key.OrganizationId, token).ConfigureAwait(false);' },
    @{ Id = 'PM-07'; What = 'the durable lookup decision ignored (always append)'; File = $recorder; Groups = 'T'
       Anchor = 'var (verdict, reason) = CapabilityRefusalEvidenceLookup.Decide(rows, key, databaseTime.Value);'; Replace = 'var (verdict, reason) = (CapabilityRefusalEvidenceVerdict.Append, CapabilityRefusalEvidenceReason.None);' },
    @{ Id = 'MZ-22'; What = 'E3-M2f-3b MZ-22 re-run on PostgreSQL: "eight rows, none older than the bucket => inconclusive" disabled (expected equivalent, F-09)'; File = $lookup; Groups = 'F'
       Anchor = 'if (entries.Count >= CapabilityRefusalEvidence.LookupLimit && !entries.Any(e => e.Bucket < bucket))'; Replace = 'if (entries.Count > 99 && !entries.Any(e => e.Bucket < bucket))' }
)
if ($Only) { $mutations = @($mutations | Where-Object { $Only -contains $_.Id }) }

function Get-Sha([string]$path) { (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant() }

# Anchors first (dry run checks every anchor without writing).
foreach ($m in $mutations) {
    $text = [System.IO.File]::ReadAllText((Join-Path $repo $m.File))
    $count = ([regex]::Matches($text, [regex]::Escape($m.Anchor))).Count
    if ($count -ne 1) { throw "$($m.Id): anchor occurs $count times in $($m.File)" }
}
if ($DryRun) { Write-Output "dry run: $($mutations.Count) anchors verified"; exit 0 }

$results = New-Object System.Collections.Generic.List[object]
foreach ($m in $mutations) {
    $path = Join-Path $repo $m.File
    $original = [System.IO.File]::ReadAllBytes($path)
    $originalSha = Get-Sha $path
    $evidence = Join-Path $Out $m.Id
    $classification = 'INCONCLUSIVE'
    $failed = @()
    try {
        $text = [System.Text.Encoding]::UTF8.GetString($original)
        [System.IO.File]::WriteAllText($path, $text.Replace($m.Anchor, $m.Replace), (New-Object System.Text.UTF8Encoding($false)))
        $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $gateScript, '-Evidence', $evidence, '-Groups', $m.Groups, '-BigRows', "$BigRows", '-RunLabel', "mutation-$($m.Id)")
        if ($LegacyBuildsRoot) { $args += @('-LegacyBuildsRoot', $LegacyBuildsRoot) }
        & powershell @args *> (Join-Path $Out "$($m.Id).console.txt")
        $report = Join-Path $evidence 'm2f3g-evidence.json'
        if (Test-Path $report) {
            $json = Get-Content $report -Raw | ConvertFrom-Json
            $failed = @($json.checks | Where-Object { -not $_.Pass } | ForEach-Object { $_.Id })
            $classification = if ($failed.Count -gt 0) { 'DETECTED' } else { 'SURVIVED' }
        }
    }
    finally {
        [System.IO.File]::WriteAllBytes($path, $original)
    }
    $restored = (Get-Sha $path) -eq $originalSha
    if (-not $restored) { throw "$($m.Id): restoration failed for $($m.File)" }
    $results.Add([pscustomobject]@{ Id = $m.Id; What = $m.What; File = $m.File; Groups = $m.Groups; Classification = $classification; FailedChecks = ($failed -join ','); Restored = $restored; OriginalSha256 = $originalSha })
    Write-Output "$($m.Id): $classification [$($failed -join ',')] restored=$restored"
}

# Restored baseline: the union of the groups on the restored tree must pass.
$union = (@($mutations | ForEach-Object { $_.Groups -split ',' }) | Sort-Object -Unique) -join ','
$baselineEvidence = Join-Path $Out 'restored-baseline'
$args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $gateScript, '-Evidence', $baselineEvidence, '-Groups', $union, '-BigRows', "$BigRows", '-RunLabel', 'mutation-restored-baseline')
if ($LegacyBuildsRoot) { $args += @('-LegacyBuildsRoot', $LegacyBuildsRoot) }
& powershell @args *> (Join-Path $Out 'restored-baseline.console.txt')
$baselineCode = $LASTEXITCODE
$baselineReport = Join-Path $baselineEvidence 'm2f3g-evidence.json'
$baseline = if (Test-Path $baselineReport) { Get-Content $baselineReport -Raw | ConvertFrom-Json } else { $null }
$summary = [pscustomobject]@{
    Mutations = $results
    Total = $results.Count
    Detected = @($results | Where-Object { $_.Classification -eq 'DETECTED' }).Count
    Survived = @($results | Where-Object { $_.Classification -eq 'SURVIVED' }).Count
    Inconclusive = @($results | Where-Object { $_.Classification -eq 'INCONCLUSIVE' }).Count
    AllRestored = -not ($results | Where-Object { -not $_.Restored })
    RestoredBaseline = [pscustomobject]@{ Groups = $union; ExitCode = $baselineCode; Checks = if ($baseline) { $baseline.checksTotal } else { 0 }; Failed = if ($baseline) { $baseline.checksFailed } else { -1 } }
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $Out 'mutations.json')
$summary | Format-List | Out-String | Write-Output
