<#
.SYNOPSIS
  E3-M2e-2 enforcement-removal (mutation) runs. Development/Testing only. TEST-ONLY. Never modifies the repository.

.DESCRIPTION
  Source mutations: the repository HEAD is exported once with `git archive` into a scratch directory; for each mutation the target file
  of the SCRATCH COPY is backed up, its sha256 is checked equal to the repository's file, exactly one occurrence of the find text is
  replaced, the scratch Authority is built (Release), and scripts/dev/e3-m2e2-acceptance-pg-gate.ps1 runs the focused harness groups
  against that build (-NewBinaries) in fresh disposable containers. The scratch file is then restored from its backup and both its sha256
  and the repository file's sha256 are verified equal to the originals. A mutation is DETECTED when at least one of its expected
  detecting checks fails; any mutation whose expected checks all pass SURVIVED and is reported.

  Database mutations: no source change; the gate runs this tree's build with -DbMutationApply / -DbMutationRestore (SQL in the run's
  disposable database only, restored before teardown and verified by catalog digest and runtime grants).

.PARAMETER BaseWorkDir
  The work directory of an earlier gate run of this tree (reuses its frozen builds and business binaries).
#>
param(
    [Parameter(Mandatory = $true)][string]$BaseWorkDir,
    [Parameter(Mandatory = $true)][string]$Out,
    [string[]]$Only
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

$app = 'src\authority\AuthorizationAuthority.Application\Features\Content\'
$infra = 'src\authority\AuthorizationAuthority.Infrastructure\Persistence\'
$mutations = @(
    @{ Id = 'MX-01'; What = 'DK-4 ledger lookup removed (PrepareViolation sees no binding)'; File = $app + 'ContentService.cs'; Groups = 'N,C'
       Find = 'DataKeyLedgerRules.PrepareViolation(newV2Groups, existingBindings) is { } reuse'; Replace = 'DataKeyLedgerRules.PrepareViolation(newV2Groups, existingBindings.Take(0).ToList()) is { } reuse'
       Expect = @('N-01b', 'N-02', 'N-03', 'N-05', 'N-08', 'N-11', 'C-01') },
    @{ Id = 'MX-02'; What = 'DK-5 binding insert removed'; File = $app + 'ContentService.cs'; Groups = 'P'
       Find = 'scope.AddDataKeyBindings(DataKeyLedgerRules.BindingsFor(newV2Groups, state.OrganizationId, request.FormRecordId, operationId, clock.GetUtcNow()));'
       Replace = '_ = DataKeyLedgerRules.BindingsFor(newV2Groups, state.OrganizationId, request.FormRecordId, operationId, clock.GetUtcNow());'
       Expect = @('P-01', 'P-02', 'P-03', 'P-05', 'P-07', 'P-09') },
    @{ Id = 'MX-03'; What = 'DK-6 tuple comparison removed'; File = $app + 'DataKeyLedgerRules.cs'; Groups = 'N'
       Find = '|| !SameTuple(binding, group, organizationId, formRecordId) || binding.BindingItemCount != group.ItemCount)'; Replace = '|| binding.BindingItemCount < 0)'
       Expect = @('N-21', 'N-22', 'N-23', 'N-24') },
    @{ Id = 'MX-04'; What = 'P-4 carried-group verification removed'; File = $app + 'DataKeyLedgerRules.cs'; Groups = 'N'
       Find = "if (!committedOperationIds.Contains(binding.BindingContentChangeOperationId) || !SameTuple(binding, group, organizationId, formRecordId)`n                || group.ItemCount > binding.BindingItemCount)"
       Replace = 'if (binding.BindingItemCount < 0)'
       Expect = @('N-25', 'N-26', 'N-27', 'N-28') },
    @{ Id = 'MX-05'; What = 'Commit-time switch re-check removed'; File = $app + 'ContentService.cs'; Groups = 'N'
       Find = 'if (newGroups.Count > 0 && !v2Acceptance.Enabled)'; Replace = 'if (newGroups.Count < 0 && !v2Acceptance.Enabled)'
       Expect = @('N-06', 'N-07') },
    @{ Id = 'MX-06'; What = 'Prepare-time switch check removed'; File = $app + 'ContentService.cs'; Groups = 'P,N'
       Find = 'if (newV2Rows.Count > 0 && !v2Acceptance.Enabled)'; Replace = 'if (newV2Rows.Count < 0 && !v2Acceptance.Enabled)'
       Expect = @('S-14', 'P-15', 'P-16', 'N-33') },
    @{ Id = 'MX-07'; What = 'DK-8 binding tuples dropped from the ContentHeadCommitted body'; File = $app + 'ContentService.cs'; Groups = 'P,R'
       Find = 'DataKeyLedgerRules.ArchiveTuples(newBindings));'; Replace = 'DataKeyLedgerRules.ArchiveTuples(newBindings.Take(0)));'
       Expect = @('P-02', 'P-09', 'P-21', 'R-01') },
    @{ Id = 'MX-08'; What = 'v2 -> v1 downgrade guard removed'; File = $app + 'EnvelopeV2Rules.cs'; Groups = 'N'
       Find = 'throw EnvelopeRules.Refused(Reasons.Downgrade);'; Replace = '_ = EnvelopeRules.Refused(Reasons.Downgrade);'
       Expect = @('N-40') },
    @{ Id = 'MX-09'; What = 'S-4 schema/envelope tie removed'; File = $app + 'EnvelopeSchemaTie.cs'; Groups = 'N'
       Find = 'if (Violation(document) is { } reason)'; Replace = 'if (Violation(document) is { } reason && reason.Length < 0)'
       Expect = @('N-34', 'N-35', 'N-38') },
    @{ Id = 'MX-10'; What = 'L-13 ledger verification made a no-op'; File = $infra + 'AuthorityStore.cs'; Groups = 'D'
       Find = 'if (dataKeyLedgerVerified)'; Replace = 'if (dataKeyLedgerVerified || cancellationToken.CanBeCanceled || !cancellationToken.CanBeCanceled)'
       Expect = @('D-01', 'D-02', 'D-03', 'D-04', 'D-05', 'D-06', 'D-07') },
    @{ Id = 'MX-11'; What = 'policy-observation gate removed (acceptance effective without a recorded policy)'; File = $app + 'EnvelopeV2AcceptanceSwitch.cs'; Groups = 'N'
       Find = "return requested && StaticPrerequisitesHold && observedPolicyDigest is not null`n                    && string.Equals(observedPolicyDigest, PolicyDigestCore(requested), StringComparison.Ordinal);"
       Replace = 'return requested && StaticPrerequisitesHold;'
       Expect = @('S-13', 'S-14', 'N-33') },
    @{ Id = 'MX-12'; What = '23505 ledger refusal translation removed'; File = $infra + 'AuthorityStore.cs'; Groups = 'C'
       Find = 'catch (DbUpdateException ex) when (DataKeyLedgerRefusals.Translate(ex, alerts) is { } refused)'; Replace = 'catch (DbUpdateException ex) when (ex.Entries.Count < 0 && DataKeyLedgerRefusals.Translate(ex, alerts) is { } refused)'
       Expect = @('C-02') },
    @{ Id = 'MX-13'; What = 'Release B transactional floor fence made a no-op'; File = $infra + 'AuthorityStore.cs'; Groups = 'D'
       Find = 'if (!policy.GuardsContentWrites || contentFenceVerified)'; Replace = 'if (!policy.GuardsContentWrites || contentFenceVerified || cancellationToken.CanBeCanceled || !cancellationToken.CanBeCanceled)'
       Expect = @('D-08', 'D-09', 'D-10', 'D-11', 'D-12', 'D-13') },
    @{ Id = 'MD-01'; What = 'database: P-1 binding guard trigger disabled'; Groups = 'L'
       Apply = 'ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_guard'; Restore = 'ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_guard'
       Expect = @('L-02') },
    @{ Id = 'MD-02'; What = 'database: P-2 UPDATE/DELETE immutability trigger disabled'; Groups = 'L'
       Apply = 'ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_permanent'; Restore = 'ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_permanent'
       Expect = @('L-05') },
    @{ Id = 'MD-03'; What = 'database: global unique index replaced by a non-unique index'; Groups = 'L'
       Apply = 'DROP INDEX authority.uq_content_data_key_binding_identifier; CREATE INDEX uq_content_data_key_binding_identifier ON authority.content_data_key_bindings (data_key_identifier)'
       Restore = 'ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_permanent; DELETE FROM authority.content_data_key_bindings b WHERE b.ctid <> (SELECT min(c.ctid) FROM authority.content_data_key_bindings c WHERE c.data_key_identifier = b.data_key_identifier); ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_permanent; DROP INDEX authority.uq_content_data_key_binding_identifier; CREATE UNIQUE INDEX uq_content_data_key_binding_identifier ON authority.content_data_key_bindings (data_key_identifier)'
       Expect = @('L-03') },
    @{ Id = 'MD-04'; What = 'database: runtime role granted UPDATE and DELETE on the ledger'; Groups = 'L'
       Apply = 'GRANT UPDATE, DELETE ON authority.content_data_key_bindings TO authority_runtime'; Restore = 'REVOKE UPDATE, DELETE ON authority.content_data_key_bindings FROM authority_runtime'
       Expect = @('L-04') }
)

function Get-Sha([string]$path) { (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant() }

$results = New-Object System.Collections.Generic.List[object]
foreach ($m in $mutations) {
    if ($Only -and $m.Id -notin $Only) { continue }
    $runDir = Join-Path $Out $m.Id
    New-Item -ItemType Directory -Force $runDir | Out-Null
    $common = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $gateScript, '-WorkDir', (Join-Path $runDir 'work'), '-Evidence', (Join-Path $runDir 'evidence'),
        '-LegacyBuildsRoot', $BaseWorkDir, '-BusinessBinaries', (Join-Path $BaseWorkDir 'business'), '-Groups', $m.Groups, '-Label', $m.Id)
    $restoreOk = $true
    $note = ''
    if ($m.File) {
        $scratchFile = Join-Path $scratch $m.File
        $repoFile = Join-Path $repo $m.File
        $original = Get-Sha $scratchFile
        $repoOriginal = Get-Sha $repoFile
        if ($original -ne $repoOriginal) { throw "$($m.Id): the scratch copy differs from the repository file before mutation" }
        $backup = Join-Path $runDir ('backup-' + (Split-Path -Leaf $m.File))
        Copy-Item $scratchFile $backup -Force
        $text = [System.IO.File]::ReadAllText($scratchFile)
        $find = $m.Find.Replace("`r`n", "`n")
        $count = ([regex]::Matches($text, [regex]::Escape($find))).Count
        if ($count -ne 1) { throw "$($m.Id): find text occurs $count times in $($m.File)" }
        [System.IO.File]::WriteAllText($scratchFile, $text.Replace($find, $m.Replace))
        try {
            $build = & dotnet build (Join-Path $scratch 'src\authority\AuthorizationAuthority.Api\AuthorizationAuthority.Api.csproj') -c Release -nologo -v q 2>&1
            if ($LASTEXITCODE -ne 0) { $build | Select-Object -Last 20 | Set-Content (Join-Path $runDir 'build-error.txt'); throw "$($m.Id): the mutated scratch build failed" }
            $mutantBin = Join-Path $runDir 'mutant-bin'
            Copy-Item (Join-Path $scratch 'src\authority\AuthorizationAuthority.Api\bin\Release\net10.0') $mutantBin -Recurse -Force
            & powershell @common -NewBinaries $mutantBin *> (Join-Path $runDir 'gate-output.txt')
            $gateExit = $LASTEXITCODE
        }
        finally {
            Copy-Item $backup $scratchFile -Force
            $restoreOk = (Get-Sha $scratchFile) -eq $original -and (Get-Sha $repoFile) -eq $repoOriginal
            $note = "scratch restored sha256 $(if ((Get-Sha $scratchFile) -eq $original) { 'equal' } else { 'DIFFERENT' }); repository file sha256 $(if ((Get-Sha $repoFile) -eq $repoOriginal) { 'unchanged' } else { 'CHANGED' })"
        }
    }
    else {
        & powershell @common -DbMutationApply $m.Apply -DbMutationRestore $m.Restore *> (Join-Path $runDir 'gate-output.txt')
        $gateExit = $LASTEXITCODE
        $note = 'database mutation; restored in the disposable database (M-RESTORE)'
    }

    $evidenceFile = Join-Path $runDir 'evidence\m2e2-evidence.json'
    $evidence = if (Test-Path $evidenceFile) { Get-Content $evidenceFile -Raw | ConvertFrom-Json } else { $null }
    $summaryFile = Join-Path $runDir 'evidence\summary.json'
    $summary = if (Test-Path $summaryFile) { Get-Content $summaryFile -Raw | ConvertFrom-Json } else { $null }
    $failedIds = @(if ($evidence) { $evidence.checks | Where-Object { -not $_.Pass } | ForEach-Object { $_.Id } })
    $detectedBy = @($m.Expect | Where-Object { $_ -in $failedIds })
    $restoreCheck = @(if ($evidence) { $evidence.checks | Where-Object { $_.Id -eq 'M-RESTORE' } }) # @() outside: PowerShell 5.1 unwraps a one-element result
    if ($restoreCheck.Count -gt 0) { $restoreOk = $restoreOk -and [bool]$restoreCheck[0].Pass; if (-not $restoreCheck[0].Pass) { $note += '; IN-PLACE RESTORE FAILED: ' + $restoreCheck[0].Observed + ' (the disposable database was discarded with its container)' } }
    $results.Add([pscustomobject]@{
        Id = $m.Id; What = $m.What; Groups = $m.Groups; GateExit = $gateExit
        Detected = $detectedBy.Count -gt 0; DetectedBy = ($detectedBy -join ','); ExpectedDetectors = ($m.Expect -join ',')
        AllFailedChecks = ($failedIds -join ','); ContainersRemoved = if ($summary) { $summary.ContainersRemoved } else { $null }
        Restored = $restoreOk; Note = $note
    })
    Write-Output ("{0}: detected={1} by [{2}]; restored={3}; containers removed={4}" -f $m.Id, ($detectedBy.Count -gt 0), ($detectedBy -join ','), $restoreOk, $(if ($summary) { $summary.ContainersRemoved } else { 'n/a' }))
}

$results | ConvertTo-Json -Depth 4 | Set-Content -Encoding ascii (Join-Path $Out 'mutation-results.json')
$survived = @($results | Where-Object { -not $_.Detected })
$unrestored = @($results | Where-Object { -not $_.Restored })
Write-Output "mutations: $($results.Count); detected: $(@($results | Where-Object Detected).Count); survived: $($survived.Count) [$(($survived | ForEach-Object Id) -join ',')]; not restored: $($unrestored.Count)"
if ($results.Count -eq 0) { Write-Output 'no mutation selected'; exit 1 }
if ($survived.Count -gt 0 -or $unrestored.Count -gt 0) { exit 1 }
exit 0
