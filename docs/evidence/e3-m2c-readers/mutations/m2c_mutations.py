"""E3-M2c enforcement-removal driver: one mutation at a time, build, focused tests, restore (sha256-verified)."""
import hashlib, json, os, shutil, subprocess, sys, time

REPO = sys.argv[1]
OUT = sys.argv[2]
ONLY = set(sys.argv[3].split(",")) if len(sys.argv) > 3 and sys.argv[3] else None
B = "src/backend/"
FIELD = B + "EnterpriseForms.Infrastructure/Features/FormRecords/AesGcmFieldEncryptionService.cs"
FILE = B + "EnterpriseForms.Infrastructure/Features/Documents/AesGcmFileEncryptionService.cs"
READER = B + "EnterpriseForms.Infrastructure/Features/Documents/EnvelopeV2/EnvelopeV2ProductReader.cs"
STEP = B + "EnterpriseForms.Application/Features/FormRecords/Values/DecryptedFieldValues.cs"
CODEC = B + "EnterpriseForms.Application/Features/FormRecords/Values/FieldValueCodec.cs"
CREATE = B + "EnterpriseForms.Application/Features/FormRecords/CreateFormRecord/CreateFormRecordHandler.cs"
UPDATE = B + "EnterpriseForms.Application/Features/FormRecords/UpdateFormRecord/UpdateFormRecordHandler.cs"
VREAD = B + "EnterpriseForms.Application/Features/FormRecords/Projection/VerifiedRecordReader.cs"
REG = B + "EnterpriseForms.Infrastructure/BusinessInfrastructureRegistration.cs"

MUTATIONS = [
    ("MU-01", "verified reader accepts schema 0", FIELD,
     "case (1, EnvelopeVersion) when !HasAnyEnvelopeV2Column(value):", "case (0 or 1, EnvelopeVersion) when !HasAnyEnvelopeV2Column(value):"),
    ("MU-02", "verified reader ignores v2 columns on schema 1", FIELD,
     "case (1, EnvelopeVersion) when !HasAnyEnvelopeV2Column(value):", "case (1, EnvelopeVersion):"),
    ("MU-03", "verified reader skips the v2 completeness check (second layer: product reader)", FIELD,
     "case (2, 2) when HasEveryEnvelopeV2Column(value):", "case (2, 2):"),
    ("MU-04", "verified reader skips the organization check", FIELD,
     "            if (value.TenantId != organizationId)\n", "            if (false)\n"),
    ("MU-05", "verified reader unwraps v1 rows before every row is validated", FIELD,
     "                case (1, EnvelopeVersion) when !HasAnyEnvelopeV2Column(value):\n                    break;",
     "                case (1, EnvelopeVersion) when !HasAnyEnvelopeV2Column(value):\n                    _ = await OpenV1Async(value, EncryptionKeyPurpose.FieldValue, cancellationToken).ConfigureAwait(false);\n                    break;"),
    ("MU-06", "v2 failure falls back to the v1 reader", FIELD,
     "                AttachFieldFailure(ex, reason, values[v2Indexes[0]]);\n                throw;",
     "                foreach (var k in v2Indexes)\n                {\n                    results[k] = await OpenV1Async(values[k], EncryptionKeyPurpose.FieldValue, cancellationToken).ConfigureAwait(false);\n                }\n\n                return results;"),
    ("MU-07", "self-sealed API accepts schema 2", FIELD,
     "value.RowSignatureSchemaVersion is not (0 or 1)", "value.RowSignatureSchemaVersion is not (0 or 1 or 2)"),
    ("MU-08", "self-sealed API ignores v2 metadata", FIELD,
     "value.RowSignatureSchemaVersion is not (0 or 1) || HasAnyEnvelopeV2Column(value))", "value.RowSignatureSchemaVersion is not (0 or 1))"),
    ("MU-09", "self-sealed registry check disabled", STEP,
     "        if (values.Any(v => !IsSelfSealed(v)))", "        if (false)"),
    ("MU-10", "ProtectAsync registers an already-encrypted row passed in", CODEC,
     "        if (field.FieldProtectionMode != FieldProtectionModes.ApplicationEncrypted || typedRow is FieldEncryptedValue)\n        {\n            return typedRow;",
     "        if (field.FieldProtectionMode != FieldProtectionModes.ApplicationEncrypted || typedRow is FieldEncryptedValue)\n        {\n            if (typedRow is FieldEncryptedValue passed)\n            {\n                SelfSealedFieldValues.Register(passed);\n            }\n\n            return typedRow;"),
    ("MU-11", "path C switched to verified mode", CREATE,
     "DecryptedFieldValues.DecryptSelfSealedAsync(encryption, candidateValues", "DecryptedFieldValues.DecryptAsync(encryption, candidateValues"),
    ("MU-12", "path E sends every released row to self-sealed mode", UPDATE,
     "released.Where(v => !freshlySealed.Contains(v)), cancellationToken).ConfigureAwait(false),\n            await Values.DecryptedFieldValues.DecryptSelfSealedAsync(encryption, released.Where(freshlySealed.Contains)",
     "released.Where(v => false), cancellationToken).ConfigureAwait(false),\n            await Values.DecryptedFieldValues.DecryptSelfSealedAsync(encryption, released"),
    ("MU-13", "path E sends every released row to verified mode", UPDATE,
     "released.Where(v => !freshlySealed.Contains(v)), cancellationToken).ConfigureAwait(false),\n            await Values.DecryptedFieldValues.DecryptSelfSealedAsync(encryption, released.Where(freshlySealed.Contains)",
     "released, cancellationToken).ConfigureAwait(false),\n            await Values.DecryptedFieldValues.DecryptSelfSealedAsync(encryption, released.Where(v => false)"),
    ("MU-14", "document reader accepts schema 0", FILE,
     "case (1, EnvelopeVersion) when !anyV2Column:", "case (0 or 1, EnvelopeVersion) when !anyV2Column:"),
    ("MU-15", "document reader skips the v2 completeness check (second layer: product reader)", FILE,
     "case (2, 2) when everyV2Column:", "case (2, 2):"),
    ("MU-16", "product reader derives the wrap from the reference, not the stored code", READER,
     "var prefix = EnvelopeV2HeaderPrefix.Create(kind, (EnvelopeV2WrapAlgorithm)code, organizationId, keyReference, dataKey);",
     "var prefix = EnvelopeV2HeaderPrefix.Create(kind, EnvelopeV2Format.WrapAlgorithmOf(keyReference), organizationId, keyReference, dataKey);"),
    ("MU-17", "product reader references a v2 writer", READER,
     "    /// <summary>The AEAD algorithm name a v2 row carries (same spelling as v1).</summary>",
     "    internal static Type Writer() => typeof(FieldSealBatchV2);\n\n    /// <summary>The AEAD algorithm name a v2 row carries (same spelling as v1).</summary>"),
    ("MU-18", "another product type references the v2 core directly", REG,
     "        services.AddSingleton<IFieldEncryptionService, AesGcmFieldEncryptionService>();",
     "        services.AddSingleton<IFieldEncryptionService, AesGcmFieldEncryptionService>();\n        services.AddSingleton(new object[] { typeof(Features.Documents.EnvelopeV2.FieldEnvelopeV2Opener) }); // a reference the compiler cannot elide"),
    ("MU-19", "a verified storage reader switched to self-sealed mode", VREAD,
     "var decrypted = await DecryptedFieldValues.DecryptAsync(Encryption, released, cancellationToken)", "var decrypted = await DecryptedFieldValues.DecryptSelfSealedAsync(Encryption, released, cancellationToken)"),
    ("MU-20", "application batch step skips the one-organization check (second layer: service)", STEP,
     "        if (encrypted.Any(v => v.TenantId != organizationId))", "        if (false)"),
]

