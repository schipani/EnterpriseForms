#!/usr/bin/env python3
"""Generate the security-journal envelope v1 known-answer vectors (SEC-BL-01 WP-1, C-155).

Output (repository-relative):
  trust_authority/journal-envelope-v1.test-vectors.json

This is an independent re-implementation of the canonical envelope that JournalChain.Append writes
(src/authority/AuthorizationAuthority.Application/Features/Journal/JournalChain.cs) through CanonicalJson
(System.Text.Json, snake_case, no indentation, UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition.Never):

  {"envelope_format":"DynamicForms/SecurityJournal/v1","organization_id":<uuid D>,"stream_name":...,
   "stream_revision":<integer>,"event_kind":...,"previous_digest_hex":<64 lower hex | null>,
   "created_on_utc":<ISO 8601, +00:00, fraction trimmed>,"body":{...}}

journal_event_digest = SHA-256 of exactly those UTF-8 bytes; previous_digest_hex is the digest of the
previous entry of the same (organization, stream), null at revision 1.

The C# test JournalEnvelopeKnownAnswerTests drives the real JournalChain.Append with these inputs and
requires byte-identical output, then verifies every vector with JournalChainRules. Regenerating this file
is not evidence that the format is correct: any change needs review (the format is frozen as v1).
Writes LF only (spec-files.json digests are computed over LF content).
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "trust_authority" / "journal-envelope-v1.test-vectors.json"

ORG_A = "019a5b2c-5ec1-7b01-8000-00000000000a"
ORG_B = "019a5b2c-5ec1-7b01-8000-00000000000b"
KIND = "KnownAnswerVectorEvent"


def canonical_string(value: str) -> str:
    # Relaxed escaping for the characters these vectors use: quote and backslash escaped, everything else
    # (including non-ASCII, '<', '>', '&', '\'' and '+') emitted as is. Control characters are not used.
    assert not any(ord(c) < 0x20 for c in value), "control characters are outside these vectors"
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def canonical_value(value) -> str:
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, str):
        return canonical_string(value)
    raise TypeError(type(value))


def canonical_object(pairs: list[tuple[str, object]]) -> str:
    return "{" + ",".join(canonical_string(k) + ":" + (v if isinstance(v, RawJson) else canonical_value(v)) for k, v in pairs) + "}"


class RawJson(str):
    pass


def envelope(org: str, stream: str, revision: int, previous_hex: str | None, created: str, body: dict) -> str:
    body_json = RawJson(canonical_object(list(body.items())))
    return canonical_object([
        ("envelope_format", "DynamicForms/SecurityJournal/v1"),
        ("organization_id", org),
        ("stream_name", stream),
        ("stream_revision", revision),
        ("event_kind", KIND),
        ("previous_digest_hex", previous_hex),
        ("created_on_utc", created),
        ("body", body_json),
    ])


def body(note: str, counter: int, flag: bool, nullable: str | None) -> dict:
    # Mirrors the C# record KatBody(string NoteText, long CounterValue, bool FlagValue, string? NullableValue).
    return {"note_text": note, "counter_value": counter, "flag_value": flag, "nullable_value": nullable}


def main() -> None:
    cases = [
        ("JEV1-AUDIT-GENESIS", ORG_A, "audit", 1, None, "2026-11-17T12:00:00+00:00", 0, body("genesis", 1, True, None),
         "Revision 1 of a stream: previous_digest_hex is null (R-5). Whole-second time: no fraction is written."),
        ("JEV1-AUDIT-SECOND", ORG_A, "audit", 2, "JEV1-AUDIT-GENESIS", "2026-11-17T12:00:00.1234567+00:00", 1234567, body("second <&>' é", 2, False, "present"),
         "Revision 2 links to the digest of revision 1 (R-7). Seven fraction digits; relaxed escaping keeps <, >, &, ' and é unescaped."),
        ("JEV1-AUDIT-THIRD", ORG_A, "audit", 3, "JEV1-AUDIT-SECOND", "2026-11-17T12:00:01.5+00:00", 15000000, body("quote \" and backslash \\", 3, True, None),
         "Revision 3 links to revision 2. Fraction trimmed to one digit; quote and backslash are escaped."),
        ("JEV1-AUTHORIZATION-GENESIS", ORG_A, "authorization", 1, None, "2026-11-17T12:00:02+00:00", 20000000, body("authorization stream", 9007199254740993, False, None),
         "Another stream of the same organization starts its own chain at revision 1; a 64-bit integer beyond double precision is written exactly."),
        ("JEV1-OTHER-ORGANIZATION-GENESIS", ORG_B, "audit", 1, None, "2026-11-17T12:00:00+00:00", 0, body("genesis", 1, True, None),
         "Same body and time as JEV1-AUDIT-GENESIS for another organization: a different envelope and digest (organization binding)."),
    ]
    digests: dict[str, str] = {}
    vectors = []
    for vector_id, org, stream, revision, previous_id, created, ticks_after_noon, b, description in cases:
        previous_hex = digests[previous_id] if previous_id else None
        text = envelope(org, stream, revision, previous_hex, created, b)
        digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
        digests[vector_id] = digest
        vectors.append({
            "id": vector_id,
            "description": description,
            "organization_id": org,
            "stream_name": stream,
            "stream_revision": revision,
            "event_kind": KIND,
            "previous_vector_id": previous_id,
            "previous_digest_hex": previous_hex,
            "created_on_utc": created,
            "created_ticks_after_2026_11_17_12_00_utc": ticks_after_noon,
            "body": b,
            "expected_canonical_utf8": text,
            "expected_sha256_hex": digest,
        })

    document = {
        "format": "DynamicForms/SecurityJournal/v1",
        "purpose": "Known-answer vectors for the security-journal canonical envelope (SEC-BL-01 WP-1; contract docs/security/SEC_BL_01_DR_CONTRACT_CLOSURE.md section 4).",
        "generator": "scripts/integrity/generate_journal_envelope_vectors.py",
        "vectors": vectors,
    }
    OUTPUT.write_bytes((json.dumps(document, indent=2, ensure_ascii=True) + "\n").encode("utf-8"))
    print(f"wrote {OUTPUT.relative_to(ROOT).as_posix()} ({len(vectors)} vectors)")


if __name__ == "__main__":
    main()
