"""E3-M2e-1 enforcement-removal driver (pattern of the E3-M2d driver). Applies one mutation at a time to the Authority source, rebuilds,
runs the focused test classes (time-boxed; "unit") or the disposable-PostgreSQL gate ("pg"), restores every file from a backup outside
the repository and verifies its sha256. Writes a JSON result. Usage: m2e1_mutations.py <repo> <out.json> [unit|pg] [ids,...]
Non-constant conditions (Environment.ProcessorCount) keep every mutant compiling with warnings as errors."""
import hashlib, json, os, re, shutil, subprocess, sys, tempfile

ROOT = sys.argv[1]
OUT = sys.argv[2]
MODE = sys.argv[3] if len(sys.argv) > 3 else "unit"
SCRATCH = tempfile.mkdtemp(prefix="m2e1-mutations-")  # backups and gate evidence live outside the repository
A = os.path.join(ROOT, "src", "authority")
CONTENT = os.path.join(A, "AuthorizationAuthority.Application", "Features", "Content")
PERSIST = os.path.join(A, "AuthorizationAuthority.Infrastructure", "Persistence")
SERVICE = os.path.join(CONTENT, "ContentService.cs")
LEDGER = os.path.join(CONTENT, "DataKeyLedgerRules.cs")
SWITCH = os.path.join(CONTENT, "EnvelopeV2AcceptanceSwitch.cs")
OBSERVER = os.path.join(CONTENT, "EnvelopeAcceptancePolicyObserver.cs")
CARV = os.path.join(CONTENT, "ContentAcceptanceRules.cs")
V2 = os.path.join(CONTENT, "EnvelopeV2Rules.cs")
LEDGER_SQL = os.path.join(PERSIST, "ContentDataKeyLedgerSql.cs")
FLOOR3 = os.path.join(PERSIST, "ContentAcceptanceFloor3Sql.cs")
STORE = os.path.join(PERSIST, "AuthorityStore.cs")
GRANTS = os.path.join(PERSIST, "AuthorityRuntimeGrants.cs")
REFUSALS = os.path.join(PERSIST, "DataKeyLedgerRefusals.cs")
MIGRATION = os.path.join(PERSIST, "Migrations", "20261116031706_AuthorityContentDataKeyBindings.cs")
FILES = [SERVICE, LEDGER, SWITCH, OBSERVER, CARV, V2, LEDGER_SQL, FLOOR3, STORE, GRANTS, REFUSALS, MIGRATION]

AUTHORITY_PROJECT = os.path.join(ROOT, "tests", "authority", "AuthorizationAuthority.Tests", "AuthorizationAuthority.Tests.csproj")
AUTHORITY_EXE = os.path.join(ROOT, "tests", "authority", "AuthorizationAuthority.Tests", "bin", "Debug", "net10.0", "AuthorizationAuthority.Tests.exe")
ARCH_PROJECT = os.path.join(ROOT, "tests", "architecture", "EnterpriseForms.ArchitectureTests", "EnterpriseForms.ArchitectureTests.csproj")
ARCH_EXE = os.path.join(ROOT, "tests", "architecture", "EnterpriseForms.ArchitectureTests", "bin", "Debug", "net10.0", "EnterpriseForms.ArchitectureTests.exe")
CLASSES = ["EnvelopeV2RulesTests", "ContributorWriteProtocolTests", "DataKeyLedgerTests", "ContentAcceptanceFloor2Tests", "ContentAcceptanceFloorTests",
           "ContentAcceptancePolicyTests", "ContentAcceptanceRuleGuardTests", "RuntimeGrantsTests"]
ARCH_CLASSES = ["EnvelopeV2RuleBoundaryTests", "SearchIndexBoundaryTests"]
GATE = os.path.join(ROOT, "scripts", "dev", "e3-m2e1-ledger-pg-gate.ps1")
NEVER = "Environment.ProcessorCount < 0"