FINGER = B + "EnterpriseForms.Application/Features/FormRecords/CreateFormRecord/CreationRequestFingerprint.cs"
MUTATIONS += [
    ("MX-1", "closure: registry ignores the recorded binding (identity only)", STEP,
     "Sealed.TryGetValue(value, out var binding) && binding.Equals(Binding.Of(value));", "Sealed.TryGetValue(value, out var binding);"),
    ("MX-2", "closure: fingerprint JSON parsed leniently (unknown members ignored)", FINGER,
     "attempt.FingerprintKeyEnvelopeJson, StrictEnvelopeJson)", "attempt.FingerprintKeyEnvelopeJson, Json)"),
    ("MX-3", "closure: fingerprint operation skips its envelope-version check (second layer: v1 core)", FIELD,
     "envelope.FormRecordCreationAttemptId == Guid.Empty || envelope.EnvelopeVersion != EnvelopeVersion)", "envelope.FormRecordCreationAttemptId == Guid.Empty)"),
    ("MX-4", "closure: fingerprint operation accepts a key of any length", FIELD,
     "        if (key.Length != 32)\n", "        if (key.Length < 0)\n"),
    ("MX-5", "closure: fingerprint check back on the raw self-sealed API", FINGER,
     "key = await encryption.OpenCreationFingerprintKeyAsync(envelope.ToSealedKey(attempt), cancellationToken).ConfigureAwait(false);",
     "key = Convert.FromBase64String(await encryption.DecryptAsync(new FieldEncryptedValue { TenantId = attempt.TenantId, FormRecordId = attempt.FormRecordId, FieldDefinitionId = SealPurposeId, FieldValueItemId = attempt.FormRecordCreationAttemptId, EncryptedLogicalType = SealLogicalType, EncryptedValueBytes = Convert.FromBase64String(envelope.Ciphertext), EncryptionNonceBytes = Convert.FromBase64String(envelope.Nonce), EncryptionAuthTagBytes = Convert.FromBase64String(envelope.Tag), WrappedDataKeyBytes = Convert.FromBase64String(envelope.WrappedKey), EncryptionAlgorithmName = envelope.Algorithm, EncryptionKeyReference = envelope.KeyReference, EncryptionEnvelopeVersion = envelope.EnvelopeVersion }, EncryptionKeyPurpose.IdempotencyProtection, cancellationToken).ConfigureAwait(false));"),
]

