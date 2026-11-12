# Phase C8 M4-1: Design A (M1 reason-note sealing) and Design B (S1 projection integrity)

- **Status:** **Revision 2, approved in C-117 and implemented in Development/Testing** (S1 `5f5fb07` with test fix `051a65d`; M1 `9b47a8b`). Not pushed, not merged, not independently reviewed. Revision 1 was C-115; revision 2 was C-116.
  * **Approved in C-117:** A-Q2, B-Q2, B-Q5 (journal digest in the marker), B-Q6 (quarantine on any confirmed unchanged-state mismatch), B-Q7 (permanence trigger), B-Q8 (one database backend behind SpiceDB at a time; replacement and restore only with SpiceDB stopped), B-Q9 (added p95 ≤ 15 ms, subject to Azure staging validation).
  * **V3 amended (owner approval, C-118):** an explicit `null` envelope is equivalent to an omitted one (no note); §1.5.
  * T16-S passes; T16S-1 is remediated in Development/Testing (RR-11), pending independent review and staging evidence. D8-72 containment is lifted (RR-12).
  * Still not authorized: push, PR, merge, deploy, M4-2, RCF-1 remediation and G14.
- **Audience:** the project owner (the final decisions in §3) and the independent Authority reviewer. Claude (Opus) wrote this in the owner's session. It is not an independent review.
- **Baseline:** branch `phase-c8/m4-governance-audit` at `e117399`. Findings come from `PHASE_C8_M4_1_REVIEW_AND_REMEDIATION.md` §1.5 and §2.4.
- **Evidence:** focused protocol verification under C-116, in `docs/evidence/phase-c8/m4-1-protocol-verification/`, run against the security-test stack (`authzed/spicedb:v1.56.2`):
  * read-only characterisation (P1–P6);
  * live datastore rollback during active checks, with and without a SpiceDB restart (E1, E3);
  * a snapshot pinned across a restore (E2, E3);
  * datastore replacement (E4, E5);
  * latency under load;
  * the M1 known-answer vector, independently reproduced.

---

## 1. Design A: M1, a seal-only encryption component for Security Auditor reason notes

### 1.1 What is reused and what is new

| | Reused unchanged | New |
|---|---|---|
| Primitive | AES-256-GCM (`System.Security.Cryptography.AesGcm`), 128-bit tag | — |
| Data key | A random 256-bit DEK per note (`RandomNumberGenerator`), single use, zeroed after use | — |
| Nonce | A random 96-bit nonce (unique because the DEK is single use) | — |
| Key wrap | `IEncryptionKeyProvider.WrapDataKey(dek, CurrentKeyReference)`. Today that is `LocalDevelopmentKeyProvider`: AES-GCM wrap with AAD `"DynamicForms/DekWrap/v1:" + keyReference`, output `nonce(12) ‖ cipher(32) ‖ tag(16)`. Elsewhere it is `UnavailableKeyProvider`, which fails closed. | — |
| Context | — | `AuditorReasonNoteContext` and its AAD domain `"DynamicForms/AuditorReasonNote/v1"` (§1.2) |
| Plaintext encoding | — | A fixed 2,048-byte padded encoding (§1.3) |
| Component | — | `IAuditorNoteSealer` port (Application) with a single method, `Seal`. `AesGcmAuditorNoteSealer` (Infrastructure, `Features/Organizations`). |

- **Not modified:** `AesGcmFieldEncryptionService`, `AesGcmFileEncryptionService` and `IEncryptionKeyProvider`.
- **No `Open`:** the sealer has no decrypt method, and none exists anywhere in the product (D8-73).
- **Test-only opener:** decryption exists only as a test helper in the test project, to prove the confidentiality and binding properties (§1.8).

### 1.2 Authenticated context and associated data (exact construction)

**Context values and where each comes from.** The Authority never takes these from the envelope.

| Context field | Business side (sealing) | Authority side (validation, and any future authorized opening) |
|---|---|---|
| `organization_id` | the verified request context (`RequestContext.OrganizationId`) | the command's `organization_id` |
| `target_authority_user_id` | the route or body user id | the payload's `authority_user_id` |
| `role_name` | the constant `security_auditor` | the payload's `role_name` (only `security_auditor` is permitted with an envelope) |
| `direction` | `appoint` for GrantSystemRole, `remove` for RevokeSystemRole | derived from the command kind, the same mapping |
| `note_id` | a new UUID v7 per note (`Guid.CreateVersion7()`) | the envelope's `note_id` (§1.4) |

**AAD byte string.** Every integer is big-endian. A UUID is its 16-byte big-endian form (`Guid.TryWriteBytes(span, bigEndian: true, out _)`; in Python, `uuid.UUID(...).bytes`).

```
AAD =  ASCII("DynamicForms/AuditorReasonNote/v1")      33 bytes, no terminator
    ‖  Int32BE(envelope_version = 1)                    4
    ‖  UUID16(organization_id)                         16
    ‖  UUID16(target_authority_user_id)                16
    ‖  UUID16(note_id)                                 16
    ‖  Int32BE(len(role_name))  ‖ ASCII(role_name)      4 + 16 ("security_auditor")
    ‖  Int32BE(len(direction))  ‖ ASCII(direction)     4 + 7 ("appoint") or 4 + 6 ("remove")
                                                        total 116 (appoint) or 115 (remove) bytes
```

- **Domain separation by construction.**
  * Field-value AAD starts with `DynamicForms/FieldValue/v1`.
  * Document-segment AAD starts with `Int32BE(1)` (`00 00 00 01`).
  * The note label differs from the field label at byte 13 (`A` vs `F`), and from the document AAD at byte 0. Every variable-length part is length-prefixed, so no field-value or document AAD can equal a note AAD.
- **What a note cannot be moved to:** another organization, person, role, direction or note id. Opening it under any changed value fails the GCM tag check.
- **No command id in the AAD.** The Authority creates the command id after the business seals.
  * Moving an envelope to another command with the same organization, person, role and direction is blocked by **note-id uniqueness** (§1.5, V10).
  * The note id is in the AAD, so it cannot be changed without breaking the tag.
- **Key reference not in the AAD:** like the field service, the AAD omits the key reference. The DEK-wrap AAD already binds it.
- **Format version:** `envelope_version = 1` is in the AAD, so the padding format (§1.3) is bound to it. Any future format needs a new version.

### 1.3 Plaintext padding (A-Q1 approved): deterministic, unambiguous and authenticated

**Encoding**, business side, after the B2 validation:

```
N   = Normalize(Trim(note), NFC)                    (string.Trim(), then NormalizationForm.FormC)
U   = UTF-8(N), strict (new UTF8Encoding(false, throwOnInvalidBytes: true); lone surrogates are refused)
L   = len(U)                                        1 ≤ L ≤ 1,500  (≤ 500 UTF-16 units × 3 bytes)
P   = UInt16BE(L) ‖ U ‖ 0x00 × (2,046 − L)           |P| = 2,048 exactly
C‖T = AES-256-GCM(DEK, nonce, P, AAD)                |C| = 2,048, |T| = 16
```

