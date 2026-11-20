"""SEC-BL-01 WP-7 / WP-7A mutation driver, contract §13.3 MV-01..MV-18, integrated cross-component mutations and the WP-7A MV-7A-* set (Development/Testing only;
record docs/security/SEC_BL_01_WP6_WP7_VERIFICATION_RECORD.md).

Same procedure as scripts/dev/g08-mutations.py: operates ONLY on a scratch clone (--clone), verifies every anchor occurs exactly once,
keeps the original bytes, applies the edit, builds --no-incremental, runs the declared check, classifies DETECTED / SURVIVED /
INCONCLUSIVE, restores the original bytes in a finally block and verifies SHA-256. Kinds of check:
  ip    the Authority test project, declared in-process classes;
  arch  the architecture test project, JournalVerificationBoundaryTests;
  pg    scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1 of the clone with one -MethodFilter pattern (fresh disposable PostgreSQL 18 + Azurite).
Where a contract mutation is not expressible as written, the closest expressible variant is named (a/b) and reported as such.
"""
import argparse, hashlib, json, os, re, shutil, subprocess, time

ap = argparse.ArgumentParser()
ap.add_argument('--clone', required=True)
ap.add_argument('--out', required=True)
ap.add_argument('--only', required=True)
ap.add_argument('--dry-run', action='store_true')
a = ap.parse_args()
clone = a.clone
dotnet = shutil.which('dotnet')
pwsh = shutil.which('powershell') or shutil.which('pwsh')

APP = 'src/authority/AuthorizationAuthority.Application/'
COMPARER = APP + 'Features/Journal/Reconciliation/JournalStreamComparer.cs'
VERIFIER = APP + 'Features/Journal/Reconciliation/JournalVerifier.cs'
BASELINE = APP + 'Features/Journal/Reconciliation/JournalBaselineService.cs'
RULES = APP + 'Features/Journal/Verification/JournalChainRules.cs'
CHECKPOINTS = APP + 'Features/Journal/Verification/JournalVerificationCheckpoints.cs'
DPLUS = APP + 'Features/Journal/AuditDeliveryEnforcement.cs'
PROJECTION = APP + 'Features/Projection/ProjectionProcessor.cs'
RECEIPTS = APP + 'Features/Journal/TrustedJournalReceipts.cs'
GATE = APP + 'Common/OrganizationGate.cs'
READER = 'src/authority/AuthorizationAuthority.Infrastructure/Persistence/JournalVerificationReader.cs'
PROJECT = 'tests/authority/AuthorizationAuthority.Tests/AuthorizationAuthority.Tests.csproj'
EXE = 'tests/authority/AuthorizationAuthority.Tests/bin/Debug/net10.0/AuthorizationAuthority.Tests.exe'
ARCH_PROJECT = 'tests/architecture/EnterpriseForms.ArchitectureTests/EnterpriseForms.ArchitectureTests.csproj'
ARCH_EXE = 'tests/architecture/EnterpriseForms.ArchitectureTests/bin/Debug/net10.0/EnterpriseForms.ArchitectureTests.exe'
NS = 'AuthorizationAuthority.Tests.'
CLASSES = ['JournalVerifierTests', 'JournalBaselineServiceTests', 'VerificationCheckpointTests', 'JournalChainRulesTests', 'JournalVerificationRaceTests',
           'CapabilityRefusalEvidenceRemovalTests', 'StopMarkerVerifierTests', 'JournalAcceptedExceptionTests', 'DPlusCommitmentVerificationTests',
           'DPlusDeliveryEnforcementAuditTests', 'DPlusDeliveryEnforcementContentTests', 'DPlusDeliveryEnforcementAuthorizationTests', 'ArchiveProcessorTests',
           'JournalEnvelopeKnownAnswerTests', 'TrustedJournalReceiptTests', 'AuthorizationStreamGateDPlusTests', 'ContentStreamDPlusReadabilityTests', 'ProjectionIntegrityTests',
           'TrustedJournalReceiptValidityTests']

