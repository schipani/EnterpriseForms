<#
.SYNOPSIS
  E3-M2f-2G enforcement-removal (mutation) runs on disposable PostgreSQL. Development/Testing only. TEST-ONLY. Never modifies the repository.

.DESCRIPTION
  docs/security/E3_M2F2G_VERIFICATION_RECORD.md §9. The repository HEAD is exported once with `git archive` into a scratch directory; for
  each mutation the target file of the SCRATCH COPY is backed up, its sha256 is checked equal to the repository's file, exactly one
  occurrence of the find text is replaced, the scratch Authority is built (Release), and scripts/dev/e3-m2e2-acceptance-pg-gate.ps1 runs
  the focused capability groups against that build (-NewBinaries: the in-process Authority AND the host copy) in fresh disposable
  containers. The scratch file is then restored from its backup and both its sha256 and the repository file's sha256 are verified equal
  to the originals. A mutation is DETECTED when at least one of its expected detecting checks fails; a mutation whose expected checks all
  pass SURVIVED (a stop condition of the authorization) and is reported.

.PARAMETER BaseWorkDir
  The work directory of an earlier capability gate run of this tree (reuses its frozen Authority, business and historical builds).
#>
param(
    [Parameter(Mandatory = $true)][string]$BaseWorkDir,
    [Parameter(Mandatory = $true)][string]$Out,
    [string[]]$Only,
    [switch]$DryRun,
    # E3-M2f-2G follow-up: after each selected mutation is restored, rebuild the restored scratch copy and run the SAME groups against it;
    # the declared detectors must then execute and pass (the detector is not failing for reasons unrelated to the mutation).
    [switch]$VerifyRestored
)

