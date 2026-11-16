"""E3-M2d enforcement-removal driver. Applies one mutation at a time to the Authority source, rebuilds, runs the focused test classes
(time-boxed; "unit") or the disposable-PostgreSQL gate ("pg"), restores every file from a backup outside the repository and verifies
its sha256. Writes a JSON result. Usage: m2d_mutations.py <repo> <out.json> [unit|pg] [ids,...]"""
import hashlib, json, os, re, shutil, subprocess, sys, tempfile

ROOT = sys.argv[1]
OUT = sys.argv[2]
MODE = sys.argv[3] if len(sys.argv) > 3 else "unit"
SCRATCH = tempfile.mkdtemp(prefix="m2d-mutations-")  # backups and gate evidence live outside the repository
A = os.path.join(ROOT, "src", "authority")
CONTENT = os.path.join(A, "AuthorizationAuthority.Application", "Features", "Content")
V2 = os.path.join(CONTENT, "EnvelopeV2Rules.cs")
RULES = os.path.join(CONTENT, "EnvelopeRules.cs")
SERVICE = os.path.join(CONTENT, "ContentService.cs")
CARV = os.path.join(CONTENT, "ContentAcceptanceRules.cs")
POLICY = os.path.join(CONTENT, "ContentAcceptancePolicy.cs")
FENCE = os.path.join(A, "AuthorizationAuthority.Infrastructure", "Persistence", "ContentAcceptanceWriteFence.cs")
FLOOR2 = os.path.join(A, "AuthorizationAuthority.Infrastructure", "Persistence", "ContentAcceptanceFloor2Sql.cs")
COMMAND = os.path.join(A, "AuthorizationAuthority.Api", "Composition", "ContentAcceptanceFloorCommand.cs")
FILES = [V2, RULES, SERVICE, CARV, POLICY, FENCE, FLOOR2, COMMAND]

AUTHORITY_PROJECT = os.path.join(ROOT, "tests", "authority", "AuthorizationAuthority.Tests", "AuthorizationAuthority.Tests.csproj")
AUTHORITY_EXE = os.path.join(ROOT, "tests", "authority", "AuthorizationAuthority.Tests", "bin", "Debug", "net10.0", "AuthorizationAuthority.Tests.exe")
ARCH_PROJECT = os.path.join(ROOT, "tests", "architecture", "EnterpriseForms.ArchitectureTests", "EnterpriseForms.ArchitectureTests.csproj")
ARCH_EXE = os.path.join(ROOT, "tests", "architecture", "EnterpriseForms.ArchitectureTests", "bin", "Debug", "net10.0", "EnterpriseForms.ArchitectureTests.exe")
CLASSES = ["EnvelopeV2RulesTests", "ContributorWriteProtocolTests", "ContentAcceptanceFloor2Tests", "ContentAcceptancePolicyTests", "ContentAcceptanceFloorTests",
           "ContentAcceptanceRuleGuardTests", "EnvelopeRulesTests", "EnvelopeSchemaTieTests", "ProtectionReconciliationTests"]
GATE = os.path.join(ROOT, "scripts", "dev", "e3-m2d-carv2-pg-gate.ps1")