- **Authenticated:** the length prefix and the padding are inside the GCM plaintext, so the tag covers them. Changing either, or truncating `C`, fails decryption.
- **Deterministic and unambiguous:** `L` fixes the content boundary. Distinct notes give distinct `P`, and each `P` decodes to exactly one note.
- **Decoding rules,** which bind the test-only opener and any future reviewed read path. All must hold, or the note is rejected as malformed even though the tag verified:
  * `|P| = 2,048`;
  * `1 ≤ L ≤ 1,500`;
  * every byte after `U` equals `0x00`;
  * `U` is strict UTF-8;
  * `N` equals its own NFC normalization and trim;
  * `N` contains no C0 or C1 control character except LF.
- **No length disclosure:** every ciphertext is 2,048 bytes.

### 1.4 Envelope schema (wire and stored form)

The member sits inside the GrantSystemRole and RevokeSystemRole payload for `role_name = security_auditor`, as `reason_note_envelope`. It is part of the normalized payload, so the command request digest, idempotency, the authorization-stream journal body and its hash chain, and the archive all cover the ciphertext.

```json
"reason_note_envelope": {
  "envelope_format":       "DynamicForms/AuditorReasonNote",
  "envelope_version":      1,
  "algorithm_name":        "AES-256-GCM",
  "key_reference":         "local-dev-kek-v1",
  "note_id":               "019a5b2c-3333-7000-8000-000000000003",
  "nonce_b64":             "<12 bytes>",
  "ciphertext_b64":        "<exactly 2,048 bytes>",
  "tag_b64":               "<16 bytes>",
  "wrapped_data_key_b64":  "<29..512 bytes; 60 for LocalDevelopment>"
}
```

- **Excluded on purpose:**
  * plaintext length, which is hidden (§1.3);
  * provider name: the key reference identifies the KEK version;
  * any copy of the context fields: they come from the command, so they cannot disagree with it.
- `reason_note` (plaintext) stays a **recognized and permanently refused** member.

### 1.5 Authority validation contract (structural, before any persistence)

**Where it runs:** in `SecurityAuditorAsync`, which runs inside `CommandPlanner.PlanAsync`, under the organization lock that `SecurityCommandService.SubmitAsync` holds for the whole command.

**What gets persisted:**
- Nothing is persisted before planning succeeds.
- On success, the stored payload is the **normalized** payload, which contains only validated values.
- On any rejection, the row stores only `RoleCommandEvidence`, never the envelope.

**Rules.** Every check is exact; nothing is normalized, trimmed or case-folded.

| # | Rule | Refusal |
|---|---|---|
| V1 | The payload binds strictly (`UnmappedMemberHandling.Disallow`). An unknown member, in the payload or the envelope, is refused. | `INVALID_COMMAND` |
| V2 | `reason_note` present with any non-null value | `REASON_NOTE_NOT_ACCEPTED` (permanent; the D8-72 plaintext refusal) |
| V3 | `reason_note_envelope` is absent, an explicit JSON `null`, or an object. Absent and `null` are equivalent: no note (amended by owner approval, C-118). Any other value (string, number, boolean, array) is refused, and an object must pass V4–V11. A plaintext `reason_note` is refused under V2 whatever the envelope is. | `INVALID_COMMAND` |
| V4 | `envelope_format`, `envelope_version` and `algorithm_name` equal exactly `DynamicForms/AuditorReasonNote`, `1` and `AES-256-GCM` | `INVALID_COMMAND` |
| V5 | `key_reference` is 1–128 characters from `[A-Za-z0-9._:/-]` | `INVALID_COMMAND` |
| V6 | `note_id` is a canonical lowercase hyphenated UUID, version nibble `7`, RFC 4122 variant, not all zeros | `INVALID_COMMAND` |
| V7 | Each `*_b64` is strict RFC 4648 §4 base64: standard alphabet, required padding, no whitespace, canonical re-encoding equal to the input | `INVALID_COMMAND` |
| V8 | Decoded sizes: nonce = 12, tag = 16, **ciphertext = 2,048**, wrapped DEK = 29–512 | `INVALID_COMMAND` |
| V9 | Serialized envelope ≤ 4,096 bytes; whole payload ≤ 8,192 bytes | `INVALID_COMMAND` |
| V10 | `note_id` is unused in the organization: no non-rejected `security_commands` row of kind Grant or RevokeSystemRole with `role_name = security_auditor` carries it. The rows are read and parsed in the application, under the organization lock, so the check is race-free. An idempotent replay (same key, same digest) returns before planning. | `INVALID_COMMAND` |
| V11 | An envelope with `role_name` other than `security_auditor` | `INVALID_COMMAND` |

- **Rejection evidence is unchanged:**
  * the row keeps only `RoleCommandEvidence`;
  * the `SecurityAuditorCommandRejected` audit entry keeps its fixed field list;
  * the detail never echoes envelope values.
- **What the Authority cannot check:**
  * whether the tag verifies;
  * whether the plaintext is a real note.

  A business workload able to call the Authority could submit random but well-formed bytes. That stays within the existing trust model: notes are non-authoritative and never decrypted.

### 1.6 Business-side contract (sealing)

| # | Rule |
|---|---|
| B1 | `reasonNote` (string, optional) comes back to the Security Auditor appoint and remove request bodies, and is refused (422) for any other role command |
| B2 | Validation: `Trim()`, then `Normalize(FormC)`. 1–500 UTF-16 units. Strict UTF-8 (no lone surrogates). No C0 or C1 control characters except LF (U+000A). Otherwise `422 INVALID_REQUEST`, and the value is never echoed. |
| B3 | `keys.IsConfigured == false`: a request **with** a note gets `503 ENCRYPTION_UNAVAILABLE` and nothing is sent to the Authority. A request **without** a note proceeds unchanged. |
| B4 | Seal exactly once per request, before `SubmitAndAwaitAsync`. The same payload object, and therefore the same envelope bytes, is reused on every retry under the request's single idempotency key. |
| B5 | Any exception during seal or wrap: `503 ENCRYPTION_UNAVAILABLE` (generic detail), and nothing is sent |
| B6 | Plaintext lifetime: DTO → `N` → `U`/`P` buffers → `AesGcm.Encrypt`. `U`, `P` and the DEK are zeroed in `finally`. Managed strings cannot be zeroed, as with the field service. |
| B7 | Never persisted or logged: no BusinessDb column, no audit metadata, no problem-details echo, no request-body logging |
| B8 | **Decided (A-Q3):** business audit metadata stays `{reasonCode}` only, with no note-presence flag |

### 1.7 Key versions, availability and recovery

- **Key version:**
  * `key_reference` records the KEK version that wrapped the DEK.
  * It is immutable in the journal and archive.
  * A future authorized opening needs that exact KEK version.
- **Recovery inputs:**
  * the archived authorization-stream journal body. It contains the envelope plus every AAD input: the organization in the journal envelope, the command kind, and `authority_user_id` and `role_name` in the normalized payload. A test asserts this (A-T14).
  * the business KEK version.
- **Inherited gaps,** with no new claim:
  * no production key provider;
  * no rotation (`LocalDevelopmentKeyProvider` unwraps only its single reference);
  * no per-organization KEK.

  Before any production use, retired key references must stay able to unwrap for as long as notes, archives and backups are retained (G14 item 7).
- **Erasure:** the KEK is shared with field values, so notes cannot be erased separately until a dedicated key purpose exists. Purpose separation today is the AAD domain, not key separation.
- **Availability:** a missing or failing provider refuses only requests that carry a note. Auditor commands are refused in staging and production in any case (PB-08).
- **Not production-ready:** until production key providers and rotation are implemented and reviewed (D8-76 item 8).

### 1.8 Known-answer vector and security tests (Design A)

