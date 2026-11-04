# Phase C8 M4-1: Design A (M1 reason-note sealing) and Design B (S1 projection integrity)

- **Status:** **C-115 design proposal, for owner approval. Nothing here is implemented.** It was written under owner decisions D8-76 (M1 conditionally approved), D8-77 (S1 approved as a direction, subject to this protocol review) and D8-78 (T16-S stays active, mandatory and failing).
  * D8-72 containment stays active: the Authority refuses every reason note.
  * T16-S still fails, and T16S-1 is open (RR-11, High).
  * Not authorized: push, PR, merge, deploy, M4-2, RCF-1 remediation and any G14 implementation.
- **Audience:** the project owner (decisions A-Q1…A-Q3 and B-Q1…B-Q4 in §3) and the independent Authority reviewer. Claude (Opus) wrote this in the owner's session. It is not an independent review.
- **Baseline:** branch `phase-c8/m4-governance-audit` at `6663872`. Findings come from `PHASE_C8_M4_1_REVIEW_AND_REMEDIATION.md` §1.5 and §2.4.
- **New evidence in this round:**
  * read-only SpiceDB characterisation probes against the running security-test stack (`authzed/spicedb:v1.56.2`), §2.2;
  * a documentation review of the SpiceDB v1 API, §2.2.
  * No SpiceDB data was written.

---

## 1. Design A: M1, a seal-only encryption component for Security Auditor reason notes

### 1.1 What is reused and what is new

| | Reused unchanged | New |
|---|---|---|
| Primitive | AES-256-GCM (`System.Security.Cryptography.AesGcm`), 128-bit tag | — |
| Data key | A random 256-bit DEK per note (`RandomNumberGenerator`), single use, zeroed after use | — |
| Nonce | A random 96-bit nonce (unique because the DEK is single use) | — |
| Key wrap | `IEncryptionKeyProvider.WrapDataKey(dek, CurrentKeyReference)`. Today that is `LocalDevelopmentKeyProvider`: AES-GCM wrap with AAD `"DynamicForms/DekWrap/v1:" + keyReference`. Elsewhere it is `UnavailableKeyProvider`, which fails closed. | — |
| Context | — | `AuditorReasonNoteContext` and its AAD domain `"DynamicForms/AuditorReasonNote/v1"` (§1.2) |
| Component | — | `IAuditorNoteSealer` port (Application) with a single method, `Seal`. `AesGcmAuditorNoteSealer` (Infrastructure, `Features/Organizations`). |

- **Not modified:** `AesGcmFieldEncryptionService`, `AesGcmFileEncryptionService` and `IEncryptionKeyProvider`.
- **No `Open`:** the sealer has no decrypt method, and none exists anywhere in the product (D8-73).
- **Test-only opener:** decryption exists only as a test helper in the test project, to prove the confidentiality and binding properties (§1.7).

### 1.2 Authenticated context and associated data (exact construction)

**Context values and where each comes from.** The Authority never takes these from the envelope.

| Context field | Business side (sealing) | Authority side (validation, and any future authorized opening) |
|---|---|---|
| `organization_id` | the verified request context (`RequestContext.OrganizationId`) | the command's `organization_id` |
| `target_authority_user_id` | the route or body user id | the payload's `authority_user_id` |
| `role_name` | the constant `security_auditor` | the payload's `role_name` (only `security_auditor` is permitted with an envelope) |
| `direction` | `appoint` for GrantSystemRole, `remove` for RevokeSystemRole | derived from the command kind, the same mapping |
| `note_id` | a new UUID v7 per note (`Guid.CreateVersion7()`) | the envelope's `note_id` (§1.3) |

**AAD byte string.** Every integer is big-endian. A UUID is its 16-byte big-endian form (`Guid.TryWriteBytes(span, bigEndian: true, out _)`), the same convention the field service uses.

```
AAD =  ASCII("DynamicForms/AuditorReasonNote/v1")      33 bytes, no terminator
    ‖  Int32BE(envelope_version = 1)                    4
    ‖  UUID16(organization_id)                         16
    ‖  UUID16(target_authority_user_id)                16
    ‖  UUID16(note_id)                                 16
    ‖  Int32BE(len(role_name))  ‖ ASCII(role_name)      4 + 16 ("security_auditor")
    ‖  Int32BE(len(direction))  ‖ ASCII(direction)     4 + 7 ("appoint") or 4 + 6 ("remove")
```

- **Domain separation by construction.**
  * Field-value AAD starts with `DynamicForms/FieldValue/v1`.
  * Document-segment AAD starts with `Int32BE(1)` (`00 00 00 01`).
  * Note AAD starts with `DynamicForms/AuditorReasonNote/v1`.
  * The note label differs from the field label at byte 13 (`A` vs `F`), and from the document AAD at byte 0. Every variable-length part is length-prefixed, so no field-value or document AAD can equal a note AAD.
  * A known-answer test and cross-domain tests (§1.7) check this.
- **What a note cannot be moved to:** another organization, person, role, direction or note id. Opening it under any changed value fails the GCM tag check.
- **No command id in the AAD.** The Authority creates the command id after the business seals.
  * Moving an envelope to a *different* command for the same organization, person, role and direction is instead blocked by `note_id` uniqueness, which the Authority enforces at write time (§1.4, V10).
  * The note id is in the AAD, so it cannot be changed without breaking the tag.
- **Key reference not in the AAD:** like the field service, the AAD omits the key reference. The DEK-wrap AAD already binds it (`"DynamicForms/DekWrap/v1:" + keyReference`), so a wrapped DEK presented under another reference does not unwrap.