M = {
    # ---- DK-4 / DK-5 (Prepare) ----
    "ME-01": ("DK-4 removed: a bound identifier is no longer refused", [(LEDGER, "        return existingBindings.Any(b => ids.Contains(b.DataKeyIdentifier)) ? EnvelopeV2Rules.Reasons.DataKeyIdentifierReused : null;", "        return " + NEVER + " ? EnvelopeV2Rules.Reasons.DataKeyIdentifierReused : null;", 1)]),
    "ME-02": ("DK-5 removed: New groups are not bound in the Prepare transaction", [(SERVICE, "            scope.AddDataKeyBindings(DataKeyLedgerRules.BindingsFor(newV2Groups, state.OrganizationId, request.FormRecordId, operationId, clock.GetUtcNow()));\n", "", 1)]),
    "ME-03": ("the Prepare refusal of New v2 groups with acceptance off removed", [(SERVICE, "        if (newV2Rows.Count > 0 && !v2Acceptance.Enabled)", "        if (newV2Rows.Count > 0 && " + NEVER + ")", 1)]),
    "ME-04": ("AM-9 environment wrap codes no longer enforced at Prepare", [(SERVICE, "        EnvelopeV2Rules.EnforceEnvironmentWrapCodes(parsed.Select(p => p.Document), v2Acceptance.AllowedWrapCodes);\n", "", 1)]),
    # ---- DK-6 / P-4 / switch re-check / L-13 / incident (Commit) ----
    "ME-05": ("DK-6 / P-4: the commitment is no longer compared", [(LEDGER, "        && binding.DataKeyCommitmentBytes.AsSpan().SequenceEqual(group.Commitment);", ";", 1)]),
    "ME-06": ("DK-6: the item count of a New group is no longer compared", [(LEDGER, " || binding.BindingItemCount != group.ItemCount)", ")", 1)]),
    "ME-07": ("P-4: an unbound v2 identifier is accepted", [(LEDGER, "                return (mismatch, []); // an unbound v2 identifier can never be committed", "                continue; // mutated", 1)]),
    "ME-08": ("P-4: a binding of an operation that is not Committed is accepted", [(LEDGER, "            if (!committedOperationIds.Contains(binding.BindingContentChangeOperationId) || !SameTuple(", "            if (!SameTuple(", 1)]),
    "ME-09": ("P-4: more staged rows than the batch accepted", [(LEDGER, "\n                || group.ItemCount > binding.BindingItemCount)", ")", 1)]),
    "ME-10": ("the Commit switch re-check removed (a New group commits with acceptance off)", [(SERVICE, "            if (newGroups.Count > 0 && !v2Acceptance.Enabled)", "            if (newGroups.Count > 0 && " + NEVER + ")", 1)]),
    "ME-11": ("L-13 no longer required at Commit", [(SERVICE, "            await scope.RequireDataKeyLedgerAsync(cancellationToken).ConfigureAwait(false); // L-13\n", "", 1)]),
    "ME-12": ("DK-6 / P-4 mismatch journals no incident", [(SERVICE, "                scope.AddJournalEntry(JournalChain.Append(state, JournalStreamNames.Content, state.ContentIntegrityRevision, \"IntegrityVerificationFailed\", incident, null, head.CommittedOperationId, clock));\n                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);\n                throw DataKeyLedgerFailures.BindingMismatch();",
                                                       "                _ = incident;\n                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);\n                throw DataKeyLedgerFailures.BindingMismatch();", 1)]),
    "ME-13": ("DK-8: the archived tuples are dropped from the body", [(SERVICE, "            DataKeyLedgerRules.ArchiveTuples(newBindings));", "            DataKeyLedgerRules.ArchiveTuples([]));", 1)]),
    # ---- CARV 3, switch, observer ----
    "ME-14": ("CARV reverted to version 2", [(CARV, "    public const int CurrentVersion = 3;", "    public const int CurrentVersion = 2;", 1)]),
    "ME-15": ("the switch admits Staging", [(SWITCH, "        var developmentOrTesting = environmentName is \"Development\" or \"Testing\";", "        var developmentOrTesting = environmentName is \"Development\" or \"Testing\" or \"Staging\";", 1)]),
    "ME-16": ("the switch no longer requires Release B with RequiredFloor 3", [(SWITCH, "        var floorHolds = policy.IsValid && policy.Phase == ContentAcceptancePhase.ReleaseB && policy.RequiredFloor == RequiredFloor;", "        var floorHolds = policy.IsValid;", 1)]),
    "ME-17": ("acceptance no longer needs the recorded policy", [(SWITCH, "                return requested && StaticPrerequisitesHold && observedPolicyDigest is not null\n                    && string.Equals(observedPolicyDigest, PolicyDigestCore(requested), StringComparison.Ordinal);", "                return requested && StaticPrerequisitesHold;", 1)]),
    "ME-18": ("the observer skips recording a changed policy", [(OBSERVER, "        if (previous is not null && string.Equals(RecordedPolicyDigest(previous), descriptor.PolicyDigestHex, StringComparison.Ordinal))", "        if (previous is not null)", 1)]),
    "ME-19": ("the observer trusts a previous record whose body no longer matches its digest", [(OBSERVER, "        if (entry.JournalEventDigest is not { Length: 32 } || !CanonicalJson.Digest(bytes).AsSpan().SequenceEqual(entry.JournalEventDigest))", "        if (" + NEVER + ")", 1)]),
    # ---- store, grants, readiness, SQL text ----
    "ME-20": ("the RB-CARV-1 catch-all no longer treats a binding as a content-acceptance write", [(STORE, "ContentIntegrityCheckpoint or ContentDataKeyBinding:", "ContentIntegrityCheckpoint:", 1)]),
    "ME-21": ("the ledger is not insert-only to the runtime (dropped from InsertOnlyTables)", [(GRANTS, "    public static readonly string[] InsertOnlyTables = [\"content_item_identities\", \"content_data_key_bindings\"];", "    public static readonly string[] InsertOnlyTables = [\"content_item_identities\"];", 1)]),
    "ME-22": ("floor-3 SQL: the data-key ledger preconditions removed", [(FLOOR3, "        \"\"\" + LedgerPreconditions + \"\"\"\n", "", 1)]),
    "ME-23": ("floor-3 SQL: floor 2 not dropped by the raise", [(FLOOR3, "        ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v2;\n        \"\"\";", "        \"\"\";", 1)]),
    "ME-24": ("the ledger verification ignores the binding guard", [(LEDGER_SQL, "        if (!string.Equals(facts.GuardTriggerDefinition, ExpectedGuardTriggerDefinition, StringComparison.Ordinal)\n", "        if (" + NEVER + " && !string.Equals(facts.GuardTriggerDefinition, ExpectedGuardTriggerDefinition, StringComparison.Ordinal)\n", 1)]),
    "ME-25": ("P-1 guard SQL no longer requires a Prepared operation", [(LEDGER_SQL, "                              AND o.operation_state_name = 'Prepared'\n", "", 1)]),
    "ME-26": ("readiness no longer requires the data-key ledger migration", [(CARV, "        if (facts.DataKeyLedgerApplied is not true)", "        if (facts.DataKeyLedgerApplied is not true && " + NEVER + ")", 1)]),
    # ---- database-level (pg mode): the migration and SQL the gate exercises ----
    "ME-P1": ("migration: the P-1 binding guard trigger is not created", [(LEDGER_SQL, "        CREATE TRIGGER trg_content_data_key_binding_guard\n            BEFORE INSERT ON authority.content_data_key_bindings\n            FOR EACH ROW EXECUTE FUNCTION authority.content_data_key_binding_guard();\n", "", 1)]),
    "ME-P2": ("migration: the P-2 TRUNCATE trigger is not created", [(LEDGER_SQL, "        CREATE TRIGGER trg_content_data_key_binding_no_truncate\n            BEFORE TRUNCATE ON authority.content_data_key_bindings\n            FOR EACH STATEMENT EXECUTE FUNCTION authority.content_data_key_binding_is_permanent();\n", "", 1)]),
    "ME-P3": ("migration: the identifier index is not unique", [(MIGRATION, "                column: \"data_key_identifier\",\n                unique: true);", "                column: \"data_key_identifier\",\n                unique: false);", 1)]),
    "ME-P4": ("the store no longer translates a ledger refusal (23505 / P-1 / P-2)", [(STORE, "            catch (DbUpdateException ex) when (DataKeyLedgerRefusals.Translate(ex, alerts) is { } refused)", "            catch (DbUpdateException ex) when (" + NEVER + " && DataKeyLedgerRefusals.Translate(ex, alerts) is { } refused)", 1)]),
    "ME-P5": ("floor-3 SQL: the data-key ledger preconditions removed (database behaviour)", [(FLOOR3, "        \"\"\" + LedgerPreconditions + \"\"\"\n", "", 1)]),
}