**KAT-M1-01** is normative for A-T01.
- It was computed with .NET 10 `AesGcm` and independently reproduced byte for byte with Python `cryptography` 44.0.0.
- Evidence: `docs/evidence/phase-c8/m4-1-protocol-verification/m1kat-*`.

| Input | Value |
|---|---|
| KEK | bytes `00 01 … 1f` |
| DEK | bytes `20 21 … 3f` |
| DEK-wrap nonce | bytes `40 … 4b` |
| content nonce | bytes `50 … 5b` |
| key reference | `local-dev-kek-v1` |
| organization / target / note id | `019a5b2c-1111-7000-8000-000000000001` / `019a5b2c-2222-7000-8000-000000000002` / `019a5b2c-3333-7000-8000-000000000003` |
| role / direction | `security_auditor` / `appoint` |
| note | `"Covers the Q4 audit cycle.\nContact: Léa"`, which NFC turns into `…Léa` (U+00E9). UTF-8 is 40 bytes. |

| Output | Value |
|---|---|
| AAD (116 bytes) | `44796e616d6963466f726d732f41756469746f72526561736f6e4e6f74652f763100000001019a5b2c111170008000000000000001019a5b2c222270008000000000000002019a5b2c3333700080000000000000030000001073656375726974795f61756469746f72000000076170706f696e74` |
| SHA-256(P) | `53e210f6271cb59265dc59875cd3b07859def1770cb3b0e0d26cdf7cc80c38a6` |
| SHA-256(C) | `d3df0ab93fbf1a159fb9df7debf956863bbdf9c7181390379989552871841d11` |
| tag | `c88d22db2a5e2bb5434a1c52d30de413` |
| wrapped DEK | `404142434445464748494a4bc2988c000219a124e5ed3d1db7493d7449f7606f830039144f7e74deab2ed3e405e2a68bd43d7c6116749b14f108ed35` |

**Tests:**

| ID | Test | Level |
|---|---|---|
| A-T01 | KAT-M1-01 reproduced exactly, through an `internal` seal overload taking the DEK and nonce (`InternalsVisibleTo` tests only) | Unit |
| A-T02 | Round trip through the test-only opener, including every §1.3 decoding rule (a non-zero pad byte, `L` out of range, invalid UTF-8 and non-NFC each rejected) | Unit |
| A-T03 | Opening with a changed organization, target, role, direction or note id, each separately, fails | Unit |
| A-T04 | Tampered ciphertext, tag, nonce or wrapped DEK, and truncated ciphertext, each fails | Unit |
| A-T05 | Cross-domain: field-value and note ciphertexts cannot be opened as one another (same KEK, identifiers mapped across) | Unit |
| A-T06 | Each B2 rule. 500 units of 3-byte characters (1,500 bytes) are accepted; 501 units are refused; a lone surrogate is refused | Unit |
| A-T07 | Provider unavailable: a note gives 503 and **no Authority command row**; without a note the command succeeds | Stage B |
| A-T08 | Each rule V1–V11 refused, directly and through the API | Authority unit and Stage B |
| A-T09 | Plaintext `reason_note` still refused (D8-72 regression) | Authority unit and Stage B |
| A-T10 | Plaintext sentinel: absent from all of AuthorityDb and BusinessDb (`pg_dump` grep), the archive blobs and the api, authority and worker logs; the envelope present in `security_commands` and the journal body | Stage B |
| A-T11 | An idempotent replay reuses the envelope (same digest) | Stage B |
| A-T12 | Architecture: no note opener in product assemblies; `IAuditorNoteSealer` has exactly one method; the Authority has no reference to business encryption | Architecture |
| A-T13 | A rejected command's row and audit entry contain no envelope bytes | Authority unit and Stage B |
| A-T14 | The archived journal body of an accepted command contains every AAD input and the full envelope | Stage B |

### 1.9 Conformance to the C-116 conditions on A-Q2

| # | Condition | Where it is met |
|---|---|---|
| 1 | Reuse AES-256-GCM and `IEncryptionKeyProvider` | §1.1; KAT-M1-01 uses exactly that primitive and the provider's wrap format |
| 2 | A distinct authenticated context | The AAD domain `DynamicForms/AuditorReasonNote/v1` (§1.2); A-T05 |
| 3 | Bind organization, target, role, direction and note id | The §1.2 AAD; A-T03 |
| 4 | Note-id uniqueness | V10, under the organization lock; A-T08 |
| 5 | Authenticated, deterministic, unambiguous padding | §1.3 (inside the GCM plaintext, length-prefixed, zero-padded, decoding rules); A-T02 |
| 6 | Validate structure and size before persistence | §1.5: V1–V11 run in planning, before commit, and rejected rows never keep the envelope; A-T08, A-T13 |
| 7 | No application decryption endpoint | §1.1; A-T12 |
| 8 | D8-72 containment until implementation and tests pass | The Authority's envelope refusal is lifted only in the same commit that adds V1–V11 with passing A-T01…A-T14 and the shards. Plaintext refusal (V2) stays permanently. |
| 9 | No production-readiness claim without production keys and rotation | §1.7 |

### 1.10 Compatibility and rollout

- **Deploy order:** Authority first (accepts envelopes, still refuses plaintext), then business. A business that sends an envelope to a containment-era Authority gets `REASON_NOTE_NOT_ACCEPTED`, so this fails safe.
- **Historic data:** no persisted notes exist, so no data migration is needed.
  * For **new** commands only, the normalized payloads of noteless commands change from `"reason_note": null` to `"reason_note_envelope": null`.
  * Historic payloads are immutable and never re-serialized; implementation confirms this.
- **API contracts:** business OpenAPI gets `reasonNote` back (TypeScript client regenerated); Authority internal OpenAPI defines `reason_note_envelope`.
- **UI:** the note text area comes back with its 500-unit limit and a statement that the note is encrypted and cannot be read back in the app.

---

## 2. Design B: S1, Authority-to-SpiceDB projection integrity at decision time

### 2.0 Changes from revision 1

1. **History-bound marker (B-Q5, §2.3).** The marker names the revision **and** the Authority's hash-chained journal digest for that revision. A datastore from another history can no longer match (owner question 4).
2. **Quarantine on every mismatch observed while the Authority state is unchanged (B-Q6, §2.5).** Revision 1 treated a mismatch that a later read did not reproduce as transient. That could hide a flapping or split datastore. With the state unchanged there is no legitimate cause of a mismatch (§4, Q5).
3. **Permanent quarantine is enforced in AuthorityDb (B-Q7, §2.5):** a trigger forbids clearing it or changing its reason. Before, permanence relied on the absence of code.
4. **A single-use snapshot type (§2.4):** no SpiceDB permission operation is possible outside one pin, decide, verify cycle (owner question 2).
5. **Datastore and schema failure classes from E4 and E5 (§2.6),** a stated deployment invariant (B-Q8), and **measured latency** (§2.10).

### 2.1 Defect recap (T16S-1)

- **What happens:** after SpiceDB's datastore is restored from an older dump, a revoked auditor (`view_audit`) and a revoked AC-7 designer (`create_form`) are allowed. AuthorityDb still holds both revocations, with `authorization_revision = projection_applied_revision = 4` and the gate `Ready`.
- **Reproduced again in C-116 with today's check:** 44,024 stale allows without a SpiceDB restart (E1) and 62,034 with a restart (E3).