M = {}


def mut(id, what, edits, kind='ip', method=None):
    M[id] = dict(id=id, what=what, edits=edits, kind=kind, method=method)


mut('BASELINE', 'no edit', [])
mut('BASELINE-ARCH', 'no edit (architecture)', [], 'arch')
mut('BASELINE-PG', 'no edit (PostgreSQL gate, read-only tests)', [], 'pg', '*ReadOnly*')
mut('BASELINE-PG-F1', 'no edit (PostgreSQL gate, WP-7A F-1 tests)', [], 'pg', '*WP7AF1*')
mut('BASELINE-PG-J2', 'no edit (PostgreSQL gate, WP-7A J2 tests)', [], 'pg', '*WP7AJ2*')
mut('BASELINE-PG-OIDR7', 'no edit (PostgreSQL gate, WP-7A OI-DR-7 tests)', [], 'pg', '*WP7AOiDr7*')
mut('RESTORED', 'no edit (after every mutation was restored)', [])

mut('MV-01', 'VF-6: drop the anchor at v (incremental first-entry anchor and the full-walk row-at-v comparison)', [
    (COMPARER, 'var anchorFirst = first == request.WalkFromRevision && trusted > 0 && request.WalkFromRevision == trusted ? request.TrustedDigest : null;', 'var anchorFirst = (byte[]?)null;'),
    (COMPARER, 'if (row is not null && !SHA256.HashData(Encoding.UTF8.GetBytes(row.BodyJson)).AsSpan().SequenceEqual(request.TrustedDigest))', 'if (row is not null && row.StreamRevision < 0)'),
])
mut('MV-02', 'VF-7: drop the row-versus-archive byte comparison', [
    (COMPARER, 'if (row is not null && !Encoding.UTF8.GetBytes(row.BodyJson).AsSpan().SequenceEqual(bytes))', 'if (row is not null && bytes.Length < 0)'),
])
mut('MV-03', 'VF-10: an Anchored row above the archive head is trusted as archived (delivery state as evidence)', [
    (COMPARER, 'foreach (var row in rows.Where(r => r.StreamRevision > archiveHead && !listedRevisions.Contains(r.StreamRevision)))',
     'foreach (var row in rows.Where(r => r.StreamRevision > archiveHead && !listedRevisions.Contains(r.StreamRevision) && !string.Equals(r.ArchiveDeliveryState, ArchiveDeliveryStates.Anchored, StringComparison.Ordinal)))'),
])
mut('MV-04a', 'AE-4: skip the archive gap check', [
    (COMPARER, 'result.Add(JournalFindingCodes.ArchiveDiscontinuity, null, gapFirst, gapLast, [], "archived revisions are missing below the highest archived revision");', '_ = (gapFirst, gapLast);'),
])
mut('MV-04b', 'R-6: skip the row gap check (interior and trailing)', [
    (RULES, '            if (revision != expectedNext)\n            {\n                findings.Add(new JournalFinding(expectedNext, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.RevisionGap,',
     '            if (revision != expectedNext && revision < 0)\n            {\n                findings.Add(new JournalFinding(expectedNext, JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.RevisionGap,'),
    (RULES, 'if (expectation.LastRevision is { } expectedLast && expectedNext <= expectedLast)', 'if (expectation.LastRevision is { } expectedLast && expectedNext <= expectedLast && expectedNext < 0)'),
])
mut('MV-05', 'AE-2: the archive head determined by probing revisions up to the AuthorityDb head instead of enumeration', [
    (COMPARER, '''        var listedRevisions = new HashSet<long>(listing.Objects
            .Where(o => o.IsCanonicalEntryKey && o.StreamRevision is not null && ArchiveCompleteness.KnownStreams.Contains(o.StreamName!, StringComparer.Ordinal))
            .Select(o => o.StreamRevision!.Value));''', '''        var listedRevisions = new HashSet<long>();
        for (var probe = bounded ? trusted + 1 : 1; probe <= databaseHead; probe++)
        {
            if ((await archive.ReadEntryAsync(org, stream, probe, cancellationToken).ConfigureAwait(false)).Status == StoredObjectStatus.Present)
            {
                listedRevisions.Add(probe);
            }
        }'''),
])
mut('MV-06', 'VF-11: an unavailable archive is treated as verified (its operational finding and inconclusive reasons dropped)', [
    (VERIFIER, '        // A confirmed security finding is never masked as Inconclusive; the inconclusive reasons are still reported.',
     '        work.Findings.RemoveAll(f => f.FindingCode == JournalFindingCodes.ArchiveUnavailable);\n'
     '        work.Inconclusive.RemoveAll(r => r.StartsWith(JournalInconclusiveReasons.ArchiveUnavailable, StringComparison.Ordinal));\n'
     '        // A confirmed security finding is never masked as Inconclusive; the inconclusive reasons are still reported.'),
    (VERIFIER, '        if (outcome == JournalSweepOutcome.Clean)\n        {\n            var head = work.Discovery!.Head!;',
     '        if (outcome == JournalSweepOutcome.Clean && work.Comparison is not null)\n        {\n            var head = work.Discovery!.Head!;'),
])
mut('MV-07', 'CK-11: the trusted anchor digest is taken from AuthorityDb (the row at v) instead of the checkpoint store', [
    (VERIFIER, '        var v = head.VerifiedRevision;\n        var request = kind == JournalSweepKind.Full',
     '        var v = head.VerifiedRevision;\n'
     '        var databaseAnchor = v == 0 ? null : await snapshot.ReadRowsAsync(org, stream, v, v, 1, cancellationToken).ConfigureAwait(false) is { Count: > 0 } anchorRows ? anchorRows[0] : null;\n'
     '        head = head with { VerifiedDigest = databaseAnchor is null ? head.VerifiedDigest : System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(databaseAnchor.BodyJson)) };\n'
     '        var request = kind == JournalSweepKind.Full'),
])
mut('MV-08', 'CK-1: a checkpoint is written despite findings', [
    (VERIFIER, '        if (outcome == JournalSweepOutcome.Clean)\n        {\n            var head = work.Discovery!.Head!;',
     '        if (outcome is JournalSweepOutcome.Clean or JournalSweepOutcome.Findings && work.Comparison is not null)\n        {\n            var head = work.Discovery!.Head!;'),
])
mut('MV-09', 'CK-2: a successor may verify a lower revision (non-monotonic checkpoint)', [
    (CHECKPOINTS, 'if (next.VerifiedRevision < previous.VerifiedRevision)', 'if (next.VerifiedRevision < previous.VerifiedRevision - 1000000)'),
])
mut('MV-10', 'DP-6: D+ skips the predecessor-existence check', [
    (DPLUS, '''            case AuditPredecessorState.Absent:
                return AuditDeliveryVerdict.Refuse(JournalFindingCodes.PredecessorMissing, null, "no archived object at the predecessor revision (sentinel present)", marksEntry: true);''',
     '''            case AuditPredecessorState.Absent:
                return AuditDeliveryVerdict.Deliver;'''),
])
mut('MV-11', 'R-4/DP-2: the envelope previous digest is not compared with the previous-digest column', [
    (RULES, 'if (!SameOptionalDigest(row.PreviousDigest, header.PreviousDigest))', 'if (row.PreviousDigest is { Length: 0 })'),
])
mut('MV-12', 'DP-7: D+ treats an archive outage on the predecessor read as "absent"', [
    (DPLUS, '''                read = await archive.ReadEntryAsync(entry.OrganizationId, entry.JournalStreamName, revision - 1, cancellationToken).ConfigureAwait(false);
            }
            catch (ArchiveUnavailableException ex)
            {
                return AuditDeliveryVerdict.Unavailable(ex.Message);
            }''', '''                read = await archive.ReadEntryAsync(entry.OrganizationId, entry.JournalStreamName, revision - 1, cancellationToken).ConfigureAwait(false);
            }
            catch (ArchiveUnavailableException ex)
            {
                _ = ex;
                read = new StoredObjectRead(StoredObjectStatus.Absent, null);
            }'''),
])
mut('MV-13a', 'AS-2/DP-5: D+ skips the sentinel check', [
    (DPLUS, 'pass.SentinelPresent = identity.Status == StoredObjectStatus.Present && identity.Identity is not null;', 'pass.SentinelPresent = true;'),
])
mut('MV-13b', 'AS-2/AS-3: the verifier skips the sentinel check', [
    (VERIFIER, 'if (identity.Status != StoredObjectStatus.Present || identity.Identity is null || !string.Equals(identity.Identity.ArchiveInstanceId, head.ArchiveInstanceId, StringComparison.Ordinal))',
     'if (identity.Status == StoredObjectStatus.Present && identity.Identity is null)'),
])
mut('MV-14', 'R-5: skip the genesis rule', [
    (RULES, 'if ((header.StreamRevision == 1) != (header.PreviousDigest is null))', 'if (header.StreamRevision < 0 && header.PreviousDigest is null)'),
])
mut('MV-15a', 'VF-14 (expressible variant): the verifier gains an AuthorityDb write/quarantine dependency (IAuthorityScope)', [
    (VERIFIER, '    public JournalVerifierOptions Options => options;',
     '    public JournalVerifierOptions Options => options;\n\n'
     '    internal static async Task LockForQuarantineAsync(IAuthorityScope scope, Guid organizationId, CancellationToken cancellationToken) =>\n'
     '        await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);'),
], 'arch')
mut('MV-15b', 'VF-14 (expressible variant): the verifier snapshot is not server-enforced read-only (READ WRITE)', [
    (READER, 'await snapshot.ExecuteAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);', 'await snapshot.ExecuteAsync("SET TRANSACTION READ WRITE", cancellationToken).ConfigureAwait(false);'),
], 'pg', '*ReadOnly*')
mut('MV-16', 'VF-12/VF-13: the full sweep is skipped (a full sweep runs as an incremental one)', [
    # WP-7A: anchor shortened (a VF-15 comment now precedes the full-sweep request); the edit is unchanged
    (VERIFIER, '        var request = kind == JournalSweepKind.Full\n', '        var request = kind == (JournalSweepKind)(-1)\n'),
])
mut('MV-17a', 'BL-2 (expressible variant): the baseline gains an AuthorityDb write dependency (IAuthorityScope)', [
    (BASELINE, '    public const string ReportSchema = "DynamicForms/JournalBaselineReport/v2";',
     '    public const string ReportSchema = "DynamicForms/JournalBaselineReport/v2";\n\n'
     '    internal static async Task RepairAsync(IAuthorityScope scope, Guid organizationId, CancellationToken cancellationToken) =>\n'
     '        await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);'),
], 'arch')
mut('MV-17b', 'BL-2 (expressible variant): the baseline repairs missing archived evidence from AuthorityDb rows before comparing', [
    (BASELINE, '                var comparison = await JournalStreamComparer.CompareAsync(snapshot, archive,',
     '                if (archive is ISecurityEventArchive repairable)\n'
     '                {\n'
     '                    var repairHead = await snapshot.ReadStreamHeadAsync(request.OrganizationId, request.StreamName, cancellationToken).ConfigureAwait(false);\n'
     '                    foreach (var repairRow in await snapshot.ReadRowsAsync(request.OrganizationId, request.StreamName, 1, repairHead?.MaximumRowRevision ?? 0, 100000, cancellationToken).ConfigureAwait(false))\n'
     '                    {\n'
     '                        if ((await archive.ReadEntryAsync(repairRow.OrganizationId, repairRow.StreamName, repairRow.StreamRevision, cancellationToken).ConfigureAwait(false)).Status == StoredObjectStatus.Absent)\n'
     '                        {\n'
     '                            await repairable.AppendAsync(repairRow.OrganizationId, repairRow.StreamName, repairRow.StreamRevision, System.Text.Encoding.UTF8.GetBytes(repairRow.BodyJson), repairRow.EventDigest, cancellationToken).ConfigureAwait(false);\n'
     '                        }\n'
     '                    }\n'
     '                }\n\n'
     '                var comparison = await JournalStreamComparer.CompareAsync(snapshot, archive,'),
])
mut('MV-18', 'VF-8: FAKE_RECEIPT raised without the second read', [
    (COMPARER, '''                    var second = await ReadArchiveAsync(archive, org, stream, row.StreamRevision, result, cancellationToken).ConfigureAwait(false);
                    if (result.IsInconclusive)
                    {
                        return Finish(result, clock, started);
                    }

                    if (second.Status == StoredObjectStatus.Present)
                    {
                        result.Inconclusive(JournalInconclusiveReasons.ArchiveChangedDuringSweep, row.StreamRevision);
                        return Finish(result, clock, started);
                    }

''', ''),
])
mut('MV-X1', 'GT-6 (integrated): the projection processor applies an authorization entry on its row state, without a trusted receipt', [
    (PROJECTION, 'if (journal is null || !(await scope.JournalReceipts.VerifyAsync(journal, cancellationToken).ConfigureAwait(false)).IsVerified)',
     'if (journal is null || await Task.FromResult(journal.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored).ConfigureAwait(false))'),
])


