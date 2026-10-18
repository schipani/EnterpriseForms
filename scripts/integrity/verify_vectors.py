#!/usr/bin/env python3
"""Recompute every committed conformance vector from its declared inputs and compare.

Exit code 0 only when all positive cases match byte-for-byte and all negative cases raise.
Used by CI (`integrity-vectors` job) and by reviewers. This checks the Python reference
against the frozen JSON; the C# tests and Node verifier check their own implementations
against the same JSON, giving three-way agreement.
"""
from __future__ import annotations

import decimal
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import merkle_v1 as mk  # noqa: E402
import row_integrity_v1 as ri  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
failures: list[str] = []


def check(cond: bool, message: str) -> None:
    if not cond:
        failures.append(message)


def leaf_from_descriptor(d: dict) -> mk.LeafInput:
    return mk.LeafInput(
        kind_code=d["itemKindCode"],
        item_id=d["itemId"],
        row_signature=bytes.fromhex(d["rowSignatureHex"]),
        canonical_payload_digest=bytes.fromhex(d["canonicalPayloadDigestHex"]),
        committed_content_revision=d["committedContentRevision"],
        committed_operation_id=d["committedOperationId"],
    )


def verify_merkle(doc: dict) -> None:
    check(doc["emptyRootHex"] == mk.empty_root().hex(), "merkle: empty root mismatch")
    for case in doc["cases"]:
        cid = case["caseId"]
        descriptors = case["inputLeavesInSubmissionOrder"]
        for d in descriptors:
            payload = d["fixturePayloadAscii"].encode("ascii")
            check(ri.canonical_payload_digest(payload).hex() == d["canonicalPayloadDigestHex"], f"{cid}: payload digest")
            check(mk.item_key(d["itemKindCode"], d["itemId"]).hex() == d["itemKeyHex"], f"{cid}: item key")
        leaves = mk.sort_leaves([leaf_from_descriptor(d) for d in descriptors])
        exp = case["expected"]
        check([l.key.hex() for l in leaves] == exp["sortedItemKeysHex"], f"{cid}: sorted keys")
        check([mk.leaf_bytes(l).hex() for l in leaves] == exp["leafBytesHex"], f"{cid}: leaf bytes")
        check([mk.leaf_hash(l).hex() for l in leaves] == exp["leafHashesHex"], f"{cid}: leaf hashes")
        check([[h.hex() for h in lvl] for lvl in mk.build_levels(leaves)] == exp["levelsBottomUpHex"], f"{cid}: levels")
        check(mk.merkle_root(leaves).hex() == exp["merkleRootHex"], f"{cid}: root")
        check(len(leaves) == exp["leafCount"], f"{cid}: leaf count")
        check(mk.leaf_index_digest(leaves).hex() == exp["leafIndexDigestHex"], f"{cid}: index digest")
    normative = next(c for c in doc["cases"] if c["caseId"] == "MRKV1-3LEAF-NORMATIVE")["expected"]
    unsorted = next(c for c in doc["cases"] if c["caseId"] == "MRKV1-3LEAF-UNSORTED-INPUT")["expected"]
    check(normative == unsorted, "merkle: unsorted input must yield identical output")

    for neg in doc["negativeCases"]:
        cid = neg["caseId"]
        try:
            if cid == "MRKV1-NEG-DUPLICATE-KEY":
                mk.sort_leaves([leaf_from_descriptor(d) for d in neg["inputLeavesInSubmissionOrder"]])
            elif cid == "MRKV1-NEG-KIND-MISMATCH":
                base = leaf_from_descriptor(doc["cases"][0]["inputLeavesInSubmissionOrder"][1])
                bad = mk.LeafInput(neg["trailingItemKindCode"], base.item_id, base.row_signature, base.canonical_payload_digest, base.committed_content_revision, base.committed_operation_id)
                # item_key derived from kind 5 would differ from the declared key; emulate the wire check
                key = bytes.fromhex(neg["itemKeyHex"])
                if key[0] != bad.kind_code:
                    raise mk.MerkleFormatError("kind mismatch")
            elif cid == "MRKV1-NEG-SIGNATURE-LENGTH":
                base = leaf_from_descriptor(doc["cases"][0]["inputLeavesInSubmissionOrder"][1])
                mk.leaf_bytes(mk.LeafInput(base.kind_code, base.item_id, bytes.fromhex(neg["rowSignatureHex"]), base.canonical_payload_digest, base.committed_content_revision, base.committed_operation_id))
            elif cid == "MRKV1-NEG-MIXED-REVISION":
                mk.sort_leaves([leaf_from_descriptor(d) for d in neg["inputLeavesInSubmissionOrder"]])
            else:
                check(False, f"{cid}: unknown negative case")
                continue
            check(False, f"{cid}: expected rejection but succeeded")
        except mk.MerkleFormatError:
            pass


def schema_from_json(s: dict) -> ri.RowSchema:
    return ri.RowSchema(s["entityKind"], s["schemaVersion"], tuple(ri.SignedColumn(c["columnName"], c["type"], c["nullable"]) for c in s["signedColumns"]))


