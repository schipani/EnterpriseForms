"""SEC-BL-01 WP-5b mutation driver (Development/Testing only; record docs/security/SEC_BL_01_WP5B_IMPLEMENTATION_RECORD.md).

Mutations MV-5B-01..MV-5B-12 of the WP-5b implementation (contract amendment A-6, §26): stream enablement, per-stream batching, recovery
stream scope, the recovery hint, the organization gate, the EP-5 verifier comparison and the SC-5 readiness check. Same procedure as
scripts/dev/g14-r2-mutations.py: operates ONLY on a separate scratch clone given by --clone, verifies every anchor occurs exactly once, builds
with --no-incremental, runs the declared classes, classifies DETECTED / SURVIVED / INCONCLUSIVE, restores the original bytes in a finally block
and verifies SHA-256. BASELINE and RESTORED run with no edit.
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

J = 'src/authority/AuthorizationAuthority.Application/Features/Journal/'
ARCH = J + 'ArchiveProcessor.cs'
ENF = J + 'AuditDeliveryEnforcement.cs'
SERVICE = J + 'Recovery/DPlusStreamRecoveryService.cs'
INSPECT = J + 'Reconciliation/DPlusCommitmentInspection.cs'
VERIFIER = J + 'Reconciliation/JournalVerifier.cs'
GATE = 'src/authority/AuthorizationAuthority.Application/Common/OrganizationGate.cs'
READY = 'src/authority/AuthorizationAuthority.Api/Composition/SigningProviderPolicy.cs'
PROJECT = 'tests/authority/AuthorizationAuthority.Tests/AuthorizationAuthority.Tests.csproj'
EXE = 'tests/authority/AuthorizationAuthority.Tests/bin/Debug/net10.0/AuthorizationAuthority.Tests.exe'
NS = 'AuthorizationAuthority.Tests.'
CLASSES = ['DPlusDeliveryEnforcementAuditTests', 'DPlusDeliveryEnforcementContentTests', 'DPlusDeliveryEnforcementAuthorizationTests', 'DPlusRecoveryAuditTests',
           'DPlusRecoveryContentTests', 'DPlusRecoveryAuthorizationTests', 'DPlusStreamPhaseTests', 'DPlusCommitmentVerificationTests', 'DPlusReadinessTests',
           'AuthorizationStreamGateDPlusTests', 'ContentStreamDPlusReadabilityTests', 'ArchiveProcessorTests']

M = {}


def mut(id, what, edits, classes=None):
    M[id] = dict(id=id, what=what, edits=edits, classes=classes or CLASSES)


ALL = 'public static IReadOnlyList<string> EnforcedStreams { get; } = [JournalStreamNames.Audit, JournalStreamNames.Authorization, JournalStreamNames.Content];'
mut('BASELINE', 'no edit (the unmodified clone)', [])
mut('RESTORED', 'no edit (after every mutation was restored)', [])
mut('MV-5B-01', 'the content stream is not D+-enabled', [(ENF, ALL, ALL.replace(', JournalStreamNames.Content]', ']'))])
mut('MV-5B-02', 'the authorization stream is not D+-enabled', [(ENF, ALL, ALL.replace(' JournalStreamNames.Authorization,', ''))])
mut('MV-5B-03', 'one shared batch for all streams (a stopped stream starves the others)',
    [(ARCH, 'var pending = await scope.GetPendingJournalEntriesByStreamAsync(organizationId, stream, includeStream: true, BatchSize, cancellationToken).ConfigureAwait(false);',
      'var pending = (await scope.GetPendingJournalEntriesAsync(organizationId, BatchSize, cancellationToken).ConfigureAwait(false)).Where(e => e.JournalStreamName == stream).ToList();')])
mut('MV-5B-04', 'a D+ refusal ends the pass for every stream',
    [(ARCH, 'return (delivered, false); // this stream stops for this pass; other streams continue', 'return (delivered, true); // this stream stops for this pass; other streams continue')])
mut('MV-5B-05', 'recovery is accepted for any stream name (SR-PB-2 removed)',
    [(SERVICE, 'if (!AuditDeliveryEnforcement.IsEnforced(request.StreamName))', 'if (request.StreamName is null)')])
mut('MV-5B-06', 'the recovery hint marks the row Anchored instead of Pending (delivery state granted by recovery)',
    [(SERVICE, 'row.ArchiveDeliveryState = ArchiveDeliveryStates.Pending;' + chr(10) + '        row.LastArchiveErrorCode = ArchiveProcessor.RecoveryAuthorizedCodePrefix',
      'row.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;' + chr(10) + '        row.LastArchiveErrorCode = ArchiveProcessor.RecoveryAuthorizedCodePrefix')])
mut('MV-5B-07', 'the organization gate ignores undelivered authorization entries',
    [(GATE, 'await scope.CountPendingArchiveAsync(state.OrganizationId, JournalStreamNames.Authorization, cancellationToken).ConfigureAwait(false) > 0)',
      'await scope.CountPendingArchiveAsync(state.OrganizationId, JournalStreamNames.Authorization, cancellationToken).ConfigureAwait(false) < 0)')])
mut('MV-5B-08', 'EP-5: the row-digest comparison with the archived entry is skipped',
    [(INSPECT, 'if (!string.Equals(commitment.RowBodySha256Hex, digest, StringComparison.Ordinal))', 'if (commitment.RowBodySha256Hex is null)')])
mut('MV-5B-09', 'EP-5: conflicting copies are not compared',
    [(INSPECT, 'if (archived is not null && stored is not null && !archived.AsSpan().SequenceEqual(stored))', 'if (archived is null && stored is null)')])
mut('MV-5B-10', 'EP-5: an archived revision without a commitment is not reported',
    [(INSPECT, 'if (revision >= firstCommitted && !commitments.Copies.Keys.Any(k => k.Revision == revision))', 'if (revision < 0)')])
mut('MV-5B-11', 'EP-5: the verifier never inspects the journal-verification store copies',
    [(VERIFIER, 'CompareAsync(snapshot, archive, request, options.Limits, clock, cancellationToken, commitmentStore)', 'CompareAsync(snapshot, archive, request, options.Limits, clock, cancellationToken, commitmentStore is null ? null : null)')])
mut('MV-5B-12', 'SC-5: a missing D+ composition is reported ready',
    [(READY, 'durability is null ? (false, AuthorizationAuthority.Application.Features.Journal.ArchiveProcessor.DurabilityNotComposedCode) : (true, null);', '(true, null);')])


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