def sha(p):
    return hashlib.sha256(open(p, 'rb').read()).hexdigest()


# ---- WP-7A (C-160, A-9): F-1 / TR-6' (RV-1..RV-9) and OI-DR-7 / VF-15 mutations ----
mut('MV-7A-01', "RV-2: the freshness bound is ignored (a verified attestation answers for the process lifetime: the pre-remediation TR-6)", [
    (RECEIPTS, 'return attestation.StartedTimestamp >= arrived || clock.GetElapsedTime(attestation.StartedTimestamp, arrived) <= options.CurrentStateMaxAge;', 'return options.CurrentStateMaxAge >= TimeSpan.Zero; // CA1822: keeps instance access'),
])
mut('MV-7A-02', 'RV-6: withdrawn evidence is not remembered (a restored object re-opens the key in the same process)', [
    (RECEIPTS, 'withdrawn.TryAdd(key, reason);', '_ = reason;'),
])
mut('MV-7A-03', 'RV-6: a changed evidence identity (instance, generation, commitment bytes) is accepted when the new evidence verifies', [
    (RECEIPTS, 'if (earlier is not null && earlier != evidence)', 'if (earlier is not null && earlier != evidence && evidence is null)'),
])
mut('MV-7A-04', 'RV-3: a refusal or an outage is reused within the bound (both guards: the reuse check and the removal of refused attestations; attempt 1 edited only the first, an equivalent mutant)', [
    (RECEIPTS, 'return false; // RV-3: never reuse a refusal or an outage', 'return clock.GetElapsedTime(attestation.StartedTimestamp, arrived) <= options.CurrentStateMaxAge;'),
    (RECEIPTS, 'attestations.TryRemove(new KeyValuePair<(Key, bool), Attestation>((key, head), attestation)); // RV-3: a refusal or outage is never held', '_ = head; // removal disabled'),
])
mut('MV-7A-05', 'RV-4: successor evidence (object, commitments) above a claimed head is ignored', [
    (RECEIPTS, 'if (head && (successorObject!.Result.Status != StoredObjectStatus.Absent || successorArchived!.Result.Count > 0 || successorStored!.Result.Count > 0))',
     'if (head && successorObject!.Result.Status == StoredObjectStatus.ContainerAbsent && successorArchived!.Result.Count < 0 && successorStored!.Result.Count < 0)'),
])
mut('MV-7A-06', 'GT-2/RV-4 (integrated): the gate asks for an entry receipt instead of a stream-head receipt', [
    (GATE, 'scope.JournalReceipts.VerifyStreamHeadAsync(head, cancellationToken)', 'scope.JournalReceipts.VerifyAsync(head, cancellationToken)'),
])
mut('MV-7A-07', 'RV-5: stop markers are ignored (newly established, unresolved or above a head)', [
    (RECEIPTS, 'foreach (var marker in stopsRead.Result)', 'foreach (var marker in stopsRead.Result.Where(_ => false))'),
])
mut('MV-7A-08', 'RV-5: a generation g >= 1 commitment is accepted without a durable stop of generation g - 1', [
    (RECEIPTS, 'if (generation > 0 && !stopsRead.Result.Any(', 'if (generation < 0 && !stopsRead.Result.Any('),
])
mut('MV-7A-09', 'RV-2: the 30 s ceiling of the freshness bound is not enforced', [
    (RECEIPTS, 'if (CurrentStateMaxAge < TimeSpan.Zero || CurrentStateMaxAge > MaximumCurrentStateMaxAge)', 'if (CurrentStateMaxAge < TimeSpan.Zero)'),
])
mut('MV-7A-10', 'VF-15 (OI-DR-7): the full sweep byte-compares only above the trusted checkpoint (the pre-remediation behaviour)', [
    (VERIFIER, 'head.VerifiedDigest, 1, string.Equals(stream, JournalStreamNames.Audit, StringComparison.Ordinal), now, options.DeliveryLagThreshold,',
     'head.VerifiedDigest, v + 1, string.Equals(stream, JournalStreamNames.Audit, StringComparison.Ordinal), now, options.DeliveryLagThreshold,'),
])
mut('MV-7A-10-PG', 'VF-15 (OI-DR-7) on real stores: as MV-7A-10, PostgreSQL + Azurite historical-attack tests', [
    (VERIFIER, 'head.VerifiedDigest, 1, string.Equals(stream, JournalStreamNames.Audit, StringComparison.Ordinal), now, options.DeliveryLagThreshold,',
     'head.VerifiedDigest, v + 1, string.Equals(stream, JournalStreamNames.Audit, StringComparison.Ordinal), now, options.DeliveryLagThreshold,'),
], 'pg', '*WP7AOiDr7*')
mut('MV-7A-05-PG', 'RV-4 on real stores: successor evidence ignored; the J2 revocation-deletion phases on the real gate', [
    (RECEIPTS, 'if (head && (successorObject!.Result.Status != StoredObjectStatus.Absent || successorArchived!.Result.Count > 0 || successorStored!.Result.Count > 0))',
     'if (head && successorObject!.Result.Status == StoredObjectStatus.ContainerAbsent && successorArchived!.Result.Count < 0 && successorStored!.Result.Count < 0)'),
], 'pg', '*WP7AJ2*')
mut('MV-7A-01-PG', 'RV-2 on real stores: the freshness bound ignored; the F-1 attack variants', [
    (RECEIPTS, 'return attestation.StartedTimestamp >= arrived || clock.GetElapsedTime(attestation.StartedTimestamp, arrived) <= options.CurrentStateMaxAge;', 'return options.CurrentStateMaxAge >= TimeSpan.Zero; // CA1822: keeps instance access'),
], 'pg', '*WP7AF1*')

