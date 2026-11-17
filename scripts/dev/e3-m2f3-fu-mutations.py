"""E3-M2f-3 FU-M2f3-01/02 mutation driver (Development/Testing only; record docs/security/E3_M2F3_FU_VERIFICATION_RECORD.md).

Operates ONLY on a separate scratch clone given by --clone (git clone --local of the repository plus the uncommitted test files),
never on the working tree, so an interrupted run cannot leave a product file mutated in the repository. For each mutation: verify
every anchor occurs exactly once, keep the original bytes in memory, apply the edits, build the affected projects with
--no-incremental, run the declared test classes and/or the disposable PostgreSQL gates (scripts/dev/e3-m2f3i-index-pg-gate.ps1,
scripts/dev/e3-m2f3g-evidence-pg-gate.ps1, -BigRows 200000), classify (DETECTED / SURVIVED / INCONCLUSIVE), restore the exact original
bytes in a finally block, verify SHA-256 and touch the file so the next build cannot reuse a stale binary. BASE-* and RESTORED-*
entries run the same checks with no edit. Evidence: docs/evidence/e3-m2f3-fu (exported, scrubbed summaries).

Example (Git Bash):
  git clone --local --no-hardlinks <repo> <scratch>/mz   # then copy the uncommitted test files into the clone
  python scripts/dev/e3-m2f3-fu-mutations.py --clone <scratch>/mz --out <scratch>/mz-out --legacy <folder with business-11e15ee and business-0be5f52> --only MZ-24
"""
import argparse, hashlib, json, os, re, shutil, subprocess, sys, time

ap = argparse.ArgumentParser()
ap.add_argument('--clone', required=True)
ap.add_argument('--out', required=True)
ap.add_argument('--only', required=True, help='comma list of mutation ids, or BASELINE')
ap.add_argument('--legacy', default='')
ap.add_argument('--dry-run', action='store_true')
a = ap.parse_args()
clone = a.clone
dotnet = shutil.which('dotnet')
pwsh = shutil.which('powershell')

REC = 'src/authority/AuthorizationAuthority.Application/Features/Content/CapabilityRefusalEvidenceRecorder.cs'
VOC = 'src/authority/AuthorizationAuthority.Application/Features/Content/CapabilityRefusalEvidence.cs'
FLOOR = 'src/authority/AuthorizationAuthority.Application/Features/Content/EnvelopeCapabilityFloor.cs'
SVC = 'src/authority/AuthorizationAuthority.Application/Features/Content/ContentService.cs'
FILTER = 'src/authority/AuthorizationAuthority.Api/Composition/CapabilityRefusalEvidenceComposition.cs'
ARCH = 'src/authority/AuthorizationAuthority.Application/Features/Journal/ArchiveProcessor.cs'
IDX = 'src/authority/AuthorizationAuthority.Infrastructure/Persistence/CapabilityRefusalEvidenceIndex.cs'
CMD = 'src/authority/AuthorizationAuthority.Api/Composition/CapabilityRefusalEvidenceIndexCommand.cs'

PROJ = {
    'A': 'tests/authority/AuthorizationAuthority.Tests/AuthorizationAuthority.Tests.csproj',
    'H': 'tests/features/EnterpriseForms.Api.ContractTests/EnterpriseForms.Api.ContractTests.csproj',
    'X': 'tests/architecture/EnterpriseForms.ArchitectureTests/EnterpriseForms.ArchitectureTests.csproj',
}
EXE = {
    'A': 'tests/authority/AuthorizationAuthority.Tests/bin/Debug/net10.0/AuthorizationAuthority.Tests.exe',
    'H': 'tests/features/EnterpriseForms.Api.ContractTests/bin/Debug/net10.0/EnterpriseForms.Api.ContractTests.exe',
    'X': 'tests/architecture/EnterpriseForms.ArchitectureTests/bin/Debug/net10.0/EnterpriseForms.ArchitectureTests.exe',
}
NS = {'A': 'AuthorizationAuthority.Tests.', 'H': 'EnterpriseForms.Api.ContractTests.', 'X': 'EnterpriseForms.ArchitectureTests.'}