### 2.2 SpiceDB behaviour this protocol relies on (verified)

The documentation sources are authzed.com (consistency, datastores, commands) and authzed/api `permission_service.proto`, `core.proto` and `error_reason.proto`. They are current and are not pinned to v1.56. The probes are in the evidence folder.

| # | Behaviour | Source | Used for |
|---|---|---|---|
| F1 | WriteRelationships applies all updates and preconditions in one transaction | proto (documented) | The marker and the revision's deltas are visible together |
| F2 | `at_exact_snapshot` uses exactly the given snapshot, and errors if it is unavailable | proto (documented) | Pins a decision |
| F3 | `fully_consistent` uses the most recent snapshot and "explicitly bypasses caching" | proto and docs (documented) | The decisive post-decision marker read |
| F4 | `at_least_as_fresh` may use newer data | proto (documented) | Pin read only |
| P1 | A marker read `at_least_as_fresh` returns one message with a quantized `read_at` | probe | The pin `S` |
| P2 | A zero-match read streams no message and so no `read_at` | probe and proto | A missing marker is explicit and fails closed |
| P3 | Check and CheckBulk at `at_exact_snapshot(S)` return `checked_at` **byte-identical** to `S` | probe | The decision proves it ran at `S` |
| P4 | `at_least_as_fresh` with a **foreign-datastore** token is silently ignored and serves current data | probe | **The ZedToken is not integrity evidence** |
| P5 | `at_least_as_fresh` with a **future** token is accepted and echoed | probe | The same |
| P6 | `at_exact_snapshot` errors: different datastore gives `Unknown`; future and expired give `OutOfRange`; a malformed token gives `InvalidArgument` (all modes) | probe | §2.6 |
| P7 | GC window 24 h; quantization 5 s (defaults) | docs | `S` is used within milliseconds |
| P8 | Read replicas fall back to the primary when a revision is missing | docs | Excluded by B-Q4 |
| P9 | A dump restored into another cluster can make data invisible (future xids) | docs | Appears as no marker, so it fails closed |
| **E2/E3** | **A check at a snapshot pinned *before* a restore, evaluated *after* it, sees the restored data**: the revoked user is `ALLOW`, with `checked_at = S`. The marker read at `S` also shows the restored marker. | experiment | **A pin alone is unsafe.** The decisive read must come after the decision. |
| **E4** | After the datastore is replaced by an empty one, reads fail `FailedPrecondition` "object definition … not found" until the schema is rewritten | experiment | `SCHEMA_MISSING` class (§2.6) |
| **E5** | After an old dump is restored under a running SpiceDB, every pre-existing exact-snapshot token fails with "different datastore instance", while `at_least_as_fresh` serves the restored data | experiment | §2.6; the marker reads detect it |
| U1 | Whether ReadRelationships or checks are served from a cache keyed by revision | **undocumented** | Not relied on: M2 is `fully_consistent` (F3) |

**Consequence:** integrity never rests on a ZedToken (P4, P5). It rests on four things:
- the history-bound **marker** (§2.3);
- its atomic write with each revision (F1);
- the **Authority's committed state**, which is outside SpiceDB;
- the invariants in §2.9.

### 2.3 Checkpoint: the history-bound marker (B-Q5)

- **Marker v2:** for each organization, exactly one relationship

  ```
  organization:{org:D}#projection_cursor@projection_step:step-{org:D}-{n}-{h}
  ```

  * `n` is the authorization revision (decimal, no sign, no leading zeros except `0`, fits Int64).
  * `h` is the 64 lowercase hex characters of the authorization-stream journal entry digest for revision `n` (`SecurityJournalEntry.JournalEventDigest`, SHA-256 of the canonical envelope that chains to the previous entry). For `n = 0`, `h` is 64 zeros.
  * Object ids up to 1,024 characters are allowed; this one is about 128 characters, from `[a-z0-9-]`.
- **Expected value at decision time:** the gate requires `A = P`, so `h = hex(state.LatestSecurityJournalDigest)`, which is already on the organization row. That costs no extra AuthorityDb read.
  * This assumes revision `n` has exactly one authorization-stream entry with stream revision `n`. `SecurityCommandService` is the only place that sets `AuthorizationRevision`, and it appends that entry in the same transaction. A test asserts this (U-09).
- **Projector:** applying revision `n` uses:
  * `MUST_MATCH step-{org}-{n−1}-{hex(journal.JournalPreviousDigest or zeros)}`;
  * `MUST_NOT_MATCH` any marker for `n`;
  * the deltas;
  * delete the old marker, touch `step-{org}-{n}-{hex(journal.JournalEventDigest)}`.

  The projector already reads the journal entry. A projector writing into a datastore from another history therefore fails its precondition (existing `PROJECTION_DIVERGENCE` quarantine).
- **What a matching marker means:** SpiceDB's state at that snapshot is the projection of *this* Authority history up to `n`. Every legitimate write moves the marker in the same transaction (F1). A restored dump carries the marker of the dump's own revision and history. A replaced or other-cluster datastore carries none (P9, E4). A datastore from a cloned or unrelated environment carries a different `h`.
- **Transition, one time at Authority startup, before readiness:** for each organization, under `LockOrganizationAsync`, read the marker `fully_consistent`.
  * Exactly one v1 marker `step-{org}-{P}` with `P = projection_applied_revision`: one `WriteRelationships` (`MUST_MATCH` v1, delete v1, touch v2 with `h` from the journal entry for `P`).
  * A v2 marker already as expected: nothing to do.
  * Anything else: quarantine with the matching reason (§2.5).

  Decisions and the projector stay disabled until the transition completes. That costs one write per organization, once. It applies to the development stack too.

### 2.4 Decision protocol ("pin, decide, verify") and the single-use snapshot

This applies to every SpiceDB permission operation in the Authority. There are six decision methods in three services, and they hold every `Check*` and `LookupResources` call (verified by grep at `e117399`; `SecurityCommandService` is likewise the only writer of `AuthorizationRevision`):
- `PermissionCheckService.CheckBatchAsync` and `DiscoverAsync`;
- `ContentService.GetHeadAsync`, `GetHistoryAsync` and `VerifyRecordCoreAsync` (with `CheckFieldLeavesAsync`);
- `SearchCatalogueService.IssueUserCatalogueAsync`.

```
R1  state₁ ← AuthorityDb organization state (existing gate): not quarantined, archive current, A₁ = P₁, Z₁ present.
    expected marker E = step-{org}-{P₁}-{hex(LatestSecurityJournalDigest₁)}.

M1  PIN      ReadRelationships(marker filter, at_least_as_fresh(Z₁)) → markers, S = read_at.
             Require exactly one marker = E. Zero messages (no S) or anything else → mismatch.

D   DECIDE   every Check / CheckBulk / LookupResources (every page) at at_exact_snapshot(S).
             Require checked_at = S (byte-equal); for LookupResources, looked_up_at = S for every result.

M2  VERIFY   ReadRelationships(marker filter, fully_consistent) → markers. Require exactly one marker = E.  [decisive]

R2  state₂ ← AuthorityDb organization state. Require A₂ = A₁, P₂ = P₁, digest unchanged, not quarantined.
             Otherwise retry from R1 (at most 2, as today), then 503 AUTHORITY_NOT_READY.
             M2 and R2 may run concurrently (option O-B2, §2.10); both must succeed.

Release the decision, bound to (A₁, P₁) as today.
```

