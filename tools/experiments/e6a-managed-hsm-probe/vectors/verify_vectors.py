"""E-6a independent check of e6a-reference-vectors.json (Python stdlib hmac/hashlib only; no .NET code involved).

For every vector and test key it recomputes, independently of the C# generator:
  * the input (Length vectors from the documented rule; RowHmacV1 vectors from the published canonical bytes),
  * SHA-256(input),
  * HMAC-SHA-256(key, input)            (raw-message MAC, the existing EnterpriseForms construction),
  * HMAC-SHA-256(key, SHA-256(input))   (digest-message MAC, the documented Managed HSM "sign hash" path),
and requires the two MACs to differ. RowHmacV1 vectors are also compared with the published
trust_authority/row-integrity-v1.test-vectors.json signatures and with the real Vault Transit outputs recorded in
docs/security/stage-a-vault-transit-vectors.json (Stage A, SIG-11).

Usage (from the repository root):  python -I tools/experiments/e6a-managed-hsm-probe/vectors/verify_vectors.py [vectors.json]
(the optional path exists so a deliberately corrupted copy can show that the check fails)
"""

import hashlib
import hmac
import json
import pathlib
import sys

REQUIRED_LENGTHS = [0, 1, 31, 32, 33, 64, 256, 1024, 4096, 65536]
HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[3]  # vectors -> e6a-managed-hsm-probe -> experiments -> tools -> repository root


def load(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8"))


def main() -> int:
    vectors = load(pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else HERE / "e6a-reference-vectors.json")
    rows = {c["caseId"]: c for c in load(ROOT / "trust_authority" / "row-integrity-v1.test-vectors.json")["cases"]}
    vault = {c["case_id"]: c for c in load(ROOT / "docs" / "security" / "stage-a-vault-transit-vectors.json")["cases"]}
    keys = {k["testKeyId"]: bytes.fromhex(k["keyHex"]) for k in vectors["testKeys"]}
    failures = []
    checked = 0

    lengths = [v["inputLength"] for v in vectors["vectors"] if v["vectorKind"] == "Length"]
    if lengths != REQUIRED_LENGTHS:
        failures.append(f"length set {lengths} != {REQUIRED_LENGTHS}")
    if keys.get("e6a-test-key-1", b"").hex() != load(ROOT / "trust_authority" / "row-integrity-v1.test-vectors.json")["testHmacKeyHex"]:
        failures.append("e6a-test-key-1 is not the published RowHmac/v1 test key")

    vault_matches = 0
    for v in vectors["vectors"]:
        if v["vectorKind"] == "Length":
            data = bytes(i % 256 for i in range(v["inputLength"]))
        else:
            case_id = v["vectorId"].removeprefix("row-")
            data = bytes.fromhex(rows[case_id]["expected"]["canonicalBytesHex"])
            if v.get("inputHex") != data.hex():
                failures.append(f"{v['vectorId']}: inputHex differs from the published canonical bytes")
        digest = hashlib.sha256(data).digest()
        if len(data) != v["inputLength"] or digest.hex() != v["inputSha256Hex"]:
            failures.append(f"{v['vectorId']}: input length/SHA-256 mismatch")
        for m in v["macs"]:
            key = keys[m["testKeyId"]]
            raw = hmac.new(key, data, hashlib.sha256).hexdigest()
            over_digest = hmac.new(key, digest, hashlib.sha256).hexdigest()
            checked += 1
            if raw != m["rawMessageHmacSha256Hex"]:
                failures.append(f"{v['vectorId']}/{m['testKeyId']}: raw-message MAC mismatch")
            if over_digest != m["digestMessageHmacSha256Hex"]:
                failures.append(f"{v['vectorId']}/{m['testKeyId']}: digest-message MAC mismatch")
            if raw == over_digest or m["rawEqualsDigest"]:
                failures.append(f"{v['vectorId']}/{m['testKeyId']}: raw and digest MACs coincide")
        if v["vectorKind"] == "RowHmacV1":
            case_id = v["vectorId"].removeprefix("row-")
            raw_key1 = next(m for m in v["macs"] if m["testKeyId"] == "e6a-test-key-1")["rawMessageHmacSha256Hex"]
            if raw_key1 != rows[case_id]["expected"]["rowSignatureHexWithTestKey"]:
                failures.append(f"{v['vectorId']}: differs from the published RowHmac/v1 signature")
            if case_id in vault:
                vault_matches += 1
                if raw_key1 != vault[case_id]["vault_hmac_hex"]:
                    failures.append(f"{v['vectorId']}: differs from the recorded Vault Transit HMAC")

    for f in failures:
        print("FAIL", f)
    print(f"{len(vectors['vectors'])} vectors, {checked} key/vector MAC pairs recomputed independently; "
          f"{vault_matches} RowHmacV1 vectors matched recorded Vault Transit outputs; failures: {len(failures)}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