def run(cmd, log, timeout=3000):
    t = time.time()
    with open(log, 'w', encoding='utf-8', errors='replace') as f:
        p = subprocess.run(cmd, cwd=clone, stdout=f, stderr=subprocess.STDOUT, timeout=timeout)
    return p.returncode, round(time.time() - t, 1)


def execute(m, out):
    project = ARCH_PROJECT if m['kind'] == 'arch' else PROJECT
    code, secs = run([dotnet, 'build', os.path.join(clone, project), '--no-incremental', '-nologo', '-v', 'q'], os.path.join(out, 'build.log'))
    if code != 0:
        return dict(build=code, build_seconds=secs), False
    log = os.path.join(out, 'tests.log')
    if m['kind'] == 'pg':
        exit_code, test_secs = run([pwsh, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(clone, 'scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1'),
                                    '-Class', NS + 'JournalVerificationPostgresIntegrationTests', '-MethodFilter', m['method']], log, timeout=1800)
    elif m['kind'] == 'arch':
        exit_code, test_secs = run([os.path.join(clone, ARCH_EXE), '--filter-class', 'EnterpriseForms.ArchitectureTests.JournalVerificationBoundaryTests'], log, timeout=900)
    else:
        args = [os.path.join(clone, EXE)]
        for c in CLASSES:
            args += ['--filter-class', NS + c]
        exit_code, test_secs = run(args, log, timeout=1500)
    text = open(log, encoding='utf-8', errors='replace').read()
    failed = sorted(set(re.findall(r'^\s*failed (\S+)', text, re.M)))
    total = re.findall(r'^\s+total: (\d+)', text, re.M)
    passed = re.findall(r'^\s+succeeded: (\d+)', text, re.M)
    return dict(build=code, build_seconds=secs, exit=exit_code, seconds=test_secs, kind=m['kind'], total=int(total[-1]) if total else None,
                succeeded=int(passed[-1]) if passed else None, failed=failed), True