$ErrorActionPreference = 'Stop'
if ($Only) { $Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$gateScript = Join-Path $PSScriptRoot 'e3-m2e2-acceptance-pg-gate.ps1'
New-Item -ItemType Directory -Force $Out | Out-Null
$head = (git -C $repo rev-parse HEAD).Trim()
$scratch = Join-Path $Out 'scratch-src'
if (-not (Test-Path (Join-Path $scratch 'src'))) {
    $zip = Join-Path $Out 'head.zip'
    git -C $repo archive --format=zip -o $zip $head
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
    Expand-Archive -Path $zip -DestinationPath $scratch -Force
}

$content = 'src\authority\AuthorizationAuthority.Application\Features\Content\'
$floor = $content + 'EnvelopeCapabilityFloor.cs'
$service = $content + 'ContentService.cs'
$census = 'src\authority\AuthorizationAuthority.Application\Features\Catalogue\ProtectionReconciliationService.cs'
$builder = 'src\authority\AuthorizationAuthority.Application\Features\SearchIndex\SearchIndexBuilderService.cs'
$store = 'src\authority\AuthorizationAuthority.Infrastructure\Persistence\AuthorityStore.cs'
$api = 'src\authority\AuthorizationAuthority.Api\Composition\InternalAuthentication.cs'
$check = 'await CapabilityFloor.RequireAsync(scope, state, envelopeCapability, cancellationToken).ConfigureAwait(false);'
$workloadCheck = 'await content.CapabilityFloor.RequireAsync(scope, state, envelopeCapability, cancellationToken).ConfigureAwait(false);'
$mutations = @(
    # ---- missing capability enforcement, one entry point at a time
    @{ Id = 'MG-01'; What = 'check removed from Prepare'; File = $service; Groups = 'G'; Expect = @('G-10')
       Find = "// E3-M2f-2 (CAP-O2/O3): the capability floor next, before actor resolution, the gate, replays and snapshot binding (G-2).`n        $check"
       Replace = '// E3-M2f-2 (CAP-O2/O3): MUTATED: check removed (Prepare)' },
    @{ Id = 'MG-02'; What = 'check removed from Commit'; File = $service; Groups = 'G'; Expect = @('G-11')
       Find = "// E3-M2f-2 (CAP-O2/O3): the capability floor next, before the gate, replays, expiry/CARV aborts and staged verification (G-2).`n        $check"
       Replace = '// E3-M2f-2 (CAP-O2/O3): MUTATED: check removed (Commit)' },
    @{ Id = 'MG-03'; What = 'check removed from Verify'; File = $service; Groups = 'G'; Expect = @('G-12')
       Find = "// E3-M2f-2 (CAP-O2/O3): right after the lock, before actor resolution, pinning and verification (G-2: no false quarantine).`n        $check"
       Replace = '// E3-M2f-2 (CAP-O2/O3): MUTATED: check removed (Verify)' },
    @{ Id = 'MG-04'; What = 'check removed from batch Verify'; File = $service; Groups = 'G'; Expect = @('G-13')
       Find = "// E3-M2f-2 (CAP-O2/O3): right after the lock, before actor resolution, pinning and any record's verification (G-2).`n        $check"
       Replace = '// E3-M2f-2 (CAP-O2/O3): MUTATED: check removed (batch Verify)' },
    @{ Id = 'MG-05'; What = 'check removed from the protection census'; File = $census; Groups = 'G'; Expect = @('G-14')
       Find = "// E3-M2f-2 (CAP-O2/O3): right after the lock, before the gate and every census verification (G-2: no false quarantine).`n        $workloadCheck"
       Replace = '// E3-M2f-2 (CAP-O2/O3): MUTATED: check removed (census)' },
    @{ Id = 'MG-06'; What = 'check removed from search-index backfill'; File = $builder; Groups = 'G'; Expect = @('G-15')
       Find = "// E3-M2f-2 (CAP-O2/O3): right after the lock, before the gate, the downgrade check and every record's verification (G-2).`n        $workloadCheck"
       Replace = '// E3-M2f-2 (CAP-O2/O3): MUTATED: check removed (backfill)' },
    @{ Id = 'MG-07'; What = 'check removed from deep-sweep verification'; File = $builder; Groups = 'G'; Expect = @('G-16')
       Find = "// E3-M2f-2 (CAP-O2/O3): right after the lock, before the gate, deep-check bookkeeping and every record's verification (G-2).`n        $workloadCheck"
       Replace = '// E3-M2f-2 (CAP-O2/O3): MUTATED: check removed (deep verify)' },
    # ---- enforcement after verification
    @{ Id = 'MG-08'; What = 'Verify check moved after signed-content verification'; File = $service; Groups = 'G'; Expect = @('G-12')
       Find = "        $check`n        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);`n        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));`n`n        var snapshot = await verifier.PinAsync(scope, state, organizationLocked: true, correlationId, cancellationToken).ConfigureAwait(false);`n        var (result, quarantined) = await VerifyRecordCoreAsync(scope, state, snapshot, bound, request.FormRecordId, request.RequestedAction, request.ObservedItems, request.HistoricCommittedOperationId, null, includeEditable: true, correlationId, cancellationToken).ConfigureAwait(false);"
       Replace = "        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);`n        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));`n`n        var snapshot = await verifier.PinAsync(scope, state, organizationLocked: true, correlationId, cancellationToken).ConfigureAwait(false);`n        var (result, quarantined) = await VerifyRecordCoreAsync(scope, state, snapshot, bound, request.FormRecordId, request.RequestedAction, request.ObservedItems, request.HistoricCommittedOperationId, null, includeEditable: true, correlationId, cancellationToken).ConfigureAwait(false);`n        $check" },
    # ---- incorrect organization-floor evaluation
    @{ Id = 'MG-09'; What = 'existence query not keyed on the organization (any binding anywhere)'; File = $store; Groups = 'G'; Expect = @('G-43', 'G-44', 'G-60')
       Find = 'context.ContentDataKeyBindings.AnyAsync(b => b.OrganizationId == organizationId, cancellationToken);'
       Replace = 'context.ContentDataKeyBindings.AnyAsync(cancellationToken);' },
    @{ Id = 'MG-10'; What = 'organization floor ignored (existence result never refuses level 1)'; File = $floor; Groups = 'G'; Expect = @('G-10', 'G-12', 'G-40')
       Find = "        if (bound)`n        {`n            throw Refuse(organizationId, level);"
       Replace = "        if (bound && level > 1)`n        {`n            throw Refuse(organizationId, level);" },
    # ---- missing deployment-floor enforcement
    @{ Id = 'MG-11'; What = 'deployment floor ignored (C < D no longer refuses)'; File = $floor; Groups = 'G,GR'; Expect = @('G-46', 'G-49', 'G-50', 'GR-02', 'GR-05')
       Find = "        if (level < DeploymentFloor)`n        {`n            throw Refuse(organizationId, level);`n        }`n"
       Replace = "" },
    # ---- missing or malformed header accepted as level 2
    @{ Id = 'MG-12'; What = 'lenient parser: padded, signed, zero-prefixed or listed 2 accepted'; File = $floor; Groups = 'GH'; Expect = @('GH-06')
       Find = 'values is { Count: 1 } && string.Equals(values[0], "2", StringComparison.Ordinal) ? Current : Legacy;'
       Replace = 'values is { Count: > 0 } && values.Any(v => v is not null && v.Split('','').Any(p => p.Trim().TrimStart(''+'', ''0'') == "2")) ? Current : Legacy;' },
    @{ Id = 'MG-13'; What = 'historical-client bypass: a request without the header is treated as level 2'; File = $api; Groups = 'GH'; Expect = @('GH-02', 'GH-03', 'GH-05', 'GH-06')
       Find = "        return AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability.FromHeaderValues("
       Replace = "        if (context.Request.Headers[AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability.HeaderName].Count == 0) { return AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability.Current; }`n        return AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability.FromHeaderValues(" },
    # ---- failure to serialize against the first binding; stale negative
    @{ Id = 'MG-14'; What = 'Verify decides before the organization lock (FB-1 broken)'; File = $service; Groups = 'GC'; Expect = @('GC-01')
       Find = "        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)`n                    ?? throw AuthorityException.NotRegistered(`"organization is not registered`");`n        // E3-M2f-2 (CAP-O2/O3): right after the lock, before actor resolution, pinning and verification (G-2: no false quarantine).`n        $check"
       Replace = "        var unlocked = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)`n                    ?? throw AuthorityException.NotRegistered(`"organization is not registered`");`n        await CapabilityFloor.RequireAsync(scope, unlocked, envelopeCapability, cancellationToken).ConfigureAwait(false);`n        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)`n                    ?? throw AuthorityException.NotRegistered(`"organization is not registered`");" },
    @{ Id = 'MG-15'; What = 'negative existence result cached across requests (stale "no binding")'; File = $floor; Groups = 'GC'; Expect = @('GC-03')
       Find = "            bound = await scope.OrganizationHasDataKeyBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false);"
       Replace = "            bound = !StaleUnbound.ContainsKey(organizationId) && await scope.OrganizationHasDataKeyBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false);`n            if (!bound) { StaleUnbound[organizationId] = true; }" ;
       Find2 = '    private readonly IEnvelopeCapabilityObserver? observer;'
       Replace2 = "    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> StaleUnbound = new();`n    private readonly IEnvelopeCapabilityObserver? observer;" },
    # ---- unauthorized ledger reads
    @{ Id = 'MG-16'; What = 'existence check materializes binding rows (every column read)'; File = $store; Groups = 'G'; Expect = @('G-60')
       Find = 'context.ContentDataKeyBindings.AnyAsync(b => b.OrganizationId == organizationId, cancellationToken);'
       Replace = 'context.ContentDataKeyBindings.Where(b => b.OrganizationId == organizationId).ToListAsync(cancellationToken).ContinueWith(t => t.Result.Count > 0, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);' },
    @{ Id = 'MG-17'; What = 'level-2 short-circuit removed (level 2 reads the ledger)'; File = $floor; Groups = 'G'; Expect = @('G-10c', 'G-12c', 'G-42')
       Find = 'if (level >= Math.Max(DeploymentFloor, ClientEnvelopeCapability.Current.Level))'
       Replace = 'if (level >= Math.Max(DeploymentFloor, ClientEnvelopeCapability.Current.Level) && level > 2)' },
    # MG-17 aborts group G's own setup (the current build's Commit is refused once a binding exists), so no designated G check runs.
    # MG-17b is the same mutation against groups whose setup does not depend on G; detectors declared before it was run.
    # MG-17c (E3-M2f-2G follow-up, OA-M2f2G-03/04): the same mutation against the dedicated, self-contained detector group GS (its setup needs
    # no level-2 Commit in a bound organization), with group G alongside to show that its setup failure is now recorded, not an abort.
    @{ Id = 'MG-17c'; What = 'level-2 short-circuit removed (level 2 reads the ledger), dedicated detector group GS'; File = $floor; Groups = 'G,GS'; Expect = @('GS-01', 'GS-02')
       Find = 'if (level >= Math.Max(DeploymentFloor, ClientEnvelopeCapability.Current.Level))'
       Replace = 'if (level >= Math.Max(DeploymentFloor, ClientEnvelopeCapability.Current.Level) && level > 2)' },
    @{ Id = 'MG-17b'; What = 'level-2 short-circuit removed (level 2 reads the ledger), groups P and GC'; File = $floor; Groups = 'P,GC'; Expect = @('P-01', 'GC-03', 'GC-06')
       Find = 'if (level >= Math.Max(DeploymentFloor, ClientEnvelopeCapability.Current.Level))'
       Replace = 'if (level >= Math.Max(DeploymentFloor, ClientEnvelopeCapability.Current.Level) && level > 2)' },
    # ---- incorrect refusal rollback
    @{ Id = 'MG-18'; What = 'Commit refusal journals an incident and COMMITS the refused transaction'; File = $service; Groups = 'G'; Expect = @('G-11')
       Find = "// E3-M2f-2 (CAP-O2/O3): the capability floor next, before the gate, replays, expiry/CARV aborts and staged verification (G-2).`n        $check"
       Replace = "try { $check } catch (AuthorityException) { AuthorizationAuthority.Application.Features.Journal.AuditJournal.Append(scope, state, `"IntegrityVerificationFailed`", new { refused = true }, clock); await scope.CommitAsync(cancellationToken).ConfigureAwait(false); throw; }" },
    # ---- deployment configuration
    @{ Id = 'MG-19'; What = 'invalid deployment floor accepted as 1 (lenient configuration parse)'; File = $floor; Groups = 'GH'; Expect = @('GH-09')
       Find = '        _ => throw new InvalidOperationException(ConfigurationKey + " must be absent, \"1\" or \"2\"; the Authorization Authority refuses to start."),'
       Replace = '        _ => 1,' }
)

function Get-Sha([string]$path) { (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant() }

if ($DryRun) {
    # Read-only: every anchor must occur exactly once in the REPOSITORY file; nothing is written or built.
    $bad = 0
    foreach ($m in $mutations) {
        if ($Only -and $m.Id -notin $Only) { continue }
        $text = [System.IO.File]::ReadAllText((Join-Path $repo $m.File))
        foreach ($f in @($m.Find, $m.Find2) | Where-Object { $_ }) {
            $count = ([regex]::Matches($text, [regex]::Escape($f.Replace("`r`n", "`n")))).Count
            Write-Output ("{0}: {1} anchor occurrence(s) in {2}" -f $m.Id, $count, $m.File)
            if ($count -ne 1) { $bad++ }
        }
    }
    exit $(if ($bad -eq 0) { 0 } else { 1 })
}

function Set-Mutation([string]$path, [string]$find, [string]$replace, [string]$id) {
    $text = [System.IO.File]::ReadAllText($path)
    $normalized = $find.Replace("`r`n", "`n")
    $count = ([regex]::Matches($text, [regex]::Escape($normalized))).Count
    if ($count -ne 1) { throw "${id}: find text occurs $count times in $path" }
    [System.IO.File]::WriteAllText($path, $text.Replace($normalized, $replace))
}

# Every scratch build is a full (--no-incremental) build: a restored file keeps its backup's old timestamp, so an incremental build could
# skip recompiling it and carry an earlier mutation into a later run (found in the first E3-M2f-2G attempt; that attempt was discarded).
$authorityProject = Join-Path $scratch 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj'
$authorityOut = Join-Path $scratch 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0'
function Get-AuthorityHashes {
    $map = [ordered]@{}
    Get-ChildItem $authorityOut -Filter 'AuthorizationAuthority.*.dll' | Sort-Object Name | ForEach-Object { $map[$_.Name] = Get-Sha $_.FullName }
    return $map
}
function Invoke-ScratchBuild([string]$runDir) {
    $build = & dotnet build $authorityProject -c Release -nologo -v q --no-incremental 2>&1
    if ($LASTEXITCODE -ne 0) { $build | Select-Object -Last 20 | Set-Content (Join-Path $runDir 'build-error.txt'); return $false }
    return $true
}
New-Item -ItemType Directory -Force (Join-Path $Out 'clean') | Out-Null
if (-not (Invoke-ScratchBuild (Join-Path $Out 'clean'))) { throw 'the clean scratch build failed' }
$cleanHashes = Get-AuthorityHashes

$results = New-Object System.Collections.Generic.List[object]
foreach ($m in $mutations) {
    if ($Only -and $m.Id -notin $Only) { continue }
    $runDir = Join-Path $Out $m.Id
    New-Item -ItemType Directory -Force $runDir | Out-Null
    $common = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $gateScript, '-WorkDir', (Join-Path $runDir 'work'), '-Evidence', (Join-Path $runDir 'evidence'),
        '-LegacyBuildsRoot', $BaseWorkDir, '-BusinessBinaries', (Join-Path $BaseWorkDir 'business'), '-Groups', $m.Groups, '-Label', $m.Id)
    $scratchFile = Join-Path $scratch $m.File
    $repoFile = Join-Path $repo $m.File
    $original = Get-Sha $scratchFile
    $repoOriginal = Get-Sha $repoFile
    if ($original -ne $repoOriginal) { throw "$($m.Id): the scratch copy differs from the repository file before mutation" }
    $backup = Join-Path $runDir ('backup-' + (Split-Path -Leaf $m.File))
    Copy-Item $scratchFile $backup -Force
    $gateExit = $null
    $mutantHashes = $null
    $buildFailed = $false
    $differing = @()
    try {
        Set-Mutation $scratchFile $m.Find $m.Replace $m.Id
        if ($m.Find2) { Set-Mutation $scratchFile $m.Find2 $m.Replace2 ($m.Id + '/2') }
        if (-not (Invoke-ScratchBuild $runDir)) {
            # Recorded (an undetected mutation cannot hide behind a build failure: it is reported as not run and fails the driver).
            $buildFailed = $true
        }
        else {
            $mutantHashes = Get-AuthorityHashes
            $differing = @($mutantHashes.Keys | Where-Object { $mutantHashes[$_] -ne $cleanHashes[$_] })
            $mutantBin = Join-Path $runDir 'mutant-bin'
            Copy-Item (Join-Path $scratch 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $mutantBin -Recurse -Force
            & powershell @common -NewBinaries $mutantBin *> (Join-Path $runDir 'gate-output.txt')
            $gateExit = $LASTEXITCODE
        }
    }
    finally {
        Copy-Item $backup $scratchFile -Force
        (Get-Item $scratchFile).LastWriteTime = Get-Date
        $restoredOk = (Get-Sha $scratchFile) -eq $original -and (Get-Sha $repoFile) -eq $repoOriginal
        $note = "scratch restored sha256 $(if ((Get-Sha $scratchFile) -eq $original) { 'equal' } else { 'DIFFERENT' }); repository file sha256 $(if ((Get-Sha $repoFile) -eq $repoOriginal) { 'unchanged' } else { 'CHANGED' })"
    }

    $evidenceFile = Join-Path $runDir 'evidence\m2e2-evidence.json'
    $evidence = if (Test-Path $evidenceFile) { Get-Content $evidenceFile -Raw | ConvertFrom-Json } else { $null }
    $summaryFile = Join-Path $runDir 'evidence\summary.json'
    $summary = if (Test-Path $summaryFile) { Get-Content $summaryFile -Raw | ConvertFrom-Json } else { $null }
    $failedIds = @(if ($evidence) { $evidence.checks | Where-Object { -not $_.Pass } | ForEach-Object { $_.Id } })
    $detectedBy = @($m.Expect | Where-Object { $_ -in $failedIds })
    # E3-M2f-2G follow-up classification: DETECTED only when a DECLARED detector executed and failed; INCONCLUSIVE when no declared
    # detector executed (for example a setup failure or a harness abort); SURVIVED when the declared detectors executed and all passed.
    $executed = @(if ($evidence) { $m.Expect | Where-Object { $id = $_; @($evidence.checks | Where-Object { $_.Id -eq $id }).Count -gt 0 } })
    $classification = if ($buildFailed) { 'NOT_BUILT' } elseif ($detectedBy.Count -gt 0) { 'DETECTED' } elseif ($executed.Count -eq 0) { 'INCONCLUSIVE' } else { 'SURVIVED' }
    $setupFailures = @(if ($evidence) { $evidence.checks | Where-Object { -not $_.Pass -and ($_.Kind -eq 'Setup' -or $_.Kind -eq 'Harness' -or $_.Kind -eq 'Isolation') } | ForEach-Object { $_.Id } })
    $assertionFailures = @($failedIds | Where-Object { $_ -notin $setupFailures })
    # The gate records the sha256 of the Authority assemblies it actually tested (new-sha256.txt): they must be the mutant build's.
    $testedFile = Join-Path $runDir 'evidence\new-sha256.txt'
    $tested = @{}
    if (Test-Path $testedFile) { Get-Content $testedFile | ForEach-Object { $parts = $_ -split ' \*'; if ($parts.Count -eq 2) { $tested[$parts[1]] = $parts[0] } } }
    $testedIsMutant = -not $buildFailed -and $tested.Count -gt 0 -and @($mutantHashes.Keys | Where-Object { $tested[$_] -ne $mutantHashes[$_] }).Count -eq 0
    $restoredRun = $null
    if ($VerifyRestored -and -not $buildFailed) {
        $restoredDir = Join-Path $runDir 'restored'
        New-Item -ItemType Directory -Force $restoredDir | Out-Null
        $restoredBuilt = Invoke-ScratchBuild $restoredDir
        $restoredHashes = Get-AuthorityHashes
        $reproducible = @('AuthorizationAuthority.Application.dll', 'AuthorizationAuthority.Contracts.dll', 'AuthorizationAuthority.Domain.dll', 'AuthorizationAuthority.Infrastructure.dll')
        $restoredEqualsClean = $restoredBuilt -and @($reproducible | Where-Object { $restoredHashes[$_] -ne $cleanHashes[$_] }).Count -eq 0
        $restoredBin = Join-Path $restoredDir 'bin'
        Copy-Item $authorityOut $restoredBin -Recurse -Force
        $restoredCommon = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $gateScript, '-WorkDir', (Join-Path $restoredDir 'work'), '-Evidence', (Join-Path $restoredDir 'evidence'),
            '-LegacyBuildsRoot', $BaseWorkDir, '-BusinessBinaries', (Join-Path $BaseWorkDir 'business'), '-Groups', $m.Groups, '-Label', ($m.Id + '-restored'))
        & powershell @restoredCommon -NewBinaries $restoredBin *> (Join-Path $restoredDir 'gate-output.txt')
        $restoredExit = $LASTEXITCODE
        $rEvidenceFile = Join-Path $restoredDir 'evidence\m2e2-evidence.json'
        $rEvidence = if (Test-Path $rEvidenceFile) { Get-Content $rEvidenceFile -Raw | ConvertFrom-Json } else { $null }
        $rSummaryFile = Join-Path $restoredDir 'evidence\summary.json'
        $rSummary = if (Test-Path $rSummaryFile) { Get-Content $rSummaryFile -Raw | ConvertFrom-Json } else { $null }
        $detectorsPassed = @(if ($rEvidence) { $m.Expect | Where-Object { $id = $_; @($rEvidence.checks | Where-Object { $_.Id -eq $id -and $_.Pass }).Count -eq 1 } })
        $rFailed = @(if ($rEvidence) { $rEvidence.checks | Where-Object { -not $_.Pass } | ForEach-Object { $_.Id } })
        $rTested = @{}
        $rTestedFile = Join-Path $restoredDir 'evidence\new-sha256.txt'
        if (Test-Path $rTestedFile) { Get-Content $rTestedFile | ForEach-Object { $parts = $_ -split ' \*'; if ($parts.Count -eq 2) { $rTested[$parts[1]] = $parts[0] } } }
        $restoredRun = [pscustomobject]@{
            GateExit = $restoredExit; DeclaredDetectorsPassed = ($detectorsPassed -join ','); AllDeclaredDetectorsPassed = $detectorsPassed.Count -eq $m.Expect.Count
            AllFailedChecks = ($rFailed -join ','); ReproducibleAssembliesEqualClean = $restoredEqualsClean
            TestedIsRestoredBuild = $rTested.Count -gt 0 -and @($restoredHashes.Keys | Where-Object { $rTested[$_] -ne $restoredHashes[$_] }).Count -eq 0
            ContainersRemoved = if ($rSummary) { $rSummary.ContainersRemoved } else { $null }
            RestoredApplicationSha256 = $restoredHashes['AuthorizationAuthority.Application.dll']
        }
    }

    $results.Add([pscustomobject]@{
        Id = $m.Id; What = $m.What; Groups = $m.Groups; GateExit = $gateExit
        Detected = $detectedBy.Count -gt 0; DetectedBy = ($detectedBy -join ','); ExpectedDetectors = ($m.Expect -join ',')
        Classification = $classification; DeclaredDetectorsExecuted = ($executed -join ','); SetupFailures = ($setupFailures -join ','); AssertionFailures = ($assertionFailures -join ',')
        AllFailedChecks = ($failedIds -join ','); ContainersRemoved = if ($summary) { $summary.ContainersRemoved } else { $null }
        Restored = $restoredOk; Note = $note; AssembliesDifferingFromCleanBuild = ($differing -join ','); BuildFailed = $buildFailed
        TestedAssembliesAreTheMutantBuild = $testedIsMutant; CleanApplicationSha256 = $cleanHashes['AuthorizationAuthority.Application.dll']
        MutantApplicationSha256 = if ($mutantHashes) { $mutantHashes['AuthorizationAuthority.Application.dll'] } else { $null }; RestoredBaselineRun = $restoredRun
    })
    $results[$results.Count - 1] | ConvertTo-Json -Depth 4 | Set-Content -Encoding ascii (Join-Path $runDir 'result.json')
    Write-Output ("{0}: {1}; detected={2} by [{3}]; declared executed [{4}]; setup failures [{5}]; assertion failures [{6}]; tested=mutant {7}; restored={8}; containers removed={9}{10}" -f $m.Id, $classification, ($detectedBy.Count -gt 0), ($detectedBy -join ','), ($executed -join ','), ($setupFailures -join ','), ($assertionFailures -join ','), $testedIsMutant, $restoredOk, $(if ($summary) { $summary.ContainersRemoved } else { 'n/a' }),
        $(if ($restoredRun) { "; restored baseline: detectors passed [$($restoredRun.DeclaredDetectorsPassed)], all failed [$($restoredRun.AllFailedChecks)], tested=restored $($restoredRun.TestedIsRestoredBuild), reproducible assemblies = clean $($restoredRun.ReproducibleAssembliesEqualClean)" } else { '' }))
}

