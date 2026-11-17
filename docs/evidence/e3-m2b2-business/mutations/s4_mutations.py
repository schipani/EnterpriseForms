"""R-M2b2-11 S-4 enforcement-removal driver. Applies one mutation set at a time, rebuilds the Authority tests, runs the
focused classes (time-boxed), restores every file from a backup and verifies its sha256. Writes a JSON result."""
import hashlib, json, os, re, shutil, subprocess, sys, tempfile

ROOT = sys.argv[1]
OUT = sys.argv[2]
SCRATCH = tempfile.mkdtemp(prefix="s4-mutations-")  # backups live outside the repository
CONTENT = os.path.join(ROOT, "src", "authority", "AuthorizationAuthority.Application", "Features", "Content")
RULES = os.path.join(CONTENT, "EnvelopeRules.cs")
SERVICE = os.path.join(CONTENT, "ContentService.cs")
TIE = os.path.join(CONTENT, "EnvelopeSchemaTie.cs")
PROJECT = os.path.join(ROOT, "tests", "authority", "AuthorizationAuthority.Tests", "AuthorizationAuthority.Tests.csproj")
EXE = os.path.join(ROOT, "tests", "authority", "AuthorizationAuthority.Tests", "bin", "Debug", "net10.0", "AuthorizationAuthority.Tests.exe")
CLASSES = ["EnvelopeSchemaTieTests", "ContributorWriteProtocolTests", "ProtectionReconciliationTests", "EnvelopeRulesTests"]

SCHEMA_REFUSAL = "        if (document.Header.SchemaVersion != 1)\n        {\n            return Reasons.SchemaUnsupported;\n        }\n\n"
PREPARE_CALL = "        EnvelopeSchemaTie.Enforce(parsed.Select(p => p.Document));\n"
COMMIT_CALL = "            EnvelopeSchemaTie.Enforce(stagedDocuments.Select(d => d.Item3));\n"
CENSUS_TERM = "        EnvelopeSchemaTie.Violation(document) is null // E3-M2b-2 S-4: the schema/envelope tie, as on every write\n        && "

M = {
    "MS4-01": ("EnvelopeRules schema-version refusal removed (field and document)", [(RULES, SCHEMA_REFUSAL, "", 2)]),
    "MS4-02": ("MS4-01 plus the envelope-version allow-list widened to {1, 2}", [(RULES, SCHEMA_REFUSAL, "", 2), (RULES, "new HashSet<int> { EnvelopeVersionV1 }", "new HashSet<int> { EnvelopeVersionV1, 2 }", 1)]),
    "MS4-03": ("S-4 disabled (Violation always null)", [(TIE, "        ArgumentNullException.ThrowIfNull(document);\n        if (document.Header.EntityKind is not (", "        ArgumentNullException.ThrowIfNull(document);\n        if (document.Header.EntityKind is not null) { return null; }\n        if (document.Header.EntityKind is not (", 1)]),
    "MS4-04": ("S-4 Prepare call removed", [(SERVICE, PREPARE_CALL, "", 1)]),
    "MS4-05": ("S-4 Commit call removed", [(SERVICE, COMMIT_CALL, "", 1)]),
    "MS4-06": ("S-4 census term removed (EnvelopeRules intact)", [(SERVICE, CENSUS_TERM, "        ", 1)]),
    "MS4-07": ("S-4 census term removed plus the EnvelopeRules schema refusal removed", [(SERVICE, CENSUS_TERM, "        ", 1), (RULES, SCHEMA_REFUSAL, "", 2)]),
    "MS4-08": ("S-4 admits the pair (2, 1)", [(TIE, "{ (1, 1), (2, 2) }", "{ (1, 1), (2, 2), (2, 1) }", 1)]),
    "MS4-09": ("S-4 fail-closed for an unreadable envelope version removed", [(TIE, "            return MismatchReason; // the tie cannot be proven: fail closed", "            return null;", 1)]),
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

files = [RULES, SERVICE, TIE]
original = {f: sha(f) for f in files}
backups = {}
for f in files:
    b = os.path.join(SCRATCH, "backup-" + os.path.basename(f))
    shutil.copyfile(f, b)
    backups[f] = b

only = sys.argv[3].split(",") if len(sys.argv) > 3 else list(M)
results = []
for mid in only:
    description, edits = M[mid]
    entry = {"id": mid, "mutation": description}
    try:
        for path, old, new, count in edits:
            text = open(path, encoding="utf-8", newline="").read()
            n = text.count(old)
            if n != count:
                raise RuntimeError(f"{os.path.basename(path)}: expected {count} occurrences, found {n}")
            open(path, "w", encoding="utf-8", newline="").write(text.replace(old, new))
        code, out, _ = run(["dotnet", "build", PROJECT, "-c", "Debug", "-nologo", "-v", "q"], 600)
        if code != 0:
            entry["build"] = "failed"
            entry["build_output_tail"] = out[-2000:]
        else:
            args = [EXE]
            for c in CLASSES:
                args += ["--filter-class", "AuthorizationAuthority.Tests." + c]
            code, out, timed_out = run(args, 300)
            entry["timed_out"] = timed_out
            summary = re.search(r"total: (\d+)\s+failed: (\d+)\s+succeeded: (\d+)\s+skipped: (\d+)", out)
            entry["total"], entry["failed"], entry["succeeded"], entry["skipped"] = (int(x) for x in summary.groups()) if summary else (None, None, None, None)
            failed = sorted(set(m.group(1).strip() for m in re.finditer(r"^\s*failed (.+?) \(\d", out, re.M)))
            entry["failed_tests"] = failed
            entry["detected"] = bool(entry["failed"]) or timed_out
    finally:
        for f in files:
            shutil.copyfile(backups[f], f)
        entry["restored"] = all(sha(f) == original[f] for f in files)
    results.append(entry)
    print(mid, entry.get("failed"), "restored" if entry["restored"] else "NOT RESTORED", flush=True)
    if not entry["restored"]:
        break

json.dump({"original_sha256": {os.path.relpath(f, ROOT).replace("\\", "/"): h for f, h in original.items()}, "results": results}, open(OUT, "w", encoding="utf-8"), indent=2)
print("done")
