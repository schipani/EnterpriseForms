"""SEC-BL-01 G14-R2 mutation driver (Development/Testing only; record docs/security/G14_R2_IMPLEMENTATION_RECORD.md).

Mutations SR-MV-01..SR-MV-15 of docs/security/G14_R1_TEST_AND_ACCEPTANCE_MATRIX.md §3, as applied to the G14-R2 implementation. Operates ONLY on
a separate scratch clone given by --clone (git clone --local of the repository plus the uncommitted working-tree files), never on the working
tree, so an interrupted run cannot leave a product file mutated in the repository. For each mutation: verify every anchor occurs exactly
once, keep the original bytes in memory, apply the edits, build the Authority test project with --no-incremental, run the declared test
classes, classify (DETECTED / SURVIVED / INCONCLUSIVE), restore the exact original bytes in a finally block, verify SHA-256 and touch the
file so the next build cannot reuse a stale binary. BASELINE and RESTORED run the same checks with no edit.

Example (Git Bash):
  git clone --local --no-hardlinks <repo> <scratch>/mz   # then copy the uncommitted working-tree files into the clone
  python scripts/dev/g14-r2-mutations.py --clone <scratch>/mz --out <scratch>/mz-out --only BASELINE,SR-MV-01,...,SR-MV-15,RESTORED
"""
import argparse, hashlib, json, os, re, shutil, subprocess, time

ap = argparse.ArgumentParser()
ap.add_argument('--clone', required=True)
ap.add_argument('--out', required=True)
ap.add_argument('--only', required=True, help='comma list of mutation ids, BASELINE and/or RESTORED')
ap.add_argument('--dry-run', action='store_true')
a = ap.parse_args()
clone = a.clone
dotnet = shutil.which('dotnet')

ARCH = 'src/authority/AuthorizationAuthority.Application/Features/Journal/ArchiveProcessor.cs'
LEDGER = 'src/authority/AuthorizationAuthority.Application/Features/Journal/Recovery/DPlusRecoveryLedger.cs'
APPROVALS = 'src/authority/AuthorizationAuthority.Application/Features/Journal/Recovery/DPlusRecoveryApprovals.cs'
SERVICE = 'src/authority/AuthorizationAuthority.Application/Features/Journal/Recovery/DPlusStreamRecoveryService.cs'
PROJECT = 'tests/authority/AuthorizationAuthority.Tests/AuthorizationAuthority.Tests.csproj'
EXE = 'tests/authority/AuthorizationAuthority.Tests/bin/Debug/net10.0/AuthorizationAuthority.Tests.exe'
NS = 'AuthorizationAuthority.Tests.'
# WP-5b: the suites became per-stream subclasses; every D+-enabled stream runs them.
CLASSES = ['DPlusRecoveryAuditTests', 'DPlusRecoveryContentTests', 'DPlusRecoveryAuthorizationTests', 'DPlusDeliveryEnforcementAuditTests', 'DPlusDeliveryEnforcementContentTests',
           'DPlusDeliveryEnforcementAuthorizationTests']

M = {}


def mut(id, what, edits, classes=None):
    M[id] = dict(id=id, what=what, edits=edits, classes=classes or CLASSES)


mut('BASELINE', 'no edit (the unmodified clone)', [])
mut('RESTORED', 'no edit (after every mutation was restored)', [])
mut('SR-MV-01', 'the lift rests on the execution record alone: no authorization (no approvals, rejections or cancellations) is required for the lift',
    [(LEDGER, '    public bool Executed => Authorized && Execution is not null;', '    public bool Executed => Execution is not null && Statement is not null;')])
mut('SR-MV-02', 'decision signatures are not verified (the ES256 check is removed from the verifier used on every pass and at execution)',
    [(APPROVALS, 'if (signature.Length != 64 || !key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256))',
      'if (signature.Length != 64)')])
mut('SR-MV-03', 'two approvals from the same approver id count as two',
    [(APPROVALS, '.GroupBy(d => d.ApproverId, StringComparer.Ordinal).Select(g => g.OrderBy(d => d.IssuedOn).First()).OrderBy(d => d.ApproverId, StringComparer.Ordinal).ToList();',
      '.OrderBy(d => d.ApproverId, StringComparer.Ordinal).ToList();')])