FILTER_ANCHOR = '''        catch (Exception refused) when (CapabilityRefusalContext.From(refused) is not null)
        {
            var refusal = CapabilityRefusalContext.From(refused)!;'''
ARGS_ORG = 'context.Arguments.Select(a => a?.GetType().GetProperty("OrganizationId")?.GetValue(a)).OfType<Guid>().FirstOrDefault()'

M = {}
def mut(id, what, edits, checks, kind='MZ'):
    M[id] = dict(id=id, what=what, edits=edits, checks=checks, kind=kind)

# ---------------- violation demonstrations for the three dedicated tests ----------------
mut('V22-1', 'any AuthorityException is treated as a capability refusal (context synthesized when absent): pre-floor rejections inside the endpoint reach the recorder',
    [(VOC, '''        exception is AuthorityException { ErrorCode: AuthorityErrorCodes.ClientEnvelopeCapabilityInsufficient } refusal
        && refusal.Data[ExceptionDataKey] is CapabilityRefusalContext context
            ? context
            : null;''', '''        exception is AuthorityException refusal
            ? refusal.Data[ExceptionDataKey] as CapabilityRefusalContext ?? new CapabilityRefusalContext(Guid.Empty, 1)
            : null;''')],
    [('H', ['CapabilityRefusalEvidencePreFloorHostTests', 'CapabilityRefusalEvidenceHostTests'])], kind='V')
mut('V22-2', 'the filter records every rejected level-1 request (organization taken from the request arguments), not only the floor refusal',
    [(FILTER, FILTER_ANCHOR, '''        catch (Exception refused) when (CapabilityRefusalContext.From(refused) is not null || context.HttpContext.Request.Headers[ClientEnvelopeCapability.HeaderName].Count == 0)
        {
            var refusal = CapabilityRefusalContext.From(refused) ?? new CapabilityRefusalContext(''' + ARGS_ORG + ''', 1);''')],
    [('H', ['CapabilityRefusalEvidencePreFloorHostTests', 'CapabilityRefusalEvidenceHostTests'])], kind='V')
mut('V25-1', 'a summary event is appended (and committed) when T2 finds the key already recorded',
    [(REC, '''                    case CapabilityRefusalEvidenceVerdict.AlreadyRecorded:
                        return (CapabilityRefusalEvidenceOutcome.AlreadyRecorded, CapabilityRefusalEvidenceReason.None, databaseTime);''', '''                    case CapabilityRefusalEvidenceVerdict.AlreadyRecorded:
                        AuditJournal.Append(scope, locked, "EnvelopeCapabilityRefusalSummary", new { organization_id = key.OrganizationId, entry_point = key.EntryPoint, suppressed_repeats = 1 }, clock);
                        await scope.CommitAsync(token).ConfigureAwait(false);
                        return (CapabilityRefusalEvidenceOutcome.AlreadyRecorded, CapabilityRefusalEvidenceReason.None, databaseTime);''')],
    [('A', ['CapabilityRefusalEvidenceNoSummaryTests', 'CapabilityRefusalEvidenceRecorderTests'])], kind='V')
mut('V25-2', 'a summary event (count of in-process suppressions) is appended together with each evidence entry',
    [(REC, '''                AuditJournal.Append(scope, locked, CapabilityRefusalEvidence.EventKind, body, clock);
                committing = true;''', '''                AuditJournal.Append(scope, locked, CapabilityRefusalEvidence.EventKind, body, clock);
                AuditJournal.Append(scope, locked, "EnvelopeCapabilityRefusalSummary", new { organization_id = key.OrganizationId, suppressed_in_process = CachedCount }, clock);
                committing = true;''')],
    [('A', ['CapabilityRefusalEvidenceNoSummaryTests', 'CapabilityRefusalEvidenceRecorderTests'])], kind='V')