**Plaintext encoding (A-Q1, recommended):**
- the note is trimmed and normalized to Unicode NFC (validation in §1.5, B2);
- `P = UInt16BE(byte_length) ‖ UTF-8 bytes ‖ 0x00 padding to exactly 2,048 bytes`.

Every ciphertext is then 2,048 bytes, so ciphertext length no longer discloses note length to AuthorityDb administrators, backup holders or archive readers. A 500-character note is at most 2,000 UTF-8 bytes, so it always fits. **Alternative:** no padding (ciphertext length = UTF-8 length), with the length disclosure accepted and documented.

### 1.3 Envelope schema (wire and stored form)

The member sits inside the GrantSystemRole and RevokeSystemRole payload for `role_name = security_auditor`, as `reason_note_envelope`. It is part of the normalized payload, so the command request digest, idempotency, the authorization-stream journal body and its hash chain, and the archive all cover the ciphertext.

```json
"reason_note_envelope": {
  "envelope_format":       "DynamicForms/AuditorReasonNote",
  "envelope_version":      1,
  "algorithm_name":        "AES-256-GCM",
  "key_reference":         "local-dev-kek-v1",
  "note_id":               "019a5b2c-0000-7000-8000-000000000000",
  "nonce_b64":             "<12 bytes>",
  "ciphertext_b64":        "<2048 bytes (A-Q1 padding) or 1..2000 bytes>",
  "tag_b64":               "<16 bytes>",
  "wrapped_data_key_b64":  "<29..512 bytes; 60 bytes for LocalDevelopment>"
}
```

- **Excluded on purpose:**
  * plaintext length;
  * provider name: the key reference identifies the KEK version, matching `FieldEncryptedValue`;
  * any copy of the context fields: they come from the command (§1.2), so they cannot disagree with it.
- `reason_note` (plaintext) stays a **recognized and permanently refused** member.

### 1.4 Authority validation contract (structural; the Authority holds no key and cannot decrypt)

These checks run in `SecurityAuditorAsync`, under the organization lock that `SecurityCommandService.SubmitAsync` holds for the whole command (`LockOrganizationAsync`). Every check is exact; nothing is normalized, trimmed or case-folded.

| # | Rule | Refusal |
|---|---|---|
| V1 | The payload binds strictly, as today (`UnmappedMemberHandling.Disallow`). An unknown member, in the payload or the envelope, is refused. | `INVALID_COMMAND` |
| V2 | `reason_note` present with any non-null value | `REASON_NOTE_NOT_ACCEPTED` (permanent; D8-72 plaintext refusal) |
| V3 | `reason_note_envelope` is absent, or an object. An explicit `null` is refused under strict binding (a decision for the reviewer). | `INVALID_COMMAND` |
| V4 | `envelope_format`, `envelope_version` and `algorithm_name` equal exactly `DynamicForms/AuditorReasonNote`, `1` and `AES-256-GCM` | `INVALID_COMMAND` |
| V5 | `key_reference` is 1–128 characters from `[A-Za-z0-9._:/-]` | `INVALID_COMMAND` |
| V6 | `note_id` is a canonical lowercase hyphenated UUID, version 7, not all zeros | `INVALID_COMMAND` |
| V7 | Each `*_b64` is strict RFC 4648 §4 base64: standard alphabet, required padding, no whitespace, canonical re-encoding equal to the input | `INVALID_COMMAND` |
| V8 | Decoded sizes: nonce = 12; tag = 16; ciphertext = 2,048 (with A-Q1 padding; otherwise 1–2,000); wrapped DEK = 29–512 | `INVALID_COMMAND` |
| V9 | Serialized envelope ≤ 4,096 bytes | `INVALID_COMMAND` |
| V10 | `note_id` is unused in the organization: no non-rejected `security_commands` row of kind Grant or RevokeSystemRole carries it. The query runs under the organization lock, so it is race-free. An idempotent replay with the same key and digest returns before planning and never reaches V10. | `INVALID_COMMAND` |
| V11 | An envelope with `role_name` other than `security_auditor` | `INVALID_COMMAND` (Security Administrator commands keep their unchanged shape) |

- **Rejection evidence is unchanged:**
  * the rejected row keeps only `RoleCommandEvidence`, and never the envelope (it is caller-supplied and failed validation or the command did);
  * the `SecurityAuditorCommandRejected` audit entry keeps its fixed field list;
  * the rejection code and detail never echo envelope values.
- **What the Authority cannot check:**
  * whether the tag verifies;
  * whether the sealed plaintext is a real note.

  A business workload that is able to call the Authority could submit a well-formed envelope of random bytes. That is within the existing trust model: the business is already trusted to submit commands and gains nothing by it, because notes are non-authoritative and never decrypted.

### 1.5 Business-side contract (sealing)