- **On any mismatch at M1 or M2:** never allow, and run lock-confirm (§2.5).
- **Contract (implementation-ready).** All of it lives in `AuthorizationAuthority.Application/Common`.

```csharp
public enum ProjectionSnapshotState { Open, Completed, Failed }

/// Single-use, single-request. Constructed only by ProjectionIntegrityVerifier (internal constructor).
public sealed class VerifiedProjectionSnapshot
{
    public Guid OrganizationId { get; }
    public long ExpectedRevision { get; }          // P₁
    internal string ExpectedMarker { get; }        // E
    internal string Pin { get; }                   // S (never leaves the Authority)
    public ProjectionSnapshotState State { get; }
}

public sealed class ProjectionIntegrityVerifier(ISpiceDbProjection spiceDb, IAuthorityStore store, ISecurityAlertSink alerts, ProjectionIntegrityMetrics metrics, TimeProvider clock)
{
    // M1. Precondition: the caller evaluated the gate (R1) on `state`. Throws Quarantined/NotReady on failure.
    public Task<VerifiedProjectionSnapshot> PinAsync(OrganizationAuthorizationState state, Guid correlationId, CancellationToken ct);
    // M2 + R2. Moves Open→Completed, or →Failed and throws. Required before any decision is released.
    public Task CompleteAsync(VerifiedProjectionSnapshot snapshot, IAuthorityScope scope, Guid correlationId, CancellationToken ct);
}

// ISpiceDbProjection: decision reads take the snapshot, never a raw token.
Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkAsync(IReadOnlyList<SpiceDbCheck> checks, VerifiedProjectionSnapshot snapshot, CancellationToken ct);
Task<SpiceDbCheckOutcome> CheckAsync(SpiceDbCheck check, VerifiedProjectionSnapshot snapshot, CancellationToken ct);
Task<IReadOnlyList<string>> LookupResourcesAsync(string resourceType, string permission, string subjectType, string subjectId, VerifiedProjectionSnapshot snapshot, int maximumResults, CancellationToken ct);
// Marker reads used by the verifier, the S2 check and the transition:
Task<MarkerObservation> ReadMarkerAsync(Guid organizationId, MarkerReadConsistency consistency, string? atLeastAsFreshToken, CancellationToken ct);
public sealed record MarkerObservation(IReadOnlyList<string> MarkerSubjectIds, string? ReadAt);
```

- **The operations enforce the state machine:**
  * every decision read requires `State = Open` and throws otherwise;
  * `CompleteAsync` is the only transition to `Completed`;
  * after `Completed` or `Failed`, no SpiceDB operation accepts the snapshot.

  A decision path therefore cannot check, complete, check again and return; and it cannot return without completing.
- **Architecture tests:**
  * **ARCH-01:** no `Check*` or `LookupResources` call outside a method that also calls `PinAsync` and `CompleteAsync`;
  * **ARCH-02:** `OrganizationAuthorizationState.ProjectionZedToken` is read only by the verifier, the projector and the transition;
  * **ARCH-03:** nothing outside a future, approved G14 recovery service assigns `IntegrityQuarantineEnabled = false`.

### 2.5 Lock-confirm and permanent quarantine

Triggered by any mismatch at M1 or M2, by S2 (§2.8), by the transition (§2.3), or by a `different datastore instance` error on `at_exact_snapshot(S)` (§2.6).

```
1. New scope; LockOrganizationAsync(org): the same row lock the projector holds through its whole SpiceDB write,
   so no projection write can run concurrently.
2. Re-read state. Already quarantined → done (423). If (A, P, LatestSecurityJournalDigest) ≠ (A₁, P₁, digest₁) or A ≠ P:
   the state moved → no quarantine; the caller retries or returns 503.
3. The state is unchanged: the mismatch is unexplained by any legitimate event (§4, Q5). Read the marker fully_consistent
   under the lock and record it as evidence (it does NOT veto the quarantine; revision 2 change 2).
4. In the same transaction:
     IntegrityQuarantineEnabled = true
     IntegrityQuarantineReasonCode = PROJECTION_MARKER_BEHIND | PROJECTION_MARKER_AHEAD | PROJECTION_MARKER_MISSING
                                    | PROJECTION_MARKER_INVALID | PROJECTION_FOREIGN_HISTORY | PROJECTION_SCHEMA_MISSING
     AuditJournal.Append(audit stream, "ProjectionIntegrityQuarantined", { organization_id, reason_code,
        expected_projection_revision, expected_marker_digest, observed_markers (≤ 4 subject ids, at detection and
        under the lock), detection_phase = pin | verify | periodic | transition | snapshot_error, correlation_id })
   Commit, then ISecurityAlertSink.Raise(Critical, "PROJECTION_INTEGRITY_QUARANTINE", fixed summary), then the metric.
```

**Reason codes:**
- `BEHIND`: the observed step < `P`.
- `AHEAD`: step > `P` with `A = P` (AuthorityDb older than SpiceDB).
- `FOREIGN_HISTORY`: step = `P` with a different digest.
- `MISSING`: no marker.
- `INVALID`: duplicated or malformed markers.
- `SCHEMA_MISSING`: the E4 error on a marker read.

**Persistence and permanence (B-Q7):**
- The existing columns `organization_authorization_states.integrity_quarantine_enabled` and `integrity_quarantine_reason_code`.
- An AuthorityDb migration adds the trigger `organization_quarantine_is_permanent` (`BEFORE UPDATE`). It raises when `OLD.integrity_quarantine_enabled` is true and `NEW.integrity_quarantine_enabled` is false or `NEW.integrity_quarantine_reason_code IS DISTINCT FROM OLD…`.
- The quarantine survives restarts and is shared by every Authority instance.
- A later G14 recovery needs its own reviewed mechanism (a separate identity) to lift it.
- No code path clears any quarantine today (verified: only `= true` assignments exist).

**Enforcement points:**
- `OrganizationGate`: all six decision methods, and catalogue snapshot validation;
- `SecurityCommandService` (commands refused);
- `ProjectionProcessor` (new: `Idle` without a SpiceDB write when quarantined);
- the existing checks in onboarding, owner recovery, membership provisioning and Security Administrator provisioning.

### 2.6 Failure classification, responses and telemetry

| Condition | Decision | Response to the business | Quarantine | Alert |
|---|---|---|---|---|
| Gate closed (existing reasons) | deny | 503 `AUTHORITY_NOT_READY` / 423 `INTEGRITY_QUARANTINE` (unchanged) | existing | existing |
| Marker mismatch, state unchanged (§2.5) | deny | 423 `INTEGRITY_QUARANTINE` | **yes** | Critical `PROJECTION_INTEGRITY_QUARANTINE` |
| Marker mismatch, state moved | deny | retry, then 503 | no | none; counted |
| Marker read `FailedPrecondition` "object definition … not found" (E4) | deny | 423 | **yes** (`SCHEMA_MISSING`), only through lock-confirm | Critical |
| SpiceDB unavailable, timeout, transport error, or `Internal` datastore error (seen mid-restore in E1) | deny | 503 | no | existing readiness |
| `at_exact_snapshot(S)`: `OutOfRange` (expired or future) or `InvalidArgument` | deny | 503 | no (the next request decides) | Warning `PROJECTION_SNAPSHOT_UNAVAILABLE` |
| `at_exact_snapshot(S)`: "different datastore instance" (E5) | deny | 423 if confirmed | **lock-confirm immediately** | Critical if confirmed |
| `checked_at` / `looked_up_at` ≠ `S` | deny | 503 | no | Critical `PROJECTION_SNAPSHOT_MISMATCH` |