mut('V43-1', 'the archive processor no longer checks a pending body against its digest',
    [(ARCH, 'if (!CanonicalJson.Digest(body).AsSpan().SequenceEqual(entry.JournalEventDigest))', 'if (Environment.ProcessorCount < 0)')],
    [('A', ['CapabilityRefusalEvidenceRemovalTests', 'ArchiveProcessorTests'])], kind='V')
mut('V43-2', 'the archive processor treats an archive conflict as a successful delivery (no quarantine)',
    [(ARCH, '''            catch (ArchiveConflictException)
            {
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.VerificationFailed;
                entry.LastArchiveErrorCode = "ARCHIVE_BODY_MISMATCH";
                OrganizationQuarantine.Set(state, "ARCHIVE_BODY_MISMATCH");
                break;
            }''', '''            catch (ArchiveConflictException)
            {
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
                archived++;
            }''')],
    [('A', ['CapabilityRefusalEvidenceRemovalTests', 'ArchiveProcessorTests'])], kind='V')

# ---------------- FU-M2f3-02: the eight unexecuted closure mutations ----------------
FLOOR_BOUND = '''        if (bound)
        {
            throw Refuse(organizationId, level);
        }'''
T1_APPEND = '''            var observedNow = DateTimeOffset.UtcNow;
            AuthorizationAuthority.Application.Features.Journal.AuditJournal.Append(scope, lockedOrganization, CapabilityRefusalEvidence.EventKind,
                new CapabilityRefusalEvidenceBody(CapabilityRefusalEvidence.SchemaVersion, CapabilityRefusalEvidence.RefusalCode, organizationId, CapabilityRefusalEvidence.Workloads.BusinessApi,
                    CapabilityRefusalEvidence.EntryPoints.Prepare, level, observedNow, CapabilityRefusalEvidence.BucketOf(observedNow), observedNow), TimeProvider.System);
'''
FLOOR_CHECKS = [('A', ['CapabilityRefusalEvidenceRecorderTests', 'EnvelopeCapabilityFloorTests']),
                ('H', ['CapabilityRefusalEvidenceHostTests', 'EnvelopeCapabilityEnforcementHostTests']),
                ('X', ['CapabilityRefusalEvidenceRecorderBoundaryTests'])]
mut('MZ-02a', 'record inside T1: the floor stages the evidence entry in the refused transaction (not committed; T1 rolls back)',
    [(FLOOR, FLOOR_BOUND, '''        if (bound)
        {
''' + T1_APPEND + '''            throw Refuse(organizationId, level);
        }''')], FLOOR_CHECKS)
mut('MZ-02b', 'record inside T1 and commit it there',
    [(FLOOR, FLOOR_BOUND, '''        if (bound)
        {
''' + T1_APPEND + '''            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw Refuse(organizationId, level);
        }''')], FLOOR_CHECKS)

VERIFY_ANCHOR = '''        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        // E3-M2f-2 (CAP-O2/O3): right after the lock, before actor resolution, pinning and verification (G-2: no false quarantine).
        await CapabilityFloor.RequireAsync(scope, state, envelopeCapability, cancellationToken).ConfigureAwait(false);
        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);'''
mut('MZ-03a', 'closest expressible form of "record before next returns": the Verify entry point lets the refusal leave before T1 is disposed (disposal deferred 2 s), so the filter records while T1 is still open',
    [(SVC, VERIFY_ANCHOR, '''        ArgumentNullException.ThrowIfNull(request);
#pragma warning disable CA2000, CA2012, CA2008
        var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        _ = Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ContinueWith(_ => scope.DisposeAsync().AsTask(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
#pragma warning restore CA2000, CA2012, CA2008
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        // E3-M2f-2 (CAP-O2/O3): right after the lock, before actor resolution, pinning and verification (G-2: no false quarantine).
        await CapabilityFloor.RequireAsync(scope, state, envelopeCapability, cancellationToken).ConfigureAwait(false);
        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);''')],
    [('H', ['CapabilityRefusalEvidenceHostTests']), ('X', ['CapabilityRefusalEvidenceRecorderBoundaryTests'])])