CONTRACT_CLASSES = ["EnvelopeV2ProductReaderTests", "DecryptionModeProvenanceTests", "PhaseC5SharedEditTests", "PhaseC2RulesAndOptionsTests", "PhaseC3RepeatingGroupsTests",
                    "PhaseC6RestoreTests", "EncryptionKnownAnswerTests", "EncryptionRemediationTests", "EnvelopeV2Tests", "EnvelopeV2KnownAnswerTests", "HandlerDecryptionModeTests", "DecryptionClosureRuntimeTests", "RecordCreationIdempotencyUnitTests", "KeyProviderInterfaceV2Tests"]
CONTRACT_EXE = os.path.join(REPO, "tests/features/EnterpriseForms.Api.ContractTests/bin/Debug/net10.0/EnterpriseForms.Api.ContractTests.exe")
ARCH_EXE = os.path.join(REPO, "tests/architecture/EnterpriseForms.ArchitectureTests/bin/Debug/net10.0/EnterpriseForms.ArchitectureTests.exe")


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def run(cmd, timeout):
    try:
        p = subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, timeout=timeout, encoding="utf-8", errors="replace")
        return p.returncode, p.stdout + p.stderr
    except subprocess.TimeoutExpired:
        return -999, "TIMEOUT"


def counts(text):
    out = {}
    for key in ("total", "failed", "succeeded"):
        for line in text.splitlines():
            if line.strip().startswith(key + ":"):
                out[key] = int(line.split(":")[1].strip())
    return out


def failures(text):
    return [l.split(" (")[0][7:] for l in text.splitlines() if l.startswith("failed ")][:8]


def build():
    rc1, o1 = run(["dotnet", "build", "tests/features/EnterpriseForms.Api.ContractTests", "-c", "Debug", "-v", "q", "-nologo"], 600)
    rc2, o2 = run(["dotnet", "build", "tests/architecture/EnterpriseForms.ArchitectureTests", "-c", "Debug", "-v", "q", "-nologo"], 600)
    return rc1 == 0 and rc2 == 0, (o1 + o2)[-1500:]


results = []
backup_dir = os.path.join(os.path.dirname(OUT), "m2c-backups")
os.makedirs(backup_dir, exist_ok=True)
for mid, title, rel, old, new in MUTATIONS:
    if ONLY and mid not in ONLY:
        continue
    path = os.path.join(REPO, rel)
    original_hash = sha(path)
    backup = os.path.join(backup_dir, mid + ".bak")
    shutil.copyfile(path, backup)
    text = open(path, encoding="utf-8").read()
    assert text.count(old) == 1, (mid, text.count(old))
    open(path, "w", encoding="utf-8", newline="").write(text.replace(old, new))
    started = time.time()
    try:
        built, build_log = build()
        entry = {"id": mid, "title": title, "file": rel, "built": built}
        if not built:
            entry.update(detected=True, by="build", log=build_log)
        else:
            args = [CONTRACT_EXE] + sum([["--filter-class", "EnterpriseForms.Api.ContractTests." + c] for c in CONTRACT_CLASSES], [])
            rc_c, out_c = run(args, 400)
            rc_a, out_a = run([ARCH_EXE], 400)
            entry.update(contract=counts(out_c), architecture=counts(out_a), contract_failures=failures(out_c), architecture_failures=failures(out_a), contract_rc=rc_c, architecture_rc=rc_a)
            entry["detected"] = rc_c != 0 or rc_a != 0
    finally:
        shutil.copyfile(backup, path)
        entry["restored_hash_matches"] = sha(path) == original_hash
        entry["seconds"] = round(time.time() - started)
    results.append(entry)
    print(mid, "detected" if entry["detected"] else "NOT DETECTED", entry.get("contract"), entry.get("architecture"), "restored" if entry["restored_hash_matches"] else "RESTORE MISMATCH", flush=True)
    json.dump(results, open(OUT, "w", encoding="utf-8"), indent=2)

ok, _ = build()
print("final rebuild", "ok" if ok else "FAILED", flush=True)
