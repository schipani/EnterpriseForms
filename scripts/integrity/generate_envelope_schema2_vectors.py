#!/usr/bin/env python3
"""Generate the E3-M2b-2 signed schema 2 conformance vectors for the two envelope row kinds.

Output (repository-relative):
  trust_authority/row-integrity-v1-envelope-schema2.test-vectors.json

This is a separate entry point (OD-M2-14, OD-M2-25): it writes only the new file and never the frozen
merkle-v1 / row-integrity-v1 vector files, which generate_vectors.py owns. The framing is RowHmac v1,
unchanged (docs/dynamic-forms/row-integrity-format.md section 2); only the registered schema of
field_encrypted_values and file_attachment_versions gains version 2 (contract section 4).

The schemas below mirror BusinessSignedSchemas (C#); the C# contract test checks that they are equal and
that the business encoder reproduces every case byte for byte. Regenerating the file is not evidence that
the format is correct: every change needs review.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import row_integrity_v1 as ri  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "trust_authority" / "row-integrity-v1-envelope-schema2.test-vectors.json"

TEST_HMAC_KEY = bytes(range(32))  # 000102...1f, TEST ONLY, published, never a real key

ORG = "11111111-1111-4111-8111-111111111111"
PK = "0199a2b3-c4d5-7e6f-8123-456789abce05"
OPERATION = "0199a2b3-c4d5-7e6f-8123-456789abce06"
RECORD = "0199a2b3-c4d5-7e6f-8123-456789abce07"
FIELD = "0199a2b3-c4d5-7e6f-8123-456789abce08"
ITEM = "0199a2b3-c4d5-7e6f-8123-456789abce09"
DATA_KEY = "0199a2b3-c4d5-7e6f-8123-456789abce0a"
GROUP = "0199a2b3-c4d5-7e6f-8123-456789abce0b"
ATTACHMENT = "0199a2b3-c4d5-7e6f-8123-456789abce0c"
UPLOADER = "0199a2b3-c4d5-7e6f-8123-456789abce0d"
DATA_KEY_DOCUMENT = "0199a2b3-c4d5-7e6f-8123-456789abce0e"


def col(name: str, type_name: str, nullable: bool = False) -> ri.SignedColumn:
    return ri.SignedColumn(name, type_name, nullable)


def sorted_schema(kind: str, version: int, columns: list[ri.SignedColumn]) -> ri.RowSchema:
    return ri.RowSchema(kind, version, tuple(sorted(columns, key=lambda c: c.column_name)))


VALUE_ENVELOPE = [
    col("content_change_operation_id", "Uuid"),
    col("field_definition_id", "Uuid"),
    col("field_value_item_id", "Uuid"),
    col("form_record_group_id", "Uuid", True),
    col("form_record_id", "Uuid"),
    col("tenant_id", "Uuid"),
]

FIELD_V1_VALUE_COLUMNS = [
    col("encrypted_logical_type", "Text"),
    col("encrypted_value_bytes", "Bytes"),
    col("encryption_algorithm_name", "Text"),
    col("encryption_auth_tag_bytes", "Bytes"),
    col("encryption_envelope_version", "Int32"),
    col("encryption_key_reference", "Text"),
    col("encryption_nonce_bytes", "Bytes"),
    col("wrapped_data_key_bytes", "Bytes"),
]

ENVELOPE_V2_COLUMNS = [
    col("data_key_commitment_bytes", "Bytes"),
    col("data_key_identifier", "Uuid"),
    col("key_wrap_algorithm_code", "Int32"),
]

FIELD_ENCRYPTED_VALUES_V1 = sorted_schema("field_encrypted_values", 1, VALUE_ENVELOPE + FIELD_V1_VALUE_COLUMNS)
FIELD_ENCRYPTED_VALUES_V2 = sorted_schema("field_encrypted_values", 2, VALUE_ENVELOPE + FIELD_V1_VALUE_COLUMNS + ENVELOPE_V2_COLUMNS)

ATTACHMENT_V1_COLUMNS = [
    col("ciphertext_sha256", "Bytes"),
    col("ciphertext_size_bytes", "Int64"),
    col("content_change_operation_id", "Uuid"),
    col("detected_content_type", "Text"),
    col("encryption_algorithm_name", "Text"),
    col("encryption_envelope_version", "Int32"),
    col("encryption_key_reference", "Text"),
    col("encryption_nonce_prefix", "Bytes"),
    col("encryption_segment_size_bytes", "Int32"),
    col("field_definition_id", "Uuid"),
    col("file_attachment_id", "Uuid"),
    col("form_record_id", "Uuid"),
    col("original_file_name", "Text"),
    col("plaintext_sha256", "Bytes"),
    col("plaintext_size_bytes", "Int64"),
    col("scan_signature_name", "Text", True),
    col("scan_verdict", "Text"),
    col("scanned_on", "TimestampUtc"),
    col("scanner_name", "Text"),
    col("storage_object_key", "Text"),
    col("storage_provider_name", "Text"),
    col("tenant_id", "Uuid"),
    col("uploaded_by_user_id", "Uuid"),
    col("uploaded_on", "TimestampUtc"),
    col("version_number", "Int32"),
    col("wrapped_data_key", "Bytes"),
]

FILE_ATTACHMENT_VERSIONS_V1 = sorted_schema("file_attachment_versions", 1, ATTACHMENT_V1_COLUMNS)
FILE_ATTACHMENT_VERSIONS_V2 = sorted_schema("file_attachment_versions", 2, ATTACHMENT_V1_COLUMNS + ENVELOPE_V2_COLUMNS)

SCHEMAS = [FIELD_ENCRYPTED_VALUES_V1, FIELD_ENCRYPTED_VALUES_V2, FILE_ATTACHMENT_VERSIONS_V1, FILE_ATTACHMENT_VERSIONS_V2]


def hx(b: bytes) -> str:
    return b.hex()


def schema_json(schema: ri.RowSchema) -> dict:
    return {
        "entityKind": schema.entity_kind,
        "schemaVersion": schema.schema_version,
        "signedColumns": [{"columnName": c.column_name, "type": c.type_name, "nullable": c.nullable} for c in schema.columns],
    }


def field_values(envelope_version: int, *, wrap_code: int = 1, group: str | None = None, reference_family: str = "localdev") -> dict:
    values = {
        "content_change_operation_id": OPERATION,
        "encrypted_logical_type": "String",
        "encrypted_value_bytes": "0001020304",
        "encryption_algorithm_name": "AES-256-GCM",
        "encryption_auth_tag_bytes": "aa" * 16,
        "encryption_envelope_version": envelope_version,
        "encryption_key_reference": f"{reference_family}:{ORG}:field-value:1",
        "encryption_nonce_bytes": "0b" * 12,
        "field_definition_id": FIELD,
        "field_value_item_id": ITEM,
        "form_record_group_id": group,
        "form_record_id": RECORD,
        "tenant_id": ORG,
        "wrapped_data_key_bytes": "cc" * 60,
    }
    if envelope_version == 2:
        values["data_key_commitment_bytes"] = "dd" * 32
        values["data_key_identifier"] = DATA_KEY
        values["key_wrap_algorithm_code"] = wrap_code
    return values


def attachment_values(envelope_version: int) -> dict:
    values = {
        "ciphertext_sha256": "11" * 32,
        "ciphertext_size_bytes": 1048604,
        "content_change_operation_id": OPERATION,
        "detected_content_type": "application/pdf",
        "encryption_algorithm_name": "AES-256-GCM",
        "encryption_envelope_version": envelope_version,
        "encryption_key_reference": f"vault-transit:{ORG}:document:3",
        "encryption_nonce_prefix": "22" * 8,
        "encryption_segment_size_bytes": 1048576,
        "field_definition_id": FIELD,
        "file_attachment_id": ATTACHMENT,
        "form_record_id": RECORD,
        "original_file_name": "Évaluation 2026.pdf",
        "plaintext_sha256": "33" * 32,
        "plaintext_size_bytes": 1048576,
        "scan_signature_name": None,
        "scan_verdict": "Clean",
        "scanned_on": "2026-11-15T12:00:00.123456+00:00",
        "scanner_name": "clamd",
        "storage_object_key": "documents/0199a2b3c4d57e6f8123456789abce0c/1",
        "storage_provider_name": "AzureBlob",
        "tenant_id": ORG,
        "uploaded_by_user_id": UPLOADER,
        "uploaded_on": "2026-11-15T11:59:58.000001+00:00",
        "version_number": 1,
        "wrapped_data_key": "44" * 60,
    }
    if envelope_version == 2:
        values["data_key_commitment_bytes"] = "55" * 32
        values["data_key_identifier"] = DATA_KEY_DOCUMENT
        values["key_wrap_algorithm_code"] = 2
    return values


def row_case(case_id: str, description: str, schema: ri.RowSchema, values: dict, *, key_version: int = 1, row_revision: int = 1) -> dict:
    canonical = ri.canonical_row_bytes(schema, key_version=key_version, organization_id=ORG, primary_key_id=PK, row_revision=row_revision, values=values)
    return {
        "caseId": case_id,
        "description": description,
        "entityKind": schema.entity_kind,
        "schemaVersion": schema.schema_version,
        "keyVersion": key_version,
        "organizationId": ORG,
        "primaryKeyId": PK,
        "rowRevision": row_revision,
        "values": values,
        "expected": {
            "canonicalBytesHex": hx(canonical),
            "canonicalByteLength": len(canonical),
            "canonicalPayloadDigestHex": hx(ri.canonical_payload_digest(canonical)),
            "rowSignatureHexWithTestKey": hx(ri.row_signature(canonical, TEST_HMAC_KEY)),
        },
    }


def build() -> dict:
    cases = [
        row_case("ENVS2-FIELD-S1-REFERENCE", "field_encrypted_values schema 1 / envelope 1: the contract section 4.5 fixture (unchanged v1 framing; 14 columns).",
                 FIELD_ENCRYPTED_VALUES_V1, field_values(1)),
        row_case("ENVS2-FIELD-S2-LOCAL-WRAP", "field_encrypted_values schema 2 / envelope 2, wrap code 1: the contract section 4.5 fixture (17 columns).",
                 FIELD_ENCRYPTED_VALUES_V2, field_values(2)),
        row_case("ENVS2-FIELD-S2-VAULT-WRAP-GROUP", "field_encrypted_values schema 2 / envelope 2, wrap code 2, a repeating-group occurrence, key version 4, row revision 9.",
                 FIELD_ENCRYPTED_VALUES_V2, field_values(2, wrap_code=2, group=GROUP, reference_family="vault-transit"), key_version=4, row_revision=9),
        row_case("ENVS2-ATTACHMENT-S1", "file_attachment_versions schema 1 / envelope 1 (26 columns; NULL scan signature; non-ASCII file name).",
                 FILE_ATTACHMENT_VERSIONS_V1, attachment_values(1)),
        row_case("ENVS2-ATTACHMENT-S2", "file_attachment_versions schema 2 / envelope 2 (29 columns).",
                 FILE_ATTACHMENT_VERSIONS_V2, attachment_values(2)),
    ]
    negatives = [
        {"caseId": "ENVS2-NEG-FIELD-NULL-DATA-KEY-IDENTIFIER", "baseCaseId": "ENVS2-FIELD-S2-LOCAL-WRAP", "set": {"data_key_identifier": None}, "expectedError": "NullNotPermitted",
         "description": "Schema 2 refuses a NULL data-key identifier (no v2 row without metadata can be signed)."},
        {"caseId": "ENVS2-NEG-FIELD-NULL-COMMITMENT", "baseCaseId": "ENVS2-FIELD-S2-LOCAL-WRAP", "set": {"data_key_commitment_bytes": None}, "expectedError": "NullNotPermitted",
         "description": "Schema 2 refuses a NULL key commitment."},
        {"caseId": "ENVS2-NEG-FIELD-NULL-WRAP-CODE", "baseCaseId": "ENVS2-FIELD-S2-LOCAL-WRAP", "set": {"key_wrap_algorithm_code": None}, "expectedError": "NullNotPermitted",
         "description": "Schema 2 refuses a NULL wrap-algorithm code."},
        {"caseId": "ENVS2-NEG-FIELD-MISSING-V2-COLUMN", "baseCaseId": "ENVS2-FIELD-S2-LOCAL-WRAP", "remove": ["key_wrap_algorithm_code"], "expectedError": "MissingColumn",
         "description": "Schema 2 refuses an omitted v2 column (absent is not NULL)."},
        {"caseId": "ENVS2-NEG-FIELD-S1-WITH-V2-COLUMN", "baseCaseId": "ENVS2-FIELD-S1-REFERENCE", "set": {"data_key_identifier": DATA_KEY}, "expectedError": "UnregisteredColumn",
         "description": "Schema 1 cannot carry a v2 column (no silent extension of schema 1)."},
        {"caseId": "ENVS2-NEG-ATTACHMENT-NULL-COMMITMENT", "baseCaseId": "ENVS2-ATTACHMENT-S2", "set": {"data_key_commitment_bytes": None}, "expectedError": "NullNotPermitted",
         "description": "Document schema 2 refuses a NULL key commitment."},
        {"caseId": "ENVS2-NEG-ATTACHMENT-WRAP-CODE-TYPE", "baseCaseId": "ENVS2-ATTACHMENT-S2", "set": {"key_wrap_algorithm_code": "2"}, "expectedError": "TypeMismatch",
         "description": "The wrap code is an Int32, never text."},
    ]
    return {
        "schema": "DynamicForms/RowHmac/test-vectors/envelope-schema2/v1",
        "status": "E3-M2b-2 conformance vectors (Development/Testing). RowHmac v1 framing unchanged; signed schema 2 of the two envelope tables. Machine-cross-verified in Python, Node and C#; not an independent cryptographic review. Schema 2 is registered but not accepted by the Authority.",
        "normativeSource": "docs/dynamic-forms/row-integrity-format.md section 7; docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md sections 4 and 23",
        "testHmacKeyHex": hx(TEST_HMAC_KEY),
        "testHmacKeyWarning": "Published TEST key used only to make HMAC outputs reproducible. Production integrity keys exist only inside the independent signing authority and are never exportable.",
        "schemas": [schema_json(s) for s in SCHEMAS],
        "cases": cases,
        "negativeCases": negatives,
    }


def main() -> int:
    # Bytes with LF on every platform (.gitattributes eol=lf; the specification manifest refuses CRLF).
    OUTPUT.write_bytes((json.dumps(build(), indent=2, ensure_ascii=False) + "\n").encode("utf-8"))
    print(f"wrote {OUTPUT.relative_to(ROOT).as_posix()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