mut('MZ-09a', 'record on admit: after an admitted request completes, the filter records evidence (organization from the request arguments)',
    [(FILTER, '            return await next(context).ConfigureAwait(false);', '''            var admitted = await next(context).ConfigureAwait(false);
            await context.HttpContext.RequestServices.GetRequiredService<CapabilityRefusalEvidenceRecorder>()
                .RecordAsync(new CapabilityRefusalContext(''' + ARGS_ORG + ''', 1), EntryPoint, CancellationToken.None).ConfigureAwait(false);
            return admitted;''')],
    [('H', ['CapabilityRefusalEvidenceHostTests']), ('X', ['CapabilityRefusalEvidenceRecorderBoundaryTests'])])
mut('MZ-09a-full', 'MZ-09a re-run against the FULL Authority and contract suites (does anything outside the declared tests detect it?)',
    M['MZ-09a']['edits'], [('A', []), ('H', [])])
# FU-M2f3-03 (2026-11-17): MZ-09 against the new success-path test for M2F3-21 (and the existing host tests).
FU03 = [('H', ['CapabilityRefusalEvidenceAdmittedSuccessHostTests', 'CapabilityRefusalEvidenceHostTests'])]
mut('MZ-09a-fu03', 'MZ-09a (record after an admitted request completes) against the FU-M2f3-03 success-path test', M['MZ-09a']['edits'], FU03)
mut('MZ-09b', 'record on admit: every exception of an admitted request is recorded (catch no longer limited to the floor refusal)',
    [(FILTER, FILTER_ANCHOR, '''        catch (Exception refused)
        {
            var refusal = CapabilityRefusalContext.From(refused) ?? new CapabilityRefusalContext(''' + ARGS_ORG + ''', 1);''')],
    [('H', ['CapabilityRefusalEvidenceHostTests']), ('X', ['CapabilityRefusalEvidenceRecorderBoundaryTests'])])

mut('MZ-13', 'append again after an ambiguous commit without a lookup decision (the retry of an Ambiguous key ignores the durable lookup)',
    [(REC, '    private readonly HashSet<CapabilityRefusalEvidenceKey> inFlight = [];', '''    private readonly HashSet<CapabilityRefusalEvidenceKey> inFlight = [];
    private readonly HashSet<CapabilityRefusalEvidenceKey> ambiguousKeys = [];'''),
     (REC, '''        catch (Exception) when (committing)
        {''', '''        catch (Exception) when (committing)
        {
            lock (gate)
            {
                ambiguousKeys.Add(key);
            }
'''),
     (REC, '                var (verdict, reason) = CapabilityRefusalEvidenceLookup.Decide(rows, key, databaseTime.Value);', '''                bool afterAmbiguous;
                lock (gate)
                {
                    afterAmbiguous = ambiguousKeys.Remove(key);
                }

                var (verdict, reason) = afterAmbiguous ? (CapabilityRefusalEvidenceVerdict.Append, CapabilityRefusalEvidenceReason.None) : CapabilityRefusalEvidenceLookup.Decide(rows, key, databaseTime.Value);''')],
    [('A', ['CapabilityRefusalEvidenceRecorderTests', 'CapabilityRefusalEvidenceNoSummaryTests']), ('X', ['CapabilityRefusalEvidenceRecorderBoundaryTests'])])