- **Non-disclosure:**
  * business-facing errors carry no marker, step, digest or token values;
  * the business maps `INTEGRITY_QUARANTINE` to its existing `SecurityIncident` (High). Confirm at implementation that an organization-level quarantine raises one.
  * The journal event and alert carry organization, reason, revision and marker subject ids only. They carry no user, subject, resource or permission identifiers.
- **Metrics (B-Q3 approved):** meter `AuthorizationAuthority.ProjectionIntegrity`:
  * counter `projection_integrity_verifications{outcome}`, where outcome is `ok`, `moved`, `mismatch_quarantined`, `snapshot_error`, `token_mismatch` or `unavailable`;
  * histograms `projection_integrity_marker_read_ms{phase=pin|verify|periodic}` and `projection_integrity_decision_ms{path}`.

### 2.7 Other SpiceDB readers and writers

- **`ProjectionProcessor`:**
  * the marker CAS becomes v2 (§2.3);
  * `Idle` without writing when quarantined;
  * its reconciliation reads (`ReadCursorStepAsync` and `TupleExistsAsync`, both `fully_consistent`) are not decisions.
- **Onboarding and bootstrap** (`InitializeCursorAsync`): write the v2 marker for `n = 0`.
- **Readiness `ProbeAsync`:** unchanged (schema presence). S2 adds the marker check.

### 2.8 Periodic integrity check (S2, a complement and never a replacement)

- **What it does:** a background processor every 60 s (configurable, 10–600 s), and once whenever SpiceDB readiness turns from not-ready to ready. For each organization with `A = P` that is not quarantined:
  * a `fully_consistent` marker read against `E`;
  * lock-confirm on mismatch.
- **Purpose:** it detects a rollback in an idle organization, and any intermittent divergence that no decision happened to observe.

### 2.9 Guarantee, invariants and residual exposure (owner: "do not claim absolute rollback prevention")

**Guarantee G-S1 (claimed, under I1–I6).** A decision is released only if all three hold:
- it was evaluated by SpiceDB at a snapshot `S` (`checked_at = S`);
- **after** that evaluation finished, SpiceDB's head, read without caches, carried the marker `E` that names the Authority's committed revision and its hash-chained history digest (M2);
- the Authority's committed `(A, P, digest)` equalled `(P₁, P₁, digest₁)` and was not quarantined, both before the pin (R1) and after the verify (M2, then R2).

Every legitimate SpiceDB change moves the marker (I2), and a restored, replaced or foreign datastore cannot carry `E` unless its state *is* this history's revision `P₁` (I6). So a stale authorization from a SpiceDB rollback that is **in place when M2 runs, or arrives before D completes** is never released.

**Invariants:**

| ID | Invariant | How it is held |
|---|---|---|
| I1 | Only the Authority projector writes SpiceDB | Preshared key and Authority-only network (existing deployment boundary) |
| I2 | Every relationship write moves the marker in the same `WriteRelationships` | Projector code (§2.3) and F1 |
| I3 | SpiceDB serves every call from **one** datastore at a time: one connection URI to one primary, no read replicas (B-Q4), failover only by synchronous same-endpoint HA (no data loss), and any other datastore change only through the recovery procedure that stops SpiceDB first | Deployment configuration and runbook (B-Q8). Not enforceable in code. |
| I4 | `fully_consistent` ReadRelationships reads the head without caches | Documented (F3); consistent with E1–E5 |
| I5 | AuthorityDb is not rolled back and is inside the trust boundary | Out of S1's scope: T16-A / G14 |
| I6 | A datastore state is either this history's projection at some revision, or carries a different or absent marker | Marker v2 (§2.3), F1, P9 |

**Residual exposure (precise):**

| ID | Exposure | Bound and mitigation |
|---|---|---|
| RE-1 | **A datastore that reverts to an older state and back to the current state entirely within one request's D→M2 interval** (D plus M2, about 8–10 ms at p95). Or, if I3 is violated, two live backends behind one SpiceDB at once (D on the stale one, M2 on the current one). | It cannot be eliminated by any Authority-side protocol, because SpiceDB offers no way to evaluate a check and read the marker in one datastore transaction. **Smallest defensible mitigation:** I3 as a binding deployment constraint (B-Q8), plus quarantine on *any* mismatch observed by *any* read (change 2). Under a split or flapping datastore, the first marker read that lands on the stale state (M1 or M2 of any request, or S2) quarantines the organization. Exposure is limited to decisions that complete before that first observation. With half the reads stale, a request escapes detection with probability about 1/8 (M1 current, D stale, M2 current), and the first stale observation is expected within the first one or two requests. |
| RE-2 | A writer **inside** the Authority domain copies the current marker onto stale tuples | Outside the Level 2 trust assumption (§4M). Full desired-relationship reconciliation is G14/S3. |
| RE-3 | AuthorityDb and SpiceDB restored together to the same older revision | **T16-A / G14.** Both sides agree, so S1 cannot detect it. |
| RE-4 | A revoke that commits after a decision was released | Pre-existing post-decision window, unchanged and bounded by existing rechecks (upload chunks, snapshot validation, revision-bound cursors). It is not a rollback. |
| RE-5 | A false quarantine, for example from SpiceDB misbehaving | Fail-closed by design; it stays until G14 (B-Q1). The availability cost is accepted by that decision. |
| RE-6 | Undocumented SpiceDB behaviours: the `checked_at` echo (P3), the error classes (P6, E4, E5) | Defence in depth only; the guarantee rests on F1–F3. Pinned by CT tests so an upgrade that changes them fails CI. |

### 2.10 Performance and availability (measured, C-116)

The probe ran pin, decide and verify against the security-test SpiceDB from a container on the Authority network, open-loop, with every request issuing today's check and the protocol. Evidence: `bench-results.txt`.

| Load | Today's check p50 / p95 / p99 | S1 protocol p50 / p95 / p99 | Added at p95 / p99 |
|---|---|---|---|
| 50 requests/s, 60 s, 1 check | 1.51 / 2.20 / 3.08 ms | 7.08 / 10.96 / 14.19 ms | **8.8 / 11.1 ms** |
| 50 requests/s, 30 s, 1 check | 1.55 / 2.56 / 3.54 | 7.15 / 12.36 / 15.84 | 9.8 / 12.3 |
| 150 requests/s, 60 s, 1 check | 1.47 / 2.35 / 4.29 | 7.36 / 11.92 / 18.70 | **9.6 / 14.4** |
| 50 requests/s, 60 s, 25-item batch | 2.43 / 3.62 / 5.36 | 7.91 / 12.95 / 17.49 | 9.3 / 12.1 |

- **Breakdown at p95:**
  * M1 pin: 3.3–3.7 ms;
  * M2 `fully_consistent`: 4.3–5.0 ms;
  * D at the exact snapshot: 0.3–1.3 ms slower than today's `at_least_as_fresh` check.
  * No errors in any run.
- **Verdict on the 15 ms target:** the p95 target is **met locally with about 5 ms margin**. At p99 under 150 requests/s the margin is 0.6 ms.
- **Limits of this evidence:**
  * small dataset;
  * single-node Docker;
  * no TLS;
  * no zone hops;
  * the Authority's own AuthorityDb reads are not included (unchanged by S1).

  **It is not PB-09 evidence.** PERF-01 on staging stays mandatory.