mut('SR-MV-04', 'the stop-set binding is ignored: any executed recovery of the (organization, stream) covers every stop record',
    [(LEDGER, '''        return Recoveries.Any(r => r.Executed && r.Statement!.StopSet.Any(s => string.Equals(s.ObjectName, record.ObjectName, StringComparison.Ordinal)
            && string.Equals(s.Sha256Hex, record.Sha256Hex, StringComparison.Ordinal) && string.Equals(s.RecordKind, record.RecordKind, StringComparison.Ordinal)
            && (record.StopGeneration is null || record.StopGeneration == r.Statement.StopGeneration)));''',
      '''        return Recoveries.Any(r => r.Executed && r.Statement!.OrganizationId != Guid.Empty);''')])
mut('SR-MV-05', 'an AuthorityDb row code DPLUS_RECOVERY_AUTHORIZED:* is treated as a lift',
    [(ARCH, '''        var stream = first.JournalStreamName;
        IReadOnlyList<ArchiveMetaObject> objects;''', '''        var stream = first.JournalStreamName;
        if (first.LastArchiveErrorCode?.StartsWith(RecoveryAuthorizedCodePrefix, StringComparison.Ordinal) == true)
        {
            return (StopDiscovery.Clear, await DPlusRecoveryLedger.LoadAsync(organizationId, stream, meta, durability!.Records, durability.Approvals, cancellationToken).ConfigureAwait(false));
        }

        IReadOnlyList<ArchiveMetaObject> objects;''')])
mut('SR-MV-06', 'archive presence of the failed revision resolves its stop records',
    [(ARCH, '''        markers = [.. markers.Where(m => !ledger.Covers(m.Reference))];
        refusals = [.. refusals.Where(f => !ledger.Covers(f.Reference))];''', '''        var presentRevisions = new HashSet<long>();
        foreach (var revision in markers.Select(m => m.Marker.FailedRevision).Concat(refusals.Select(f => f.Finding.FirstRevision!.Value)).Distinct().ToList())
        {
            if ((await inventory.ReadEntryAsync(organizationId, stream, revision, cancellationToken).ConfigureAwait(false)).Status == StoredObjectStatus.Present)
            {
                presentRevisions.Add(revision);
            }
        }

        markers = [.. markers.Where(m => !ledger.Covers(m.Reference) && !presentRevisions.Contains(m.Marker.FailedRevision))];
        refusals = [.. refusals.Where(f => !ledger.Covers(f.Reference) && !presentRevisions.Contains(f.Finding.FirstRevision!.Value))];''')])
mut('SR-MV-07', 'the executor skips the fresh evidence-snapshot comparison',
    [(SERVICE, '            if (observed.Refusal is not null || !string.Equals(DPlusRecoveryCodec.Sha256Hex(DPlusRecoveryCodec.Serialize(observed.Evidence!)), statement.EvidenceSha256Hex, StringComparison.Ordinal))',
      '            if (observed.Refusal is not null && string.IsNullOrEmpty(statement.Strategy))')])
mut('SR-MV-08', 'D+ skips the comparison with the statement expectation at the recovered revision',
    [(ARCH, '        if (lift is not null && evidence.Entry.JournalStreamRevision == lift.FailedRevision)',
      '        if (lift is not null && evidence.Entry.JournalStreamRevision == lift.FailedRevision && string.IsNullOrEmpty(lift.Strategy))')])
mut('SR-MV-09', 'the execution record is written before the row hint (the lift can take effect before the hint and after a terminated hint write)',
    [(SERVICE, '''        // 2. The row hint (scheduling only; never trusted). 3. The execution record, last.
        if (await SetHintAsync(organizationId, streamName, statement.FailedRevision, recoveryId, cancellationToken).ConfigureAwait(false) is { } hintFailure)
        {
            return hintFailure;
        }
''', '''        // MUTATED: execution record first, hint after it.
'''), (SERVICE, '''        return new DPlusRecoveryCommandResult(DPlusRecoveryCommandOutcome.Done, resuming ? "EXECUTION_RESUMED"''', '''        if (await SetHintAsync(organizationId, streamName, statement.FailedRevision, recoveryId, cancellationToken).ConfigureAwait(false) is { } hintFailure)
        {
            return hintFailure;
        }

        return new DPlusRecoveryCommandResult(DPlusRecoveryCommandOutcome.Done, resuming ? "EXECUTION_RESUMED"''')])
mut('SR-MV-10', 'stops and commitments always use generation 0 (the generation-0 marker key is reused after a recovery)',
    [(ARCH, '            var generation = ledger!.Generation;', '            var generation = ledger!.Generation * 0;')])
mut('SR-MV-11', 'W2-S: D+ evaluates when the commitment could not be established',
    [(ARCH, '''            case DPlusCommitmentOutcome.Unavailable:
                return AuditDeliveryVerdict.Wait(CommitmentUnavailableCode, commitment.Detail);''', '''            case DPlusCommitmentOutcome.Unavailable:
                break;''')])