M = {
    # ---- version dispatch and the v2 rules (Prepare, Commit, census) ----
    "MD-01": ("dispatch removed: v2 rows judged by the v1 rules again", [(RULES, "            if (EnvelopeV2Rules.IsV2(document))\n            {\n                continue;\n            }\n\n", "", 1)]),
    "MD-02": ("the v2 candidate rules are no longer reached from the dispatching entry point", [(RULES, "        EnvelopeV2Rules.Enforce(all);\n", "", 1)]),
    "MD-03": ("new-v2 refusal removed (a new v2 row reaches signing)", [(V2, "        if (newRows.Count > 0)\n", "        if (newRows.Count < 0)\n", 1)]),
    "MD-04": ("downgrade guard removed", [(V2, "                    throw EnvelopeRules.Refused(Reasons.Downgrade);\n", "                    _ = Reasons.Downgrade;\n", 1)]),
    "MD-05": ("DK-3 removed", [(V2, "                throw EnvelopeRules.Refused(Reasons.DataKeyIdentifierReused);\n", "                _ = Reasons.DataKeyIdentifierReused;\n", 1)]),
    "MD-06": ("the Prepare call of the Prepare-only v2 rules removed", [(SERVICE, "        EnvelopeV2Rules.EnforceAgainstCurrent(parsed.Select(p => (p.ItemId, p.Document)).ToList(), current.Select(c => (c.ItemId, c.Document)).ToList());\n", "", 1)]),
    "MD-07": ("the Commit refusal of v2 rows removed", [(SERVICE, "        if (EnvelopeV2Rules.CommitViolation(stagedDocuments.Select(d => d.Item3)) is { } v2Violation)", "        if (EnvelopeV2Rules.CommitViolation([]) is { } v2Violation)", 1)]),
    "MD-08": ("the Commit refusal weakened (never refuses)", [(V2, "        return staged.Any(IsV2) ? Reasons.VersionNotEnabled : null;", "        return null;", 1)]),
    "MD-09": ("batch consistency weakened (commitment not compared)", [(V2, "                || !string.Equals(Commitment, other.Commitment, StringComparison.Ordinal) || WrapCode != other.WrapCode", "                || WrapCode != other.WrapCode", 1)]),
    "MD-10": ("v1/v2 wrapped-key separation removed (within a candidate)", [(V2, "            if (v1Wrapped.Contains(wrapped))\n", "            if (Environment.ProcessorCount < 0)\n", 1)]),
    "MD-11": ("v1 rows may reuse a current v2 wrapped key", [(V2, "                if (currentV2Wrapped.Contains(WrappedOf(document)))\n", "                if (Environment.ProcessorCount < 0)\n", 1)]),
    "MD-12": ("the codec-derived field bound removed", [(V2, "        if (ciphertext.Value.Length > MaximumFieldCiphertextBytes)\n", "        if (Environment.ProcessorCount < 0)\n", 1)]),
    "MD-13": ("the document size formula removed", [(V2, "        return ExpectedDocumentCiphertextSize(plaintextSize.Value) == ciphertextSize.Value ? null : Reasons.CiphertextSize;", "        return null;", 1)]),
    "MD-14": ("the v1 cross-revision rule indexes v2 rows again (F-14 regression)", [(RULES, "            if (row.Document.Header.EntityKind == FieldEntityKind && !EnvelopeV2Rules.IsV2(row.Document))", "            if (row.Document.Header.EntityKind == FieldEntityKind)", 1),
                                                                                    (RULES, "            if (row.Document.Header.EntityKind != FieldEntityKind || EnvelopeV2Rules.IsV2(row.Document) || ", "            if (row.Document.Header.EntityKind != FieldEntityKind || ", 1)]),
    "MD-15": ("census: v2 rows judged by the v1 census rule", [(V2, "            if (!IsV2(document))\n            {\n                // Unchanged E3-M2a census rule", "            if (Environment.ProcessorCount > 0)\n            {\n                // Unchanged E3-M2a census rule", 1)]),
    # ---- CARV 2 ----
    "MD-16": ("CARV reverted to version 1", [(CARV, "    public const int CurrentVersion = 2;", "    public const int CurrentVersion = 1;", 1)]),
    "MD-17": ("CARV Commit rule accepts any stamped version (no exact equality with the build)", [(CARV, "prepareAcceptanceRuleVersion == CurrentVersion;", "prepareAcceptanceRuleVersion >= 1;", 1)]),
    # ---- floor 2 support: fence, policy, command ----
    "MD-18": ("fence minimum semantics removed (a lower installed floor accepted)", [(FENCE, "        if (installed < requiredFloor)\n", "        if (Environment.ProcessorCount < 0)\n", 1)]),
    "MD-19": ("fence: a recorded higher floor no longer required to be installed (lowering accepted)", [(FENCE, "        if (recorded.Count > 0 && recorded.Max() > installed)\n", "        if (Environment.ProcessorCount < 0)\n", 1)]),
    "MD-20": ("fence: the installed floor's exact definition no longer compared", [(FENCE, "        if (!string.Equals(facts.FloorDefinition, floor.ExpectedDefinition, StringComparison.Ordinal)\n", "        if (Environment.ProcessorCount < 0 && !string.Equals(facts.FloorDefinition, floor.ExpectedDefinition, StringComparison.Ordinal)\n", 1)]),
    "MD-21": ("fence: more than one floor constraint accepted", [(FENCE, "        if (count != 1 || facts.FloorDefinition is null", "        if (facts.FloorDefinition is null", 1)]),
    "MD-22": ("policy accepts an unrecognized floor above the build", [(POLICY, "        return !ContentAcceptanceRules.RecognizedFloors.Contains(floor) || floor > ContentAcceptanceRules.CurrentVersion", "        return floor > 99", 1)]),
    "MD-23": ("command migrates every pending floor (no explicit target)", [(COMMAND, "context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate(target.MigrationId);", "context.Database.Migrate();", 1)]),
    "MD-24": ("floor-2 SQL: an older-marker session no longer blocks the raise", [(FLOOR2, ")::numeric, 0) < 2", ")::numeric, 0) < 0", 1)]),
    "MD-25": ("floor-2 SQL: floor 1 not dropped by the raise", [(FLOOR2, "        ALTER TABLE authority.content_change_operations DROP CONSTRAINT ck_content_operation_rule_floor_v1;\n", "", 1)]),
    "MD-26": ("command: applying a lower floor after a higher one no longer refused", [(COMMAND, "            if (recorded.Count > 0 && recorded.Max() > target.Version)\n", "            if (Environment.ProcessorCount < 0)\n", 1)]),
    "MD-27": ("command: skipping floor 1 no longer refused", [(COMMAND, "            if (ContentAcceptanceFloors.Recognized.Any(f => f.Version < target.Version && !recorded.Contains(f.Version)))\n", "            if (Environment.ProcessorCount < 0)\n", 1)]),
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
    args = [exe]
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

only = sys.argv[4].split(",") if len(sys.argv) > 4 else list(M)
results = []
for mid in only:
    description, edits = M[mid]
    entry = {"id": mid, "mutation": description, "mode": MODE}
    try:
        for path, old, new, count in edits:
            text = open(path, encoding="utf-8", newline="").read()
            n = text.count(old)
            if n != count:
                raise RuntimeError(f"{os.path.basename(path)}: expected {count} occurrences, found {n}")
            open(path, "w", encoding="utf-8", newline="").write(text.replace(old, new))
        if MODE == "pg":
            evidence = os.path.join(SCRATCH, "gate-" + mid)
            code, out, timed_out = run(["powershell", "-NoProfile", "-File", GATE, "-Evidence", evidence], 1200)
            report = os.path.join(evidence, "m2d-evidence.json")
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
                arch_failed, arch_tests, arch_timed_out = run_tests(ARCH_EXE, ["EnvelopeV2RuleBoundaryTests"], "EnterpriseForms.ArchitectureTests.")
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