LOCK_BLOCK = '''                await scope.SetCapabilityEvidenceLockTimeoutAsync(token).ConfigureAwait(false);
                var locked = await scope.LockOrganizationAsync(key.OrganizationId, token).ConfigureAwait(false);
                if (locked is null)
                {
                    return (CapabilityRefusalEvidenceOutcome.Deferred, CapabilityRefusalEvidenceReason.Error, null);
                }

                databaseTime = await scope.StartCapabilityEvidenceLookupAsync(token).ConfigureAwait(false);
                if (!await scope.IsCapabilityRefusalEvidenceIndexUsableAsync(token).ConfigureAwait(false))
                {
                    return (CapabilityRefusalEvidenceOutcome.Deferred, CapabilityRefusalEvidenceReason.IndexUnavailable, databaseTime);
                }

                var rows = await scope.GetNewestCapabilityRefusalEvidenceAsync(key.OrganizationId, token).ConfigureAwait(false);'''
mut('MZ-24', 'look up before taking the organization lock (clock, index check and lookup moved before LockOrganizationAsync)',
    [(REC, LOCK_BLOCK, '''                await scope.SetCapabilityEvidenceLockTimeoutAsync(token).ConfigureAwait(false);
                databaseTime = await scope.StartCapabilityEvidenceLookupAsync(token).ConfigureAwait(false);
                if (!await scope.IsCapabilityRefusalEvidenceIndexUsableAsync(token).ConfigureAwait(false))
                {
                    return (CapabilityRefusalEvidenceOutcome.Deferred, CapabilityRefusalEvidenceReason.IndexUnavailable, databaseTime);
                }

                var rows = await scope.GetNewestCapabilityRefusalEvidenceAsync(key.OrganizationId, token).ConfigureAwait(false);
                var locked = await scope.LockOrganizationAsync(key.OrganizationId, token).ConfigureAwait(false);
                if (locked is null)
                {
                    return (CapabilityRefusalEvidenceOutcome.Deferred, CapabilityRefusalEvidenceReason.Error, null);
                }''')],
    [('A', ['CapabilityRefusalEvidenceRecorderTests']), ('X', ['CapabilityRefusalEvidenceRecorderBoundaryTests']), ('PG3G', 'T')])

INDEX_CHECKS = [('A', ['CapabilityRefusalEvidenceIndexTests']), ('X', ['CapabilityRefusalEvidenceIndexBoundaryTests']), ('PG3I', '')]
mut('MZ-34a', 'the index command re-applies the runtime grants (AuthorityRuntimeGrants.Apply) after the build',
    [(CMD, '            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);', '''            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using (var grants = new AuthorityDbContext(Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions.UseNpgsql(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AuthorityDbContext>(), connection).Options))
            {
                AuthorityRuntimeGrants.Apply(grants, "authority_runtime");
            }''')], INDEX_CHECKS)
mut('MZ-34b', 'the index command runs the concurrent build inside a transaction block',
    [(CMD, '''            await using var create = new NpgsqlCommand(CapabilityRefusalEvidenceIndex.CreateSql, connection) { CommandTimeout = 0 };
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);''', '''            await using var buildTransaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var create = new NpgsqlCommand(CapabilityRefusalEvidenceIndex.CreateSql, connection, buildTransaction) { CommandTimeout = 0 };
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await buildTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);''')], INDEX_CHECKS)
mut('MZ-35', 'an INVALID index (indisvalid = false) is classified as present (ValidMatching) by the shared classification of the command and T2-8',
    [(IDX, 'if (facts.IsValid && facts.IsReady && facts.IsLive)', 'if (facts.IsReady && facts.IsLive)')],
    INDEX_CHECKS + [('PG3G', 'L')])
mut('MZ-36a', 'the index command creates the index with an event-kind literal that differs from the recorder constant',
    [(IDX, '''        + "' AND journal_event_kind = '" + CapabilityRefusalEvidence.EventKind + "'";''', '''        + "' AND journal_event_kind = 'EnvelopeCapabilityRefusal'";''')], INDEX_CHECKS)