def sha(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def run(cmd, timeout):
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, encoding="utf-8", errors="replace")
        return p.returncode, p.stdout + p.stderr, False
    except subprocess.TimeoutExpired as e:
        return -1, (e.stdout or "") if isinstance(e.stdout, str) else "", True


def run_tests(exe, classes, prefix):
    args = [exe, "--progress", "off"]
    for c in classes:
        args += ["--filter-class", prefix + c]
    code, out, timed_out = run(args, 600)
    summary = re.search(r"total: (\d+)\s+failed: (\d+)\s+succeeded: (\d+)\s+skipped: (\d+)", out)
    failed = sorted(set(m.group(1).strip() for m in re.finditer(r"^\s*failed (.+?) \(\d", out, re.M)))
    return (int(summary.group(2)) if summary else None), failed, timed_out


original = {f: sha(f) for f in FILES}
backups = {}
for f in FILES:
    b = os.path.join(SCRATCH, "backup-" + os.path.basename(f))
    shutil.copyfile(f, b)
    backups[f] = b

only = sys.argv[4].split(",") if len(sys.argv) > 4 else [k for k in M if not k.startswith("ME-P")]
results = []
for mid in only:
    description, edits = M[mid]
    entry = {"id": mid, "mutation": description, "mode": MODE}
    try:
        for path, old, new, count in edits:
            text = open(path, encoding="utf-8", newline="").read()
            normalized = text.replace("\r\n", "\n")
            n = normalized.count(old)
            if n != count:
                raise RuntimeError(f"{os.path.basename(path)}: expected {count} occurrences, found {n}")
            out_text = normalized.replace(old, new)
            if "\r\n" in text:
                out_text = out_text.replace("\n", "\r\n")
            open(path, "w", encoding="utf-8", newline="").write(out_text)
        if MODE == "pg":
            evidence = os.path.join(SCRATCH, "gate-" + mid)
            code, out, timed_out = run(["powershell", "-NoProfile", "-File", GATE, "-Evidence", evidence, "-WorkDir", os.path.join(SCRATCH, "work-" + mid)]
                                       + (["-LegacyBuildsRoot", os.environ["M2E1_LEGACY_BUILDS"]] if os.environ.get("M2E1_LEGACY_BUILDS") else []), 1800)
            report = os.path.join(evidence, "m2e1-evidence.json")
            checks = json.load(open(report, encoding="utf-8-sig")) if os.path.exists(report) else None
            entry["gate_exit"] = code
            entry["timed_out"] = timed_out
            entry["failed_checks"] = [c["Id"] for c in checks["checks"] if not c["Pass"]] if checks else ["(no report)"]
            entry["detected"] = code != 0 or timed_out
        else:
            code, out, _ = run(["dotnet", "build", AUTHORITY_PROJECT, "-c", "Debug", "-nologo", "-v", "q"], 900)
            code2, out2, _ = run(["dotnet", "build", ARCH_PROJECT, "-c", "Debug", "-nologo", "-v", "q"], 900)
            if code != 0 or code2 != 0:
                entry["build"] = "failed"
                entry["build_output_tail"] = (out + out2)[-2000:]
                entry["detected"] = True  # a removal that does not even compile is noticed, but reported separately
            else:
                failed, tests, timed_out = run_tests(AUTHORITY_EXE, CLASSES, "AuthorizationAuthority.Tests.")
                arch_failed, arch_tests, arch_timed_out = run_tests(ARCH_EXE, ARCH_CLASSES, "EnterpriseForms.ArchitectureTests.")
                entry["failed"] = (failed or 0) + (arch_failed or 0)
                entry["failed_tests"] = tests + arch_tests
                entry["timed_out"] = timed_out or arch_timed_out
                entry["detected"] = bool(entry["failed"]) or entry["timed_out"]
    except Exception as ex:
        entry["error"] = str(ex)
        entry["detected"] = None
    finally:
        for f in FILES:
            shutil.copyfile(backups[f], f)
        entry["restored"] = all(sha(f) == original[f] for f in FILES)
    results.append(entry)
    print(json.dumps({k: entry[k] for k in entry if k not in ("failed_tests", "build_output_tail")}), flush=True)

json.dump({"original_sha256": {os.path.relpath(f, ROOT).replace(os.sep, "/"): h for f, h in original.items()}, "results": results},
          open(OUT, "w", encoding="utf-8"), indent=2)