# After every mutation is restored, a full rebuild of the scratch copy must reproduce the clean assemblies byte for byte.
$finalOk = Invoke-ScratchBuild (Join-Path $Out 'clean')
$finalHashes = Get-AuthorityHashes
$finalEqual = $finalOk -and @($finalHashes.Keys | Where-Object { $finalHashes[$_] -ne $cleanHashes[$_] }).Count -eq 0
Write-Output "final clean rebuild reproduces the initial clean assemblies: $finalEqual"
$results | ConvertTo-Json -Depth 4 | Set-Content -Encoding ascii (Join-Path $Out 'mutation-results.json')
"clean assemblies: " + (($cleanHashes.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join '; ') + "; final rebuild equal: $finalEqual" | Set-Content -Encoding ascii (Join-Path $Out 'clean-build.txt')
"repository HEAD $head; scratch copy from git archive; mutations applied only to the scratch copy" | Set-Content -Encoding ascii (Join-Path $Out 'provenance.txt')
# E3-M2f-2G follow-up: report by classification (DETECTED / INCONCLUSIVE / SURVIVED / NOT_BUILT); only DETECTED is a detection.
$byClass = @($results | Group-Object Classification | ForEach-Object { "$($_.Name) $($_.Count) [$(($_.Group | ForEach-Object Id) -join ',')]" }) -join '; '
$notDetected = @($results | Where-Object { $_.Classification -ne 'DETECTED' })
$unrestored = @($results | Where-Object { -not $_.Restored })
$restoredBad = @($results | Where-Object { $_.RestoredBaselineRun -and -not $_.RestoredBaselineRun.AllDeclaredDetectorsPassed })
Write-Output "mutations: $($results.Count); by classification: $byClass; not restored: $($unrestored.Count); restored-baseline detector failures: $($restoredBad.Count)"
if ($results.Count -eq 0) { Write-Output 'no mutation selected'; exit 1 }
if ($notDetected.Count -gt 0 -or $unrestored.Count -gt 0 -or $restoredBad.Count -gt 0 -or -not $finalEqual) { exit 1 }
exit 0
