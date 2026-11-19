"""SEC-BL-01 G08 / E-6 / E-7 mutation driver (Development/Testing only; record docs/security/SEC_BL_01_G08_E6_E7_IMPLEMENTATION_RECORD.md).

Same procedure as scripts/dev/g14-r2-mutations.py: operates ONLY on a scratch clone (--clone), verifies every anchor occurs exactly once,
keeps the original bytes, applies the edit, runs the declared check, classifies DETECTED / SURVIVED / INCONCLUSIVE, restores the original
bytes in a finally block and verifies SHA-256. Two kinds of check:
  ip  build the Authority test project (--no-incremental) and run the declared in-process classes;
  pg  run scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1 of the clone with -MethodFilter '*G08*' (fresh disposable PostgreSQL 18 + Azurite,
      the clone's own build, so the migration and grants under test are the mutated ones).
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
GATE = APP + 'Common/OrganizationGate.cs'
RECEIPTS = APP + 'Features/Journal/TrustedJournalReceipts.cs'
CONTENT = APP + 'Features/Content/ContentCheckpointReceipts.cs'
SQL = 'src/authority/AuthorizationAuthority.Infrastructure/Persistence/JournalImmutabilitySql.cs'
GRANTS = 'src/authority/AuthorizationAuthority.Infrastructure/Persistence/AuthorityRuntimeGrants.cs'
PROJECT = 'tests/authority/AuthorizationAuthority.Tests/AuthorizationAuthority.Tests.csproj'
EXE = 'tests/authority/AuthorizationAuthority.Tests/bin/Debug/net10.0/AuthorizationAuthority.Tests.exe'
NS = 'AuthorizationAuthority.Tests.'
CLASSES = ['TrustedJournalReceiptTests', 'AuthorizationStreamGateDPlusTests', 'ContentStreamDPlusReadabilityTests', 'ArchiveProcessorTests']

M = {}


def mut(id, what, edits, kind='ip'):
    M[id] = dict(id=id, what=what, edits=edits, kind=kind)


mut('BASELINE', 'no edit', [])
mut('BASELINE-PG', 'no edit (PostgreSQL gate)', [], 'pg')
mut('RESTORED', 'no edit (after every mutation was restored)', [])
mut('MV-G8-01', 'E-6: the gate ignores the trusted receipt of the journal head',
    [(GATE, '&& !(await scope.JournalReceipts.VerifyAsync(head, cancellationToken).ConfigureAwait(false)).IsVerified)', '&& head is null)')])
mut('MV-G8-02', 'E-6: the gate takes the head from the mutable organization revision column',
    [(GATE, 'await scope.FindLatestJournalEntryAsync(state.OrganizationId, JournalStreamNames.Authorization, cancellationToken)',
      'await scope.FindJournalEntryAsync(state.OrganizationId, JournalStreamNames.Authorization, state.AuthorizationRevision, cancellationToken)')])
mut('MV-G8-03', 'E-7: content release trusts the checkpoint row state',
    [(CONTENT, '''        return checkpoint is not null && Binds(checkpoint, head)
            && (await scope.JournalReceipts.VerifyAsync(checkpoint, cancellationToken).ConfigureAwait(false)).IsVerified;''',
      '''        return checkpoint is not null && Binds(checkpoint, head) && await Task.FromResult(checkpoint.ArchiveDeliveryState == ArchiveDeliveryStates.Anchored).ConfigureAwait(false);''')])
mut('MV-G8-04', 'E-7: the checkpoint is not bound to the head',
    [(CONTENT, 'return checkpoint is not null && Binds(checkpoint, head)', 'return checkpoint is not null')])
mut('MV-G8-05', 'receipt: the archived object is not compared with the row digest',
    [(RECEIPTS, 'if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(read.Bytes!)), digestHex, StringComparison.Ordinal))', 'if (read.Bytes is null)')])
mut('MV-G8-06', 'receipt: the sentinel instance is not compared with the pin',
    [(RECEIPTS, 'if (sentinel.Identity?.ArchiveInstanceId is not { } instance || !string.Equals(instance, pin.ExpectedArchiveInstanceId, StringComparison.Ordinal))',
      'if (sentinel.Identity?.ArchiveInstanceId is not { } instance)')])
mut('MV-G8-07', 'receipt: the W2-S commitment is not checked',
    [(RECEIPTS, '''        var generation = archived.Copies.Keys.Concat(stored.Copies.Keys).DefaultIfEmpty(-1).Max();''',
      '''        if (instance.Length > 0)
        {
            return new JournalReceiptVerdict(JournalReceiptStatus.Verified, JournalReceiptCodes.Verified, 0, instance);
        }

        var generation = archived.Copies.Keys.Concat(stored.Copies.Keys).DefaultIfEmpty(-1).Max();''')])
mut('MV-G8-08', 'receipt: an Anchored row is a receipt',
    [(RECEIPTS, '''        var digestHex = Convert.ToHexStringLower(entry.JournalEventDigest);''',
      '''        if (entry.ArchiveDeliveryState.Length > 0)
        {
            return new JournalReceiptVerdict(JournalReceiptStatus.Verified, JournalReceiptCodes.Verified);
        }

        var digestHex = Convert.ToHexStringLower(entry.JournalEventDigest);''')])
mut('MV-G8-09', 'receipt: the cache is not keyed by the row digest (stale receipts)',
    [(RECEIPTS, 'var key = (entry.OrganizationId, entry.JournalStreamName, entry.JournalStreamRevision, digestHex);',
      'var key = (entry.OrganizationId, entry.JournalStreamName, entry.JournalStreamRevision, digestHex.Length.ToString(CultureInfo.InvariantCulture));')])
mut('MV-G8-10', 'receipt: an archive outage counts as delivered',
    [(RECEIPTS, '''        catch (ArchiveUnavailableException)
        {
            return JournalReceiptVerdict.Unavailable(JournalReceiptCodes.ArchiveUnavailable);
        }''', '''        catch (ArchiveUnavailableException)
        {
            return new JournalReceiptVerdict(JournalReceiptStatus.Verified, JournalReceiptCodes.Verified);
        }''')])
mut('MV-G8-DB1', 'trigger: immutable fields are not checked (body, digest)',
    [(SQL, '''               OR NEW.journal_event_body_json IS DISTINCT FROM OLD.journal_event_body_json
               OR NEW.journal_event_digest IS DISTINCT FROM OLD.journal_event_digest''', '''               OR false''')], 'pg')
mut('MV-G8-DB2', 'trigger: an Anchored entry is not final',
    [(SQL, '''            IF OLD.archive_delivery_state = 'Anchored' THEN
                IF NEW IS DISTINCT FROM OLD THEN''', '''            IF OLD.archive_delivery_state = 'Anchored' THEN
                IF false THEN''')], 'pg')
mut('MV-G8-DB3', 'trigger: a VerificationFailed entry may change freely',
    [(SQL, "                IF NEW IS DISTINCT FROM OLD AND NOT (NEW.archive_delivery_state = 'Pending'", "                IF false AND NOT (NEW.archive_delivery_state = 'Pending'")], 'pg')
mut('MV-G8-DB4', 'trigger: deletion is not refused',
    [(SQL, '''            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'G08: security journal entries are append-only; they are never deleted' USING ERRCODE = 'P0001';
            END IF;''', '''            IF TG_OP = 'DELETE' THEN
                RETURN OLD;
            END IF;''')], 'pg')
mut('MV-G8-DB5', 'grant: the runtime role gets table-wide UPDATE on the journal',
    [(GRANTS, '''        statements.Add($"GRANT SELECT, INSERT ON {schema}.{JournalTable} TO {runtimeRole}");''',
      '''        statements.Add($"GRANT SELECT, INSERT, UPDATE ON {schema}.{JournalTable} TO {runtimeRole}");''')], 'pg')
mut('MV-G8-DB6', 'grant: the verifier role can insert into the journal',
    [(GRANTS, '''        yield return $"GRANT SELECT ON {schema}.{JournalTable} TO {JournalVerifierRole}";''',
      '''        yield return $"GRANT SELECT, INSERT, UPDATE ON {schema}.{JournalTable} TO {JournalVerifierRole}";''')], 'pg')


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
    log = os.path.join(out, 'tests.log')
    if m['kind'] == 'pg':
        exit_code, test_secs = run([pwsh, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(clone, 'scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1'),
                                    '-Class', NS + 'JournalVerificationPostgresIntegrationTests', '-MethodFilter', '*G08*'], log, timeout=1800)
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