mut('SR-MV-12', 'W2-S: a differing or contradictory commitment is ignored (evaluated as if it matched)',
    [(ARCH, '''            case DPlusCommitmentOutcome.Changed:
                return AuditDeliveryVerdict.Refuse(JournalFindingCodes.DPlusEvidenceChangedAfterCommitment, null, commitment.Detail, marksEntry: true);
            case DPlusCommitmentOutcome.Contradictory:
                return AuditDeliveryVerdict.Refuse(JournalFindingCodes.DPlusEvidenceChangedAfterCommitment, "COMMITMENT_CONTRADICTORY", commitment.Detail, marksEntry: true);''',
      '''            case DPlusCommitmentOutcome.Changed:
            case DPlusCommitmentOutcome.Contradictory:
                break;''')])
mut('SR-MV-13', 'rejections and cancellations are ignored',
    [(APPROVALS, '''        var rejected = decisions.Any(d => d.ApprovalKind == DPlusRecoveryClaims.RecoveryKind && d.Decision == DPlusRecoveryClaims.Reject);
        var cancelled = decisions.Any(d => d.ApprovalKind == DPlusRecoveryClaims.CancellationKind);''', '''        var rejected = decisions.Count < 0;
        var cancelled = decisions.Count < 0;''')])
mut('SR-MV-14', 'SR-C1 accepts a present predecessor (an overwrite is attempted)',
    [(SERVICE, '''                if (predecessorState != AuditPredecessorState.Absent)
                {
                    return Refuse(DPlusRecoveryRefusals.StrategyPreconditionFailed, "SR-C1 requires the predecessor to be absent (nothing is ever overwritten)");
                }
''', '')])
mut('SR-MV-15', 'the synthetic approval mode is accepted outside Development/Testing',
    [(APPROVALS, '        if (environmentName is not ("Development" or "Testing"))', '        if (environmentName is null)')])


def sha(p):
    return hashlib.sha256(open(p, 'rb').read()).hexdigest()


def run(cmd, log, timeout=3000):
    t = time.time()
    with open(log, 'w', encoding='utf-8', errors='replace') as f:
        p = subprocess.run(cmd, cwd=clone, stdout=f, stderr=subprocess.STDOUT, timeout=timeout)
    return p.returncode, round(time.time() - t, 1)


def execute(m, out):
    code, secs = run([dotnet, 'build', os.path.join(clone, PROJECT), '--no-incremental', '-nologo', '-v', 'q'], os.path.join(out, 'build.log'))
    if code != 0:
        return dict(build=code, build_seconds=secs), False
    args = [os.path.join(clone, EXE)]
    for c in m['classes']:
        args += ['--filter-class', NS + c]
    log = os.path.join(out, 'tests.log')
    exit_code, test_secs = run(args, log, timeout=1500)
    text = open(log, encoding='utf-8', errors='replace').read()
    failed = sorted(set(re.findall(r'^failed (\S+)', text, re.M)))
    total = re.findall(r'^\s+total: (\d+)', text, re.M)
    passed = re.findall(r'^\s+succeeded: (\d+)', text, re.M)
    return dict(build=code, build_seconds=secs, exit=exit_code, seconds=test_secs, classes=m['classes'], total=int(total[-1]) if total else None,
                succeeded=int(passed[-1]) if passed else None, failed=failed), True


def classify(mid, result, built):
    if not built:
        return 'INCONCLUSIVE (build failed)'
    if result['total'] is None:
        return 'INCONCLUSIVE (no report)'
    if mid in ('BASELINE', 'RESTORED'):
        return 'PASS' if not result['failed'] else 'FAIL'
    return 'DETECTED' if result['failed'] else 'SURVIVED'


os.makedirs(a.out, exist_ok=True)
ids = a.only.split(',')
for mid in ids:
    for f, anchor, _ in M[mid]['edits']:
        n = open(os.path.join(clone, f), encoding='utf-8').read().count(anchor)
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
            for ef, anchor, repl in m['edits']:
                if ef == f:
                    text = text.replace(anchor, repl)
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
    record = dict(id=mid, what=m['what'], files=files, original_sha256=before, restored=restored, verdict=verdict, result=result, seconds=round(time.time() - started, 1))
    json.dump(record, open(os.path.join(out, 'result.json'), 'w', encoding='utf-8'), indent=2)
    print(json.dumps(dict(id=mid, verdict=verdict, restored=restored, failed=len(result.get('failed') or []), total=result.get('total'), seconds=record['seconds'])), flush=True)
    if not restored:
        raise SystemExit(mid + ': RESTORATION FAILED')