| # | Rule |
|---|---|
| B1 | `reasonNote` (string, optional) comes back to the appoint and remove request bodies. It is refused (422) for anything but Security Auditor commands. |
| B2 | Validation: trim, then NFC. 1–500 UTF-16 characters. No C0 or C1 control characters except LF (U+000A). Otherwise refused with `INVALID_REQUEST`; the value is never echoed. |
| B3 | `keys.IsConfigured == false`: a request **with** a note is refused (`503 ENCRYPTION_UNAVAILABLE`) and nothing is sent to the Authority. A request **without** a note proceeds unchanged. |
| B4 | Seal exactly once per request, before `SubmitAndAwaitAsync`. The idempotency key is created once per request (`security-auditor-{appoint,remove}:{user}:{v7}`) and the same payload object, and therefore the same envelope bytes, is reused on every retry. Re-sealing a retry would be an `IDEMPOTENCY_CONFLICT`, and tests prove it never happens. |
| B5 | Any exception during seal or wrap: the request fails closed (`503 ENCRYPTION_UNAVAILABLE`, generic detail) and nothing is sent. |
| B6 | Plaintext lifetime: request DTO → normalization → UTF-8 buffer (`P`) → `AesGcm.Encrypt`. `P` and the DEK are zeroed (`CryptographicOperations.ZeroMemory`) in `finally`. The managed `string` cannot be zeroed, a limitation the field service shares. |
| B7 | Never persisted or logged by the business: no BusinessDb column, no audit metadata (still the code only), no problem-details echo, no request-body logging. A sentinel test (§1.7) proves this. |
| B8 | Business audit metadata stays `{reasonCode}`. It may add `reasonNoteSupplied: true\|false` only if you approve it. **Recommended: not added**, so even note presence is not disclosed to business-audit readers. |

### 1.6 Key versions, availability and recovery

- **Key version:**
  * `key_reference` records the KEK version that wrapped the DEK.
  * It is immutable in the journal and archive.
  * A future authorized opening needs that exact KEK version.
- **Recovery inputs:**
  * the archived authorization-stream journal body. It contains the envelope and the context fields (organization, kind, `authority_user_id`, `role_name`); confirm at implementation that the body includes every AAD input, and add any that is missing;
  * the business KEK version.
  * Nothing else is needed: the envelope plus that body is self-sufficient.
- **Inherited gaps:** these are the same dependencies field encryption already has, and **M1 adds no claim**:
  * no production key provider (Azure Key Vault or Managed HSM);
  * no rotation: `LocalDevelopmentKeyProvider` unwraps only its single configured reference;
  * no per-organization KEK.

  Before any production use, the provider must keep retired key references able to unwrap for as long as sealed notes, archives and backups are retained (G14 item 7).
- **Erasure:** destroying a KEK version makes every note sealed under it unreadable. The KEK is shared with field values, so notes cannot be erased separately until a dedicated key purpose or reference exists. Purpose separation today rests on the AAD domain (§1.2). That is cryptographically sound, but it is not key separation.
- **Availability:** a missing or failing key provider refuses only requests that carry a note (B3, B5). Auditor commands are refused in staging and production in any case (PB-08 step-up gate).
- **Not production-ready:** until production key providers and rotation are implemented and reviewed (D8-76 item 8).

### 1.7 Security tests (Design A)

| ID | Test | Level |
|---|---|---|
| A-T01 | Known-answer vector: a fixed KEK, DEK, nonce, context and note give a fixed AAD (hex), ciphertext and tag. Test-only hook: an `internal` seal overload taking DEK and nonce, exposed to tests through `InternalsVisibleTo`. | Unit |
| A-T02 | Round trip through the **test-only** opener, padding and length prefix included | Unit |
| A-T03 | Opening with a changed organization, target, role, direction or note id, each separately, fails | Unit |
| A-T04 | Tampered ciphertext, tag, nonce or wrapped DEK each fails | Unit |
| A-T05 | Cross-domain: a field-value ciphertext cannot be opened as a note, nor a note as a field value, with the same KEK and identifiers mapped across | Unit |
| A-T06 | Each B2 validation rule; 500 characters at 4 bytes per character fits; 501 is refused | Unit |
| A-T07 | Provider unavailable: a note gives 503 and **no Authority command row**; without a note the command succeeds | Integration (Stage B) |
| A-T08 | Each Authority rule V1–V11 refused, directly and through the API (V10 by reusing an envelope in a second command) | Authority unit and Stage B |
| A-T09 | Plaintext `reason_note` is still refused (D8-72 regression) | Authority unit and Stage B |
| A-T10 | Plaintext sentinel: a unique note text is absent from every table of AuthorityDb (`pg_dump` grep), the archive blobs (Azurite), every table of BusinessDb (`pg_dump` grep), and the api, authority and worker container logs. The envelope is present in `security_commands` and the journal body. | Stage B |
| A-T11 | Idempotent replay reuses the envelope (same digest); an internally re-sealed retry is impossible (B4) | Stage B |
| A-T12 | Architecture: no product assembly contains a note opener; `IAuditorNoteSealer` has exactly one method; the Authority has no reference to `IEncryptionKeyProvider` or business encryption | Architecture |
| A-T13 | Rejected command: the stored row and audit entry contain no envelope bytes | Authority unit and Stage B |

### 1.8 Compatibility and rollout

- **Deploy order:**
  * The Authority goes first: it accepts envelopes and still refuses plaintext.
  * The business follows. A business sending an envelope to a containment-era Authority gets `REASON_NOTE_NOT_ACCEPTED`, so this fails safe.
- **Historic data:** no persisted notes exist (`PHASE_C8_M4_1_REVIEW_AND_REMEDIATION.md` §1.1), so no data migration is needed. Historic journal bodies and command payloads are immutable and never re-serialized.
  * Noteless commands' normalized payloads change from `"reason_note": null` to `"reason_note_envelope": null` for **new** commands only (`CanonicalJson` serializes nulls).
  * Implementation must confirm that nothing re-serializes historic payloads.
- **API contracts:**
  * Business OpenAPI: `reasonNote` comes back, and the TypeScript client is regenerated.
  * Authority internal OpenAPI: `reason_note_envelope` is defined.
  * Frontend: the note text area comes back to the People dialog, with the 500-character limit and a statement that the note is encrypted and cannot be read back in the app.