- **If staging exceeds the target:**
  * **O-B2:** run M2 and R2 concurrently. This is safe per §2.4 and saves one AuthorityDb read.
  * **O-B1:** drop M1 on check-only paths and pin `S` from `checked_at` of an `at_least_as_fresh` check. That saves about 3.5 ms. M2 stays. Lookups keep M1, because zero results carry no token.
  * Both need approval.
  * Never cache verified snapshots across requests.
- **Availability:**
  * no new dependency, since SpiceDB is already required for every decision;
  * a SpiceDB outage fails closed exactly as today;
  * lock-confirm runs only on a mismatch.

### 2.11 Regression and verification matrix (Design B)

**Behavioural contract tests.** The six from revision 1 are retained, plus two that pin the C-116 experiments. All are pinned to `authzed/spicedb:v1.56.2`.

| ID | Asserts |
|---|---|
| CT-1 | Marker read `at_least_as_fresh` returns one message with `read_at` (P1) |
| CT-2 | A zero-match ReadRelationships streams no message (P2) |
| CT-3 | `checked_at` = `S` byte for byte for Check and CheckBulk at `at_exact_snapshot`; `looked_up_at` = `S` for every LookupResources result (P3) |
| CT-4 | `at_least_as_fresh` silently accepts foreign and future tokens (P4, P5), which is why the token is never relied on |
| CT-5 | The `at_exact_snapshot` error classes (P6) |
| CT-6 | A marker write together with deltas is atomic under concurrent reads (F1) |
| CT-7 | After an older dump is restored under a running SpiceDB, pre-existing exact-snapshot tokens fail "different datastore instance" (E5) |
| CT-8 | A snapshot pinned before a restore evaluates restored data after it (E2), which is why M2 must follow D |

**Rollback regression.** Stage B, shard `authorization-security-onboarding`. The expectation is: deny, then 423, persistent quarantine, an audit-stream event, a Critical alert and the metric, unless stated otherwise.

| ID | Area | Scenario |
|---|---|---|
| RB-01 (= T16-S) | Organization roles (AC-8.1, AC-7) | Revoked auditor `view_audit` and revoked designer `create_form` |
| RB-02 | Record access (AC-5) | A record read share revoked. Bob is denied discover, read, `GetHead`, `GetHistory` and `VerifyProjection`. |
| RB-03 | Field-level sharing (AC-5) | A field-item read share revoked; `VerifyProjection` (partial) omits the field |
| RB-04 | Team permissions (AC-9 area) | Bob removed from a team holding a record share |
| RB-05 | Discovery and search (AC-6) | Discover and `IssueUserCatalogue` do not list the record; an issued catalogue snapshot fails validation after the quarantine |
| RB-06 | Form permissions (AC-6, AC-7) | `design`, `create_record` and `query_fields` revoked |
| RB-07 | Cross-organization isolation | Org X changed after the dump and is quarantined. Org Y, unchanged since the dump, keeps a matching v2 marker, stays Ready and decides correctly. No subject gains anything across organizations. |
| RB-08 | Missing marker | Dump taken before the organization's bootstrap: `PROJECTION_MARKER_MISSING` |
| RB-09 | Marker ahead | AuthorityDb organization state set back (restore simulation): `PROJECTION_MARKER_AHEAD` |
| RB-10 | Persistence | After a restart, still quarantined; commands refused; projector writes nothing (SpiceDB marker unchanged); **the trigger refuses** `UPDATE … SET integrity_quarantine_enabled = false` and a reason change by the Authority runtime role |
| RB-11 | No false quarantine | 200 concurrent checks while 20 grant and revoke commands commit and project: zero quarantines, and every decision consistent with its reported revision |
| RB-12 | Restore without restart | Live `pg_restore --clean` under a running SpiceDB; the first decision afterwards is denied |
| RB-13 | Other datastore | Datastore dropped and migrated empty: `SCHEMA_MISSING`, or `MISSING` after the schema is rewritten. An old dump restored into the replacement: detected (E5). |
| RB-14 | Periodic check | An idle organization is quarantined by S2 within one interval |
| RB-15 | Outage | SpiceDB stopped: 503 and **no** quarantine; Ready again after restart |
| **RB-16** | **Rollback during active permission checks** | Concurrent decision load (8+ workers, all six decision paths) while (a) a live restore and (b) a stop-restore-start run. **Zero allows for revoked subjects**, quarantine committed, every Authority instance denies afterwards. Today's code is the control (C-116: 44,024 and 62,034 stale allows). |
| **RB-17** | Foreign history | A marker with the correct step and a different digest planted through the test-only SpiceDB path: `PROJECTION_FOREIGN_HISTORY` |
| **RB-18** | Transition | v1 markers upgraded to v2 at startup; a v1 marker ≠ `P` quarantines; no decision before the transition completes |

**Unit and architecture tests:**

| ID | Asserts |
|---|---|
| U-01…U-10 | The verifier state machine with a fake projection: each M1 and M2 outcome, a token mismatch, each §2.6 class, lock-confirm (moved versus unchanged), a snapshot used after `Completed`/`Failed` throws, and U-09 (one authorization-stream entry per revision) |
| ARCH-01…ARCH-03 | §2.4 |

**Performance:**
- **PERF-01:** the §2.10 measurement on every decision path, reporting p50, p95 and p99 and the M1/D/M2 breakdown. It runs on the security-test stack at implementation, and on staging before production.

**Release rule:**
- T16-S (RB-01) stays mandatory and is never skipped or marked as an expected failure (D8-78).
- **Any stale allow in RB-01…RB-18 blocks release.**

### 2.12 Rejected alternatives

| Alternative | Why not |
|---|---|
| Trust the ZedToken (status quo) | P4, P5 |
| A pin without M2 | E2/E3: the pinned snapshot evaluates restored data |
| Revision-only marker (revision 1) | It collides with a datastore from another history at the same revision (owner question 4) |
| Treat a non-reproduced mismatch as transient (revision 1) | It hides split or flapping datastores (RE-1) |
| Embed the marker in every permission (caveat or intersection) | A broad schema rewrite. SpiceDB still evaluates over several datastore queries, so it would not close RE-1 either. |
| Cache verified snapshots across requests | It reopens a rollback window |
| S2 only | D8-77 forbids it as a replacement |

---

## 3. Decisions

**Approved (C-116):** A-Q1 (padding), A-Q3 (no presence flag), B-Q1 (quarantine until G14), B-Q3 (metrics), B-Q4 (no read replicas).

**Requested for final approval:**

| ID | Decision | Recommendation |
|---|---|---|
| **A-Q2** | Approve the M1 contract, revision 2: AAD §1.2, padding §1.3, envelope §1.4, V1–V11 §1.5, B1–B8 §1.6, KAT-M1-01 and A-T01…A-T14 §1.8, conformance §1.9 | **Approve** |
| **B-Q2** | Approve the S1 protocol, revision 2: §2.3–§2.8, the guarantee and residuals in §2.9, and the matrix in §2.11 | **Approve** |
| **B-Q5** | History-bound marker v2 with the one-time startup transition (§2.3) | **Adopt** |
| **B-Q6** | Quarantine on every mismatch observed while the Authority state is unchanged, with no transient branch (§2.5). This accepts that a false quarantine lasts until G14 (RE-5). | **Adopt** |
| **B-Q7** | AuthorityDb trigger making quarantine permanent, plus ARCH-03 (§2.5) | **Adopt** |
| **B-Q8** | Deployment invariant I3 (one datastore behind SpiceDB at a time; any other datastore change only through the recovery procedure that stops SpiceDB) as a binding deployment constraint | **Adopt** |
| **B-Q9** | Performance acceptance: added p95 ≤ 15 ms (met locally with about 5 ms margin; p99 reported), with the staging PERF-01 measurement before production; O-B1 and O-B2 only by separate approval | **Adopt** |