mut('MZ-36b', 'the shared pinned definition (CREATE statement and expected predicate) uses an event-kind literal that differs from the recorder constant; the lookup keeps the constant',
    [(IDX, '''        + "' AND journal_event_kind = '" + CapabilityRefusalEvidence.EventKind + "'";''', '''        + "' AND journal_event_kind = 'EnvelopeCapabilityRefusal'";'''),
     (IDX, '''        + CapabilityRefusalEvidence.EventKind + "'::text))";''', '''        + "EnvelopeCapabilityRefusal" + "'::text))";''')],
    INDEX_CHECKS + [('PG3G', 'L')])

# ---------------- unmutated baselines (no edits; same checks) ----------------
UNIT_ALL = [('A', ['CapabilityRefusalEvidenceRecorderTests', 'EnvelopeCapabilityFloorTests', 'CapabilityRefusalEvidenceNoSummaryTests', 'CapabilityRefusalEvidenceRemovalTests',
                   'ArchiveProcessorTests', 'CapabilityRefusalEvidenceIndexTests', 'CapabilityRefusalEvidenceLookupTests']),
            ('H', ['CapabilityRefusalEvidenceHostTests', 'EnvelopeCapabilityEnforcementHostTests', 'CapabilityRefusalEvidencePreFloorHostTests']),
            ('X', ['CapabilityRefusalEvidenceRecorderBoundaryTests', 'CapabilityRefusalEvidenceIndexBoundaryTests'])]
mut('BASE-UNIT', 'unmutated: every declared test class', [], UNIT_ALL, kind='BASE')
mut('BASE-PG3I', 'unmutated: E3-M2f-3i PostgreSQL gate', [], [('PG3I', '')], kind='BASE')
mut('BASE-PG3G', 'unmutated: E3-M2f-3G PostgreSQL gate, groups T and L', [], [('PG3G', 'T,L')], kind='BASE')

mut('MZ-09b-fu03', 'MZ-09b (record every exception of an admitted request) against the FU-M2f3-03 success-path test', M['MZ-09b']['edits'], FU03)
mut('BASE-FU03', 'unmutated: FU-M2f3-03 test and the existing evidence host tests', [], FU03, kind='BASE')
mut('RESTORED-UNIT', 'restored tree after all mutations: every declared test class', [], UNIT_ALL, kind='BASE')
mut('RESTORED-PG3I', 'restored tree after all mutations: E3-M2f-3i PostgreSQL gate', [], [('PG3I', '')], kind='BASE')
mut('RESTORED-PG3G', 'restored tree after all mutations: E3-M2f-3G PostgreSQL gate, groups T and L', [], [('PG3G', 'T,L')], kind='BASE')


def sha(p):
    return hashlib.sha256(open(p, 'rb').read()).hexdigest()


def run(cmd, log, timeout=3000):
    t = time.time()
    with open(log, 'w', encoding='utf-8', errors='replace') as f:
        p = subprocess.run(cmd, cwd=clone, stdout=f, stderr=subprocess.STDOUT, timeout=timeout)
    return p.returncode, round(time.time() - t, 1)


def build(letter, out, release_authority=False):
    log = os.path.join(out, f'build-{letter}.log')
    if letter == 'R':
        return run([dotnet, 'build', os.path.join(clone, 'src/authority/AuthorizationAuthority.Api/AuthorizationAuthority.Api.csproj'), '-c', 'Release', '--no-incremental', '-nologo', '-v', 'q'], log)
    return run([dotnet, 'build', os.path.join(clone, PROJ[letter]), '--no-incremental', '-nologo', '-v', 'q'], log)


def run_classes(letter, classes, out):
    args = [os.path.join(clone, EXE[letter])]
    for c in classes:
        args += ['--filter-class', NS[letter] + c]
    log = os.path.join(out, f'tests-{letter}.log')
    code, secs = run(args, log, timeout=1500)
    text = open(log, encoding='utf-8', errors='replace').read()
    failed = sorted(set(re.findall(r'^failed (\S+)', text, re.M)))
    total = re.findall(r'^\s+total: (\d+)', text, re.M)
    passed = re.findall(r'^\s+succeeded: (\d+)', text, re.M)
    return dict(letter=letter, classes=classes, exit=code, seconds=secs, total=int(total[-1]) if total else None,
                succeeded=int(passed[-1]) if passed else None, failed=failed)