---

## 2. Design B: S1, Authority-to-SpiceDB projection integrity at decision time

### 2.1 Defect recap (T16S-1)

- **What happens:** after SpiceDB's datastore is restored from an older dump, a revoked auditor (`view_audit`) and a revoked AC-7 designer (`create_form`) are allowed. AuthorityDb still holds both revocations, with `authorization_revision = projection_applied_revision = 4` and the gate `Ready`.
- **Why:** the gate trusts AuthorityDb alone, and every check runs `at_least_as_fresh` the Authority-held ZedToken.
- **The marker is not consulted:** the `projection_cursor` marker is compared only by the projector's next write.

### 2.2 SpiceDB behaviour this protocol relies on (verified 2026-11-04)

The documentation sources are the authzed.com docs (consistency, datastores, commands) and authzed/api `permission_service.proto`, `core.proto` and `error_reason.proto`. They are current docs and are not pinned to v1.56. The probes were run read-only against `authzed/spicedb:v1.56.2` on the security-test stack with grpcurl 1.9.1.

| # | Behaviour | Source | Used for |
|---|---|---|---|
| F1 | WriteRelationships applies all updates in one transaction, with preconditions: "All updates will be applied transactionally, and if any preconditions fail, the entire transaction will be reverted." | proto (documented) | The marker and the revision's deltas become visible together (the projector writes them in one call) |
| F2 | `at_exact_snapshot`: "all data used in the API call must be *at the given* snapshot … if the snapshot is no longer available, an error will be returned" | proto (documented) | Pins every decision call to one snapshot |
| F3 | `fully_consistent`: "most recent snapshot … explicitly bypasses caching" | proto and docs (documented) | The decisive post-decision marker read, independent of undocumented caching |
| F4 | `at_least_as_fresh`: "more recent data might be used if available or faster" | proto (documented) | Pin read only |
| P1 | A marker read `at_least_as_fresh(authority token)` returns one message whose `read_at` is a quantized revision token (here `GhQKCENPOFhJTzRY…`). `fully_consistent` returns a different, head token. | probe | The pin `S` is a cache-friendly revision |
| P2 | A ReadRelationships call with no matching relationship streams **zero messages, so no `read_at` at all** (the proto says one message per relationship) | probe and proto | A missing marker yields no token: it must be handled explicitly and fail closed |
| P3 | `CheckPermission` and `CheckBulkPermissions` at `at_exact_snapshot(S)` return `checked_at` **byte-identical to `S`** (both quantized and head tokens tried) | probe (the proto has no comment) | A decision proves it used `S` |
| P4 | **`at_least_as_fresh` with a token from a different datastore is silently ignored**: data is served from the current datastore, with no error | probe | **The Authority-held ZedToken is not integrity evidence.** It does not detect a replaced datastore. |
| P5 | **`at_least_as_fresh` with a token "in the future"** (an xid far beyond the datastore's) **is accepted and echoed as `read_at`**, with current data served and no error | probe | The same conclusion: a ZedToken proves nothing about freshness against a restored datastore |
| P6 | `at_exact_snapshot` errors: a different datastore gives `Unknown` "ZedToken specified references a different datastore instance but at-exact-snapshot was requested"; a future revision gives `OutOfRange` "invalid revision: revision was invalid"; a garbage-collected revision gives `OutOfRange` "invalid revision: revision has expired"; a malformed token gives `InvalidArgument` "invalid revision requested" (for every mode) | probe (the docs name only "Snapshot Expired", with no code) | Failure classification (§2.6) |
| P7 | GC window default 24 h (`--datastore-gc-window`); quantization interval default 5 s | docs (commands) | `S` is used within milliseconds, far inside the window |
| P8 | Read replicas: "checking if a revision has been replicated into the target replica. If missing, it will fall back to the primary" | docs | **Deployment constraint:** no read replicas until S1 is re-verified with them (B-Q4) |
| P9 | pg_dump or pg_restore into a different PostgreSQL cluster can make data "associated with a future transaction ID and therefore isn't visible" | docs (datastores) | Such a restore shows **no marker**, so it fails closed as `MARKER_MISSING` |
| U1 | Whether ReadRelationships or checks are served from a cache keyed by revision | **Undocumented** | The protocol does not depend on it: the decisive read is `fully_consistent` (F3) |
| U2 | gRPC status of a failed precondition | Undocumented in the docs; the projector already treats `FailedPrecondition` this way | Unchanged |

**Consequence:** the ZedToken is a snapshot selector, not integrity evidence (P4, P5). Integrity rests on three things:
1. **The marker.** Written atomically with each revision's deltas (F1), it states which Authority revision the SpiceDB state represents.
2. **The Authority's own committed revision**, which is outside SpiceDB.
3. **The deployment invariant** that only the Authority projector holds the SpiceDB preshared key and network path. This is existing; business components have neither.

### 2.3 Checkpoint definition

- **What exists today:** one relationship per organization, `organization:{org}#projection_cursor@projection_step:step-{org}-{n}`.
- **How the projector maintains it:**
  * It applies revision `n` in **one** WriteRelationships call: `MUST_MATCH` the marker for `n−1`, `MUST_NOT_MATCH` the marker for `n`, the deltas, then delete the `n−1` marker and touch the `n` marker.
  * So `n` always equals the Authority's `projection_applied_revision` after acknowledgement.
- **No schema change:** S1 changes neither the marker nor the schema.
- **Valid marker:** exactly one relationship matching the filter, with a subject id of exactly `step-{org:D}-{decimal}`, where the decimal has no sign, no leading zeros (except `0`) and fits `Int64`.

**Why a matching marker at snapshot `X` implies SpiceDB's state at `X` is the projection of revisions `1..n`:**
1. The marker for `n` is visible exactly in snapshots after the write of `n` and before the write of `n+1` (F1).
2. Every legitimate write moves the marker.
3. A restored datastore is a dump of one consistent pg_dump snapshot, in which the marker and the tuples (one table, `relation_tuple`) agree with each other at the dump's revision `d`.
   * If `d ≠ n`, the marker shows `d`.
   * If `d = n`, the dump *is* the current state and nothing was lost.
4. A datastore restored into another cluster or never seeded shows no marker (P9).

**Not detected:** a writer *inside* the Authority domain who forges the marker for `n` over stale tuples. That is outside the Level 2 trust assumption (the Authority domain is not compromised; §4M). It is residual R-B1 (§2.9).

### 2.4 Decision protocol ("pin, decide, verify")

This applies to every Authority path that consults SpiceDB for a protected decision. There are six decision methods in three services, which hold every SpiceDB `Check*` and `LookupResources` call in the Authority (verified by grep at `6663872`):
- `PermissionCheckService.CheckBatchAsync` and `DiscoverAsync`;
- `ContentService.GetHeadAsync`, `GetHistoryAsync` and `VerifyRecordCoreAsync` (with `CheckFieldLeavesAsync`);
- `SearchCatalogueService.IssueUserCatalogueAsync`.

The projector's own reads keep their existing rules (§2.7).

```
R1  state₁ ← AuthorityDb organization state (existing gate):
      not quarantined, archive current, A₁ = P₁ (authorization_revision = projection_applied_revision), Z₁ present.
      Otherwise fail as today (423 / 503).

M1  PIN:     ReadRelationships(organization:{org}#projection_cursor, at_least_as_fresh(Z₁))
             → markers m₁, snapshot S = read_at.
             Require exactly one valid marker with step(m₁) = P₁.   [early detection]
             Zero messages → there is no S, and the candidate is MARKER_MISSING.

D   DECIDE:  every Check / CheckBulk / LookupResources of the request uses at_exact_snapshot(S),
             including every LookupResources page.
             Require checked_at = S (byte-equal), or looked_up_at = S for every result.
             Any mismatch → deny (503 AUTHORITY_NOT_READY), alert, no quarantine.

M2  VERIFY:  ReadRelationships(organization:{org}#projection_cursor, fully_consistent) → marker m₂.
             Require exactly one valid marker with step(m₂) = P₁.   [decisive; bypasses caches, F3]

R2  state₂ ← AuthorityDb organization state.
             Require A₂ = A₁, P₂ = P₁, not quarantined. Otherwise retry from R1 (at most 2, as today),
             then 503 AUTHORITY_NOT_READY.

    Return the decision, bound to (A₁, P₁) as today. S is not exposed to the business.
```

**Mismatch at M1 or M2** (marker missing, invalid, duplicated, or `step ≠ P₁`):
1. Never allow.
2. Run **lock-confirm** (§2.5).
3. If confirmed: 423 `INTEGRITY_QUARANTINE`.
4. If not confirmed: the state moved (legitimate progress). Retry, then 503 `AUTHORITY_NOT_READY`.

**Why this is enough. Check-to-use analysis:**

| Window | What can happen | Outcome |
|---|---|---|
| R1 → M1 | A new revision commits and is projected: `A` and the marker advance | M1 sees a marker > P₁; lock-confirm sees `A ≠ A₁`, so retry; no false quarantine |
| M1 → D | The projector writes `P₁+1` | D is pinned to `S`, which still shows `P₁` (F2). M2 shows `P₁+1` and R2 shows `A₂ ≠ A₁`, so retry |
| M1 → D | **A datastore restore lands in between** (S's revision is reused on restored rows) | D may evaluate restored, stale data at `S`. **M2 runs after D**, reads head without caches (F3) and sees the restored marker `d ≠ P₁`, so **deny and quarantine**. This window is why a pin-only check (M1 alone) is **not** sufficient, and why the owner was right that "a marker comparison alone" does not prove the decision's state. |
| During D | A restore while a multi-dispatch check runs (partly old, partly restored data) | M2 runs after D completes and sees the restored marker, so deny |
| D → M2 | A restore after D | D used pre-restore, current data, so the decision was correct; M2 still detects the restore, so deny and quarantine (a correct detection) |
| M2 → R2 | A legitimate revision commits | R2 shows `A₂ ≠ A₁`, so retry |
| After R2 | A revoke commits after the decision is returned | **Pre-existing post-decision revocation window**, unchanged by S1. It is bounded by the existing business rechecks (streams, exports, CONS rules). It is not T16S-1 and is listed as R-B3. |

- **Invariant obtained:** a decision is returned only if, at some instant after it was evaluated:
  * SpiceDB's head carried the marker for `P₁` (M2);
  * the decision was evaluated at a snapshot carrying the marker for `P₁` (M1 and D);
  * the Authority's committed revision was `P₁` throughout `[R1, R2]`.
- **Why that closes T16S-1:** every legitimate SpiceDB change moves the marker (F1), so the state evaluated is the projection of exactly the Authority's committed revision. A rollback is detected unless the restored marker coincidentally equals `P₁`. In that case the restored state *is* revision `P₁`'s state, so nothing is lost.

**Batch and multi-call requests:** one M1, one M2 and one R1/R2 pair per Authority request, not per check. A 250-item batch costs two marker reads. Content verification, which makes several checks, uses one `S` for all of them.

**Type-level enforcement:**
- `ISpiceDbProjection.Check*` and `LookupResources` take a `VerifiedProjectionSnapshot` (opaque: organization, expected step `P₁`, pin `S`) instead of a raw ZedToken string.
- Only the new `ProjectionIntegrityVerifier` (Application/Common) can construct one, so no call path can compile against an unverified token.
- The type is also the vehicle for the M2 obligation: `verifier.CompleteAsync(snapshot)` must be called before a decision is released. An architecture test checks every consumer.
- An architecture test forbids using `OrganizationAuthorizationState.ProjectionZedToken` outside the verifier and the projector.

### 2.5 Lock-confirm and persistent quarantine

Triggered by any marker mismatch at M1 or M2, or by the periodic check (§2.8).

```
1. New scope; LockOrganizationAsync(org): the same row lock the projector holds for its whole SpiceDB write,
   so no projection write can occur while it is held.
2. Re-read state. If quarantined already → done (423). If A ≠ P (projection pending, including a crash after a
   SpiceDB write and before acknowledgement) or (A, P) ≠ (A₁, P₁) → release, no quarantine, caller retries/503.
3. ReadRelationships(marker, fully_consistent) under the lock. If exactly one valid marker with step = P → release,
   no quarantine (transient; counted), caller retries.
4. Otherwise: confirmed. In the same transaction:
     state.IntegrityQuarantineEnabled = true
     state.IntegrityQuarantineReasonCode = PROJECTION_MARKER_BEHIND | PROJECTION_MARKER_AHEAD
                                          | PROJECTION_MARKER_MISSING | PROJECTION_MARKER_INVALID
     AuditJournal.Append(audit stream, "ProjectionIntegrityQuarantined",
        { organization_id, reason_code, expected_projection_revision = P, observed_marker_step (null if none),
          detection_source = "decision" | "periodic", correlation_id })
   Commit, then ISecurityAlertSink.Raise(Critical, "PROJECTION_INTEGRITY_QUARANTINE", fixed summary).
```

- **Meaning of each reason code:**
  * `BEHIND` (`step < P`): a SpiceDB datastore rollback.
  * `AHEAD` (`step > P` with `A = P`): AuthorityDb is older than SpiceDB (an AuthorityDb rollback or forgery). This is partial early detection of a T16-A-class event, not its remediation.
  * `MISSING`: an empty, other-cluster or pre-bootstrap datastore (P2, P9).
  * `INVALID`: duplicated or malformed markers.
- **No false positives from legitimate progress:** step 2 runs under the projector-excluding lock, and every legitimate marker change needs a committed `A` increase first, which READ COMMITTED makes visible to the re-read.
- **Quarantine is persistent:**
  * It is the existing AuthorityDb columns and needs no migration.
  * It survives restarts.
  * It blocks every protected decision (`OrganizationGate`) and every security command (`SecurityCommandService`).
  * **New in S1:** it also blocks the projector, which writes nothing to SpiceDB for a quarantined organization. Today the projector does not check quarantine.
- **Clearing (B-Q1):** **no code path clears any quarantine today**, for any reason code, and S1 adds none. Clearing requires an explicitly authorized recovery process:
  * an S3 rebuild from AuthorityDb desired state, under the G14 recovery control plane;
  * a new marker and a new pin;
  * two distinct approvals.

  That is G14 and is not authorized. **Consequence:** until G14, a quarantined organization stays unavailable for protected access. In Development/Testing the stack is disposable. The alternative needs your authorization: a minimal, two-person operator-plane "verified re-projection" command, in G14 scope.

### 2.6 Failure classification, responses and telemetry

| Condition | Decision | Response to the business | Quarantine | Alert |
|---|---|---|---|---|
| Gate closed (existing reasons) | deny | 503 `AUTHORITY_NOT_READY` / 423 `INTEGRITY_QUARANTINE` (unchanged) | existing | existing |
| Marker mismatch, confirmed (§2.5) | deny | 423 `INTEGRITY_QUARANTINE` | **yes** | Critical `PROJECTION_INTEGRITY_QUARANTINE` |
| Marker mismatch, not confirmed (the state moved) | deny | retry, then 503 `AUTHORITY_NOT_READY` | no | none; counted |
| SpiceDB unavailable or timeout (gRPC `Unavailable`, `DeadlineExceeded`, transport) | deny | 503 `AUTHORITY_NOT_READY` | no | existing readiness |
| `at_exact_snapshot(S)` `OutOfRange` (expired or future) or `InvalidArgument` | deny | 503 `AUTHORITY_NOT_READY` | no (the next request's M1 decides) | Warning `PROJECTION_SNAPSHOT_UNAVAILABLE` |
| `at_exact_snapshot(S)` "different datastore instance" (P6) | deny | 503 `AUTHORITY_NOT_READY` | **lock-confirm runs immediately** (the datastore changed mid-request) | Critical if confirmed |
| `checked_at` / `looked_up_at` ≠ S | deny | 503 `AUTHORITY_NOT_READY` | no | Critical `PROJECTION_SNAPSHOT_MISMATCH` (unexpected SpiceDB behaviour) |

- **Non-disclosure:**
  * Business-facing errors carry no marker, step or token values. The business maps `INTEGRITY_QUARANTINE` to its existing `SecurityIncident` type `IntegrityQuarantine` (High); confirm at implementation that an organization-level quarantine raises one.
  * The journal event and alert carry the organization id, reason code, expected revision and observed step. They carry no user, subject, resource or permission identifiers.
- **Telemetry (B-Q3, recommended):** a `System.Diagnostics.Metrics` meter `AuthorizationAuthority.ProjectionIntegrity`, with no new package:
  * counter `projection_integrity_verifications{outcome}`, where outcome is `ok`, `moved`, `mismatch_confirmed`, `mismatch_transient`, `snapshot_error`, `token_mismatch` or `unavailable`;
  * histogram `projection_integrity_marker_read_ms{phase=pin|verify}`.
  * There is no Authority meter today. Locally the metrics are visible through `dotnet-counters`; production export is a deployment item.

### 2.7 Other SpiceDB readers

- **`ProjectionProcessor`:**
  * keeps its marker CAS and its fully consistent reconciliation reads;
  * new: returns `Idle` without writing when the organization is quarantined.
  * Its `DeltaIsReflectedAsync` reads are not decisions.
- **Onboarding and bootstrap** (`InitializeCursorAsync`): unchanged. A new organization has no grants until its first revision is projected and acknowledged.
- **Readiness `ProbeAsync`:** unchanged (schema presence). S2 adds the marker check.

### 2.8 Periodic integrity check (S2, a complement and never a replacement)

- **What it does:** a background processor in the Authority, every 60 s (configurable, 10–600 s), and once whenever SpiceDB readiness turns from not-ready to ready. For every organization whose state is `A = P` and not quarantined:
  * a `fully_consistent` marker read;
  * the same comparison;
  * lock-confirm on mismatch.
- **Purpose:** it catches a rollback in an idle organization (no traffic, so no decisions) and raises the alert without waiting for a user.
- **Cost:** one point read per organization per interval, so about 25 reads a minute at the PB-09 envelope.

### 2.9 Residual risks (stated, not resolved by S1)

| ID | Residual | Owner or track |
|---|---|---|
| R-B1 | A hostile writer inside the Authority domain forges the marker over stale tuples | Outside the Level 2 trust assumption. A future full reconciliation (desired-relationship digest versus SpiceDB) is G14/S3 work. |
| R-B2 | AuthorityDb **and** SpiceDB restored to the same older revision: the marker matches, and AuthorityDb's older revision is wrongly trusted | **T16-A / G14** (archive replay, recovery epoch). S1 cannot detect it, because both sides agree. |
| R-B3 | A revoke committed after the decision is returned (post-decision window) | Pre-existing, bounded by the existing recheck rules. Unchanged. |
| R-B4 | A datastore swap while SpiceDB keeps a revision cache from the old datastore (U1) | Covered by the cache-bypassing M2 (F3). The no-restart variant RB-12 exercises it. |
| R-B5 | Read replicas: SpiceDB's replica fallback is documented (P8) but unverified with S1 | Deployment constraint: no replicas until re-verified (B-Q4) |
| R-B6 | A quarantined organization stays unavailable until G14 | Availability consequence, accepted only by your decision (B-Q1) |

### 2.10 Performance and availability

- **Added cost per protected Authority request:**
  * one `at_least_as_fresh` point read (M1), which can use SpiceDB's quantized revision;
  * one `fully_consistent` point read (M2), which bypasses caches.

  Both are a single indexed lookup of one relationship. Checks keep the quantized revision (P1, P3), so dispatch caching behaves as it does under `at_least_as_fresh` today; the benchmark will confirm.
- **No new dependency:** SpiceDB is already required for every decision, so a SpiceDB outage fails closed exactly as today. Lock-confirm runs only on mismatch.
- **Benchmark before acceptance (B-Q2):** the PB-09 harness on the security-test stack, before and after, at 50 requests/s sustained.
  * Paths: single check, 250-item batch check, `DiscoverAsync` (LookupResources), `VerifyProjection` (full and partial share), and the search catalogue.
  * Report p50, p95 and p99, error rate, and SpiceDB CPU.
  * **Proposed acceptance:** added p95 ≤ 15 ms per request locally, error rate unchanged.
  * If it fails, the optimization **O-B1** needs your approval: drop M1 on check-only paths and pin `S` from `checked_at` of an `at_least_as_fresh` check (still followed by M2). Lookups keep M1, because zero results carry no token (P2). No caching of verified snapshots across requests (it would reopen the window).

### 2.11 Regression and verification matrix (Design B)

**Characterisation tests.** These turn §2.2 into executable tests pinned to `authzed/spicedb:v1.56.2`. A SpiceDB upgrade that changes any of them fails CI.

| ID | Asserts |
|---|---|
| CT-1 | Marker read `at_least_as_fresh` returns one message with `read_at` (P1) |
| CT-2 | A zero-match ReadRelationships streams no message (P2) |
| CT-3 | `checked_at` equals `S` byte for byte for Check and CheckBulk at `at_exact_snapshot` (P3); `looked_up_at` equals `S` for LookupResources |
| CT-4 | `at_least_as_fresh` silently accepts a foreign-datastore token and a future token (P4, P5), so that S1 never relies on the token |
| CT-5 | The `at_exact_snapshot` error codes in P6 |
| CT-6 | A marker write together with deltas is atomic: the marker and a delta tuple are never visible apart, under concurrent reads (F1) |

**Rollback regression.** Stage B, shard `authorization-security-onboarding`. Each test dumps `spicedb-db`, makes the change, restores, then asserts. The expectation in each case is deny, then 423 quarantine, a journal event and a Critical alert, unless stated otherwise.

| ID | Area | Scenario |
|---|---|---|
| RB-01 (= T16-S) | Organization roles (AC-8.1, AC-7) | Revoked auditor `view_audit` and revoked designer `create_form` |
| RB-02 | Record access (AC-5) | Owner shares read with Bob; dump; revoke the share; restore. Bob is denied discover, read, `GetHead`, `GetHistory` and `VerifyProjection`. |
| RB-03 | Field-level sharing (AC-5) | A field-item read share is revoked. `VerifyProjection` (partial) does not return the field. `CheckBulk` path. |
| RB-04 | Team permissions (AC-9 area) | Bob is removed from a team holding a record share, and is denied |
| RB-05 | Discovery and search (AC-6) | Discover and `IssueUserCatalogue` (LookupResources) do not list the record. No partial result. |
| RB-06 | Form permissions (AC-6, AC-7) | `design`, `create_record` and `query_fields` revoked |
| RB-07 | Cross-organization isolation | Org X changes after the dump and is quarantined. Org Y is unchanged since the dump: **its marker matches, it stays Ready, and its decisions are correct**. No org X subject gains anything in Y, or the reverse. SpiceDB is shared across organizations, so a restore affects all of them, each judged by its own marker. |
| RB-08 | Missing marker | Restore from a dump taken before the organization's bootstrap. `PROJECTION_MARKER_MISSING`. |
| RB-09 | Marker ahead | AuthorityDb organization state set back with the restore simulation. `PROJECTION_MARKER_AHEAD`. |
| RB-10 | Persistence | After quarantine: restart the Authority and it is still quarantined; security commands are refused; the projector writes nothing (the SpiceDB marker is unchanged) |
| RB-11 | No false quarantine | 200 concurrent checks while 20 grant/revoke commands commit and project: **zero quarantines**, and every decision is consistent with the revision it reports |
| RB-12 | Restore without restart | `pg_restore --clean` into the live `spicedb-db` while SpiceDB keeps running. The first decision afterwards is denied (R-B4). |
| RB-13 | Other cluster | The datastore is dropped, migrated empty and restored (P9). Fail closed (MISSING or schema-absent). |
| RB-14 | Periodic check | An idle organization is quarantined by S2 within one interval, with no user traffic |
| RB-15 | Outage | SpiceDB stopped: 503, **no** quarantine; after restart: Ready |

**Unit and architecture tests:**

| ID | Asserts |
|---|---|
| U-01…U-08 | Verifier state machine with a fake `ISpiceDbProjection`: each M1 and M2 outcome, a token mismatch, each P6 error class, and lock-confirm results 2, 3 and 4 |
| ARCH-01 | No `Check*` or `LookupResources` call outside `VerifiedProjectionSnapshot`; `ProjectionZedToken` used only by the verifier and the projector |
| ARCH-02 | Every method that takes a snapshot calls `CompleteAsync` before returning an allow |

**Performance:** PERF-01, the §2.10 benchmark.

- **T16-S after the fix:** it must pass by showing a denial *and* the quarantine.
- **D8-78:** it stays mandatory and is never skipped or marked as an expected failure.
- **A stale "allowed" anywhere in RB-01…RB-15 is a release-blocking failure.**

### 2.12 Rejected alternatives

| Alternative | Why not |
|---|---|
| Trust the ZedToken (status quo) | P4 and P5: SpiceDB ignores a foreign or future token under `at_least_as_fresh` |
| M1 only (pin-and-compare, no M2) | It misses a restore landing between the pin and the decision (§2.4). The owner's warning applies. |
| Embed the marker in every permission (caveat or intersection with a cursor relation) | It evaluates atomically, but rewrites every permission in `authority.schema.zed`, adds caveat context to every check, changes `Unknown`/conditional handling, and needs organization arrows on every resource type: a broad schema change for no gain over M2 |
| Cache verified snapshots per organization for a few seconds | Reopens a rollback window and is unsafe across Authority instances |
| Periodic check only (S2) | Leaves a window between probes. D8-77 forbids it as a replacement. |

---

## 3. Decisions requested (C-115)

| ID | Decision | Recommendation |
|---|---|---|
| **A-Q1** | Pad note plaintext to a fixed 2,048 bytes (hide length) or leave it unpadded (length disclosed) | **Pad** (§1.2) |
| **A-Q2** | Approve the AAD construction (§1.2), envelope schema (§1.3), Authority rules V1–V11 (§1.4) and business rules B1–B8 (§1.5) as the M1 contract | **Approve** |
| **A-Q3** | Business audit discloses note presence (`reasonNoteSupplied`) or not | **Not** (B8) |
| **B-Q1** | Quarantine clearing: none until the G14 recovery plane (organization unavailable meanwhile), or authorize a minimal two-person verified re-projection command now (G14 scope) | **None until G14** |
| **B-Q2** | Approve the pin-decide-verify protocol (§2.4–§2.8) with S2, the failure table (§2.6), and the performance acceptance of added p95 ≤ 15 ms (§2.10) | **Approve** |
| **B-Q3** | Add the Authority metrics meter (§2.6) | **Add** |
| **B-Q4** | Deployment constraint: no SpiceDB read replicas until S1 is re-verified with them | **Adopt** |

- **After approval:** implementation order is Design B first (highest priority, T16S-1), then Design A.
- **Each implementation needs:** independent shard runs, a signed local commit, and evidence for the independent review.
- **Not authorized:** push, PR, merge, deploy, M4-2, RCF-1 remediation and G14.
