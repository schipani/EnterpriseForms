#!/usr/bin/env python3
"""Generate the Gate 0 cryptographic conformance vectors.

Outputs (repository-relative):
  trust_authority/merkle-v1.test-vectors.json
  trust_authority/row-integrity-v1.test-vectors.json

Run only after a reviewed protocol change; the committed vectors are frozen inputs for the
C# (tests/integrity) and Node (scripts/integrity/verify-vectors.mjs) verifiers. Regenerating
them is not evidence that the protocol is correct: every change needs independent review.
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

ORG = "11111111-1111-4111-8111-111111111111"
RECORD = "0199a2b3-c4d5-7e6f-8123-456789abcdef"
FIELD_VALUE = "0199a2b3-c4d5-7e6f-8123-456789abcdf0"
GROUP = "0199a2b3-c4d5-7e6f-8123-456789abcdf1"
SELECTION = "0199a2b3-c4d5-7e6f-8123-456789abcdf2"
ATTACHMENT = "0199a2b3-c4d5-7e6f-8123-456789abcdf3"
OPERATION = "0199a2b3-c4d5-7000-8000-000000000007"
REVISION = 7

TEST_HMAC_KEY = bytes(range(32))  # 000102...1f — TEST ONLY, published, never a real key


def hx(b: bytes) -> str:
    return b.hex()


def fixture_leaf(kind: str, item_id: str, sig_byte: int, payload_label: str, revision: int = REVISION, operation: str = OPERATION):
    payload = f"merkle-v1-fixture-payload:{payload_label}".encode("ascii")
    digest = ri.canonical_payload_digest(payload)
    leaf = mk.LeafInput(
        kind_code=mk.KIND_CODES[kind],
        item_id=item_id,
        row_signature=bytes([sig_byte]) * 32,
        canonical_payload_digest=digest,
        committed_content_revision=revision,
        committed_operation_id=operation,
    )
    descriptor = {
        "itemKind": kind,
        "itemKindCode": leaf.kind_code,
        "itemId": item_id,
        "itemKeyHex": hx(leaf.key),
        "rowSignatureHex": hx(leaf.row_signature),
        "rowSignatureNote": "INVENTED fixed fixture bytes; not a real HMAC tag",
        "fixturePayloadAscii": payload.decode("ascii"),
        "canonicalPayloadDigestHex": hx(digest),
        "committedContentRevision": revision,
        "committedOperationId": operation,
    }
    return leaf, descriptor


def merkle_case(case_id: str, description: str, inputs: list[tuple[mk.LeafInput, dict]]):
    leaves = [l for l, _ in inputs]
    ordered = mk.sort_leaves(leaves)
    levels = mk.build_levels(ordered)
    return {
        "caseId": case_id,
        "description": description,
        "inputLeavesInSubmissionOrder": [d for _, d in inputs],
        "expected": {
            "sortedItemKeysHex": [hx(l.key) for l in ordered],
            "leafBytesHex": [hx(mk.leaf_bytes(l)) for l in ordered],
            "leafHashesHex": [hx(mk.leaf_hash(l)) for l in ordered],
            "levelsBottomUpHex": [[hx(h) for h in level] for level in levels],
            "merkleRootHex": hx(mk.merkle_root(ordered)),
            "leafCount": len(ordered),
            "leafIndexDigestHex": hx(mk.leaf_index_digest(ordered)),
        },
    }


def build_merkle_vectors() -> dict:
    header, d_header = fixture_leaf("RecordHeader", RECORD, 0x11, "RecordHeader")
    value, d_value = fixture_leaf("FieldValue", FIELD_VALUE, 0x33, "FieldValue")
    attachment, d_attachment = fixture_leaf("Attachment", ATTACHMENT, 0x55, "Attachment")
    group, d_group = fixture_leaf("RepeatedGroup", GROUP, 0x22, "RepeatedGroup")
    selection, d_selection = fixture_leaf("FieldSelection", SELECTION, 0x44, "FieldSelection")

    cases = [
        merkle_case(
            "MRKV1-3LEAF-NORMATIVE",
            "Protocol section 8 three-item manifest (RecordHeader, FieldValue, Attachment). Odd trailing leaf promoted without duplication.",
            [(header, d_header), (value, d_value), (attachment, d_attachment)],
        ),
        merkle_case(
            "MRKV1-3LEAF-UNSORTED-INPUT",
            "Same three items submitted in reverse order; output must be identical to MRKV1-3LEAF-NORMATIVE.",
            [(attachment, d_attachment), (value, d_value), (header, d_header)],
        ),
        merkle_case(
            "MRKV1-1LEAF",
            "Single RecordHeader leaf: root equals the leaf hash.",
            [(header, d_header)],
        ),
        merkle_case(
            "MRKV1-4LEAF-BALANCED",
            "Four leaves: two internal nodes then root.",
            [(header, d_header), (group, d_group), (value, d_value), (attachment, d_attachment)],
        ),
        merkle_case(
            "MRKV1-5LEAF-DOUBLE-PROMOTION",
            "Five leaves: unpaired node promoted at level 1 and again at level 2.",
            [(header, d_header), (group, d_group), (value, d_value), (selection, d_selection), (attachment, d_attachment)],
        ),
    ]

    negatives = [
        {
            "caseId": "MRKV1-NEG-DUPLICATE-KEY",
            "description": "Two leaves with the same item_key must be rejected.",
            "inputLeavesInSubmissionOrder": [d_value, d_value],
            "expectedError": "DuplicateItemKey",
        },
        {
            "caseId": "MRKV1-NEG-KIND-MISMATCH",
            "description": "item_key kind code 3 but trailing item_kind_code 5 must be rejected.",
            "itemKeyHex": hx(value.key),
            "trailingItemKindCode": 5,
            "expectedError": "KindCodeMismatch",
        },
        {
            "caseId": "MRKV1-NEG-SIGNATURE-LENGTH",
            "description": "A 31-byte row_signature must be rejected (only exact 32-byte tags are valid).",
            "rowSignatureHex": hx(bytes([0x33]) * 31),
            "expectedError": "InvalidSignatureLength",
        },
        {
            "caseId": "MRKV1-NEG-MIXED-REVISION",
            "description": "Leaves bound to different committed revisions cannot form one manifest.",
            "inputLeavesInSubmissionOrder": [d_header, {**d_value, "committedContentRevision": REVISION + 1}],
            "expectedError": "MixedRevisionOrOperation",
        },
    ]

    return {
        "schema": "DynamicForms/MerkleManifest/test-vectors/v1",
        "status": "Gate 0 conformance vectors; machine-cross-verified in Python, C# and Node. Independent human cryptographic review still required before Stage A sign-off.",
        "normativeSource": "trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md sections 2.1, 2.2 and 8",
        "algorithm": {
            "hash": "SHA-256",
            "leafFraming": "0x00 || ASCII('DynamicForms/MerkleLeaf/v1') || UInt16BE(17) || item_key[17] || row_signature[32] || canonical_payload_digest[32] || item_kind_code[1] || UInt64BE(committed_content_revision) || committed_operation_uuid[16]",
            "nodeFraming": "SHA256(0x01 || ASCII('DynamicForms/MerkleNode/v1') || left[32] || right[32])",
            "emptyRoot": "SHA256(0x02 || ASCII('DynamicForms/MerkleEmpty/v1'))",
            "indexDigest": "SHA256(0x03 || ASCII('DynamicForms/ManifestIndex/v1') || UInt64BE(leaf_count) || concat_sorted(UInt16BE(17) || item_key[17] || leaf_hash[32]))",
            "itemKey": "1-byte kind code followed by 16 RFC 4122 network-order UUID bytes; leaves sorted by unsigned item_key bytes",
            "oddLevelRule": "final unpaired node is promoted unchanged, never duplicated",
            "kindCodes": mk.KIND_CODES,
            "uuidByteOrderWarning": ".NET Guid.ToByteArray() is mixed-endian; implementations must use RFC 4122 network order (Guid.ToByteArray(bigEndian: true)).",
        },
        "fixtures": {
            "organizationId": ORG,
            "formRecordId": RECORD,
            "committedOperationId": OPERATION,
            "committedContentRevision": REVISION,
            "rowSignatureWarning": "Row signatures are invented 32-byte fixtures (0x11.., 0x22.., ...). They are not HMAC outputs and do not authorize skipping signature verification.",
        },
        "emptyRootHex": hx(mk.empty_root()),
        "cases": cases,
        "negativeCases": negatives,
    }


FORM_RECORDS_V1 = ri.RowSchema(
    entity_kind="form_records",
    schema_version=1,
    columns=(
        ri.SignedColumn("created_by_user_id", "Uuid", False),
        ri.SignedColumn("created_form_version_id", "Uuid", False),
        ri.SignedColumn("form_definition_id", "Uuid", False),
        ri.SignedColumn("owner_team_id", "Uuid", True),
        ri.SignedColumn("owner_user_id", "Uuid", True),
        ri.SignedColumn("record_status", "Text", False),
        ri.SignedColumn("submitted_on", "TimestampUtc", True),
        ri.SignedColumn("tenant_id", "Uuid", False),
    ),
)

TYPE_COVERAGE_V1 = ri.RowSchema(
    entity_kind="integrity_type_coverage",
    schema_version=1,
    columns=(
        ri.SignedColumn("boolean_flag", "Boolean", False),
        ri.SignedColumn("bytes_value", "Bytes", True),
        ri.SignedColumn("date_value", "Date", True),
        ri.SignedColumn("decimal_value", "Decimal", True),
        ri.SignedColumn("int32_value", "Int32", False),
        ri.SignedColumn("int64_value", "Int64", False),
        ri.SignedColumn("json_value", "Json", True),
        ri.SignedColumn("text_value", "Text", True),
        ri.SignedColumn("timestamp_value", "TimestampUtc", True),
        ri.SignedColumn("uuid_value", "Uuid", True),
    ),
)

USER = "0199a2b3-c4d5-7e6f-8123-456789abce01"
TEAM = "0199a2b3-c4d5-7e6f-8123-456789abce02"
FORM = "0199a2b3-c4d5-7e6f-8123-456789abce03"
VERSION = "0199a2b3-c4d5-7e6f-8123-456789abce04"
PK = "0199a2b3-c4d5-7e6f-8123-456789abce05"


def schema_json(schema: ri.RowSchema) -> dict:
    return {
        "entityKind": schema.entity_kind,
        "schemaVersion": schema.schema_version,
        "signedColumns": [{"columnName": c.column_name, "type": c.type_name, "nullable": c.nullable} for c in schema.columns],
    }


def row_case(case_id: str, description: str, schema: ri.RowSchema, values: dict, *, key_version=1, row_revision=1, organization_id=ORG, primary_key_id=PK, input_values=None):
    canonical = ri.canonical_row_bytes(
        schema,
        key_version=key_version,
        organization_id=organization_id,
        primary_key_id=primary_key_id,
        row_revision=row_revision,
        values=values,
    )
    return {
        "caseId": case_id,
        "description": description,
        "entityKind": schema.entity_kind,
        "schemaVersion": schema.schema_version,
        "keyVersion": key_version,
        "organizationId": organization_id,
        "primaryKeyId": primary_key_id,
        "rowRevision": row_revision,
        "values": input_values if input_values is not None else values,
        "expected": {
            "canonicalBytesHex": hx(canonical),
            "canonicalByteLength": len(canonical),
            "canonicalPayloadDigestHex": hx(ri.canonical_payload_digest(canonical)),
            "rowSignatureHexWithTestKey": hx(ri.row_signature(canonical, TEST_HMAC_KEY)),
        },
    }


def build_row_vectors() -> dict:
    cases = [
        row_case(
            "ROWV1-FORM-RECORD-USER-OWNED",
            "form_records with owner_user_id set and owner_team_id NULL (explicit NULL marker is signed).",
            FORM_RECORDS_V1,
            {
                "created_by_user_id": USER,
                "created_form_version_id": VERSION,
                "form_definition_id": FORM,
                "owner_team_id": None,
                "owner_user_id": USER,
                "record_status": "Submitted",
                "submitted_on": "2026-10-17T14:30:00.250000Z",
                "tenant_id": ORG,
            },
        ),
        row_case(
            "ROWV1-FORM-RECORD-TEAM-OWNED",
            "Same record transferred to a team: owner_user_id NULL, owner_team_id set, row_revision 2. Canonical bytes must differ from the user-owned case.",
            FORM_RECORDS_V1,
            {
                "created_by_user_id": USER,
                "created_form_version_id": VERSION,
                "form_definition_id": FORM,
                "owner_team_id": TEAM,
                "owner_user_id": None,
                "record_status": "Submitted",
                "submitted_on": "2026-10-17T14:30:00.250000Z",
                "tenant_id": ORG,
            },
            row_revision=2,
        ),
        row_case(
            "ROWV1-TYPES-ALL-PRESENT",
            "Every v1 type populated. Text 'Caf\\u00e9' supplied in composed (NFC) form.",
            TYPE_COVERAGE_V1,
            {
                "boolean_flag": True,
                "bytes_value": "deadbeef",
                "date_value": "2024-02-29",
                "decimal_value": decimal.Decimal("1234.5600"),
                "int32_value": -2,
                "int64_value": 9007199254740993,
                "json_value": {"b": 1, "a": [True, None, "x"], "nested": {"z": "é", "y": -5}},
                "text_value": "Café",
                "timestamp_value": "2024-03-10T07:00:00.000001Z",
                "uuid_value": FIELD_VALUE,
            },
            input_values={
                "boolean_flag": True,
                "bytes_value": "deadbeef",
                "date_value": "2024-02-29",
                "decimal_value": "1234.5600",
                "int32_value": -2,
                "int64_value": 9007199254740993,
                "json_value": {"b": 1, "a": [True, None, "x"], "nested": {"z": "é", "y": -5}},
                "text_value": "Café",
                "timestamp_value": "2024-03-10T07:00:00.000001Z",
                "uuid_value": FIELD_VALUE,
            },
        ),
        row_case(
            "ROWV1-TYPES-NFC-EQUIVALENCE",
            "Identical to ROWV1-TYPES-ALL-PRESENT except text supplied decomposed ('Cafe' + U+0301); canonical bytes MUST be identical.",
            TYPE_COVERAGE_V1,
            {
                "boolean_flag": True,
                "bytes_value": "deadbeef",
                "date_value": "2024-02-29",
                "decimal_value": decimal.Decimal("1234.56"),
                "int32_value": -2,
                "int64_value": 9007199254740993,
                "json_value": {"nested": {"y": -5, "z": "é"}, "a": [True, None, "x"], "b": 1},
                "text_value": "Café",
                "timestamp_value": "2024-03-10T02:00:00.000001-05:00",
                "uuid_value": FIELD_VALUE.upper(),
            },
            input_values={
                "boolean_flag": True,
                "bytes_value": "DEADBEEF",
                "date_value": "2024-02-29",
                "decimal_value": "1234.56",
                "int32_value": -2,
                "int64_value": 9007199254740993,
                "json_value": {"nested": {"y": -5, "z": "é"}, "a": [True, None, "x"], "b": 1},
                "text_value": "Café",
                "timestamp_value": "2024-03-10T02:00:00.000001-05:00",
                "uuid_value": FIELD_VALUE.upper(),
            },
        ),
        row_case(
            "ROWV1-TYPES-ALL-NULL",
            "All nullable columns NULL; non-nullable boolean/int columns present.",
            TYPE_COVERAGE_V1,
            {
                "boolean_flag": False,
                "bytes_value": None,
                "date_value": None,
                "decimal_value": None,
                "int32_value": 0,
                "int64_value": 0,
                "json_value": None,
                "text_value": None,
                "timestamp_value": None,
                "uuid_value": None,
            },
        ),
        row_case(
            "ROWV1-EMPTY-STRING-NOT-NULL",
            "Empty text is tag 0x08 with length 0 and must differ from NULL (ROWV1-TYPES-ALL-NULL).",
            TYPE_COVERAGE_V1,
            {
                "boolean_flag": False,
                "bytes_value": "",
                "date_value": None,
                "decimal_value": None,
                "int32_value": 0,
                "int64_value": 0,
                "json_value": None,
                "text_value": "",
                "timestamp_value": None,
                "uuid_value": None,
            },
        ),
        row_case(
            "ROWV1-DECIMAL-NORMALIZATION",
            "Decimals normalize to minimal scale: 1.5E+3 -> (1500, scale 0); -0.000100 -> (-1, scale 4); 0.000 -> (0, scale 0) demonstrated across cases.",
            TYPE_COVERAGE_V1,
            {
                "boolean_flag": True,
                "bytes_value": None,
                "date_value": "1969-12-31",
                "decimal_value": decimal.Decimal("-0.000100"),
                "int32_value": 2147483647,
                "int64_value": -9223372036854775808,
                "json_value": [],
                "text_value": "日本語 \U0001F512",
                "timestamp_value": "1969-12-31T23:59:59.999999Z",
                "uuid_value": None,
            },
            input_values={
                "boolean_flag": True,
                "bytes_value": None,
                "date_value": "1969-12-31",
                "decimal_value": "-0.000100",
                "int32_value": 2147483647,
                "int64_value": -9223372036854775808,
                "json_value": [],
                "text_value": "日本語 \U0001F512",
                "timestamp_value": "1969-12-31T23:59:59.999999Z",
                "uuid_value": None,
            },
            key_version=3,
            row_revision=18446744073709551615,
        ),
    ]

    decimal_examples = []
    for text in ["1.5E+3", "1500", "1.5000", "1.5", "0.000", "0", "-0.000100", "-0.0001", "123456789012345.6789"]:
        decimal_examples.append({"input": text, "encodedHex": hx(ri.encode_decimal(decimal.Decimal(text)))})

    negatives = [
        {"caseId": "ROWV1-NEG-NULL-IN-NON-NULLABLE", "description": "NULL supplied for non-nullable boolean_flag.", "expectedError": "NullNotPermitted"},
        {"caseId": "ROWV1-NEG-JSON-FLOAT", "description": "json_value containing 1.5 (float) is rejected in v1.", "expectedError": "JsonFloatNotPermitted"},
        {"caseId": "ROWV1-NEG-JSON-NON-ASCII-KEY", "description": "json_value with key '\\u00e9' is rejected in v1.", "expectedError": "JsonKeyNotAscii"},
        {"caseId": "ROWV1-NEG-UNSORTED-REGISTRY", "description": "A schema whose signed columns are not ASCII-sorted is rejected at registry load.", "expectedError": "RegistryNotSorted"},
        {"caseId": "ROWV1-NEG-EXTRA-COLUMN", "description": "A value for a column absent from the registry is rejected (no silent extension).", "expectedError": "UnregisteredColumn"},
        {"caseId": "ROWV1-NEG-MISSING-COLUMN", "description": "Omitting a registered column is rejected (absent is not NULL).", "expectedError": "MissingColumn"},
        {"caseId": "ROWV1-NEG-NAIVE-TIMESTAMP", "description": "Timestamp without explicit offset is rejected.", "expectedError": "TimestampOffsetRequired"},
        {"caseId": "ROWV1-NEG-ROW-REVISION-ZERO", "description": "row_revision 0 is rejected (first revision is 1).", "expectedError": "RowRevisionRange"},
    ]

    return {
        "schema": "DynamicForms/RowHmac/test-vectors/v1",
        "status": "Gate 0 conformance vectors; machine-cross-verified in Python, C# and Node. Independent human cryptographic review still required before Stage A sign-off.",
        "normativeSource": "docs/dynamic-forms/row-integrity-format.md",
        "testHmacKeyHex": hx(TEST_HMAC_KEY),
        "testHmacKeyWarning": "Published TEST key used only to make HMAC outputs reproducible. Production integrity keys exist only inside the independent signing authority (Vault Transit locally, Azure Managed HSM in production) and are never exportable.",
        "schemas": [schema_json(FORM_RECORDS_V1), schema_json(TYPE_COVERAGE_V1)],
        "decimalEncodingExamples": decimal_examples,
        "cases": cases,
        "negativeCases": negatives,
    }


def main() -> int:
    merkle_path = ROOT / "trust_authority" / "merkle-v1.test-vectors.json"
    row_path = ROOT / "trust_authority" / "row-integrity-v1.test-vectors.json"
    merkle_path.write_text(json.dumps(build_merkle_vectors(), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    row_path.write_text(json.dumps(build_row_vectors(), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {merkle_path.relative_to(ROOT).as_posix()}")
    print(f"wrote {row_path.relative_to(ROOT).as_posix()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