- **After approval:**
  * implement Design B first (T16S-1 is the highest priority), then Design A;
  * each with independent shard runs, a signed local commit and evidence for the independent review.
- **Not authorized:** push, PR, merge, deploy, M4-2, RCF-1 remediation, G14.

## 4. Answers to the C-116 Design B questions

**Q1. Can the protocol prevent stale authorization when a rollback occurs after the final marker read (M2)?**
- **Yes, for decisions:** D completed before M2, so a rollback after M2 was not visible to D. D evaluated the current committed state, as E2/E3 and the race runs confirm.
  * The rollback does not make that decision stale: it re-grants revoked subjects, and this decision did not observe it.
  * The next decision for the organization runs the full protocol, and its M1 or M2 sees the rolled-back marker and quarantines.
- **Derived artefacts** issued from correct decisions never consult SpiceDB again:
  * catalogue snapshots (≤ 900 s) are revalidated against the AuthorityDb gate, quarantine included (RB-05);
  * list cursors are revision-bound;
  * upload chunks re-run the Authority check for every chunk.
- **The only stale-authorization path left is RE-1** (a revert within one request's D→M2 interval, or a violation of I3). It is mitigated, not eliminated.

**Q2. Can any SpiceDB permission operation occur without repeating the protocol?**
- **No, by construction.** Every decision read takes a `VerifiedProjectionSnapshot`. Only the verifier creates one, it is single-use and single-request, every read requires `Open`, and the decision is released only after `CompleteAsync` (§2.4). This is backed by ARCH-01 and ARCH-02 and by the grep inventory (all `Check*` and `LookupResources` calls are in the six methods).
- **Non-decision reads** (the projector's reconciliation reads, schema probe and writes) are not permission operations.
- **The business has no SpiceDB credential or network path** (I1).
- **Writes are unaffected by SpiceDB:** content commits, security commands and roster or role planning evaluate the Authority's own `DesiredStateGraph` from AuthorityDb (verified).

**Q3. How do cache bypass and exact-snapshot checks behave across rollback, failover and datastore replacement?**

| Event | Exact-snapshot check at `S` | `fully_consistent` M2 | Outcome |
|---|---|---|---|
| Live restore, SpiceDB running (E1, E2) | `S` pinned after the restore: restored data. `S` pinned before: **also restored data** (E2). | Restored marker | Deny and quarantine. Zero stale allows in E1. |
| Restore with restart (E3) | As above | Restored marker | Deny and quarantine. Zero stale allows. |
| Replaced, empty datastore (E4) | `FailedPrecondition` (no schema), then no marker after the schema is rewritten | Same | `SCHEMA_MISSING` or `MISSING` quarantine |
| Old dump restored into the replacement under a running SpiceDB (E5) | Old tokens: "different datastore instance"; new pins: restored data | Restored marker | Deny and quarantine |
| Synchronous same-endpoint HA failover | Unchanged data | Unchanged marker | No effect (no data loss) |
| Asynchronous replica promotion | Older data | Older marker | It is a rollback: deny and quarantine. Under I3 it happens only through the recovery procedure. |
| Two live backends at once | Either | Either | RE-1 |

- **Caching:** SpiceDB's cache keying is undocumented (U1).
  * M2 is `fully_consistent`, which is documented to bypass caching, so a cached pre-restore answer cannot hide a restore.
  * A cached check result computed before the restore is the *correct* pre-restore answer, so caching cannot produce a stale allow.
  * E1 and E2 ran with warm caches and without a restart, and observed no masking in either direction.

**Q4. How can a marker be trusted when SpiceDB accepts tokens from unrelated datastores?**
- **The token is never trusted** (P4, P5, CT-4).
- **The marker is trusted only as a statement written atomically with the state it describes** (I2, F1), by the only writer (I1), and only when it names both the revision and the Authority's own hash-chained journal digest (v2).
- A datastore from an unrelated or cloned environment matches only if it holds the same 256-bit chained digest for revision `P`, which means it is this history's state.
- **Revision 1's revision-only marker could collide in exactly this case.** That is why v2 is a required change (B-Q5).
- **A copy forged inside the Authority domain** remains RE-2, outside the trust model.

**Q5. Do the final Authority check and quarantine enforcement prevent returning an authorization after a detected rollback?**
- **Yes:**
  * A request that observes a mismatch never allows, whether or not the quarantine commit succeeds.
  * The quarantine is committed in AuthorityDb and is visible to every Authority instance. Every request whose R1 or R2 runs after the commit fails with 423, including in-flight requests that reach R2 later.
  * A request whose R2 completed before the commit had an M2 that matched after its D, so its decision was correct, or it is in RE-1.
  * Commands are refused, the projector stops and catalogue validation fails.
- **Why a mismatch with the state unchanged is never legitimate:**
  * the only legitimate marker change is a projector write of `P₁+1`, which requires `A ≥ P₁+1` committed beforehand (READ COMMITTED makes that visible to the re-read under the lock);
  * an older marker cannot be observed at a snapshot at least as fresh as `Z₁` in the same datastore (F4), or at the head (F3).

  This justifies B-Q6.
- **If AuthorityDb cannot be written,** requests keep detecting and denying until it can.

**Q6. How is permanent quarantine enforced and persisted?**
- **Stored:** in the existing AuthorityDb columns, set in one transaction with the audit-stream event, before the alert.
- **Permanent:**
  * the B-Q7 trigger forbids clearing it or changing its reason, even by a buggy code path;
  * ARCH-03 forbids assigning `false`;
  * no clearing code exists today.
- **Enforced at:** the gate (all decision paths and snapshot validation), commands, the projector, and the existing onboarding, recovery and provisioning services.
- **Lifted only by:** a future, separately reviewed G14 recovery mechanism.

**Q7. How are normal projection writes prevented from clearing quarantine?**
1. The projector checks the quarantine under the organization lock and does nothing.
2. The projector never writes the quarantine columns, except to set them.
3. The trigger.
4. The quarantine is independent of the marker: re-restoring the correct datastore, or a delayed pre-quarantine write landing later (fenced by the marker CAS in any case), cannot lift it.
5. S2 skips quarantined organizations.
6. Commands are refused, so no new revision exists to project.

**Q8. Is 15 ms p95 achievable under representative load?**
- **Locally, yes:** 8.8–9.8 ms added at p95 for 50–150 requests/s and batches of 1 or 25, about 5 ms below the target.
- **At p99:** 11.1–14.4 ms added, close to the limit at 150 requests/s.
- **Not yet shown under PB-09 representative conditions** (staging data volume, TLS, zones). PERF-01 on staging is required, and O-B2 and O-B1 are the approved-only fallbacks.

**Stale authorization from a SpiceDB rollback is not claimed to be absolutely impossible.** The claim is G-S1 under I1–I6, with RE-1…RE-6 as stated.