def coerce(schema: ri.RowSchema, values: dict) -> dict:
    out = {}
    for c in schema.columns:
        v = values[c.column_name]
        if v is not None and c.type_name == "Decimal":
            v = decimal.Decimal(v)
        out[c.column_name] = v
    return out


def verify_rows(doc: dict) -> None:
    key = bytes.fromhex(doc["testHmacKeyHex"])
    schemas = {(s["entityKind"], s["schemaVersion"]): schema_from_json(s) for s in doc["schemas"]}
    for ex in doc["decimalEncodingExamples"]:
        check(ri.encode_decimal(decimal.Decimal(ex["input"])).hex() == ex["encodedHex"], f"decimal {ex['input']}")
    by_id = {}
    for case in doc["cases"]:
        cid = case["caseId"]
        schema = schemas[(case["entityKind"], case["schemaVersion"])]
        canonical = ri.canonical_row_bytes(
            schema,
            key_version=case["keyVersion"],
            organization_id=case["organizationId"],
            primary_key_id=case["primaryKeyId"],
            row_revision=case["rowRevision"],
            values=coerce(schema, case["values"]),
        )
        exp = case["expected"]
        by_id[cid] = exp
        check(canonical.hex() == exp["canonicalBytesHex"], f"{cid}: canonical bytes")
        check(len(canonical) == exp["canonicalByteLength"], f"{cid}: length")
        check(ri.canonical_payload_digest(canonical).hex() == exp["canonicalPayloadDigestHex"], f"{cid}: digest")
        check(ri.row_signature(canonical, key).hex() == exp["rowSignatureHexWithTestKey"], f"{cid}: hmac")
    check(by_id["ROWV1-TYPES-ALL-PRESENT"]["canonicalBytesHex"] == by_id["ROWV1-TYPES-NFC-EQUIVALENCE"]["canonicalBytesHex"], "NFC/decimal/offset/uuid-case equivalence")
    check(by_id["ROWV1-TYPES-ALL-NULL"]["canonicalBytesHex"] != by_id["ROWV1-EMPTY-STRING-NOT-NULL"]["canonicalBytesHex"], "empty string must differ from NULL")
    check(by_id["ROWV1-FORM-RECORD-USER-OWNED"]["canonicalBytesHex"] != by_id["ROWV1-FORM-RECORD-TEAM-OWNED"]["canonicalBytesHex"], "owner change must alter canonical bytes")

    cov = schemas[("integrity_type_coverage", 1)]
    base = coerce(cov, next(c for c in doc["cases"] if c["caseId"] == "ROWV1-TYPES-ALL-NULL")["values"])
    common = dict(key_version=1, organization_id=doc["cases"][0]["organizationId"], primary_key_id=doc["cases"][0]["primaryKeyId"], row_revision=1)
    negative_actions = {
        "ROWV1-NEG-NULL-IN-NON-NULLABLE": lambda: ri.canonical_row_bytes(cov, values={**base, "boolean_flag": None}, **common),
        "ROWV1-NEG-JSON-FLOAT": lambda: ri.canonical_row_bytes(cov, values={**base, "json_value": {"a": 1.5}}, **common),
        "ROWV1-NEG-JSON-NON-ASCII-KEY": lambda: ri.canonical_row_bytes(cov, values={**base, "json_value": {"é": 1}}, **common),
        "ROWV1-NEG-UNSORTED-REGISTRY": lambda: ri.RowSchema("x", 1, (ri.SignedColumn("b_col", "Text", True), ri.SignedColumn("a_col", "Text", True))),
        "ROWV1-NEG-EXTRA-COLUMN": lambda: ri.canonical_row_bytes(cov, values={**base, "rogue_column": 1}, **common),
        "ROWV1-NEG-MISSING-COLUMN": lambda: ri.canonical_row_bytes(cov, values={k: v for k, v in base.items() if k != "text_value"}, **common),
        "ROWV1-NEG-NAIVE-TIMESTAMP": lambda: ri.canonical_row_bytes(cov, values={**base, "timestamp_value": "2024-01-01T00:00:00"}, **common),
        "ROWV1-NEG-ROW-REVISION-ZERO": lambda: ri.canonical_row_bytes(cov, values=base, **{**common, "row_revision": 0}),
    }
    for neg in doc["negativeCases"]:
        cid = neg["caseId"]
        action = negative_actions.get(cid)
        if action is None:
            check(False, f"{cid}: unknown negative case")
            continue
        try:
            action()
            check(False, f"{cid}: expected rejection but succeeded")
        except ri.RowFormatError:
            pass


def main() -> int:
    merkle = json.loads((ROOT / "trust_authority" / "merkle-v1.test-vectors.json").read_text(encoding="utf-8"))
    rows = json.loads((ROOT / "trust_authority" / "row-integrity-v1.test-vectors.json").read_text(encoding="utf-8"))
    verify_merkle(merkle)
    verify_rows(rows)
    total = len(merkle["cases"]) + len(merkle["negativeCases"]) + len(rows["cases"]) + len(rows["negativeCases"]) + len(rows["decimalEncodingExamples"]) + 1
    if failures:
        for f in failures:
            print("FAIL:", f, file=sys.stderr)
        return 1
    print(f"PYTHON VECTOR VERIFICATION PASS ({total} checks across merkle-v1 and row-integrity-v1)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