def classify(mid, result, built):
    if not built:
        return 'INCONCLUSIVE (build failed)'
    if result['total'] is None:
        return 'INCONCLUSIVE (no report)'
    if mid.startswith('BASELINE') or mid == 'RESTORED':
        return 'PASS' if not result['failed'] else 'FAIL'
    return 'DETECTED' if result['failed'] else 'SURVIVED'


os.makedirs(a.out, exist_ok=True)
ids = a.only.split(',')
for mid in ids:
    for f, anchor, _ in M[mid]['edits']:
        n = open(os.path.join(clone, f), encoding='utf-8').read().replace('\r\n', '\n').count(anchor)
        if n != 1:
            raise SystemExit(f'{mid}: anchor occurs {n} times in {f}')
if a.dry_run:
    print('anchors verified:', ids)
    raise SystemExit(0)

for mid in ids:
    m = M[mid]
    out = os.path.join(a.out, mid)
    os.makedirs(out, exist_ok=True)
    files = sorted(set(f for f, _, _ in m['edits']))
    originals = {f: open(os.path.join(clone, f), 'rb').read() for f in files}
    before = {f: hashlib.sha256(originals[f]).hexdigest() for f in files}
    started = time.time()
    try:
        for f in files:
            text = originals[f].decode('utf-8')
            crlf = '\r\n' in text
            text = text.replace('\r\n', '\n')
            for ef, anchor, repl in m['edits']:
                if ef == f:
                    text = text.replace(anchor, repl)
            if crlf:
                text = text.replace('\n', '\r\n')
            with open(os.path.join(clone, f), 'wb') as h:
                h.write(text.encode('utf-8'))
        result, built = execute(m, out)
        verdict = classify(mid, result, built)
    finally:
        for f in files:
            with open(os.path.join(clone, f), 'wb') as h:
                h.write(originals[f])
            os.utime(os.path.join(clone, f), None)
    restored = all(sha(os.path.join(clone, f)) == before[f] for f in files)
    record = dict(id=mid, what=m['what'], kind=m['kind'], files=files, original_sha256=before, restored=restored, verdict=verdict, result=result, seconds=round(time.time() - started, 1))
    json.dump(record, open(os.path.join(out, 'result.json'), 'w', encoding='utf-8'), indent=2)
    print(json.dumps(dict(id=mid, verdict=verdict, restored=restored, failed=len(result.get('failed') or []), total=result.get('total'), seconds=record['seconds'])), flush=True)
    if not restored:
        raise SystemExit(mid + ': RESTORATION FAILED')