def run_pg(kind, groups, out, label):
    ev = os.path.join(out, 'pg3i' if kind == 'PG3I' else 'pg3g-' + groups)
    if kind == 'PG3I':
        cmd = [pwsh, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(clone, 'scripts', 'dev', 'e3-m2f3i-index-pg-gate.ps1'), '-Evidence', ev, '-BigRows', '200000']
        report = os.path.join(ev, 'm2f3i-evidence.json')
    else:
        cmd = [pwsh, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(clone, 'scripts', 'dev', 'e3-m2f3g-evidence-pg-gate.ps1'), '-Evidence', ev, '-Groups', groups,
               '-BigRows', '200000', '-RunLabel', label]
        if a.legacy:
            cmd += ['-LegacyBuildsRoot', a.legacy]
        report = os.path.join(ev, 'm2f3g-evidence.json')
    code, secs = run(cmd, ev + '.console.txt', timeout=5400)
    if not os.path.exists(report):
        return dict(kind=kind, groups=groups, exit=code, seconds=secs, report=False, failed=None)
    data = json.load(open(report, encoding='utf-8-sig'))
    checks = data.get('checks', [])
    failed = [c.get('Id') or c.get('id') for c in checks if not next(c[k] for k in ('Pass', 'passed', 'pass') if k in c)]
    return dict(kind=kind, groups=groups, exit=code, seconds=secs, report=True, checks=len(checks), failed=failed)


def execute(checks, out, label):
    results = []
    letters = [c[0] for c in checks if c[0] in PROJ]
    if any(c[0].startswith('PG') for c in checks):
        code, secs = build('R', out)
        results.append(dict(build='R', exit=code, seconds=secs))
        if code != 0:
            return results, False
    for letter in letters:
        code, secs = build(letter, out)
        results.append(dict(build=letter, exit=code, seconds=secs))
        if code != 0:
            return results, False
    for kind, arg in checks:
        if kind in PROJ:
            results.append(run_classes(kind, arg, out))
        else:
            results.append(run_pg(kind, arg, out, label))
    return results, True


def classify(results, built):
    if not built:
        return 'INCONCLUSIVE (build failed)'
    detected, unknown = False, False
    for r in results:
        if 'build' in r:
            continue
        if r.get('report') is False or ('letter' in r and r['total'] is None):
            unknown = True
        elif r['failed']:
            detected = True
    if detected:
        return 'DETECTED'
    return 'INCONCLUSIVE (no report)' if unknown else 'SURVIVED'


os.makedirs(a.out, exist_ok=True)
ids = a.only.split(',')

for mid in ids:
    m = M[mid]
    for f, anchor, _ in m['edits']:
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
        results, built = execute(m['checks'], out, 'mutation-' + mid)
        verdict = classify(results, built)
    finally:
        for f in files:
            with open(os.path.join(clone, f), 'wb') as h:
                h.write(originals[f])
            os.utime(os.path.join(clone, f), None)
    restored = all(sha(os.path.join(clone, f)) == before[f] for f in files)
    record = dict(id=mid, kind=m['kind'], what=m['what'], files=files, original_sha256=before, restored=restored, verdict=verdict,
                  results=results, seconds=round(time.time() - started, 1))
    json.dump(record, open(os.path.join(out, 'result.json'), 'w', encoding='utf-8'), indent=2)
    print(json.dumps(dict(id=mid, verdict=verdict, restored=restored, failed={(r.get('letter') or r.get('kind')): r.get('failed') for r in results if 'build' not in r},
                          builds=[(r['build'], r['exit']) for r in results if 'build' in r], seconds=record['seconds'])))
    if not restored:
        raise SystemExit(mid + ': RESTORATION FAILED')
