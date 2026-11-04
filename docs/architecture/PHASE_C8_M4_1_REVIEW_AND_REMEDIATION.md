# Phase C8 M4-1: open findings, remediation proposals and review package addendum

- **Status:** **C-112 proposal; owner decisions D8-71…D8-75 in C-113; execution recorded in C-114 (2026-11-04).**
  * **C-115:** D8-76…D8-78 decided. Designs A (M1) and B (S1) are in `PHASE_C8_M4_1_DESIGNS_M1_S1.md` and await approval. Nothing more is implemented.
  * **C-116:** design revision 2 and focused protocol verification (`docs/evidence/phase-c8/m4-1-protocol-verification/`). Final approval is pending.
  * Implemented under C-113: the D8-72 containment and T16-B/T16-S. T16-B passes. T16-S fails and exposed defect **T16S-1** (§2.4).
  * Not implemented: R1. The existing service does not support the required binding (§1.5), so a design change is presented as D8-76.
  * Not authorized: RCF-1 remediation and the T16S-1 remediation.
  * M4-1 (AC-8.1) is conditionally accepted as implementation evidence (owner, C-112). That is not milestone closure and not independent approval, and C-111 stays open.
  * Not authorized: push, PR, merge, deploy, M4-2, and any remediation below.
- **Audience:** the project owner (decisions D8-71…D8-75) and the independent Authority reviewer (§4 joins the AC-5/AC-6/AC-7/AC-9 package). This document is by Claude and is not an independent review.
- **Baseline:** branch `phase-c8/m4-governance-audit` at `c2dea1c`. M4-1 commits `b54821f`, `667643e` and `7c9acf4`; evidence in `docs/PHASE_C8_STATUS.md` §34.

## 1. RN-1: `reason_note` confidentiality (finding and remediation proposal)

### 1.1 Finding

C-110 requires the optional auditor note to be "treated as potentially confidential under the existing confidentiality requirements". M4-1 does not meet that:

- The note is **plaintext** in AuthorityDb, in the command's normalized payload (`security_commands.command_payload_json`) and the authorization-stream journal body.
- Through the journal it is also plaintext in the **archive**, which is Azurite locally and Azure immutable storage in the design.
- It never reaches BusinessDb, logs, rejection evidence, the business audit or auditors (all tested).
- **No persistent environment holds a note.** M4-1 images were never deployed to the development stack (C-78 condition 7). The security-test stack is disposable and was recreated with `down -v` for every run. Hosted CI has not run.

### 1.2 Existing encryption components (verified 2026-11-04)

| Component | Where | What it is | Usable for the note? |
|---|---|---|---|
| `IFieldEncryptionService` / `AesGcmFieldEncryptionService` | business Application/Infrastructure | AES-256-GCM per value. A random 256-bit DEK per value, wrapped by the KEK of `IEncryptionKeyProvider`. A 96-bit random nonce. AAD = domain label ‖ envelope version ‖ context identifiers. | **Yes, the same envelope with its own context.** This is the approved §4C.3 design, implemented and tested. |
| `IEncryptionKeyProvider` / `LocalDevelopmentKeyProvider` | business Infrastructure | **One** configured 32-byte KEK with one key reference. AES-GCM DEK wrap. Refused in staging and production by the startup guard. | Yes, under the same limits as all field encryption |
| `UnavailableKeyProvider` | business Infrastructure | Registered when no provider is configured. Every wrap and unwrap fails closed. **No Azure Key Vault or Managed HSM provider is implemented.** | Gives fail-closed behaviour outside Development/Testing |
| `IIntegrityMacProvider` (Local, Vault Transit HMAC, Managed HSM planned) | Authority | HMAC only, no encryption | **No.** Encryption in the Authority would be a new mechanism (§1.3, option R2). |

### 1.3 Options

| Option | Summary | Assessment |
|---|---|---|
| **R1 (recommended)** | **The business API seals the note** with the existing AES-GCM envelope (a new context record and AAD domain label, the same primitive, DEK wrap and key provider). The Authority receives and stores **only the sealed envelope**, validates its shape, and never holds a key. | Reuses approved, implemented components. The Authority store, journal and archive hold ciphertext only. No new cryptographic mechanism: the new parts are a context and AAD label. The reviewer must confirm this counts as reuse. |
| R0 | Remove `reason_note` entirely (reason code only) | Strongest and simplest; it loses the optional context the owner approved in C-110. Recommended as the **interim containment** (D8-72). |
| R2 | Authority-side encryption (Vault Transit `encrypt` locally, Managed HSM A256GCM in production) | A new mechanism in the Authority, needing a reviewed design and new key purpose, custody and rotation. **Not recommended now.** |
| R3 | Note stored encrypted in BusinessDb; the Authority keeps only a digest of the ciphertext | A new table and migration, two stores to reconcile, and a BusinessDb DBA can delete the ciphertext. More moving parts than R1 for no gain. |

### 1.4 R1 design

- **Encryption boundary.**
  * The plaintext exists only in the browser request and in business API memory for one call (zeroed after sealing, as in `AesGcmFieldEncryptionService`).
  * It is never written to BusinessDb, logs or audit.
  * The business seals before calling the Authority.
- **Envelope:** a new `AuditorReasonNoteContext(TenantId, TargetAuthorityUserId, RoleName, Direction, ReasonNoteId)`.
  * `ReasonNoteId` is a UUID v7 generated by the business and carried in the payload.
  * AAD = `"DynamicForms/AuditorReasonNote/v1"` ‖ envelope version ‖ tenant ‖ target ‖ role ‖ direction (`appoint`/`remove`) ‖ note id.
  * A note therefore cannot be moved to another organization, person, role, direction or command.
  * Fields: envelope version, algorithm name, key reference, note id, nonce, ciphertext, tag, wrapped DEK (base64).
- **Wire change (Authority, AC-8.1 payload):**
  * `reason_note` (string) is replaced by an optional `reason_note_envelope` object.
  * Strict binding then rejects any plaintext `reason_note`.
  * The Authority validates the envelope version, algorithm name, key-reference format, base64 fields and bounded sizes (ciphertext ≤ 2,000 bytes, which covers 500 UTF-8 characters; envelope ≤ 4 KB). It has no key and cannot decrypt.
  * The envelope sits in the normalized payload, so the request digest, idempotency, the journal hash chain and the archive cover the ciphertext.
- **Key management and rotation:**
  * The existing provider and its limits apply unchanged: one KEK in Development/Testing, refused elsewhere, and no rotation support (only the current reference unwraps).
  * Rotation, per-organization KEKs and a production KMS are the **existing field-encryption gaps**. R1 inherits them and adds no new claim.
  * Purpose separation rests on the distinct AAD domain label under the shared KEK, the same separation fields and documents use today. A dedicated key reference is possible once a provider supports more than one.
- **Availability:**
  * With no configured provider, a request **with** a note is refused (`ENCRYPTION_UNAVAILABLE`, fail closed).
  * A request **without** a note proceeds.
  * Auditor commands are refused in staging and production anyway (PB-08 step-up gate).
- **Archive and recovery:**
  * The archive holds ciphertext only.
  * Reading a recovered note needs both the Authority archive and the business KEK version that sealed it, so key-retention obligations (§4C.3, G14 item 7) extend to notes.
  * Losing the KEK makes notes unreadable. Notes are non-authoritative, so this does not affect access, and crypto-erasure becomes possible.
  * Replay reproduces the envelope byte for byte.
- **Who may decrypt (D8-73):**
  * **Recommended:** nobody in-app for now. The note is write-only, and no decrypt route or UI is built.
  * Any future read path needs its own reviewed design. The candidate is Security Administrators with fresh step-up, audited in the Authority stream, and never auditors (D8-67).
  * Until then, disclosure is possible only through an operator procedure holding both the archive and the KEK, which is a recovery-plane concern.
- **Migration:**
  * No persistent data exists (§1.1), so no data migration is needed.
  * The payload change is breaking only for the unreleased M4-1 note field.
  * Old payloads carrying `reason_note` are rejected after the change.
- **Tests (proposed):**
  * envelope round trip;
  * wrong tenant, target, role, direction or note id each fails;
  * tag and ciphertext tampering fail;
  * plaintext `reason_note` is rejected by the Authority, directly and through the API;
  * size bounds;
  * key provider unavailable: a note is refused and a request without one succeeds;
  * sentinel: the note plaintext is absent from AuthorityDb (`security_commands`, `security_journal_entries`), from the **archive** blobs (Azurite, via `AzuriteProbe`), from BusinessDb and from logs;
  * an architecture test that the Authority has no reference to business encryption or the key provider;
  * the idempotency digest covers the envelope.

### 1.5 C-113 verification: does the existing service support the required context binding? **No, not as designed.**

`AesGcmFieldEncryptionService.Aad` is fixed:

```
"DynamicForms/FieldValue/v1" ‖ int32 envelope version ‖ TenantId ‖ FormRecordId ‖ FieldDefinitionId ‖ FieldValueItemId (16-byte big-endian UUIDs) ‖ int32 length ‖ LogicalType (UTF-8)
```

- **Context type:** the input is the field-value-specific `FieldEncryptionContext`; the stored envelope is the `FieldEncryptedValue` entity.
- **Domain label:** a constant for field values.
- **File service:** `AesGcmFileEncryptionService` is likewise fixed to tenant, record, attachment and version.

Neither offers a context for organization, target user, role, direction and note id. There are two ways to bind the note, and both are cryptographic-usage changes, so per C-113 **R1 is not implemented** and this minimal change is presented for approval:

| Option | Change | Assessment |
|---|---|---|
| **M1 (recommended)** | A new `AesGcmAuditorNoteSealer` in business Infrastructure, behind a new port `IAuditorNoteSealer` (`Seal(context, plaintext)` only; no `Open` while D8-73 forbids decryption). It uses the **identical** construction: AES-256-GCM, a random 256-bit per-note DEK wrapped by the existing `IEncryptionKeyProvider`, a random 96-bit nonce, a 128-bit tag, the DEK zeroed after use. Only the AAD differs: `"DynamicForms/AuditorReasonNote/v1"` ‖ int32 version ‖ organization ‖ target Authority user ‖ note id (16-byte big-endian UUIDs) ‖ length-prefixed role name ‖ length-prefixed direction (`appoint`/`remove`). | Same primitive, key provider and key, with a new domain label giving cryptographic domain separation from field values and documents. The existing field and file services are **not modified**: duplicating about 40 lines avoids any risk to field encryption. A known-answer vector and cross-domain tests prove a field ciphertext cannot be opened as a note and the reverse. |
| M2 | Call the existing `IFieldEncryptionService` unchanged, mapping `TenantId` to organization, `FormRecordId` to target, `FieldDefinitionId` to note id, `FieldValueItemId` to a constant, and `LogicalType` to `"AuditorReasonNote:{role}:{direction}"` | No new code path, but the identifier slots are overloaded and separation from field values rests on the logical-type string under the field-value domain label. That is error-prone and harder to review. **Not recommended.** |

**Substitution protection with M1:**
- The Authority cannot decrypt (no key), but it validates the envelope's **clear-text context header** (organization, target, role, direction, note id) against the command: organization from the request, target from `authority_user_id`, role from `role_name`, direction from the command kind. Any mismatch is refused.
- Because the AAD binds the same values, a header altered to match another command makes the ciphertext undecryptable.
- **Reuse of an envelope in another command** (same organization, target, role and direction) is refused by a note-id uniqueness check over the organization's committed and pending auditor commands (`command_payload_json` containment query; an idempotent replay of the same key is not a reuse).

**Unchanged dependencies to document:**
- production key providers (Azure Key Vault or Managed HSM) are not implemented;
- key rotation is not supported, because only the current key reference unwraps;
- per-organization KEKs do not exist yet.

R1 notes are therefore available in Development/Testing only. Everywhere else, a request with a note is refused (`ENCRYPTION_UNAVAILABLE`), which matches the auditor commands themselves being refused there (PB-08).

## 2. T16: recovery must not re-grant a revoked auditor

**Status:** outstanding, **mandatory** (owner, C-112). Not waived. Not claimed verified until it passes. The same gap exists for AC-7 T16.

### 2.1 What the code has today (verified)

- The organization gate (`OrganizationGate.EvaluateAsync`) uses **AuthorityDb state only**: quarantine, archive receipts, and `projection_applied_revision = authorization_revision` with a ZedToken present.
- Checks call SpiceDB with `AtLeastAsFresh` the Authority-held ZedToken.
- The SpiceDB `projection_cursor` marker is compared-and-set **only when the projector writes**. Reads never compare it.
- There is no SpiceDB rebuild from AuthorityDb, no journal replay into a restored AuthorityDb, and no recovery epoch: the G14 recovery control plane is not implemented.
- The only executed restore test is SEC-P10 (BusinessDb dump and restore).

### 2.2 Proposed decomposition

| Test | Scenario | Expected | What it needs |
|---|---|---|---|
| **T16-B** | BusinessDb restored from a dump taken while the auditor was appointed; the revoke is committed in the Authority | Still denied; the roster and business projection never confer access | Test only; reuses the SEC-P10 dump and restore tooling |
| **T16-S** | **SpiceDB datastore** (`spicedb-db`) restored from a dump taken before the revoke, while AuthorityDb keeps the revoke | Denied: fail closed until an approved rebuild | Test infrastructure (dump and restore inside `spicedb-db`, SpiceDB restart). **May expose a product gap:** if SpiceDB honours the existing ZedToken over restored data, the stale grant could be answered *allowed* until the next projection write. The outcome is unknown until run. If it fails, the minimal fix candidate is for the gate to verify the SpiceDB cursor step equals `projection_applied_revision` (one fully consistent read) before trusting a check, plus an approved rebuild path. That fix is a separate decision. |
| **T16-A** | AuthorityDb **and** SpiceDB restored to before the revoke; the archive keeps the revoke event | Revoke re-applied by replay before access reopens (G14 §4R items 1–2, 5) | **Product work:** G14 replay, recovery epoch and rebuild, which are P0 and open. It cannot pass until that exists. |

**Recommendation (D8-74):** require **T16-B and T16-S passing before M4 closure** (fixing any T16-S product gap through a separately approved change). Track **T16-A under G14** as a production-readiness blocker, because no test infrastructure alone can make it pass. If you require T16-A for M4 closure, M4 closure depends on G14.

**Harness work (proposed, not authorized):**
- `ComposeStack` helpers for an Authority-domain SpiceDB datastore dump and restore, mirroring `AuthorityRestoreSimulationSql` (security-test stack only, never product code);
- a `SpiceDbRollbackTests` class in shard `authorization-security-onboarding`, covering T16-B and T16-S for both the auditor and the AC-7 roles.

### 2.3 Results (C-113 execution, 2026-11-04, commit `b662309`)

| Test | Result |
|---|---|
| **T16-B** `RestoringAnOlderBusinessDatabaseCannotRestoreARevokedAuditor` | **PASS.** After a hostile restore of the whole BusinessDb (the business really is older: the `SECURITY_AUDITOR_REMOVED` row is gone), the Authority still holds the committed revoke; `view_audit` is denied, the People roster shows no auditor role, and a second removal is `INVALID_COMMAND`. |
| **T16-S** `RestoringAnOlderSpiceDbDatastoreCannotMakeARevokedAuthorizationEffective` | **FAIL: security defect T16S-1** (below). The test asserts the secure expectation and is kept failing until an approved remediation exists. |

### 2.4 Defect T16S-1: an older SpiceDB datastore makes revoked authorizations effective again

**Observed** (fresh security-test stack, two independent runs):
1. A Security Auditor (AC-8.1) and a designer (AC-7) hold their roles.
2. The `spicedb` database is dumped (`pg_dump -Fc`).
3. Both roles are revoked through the normal commands. They are Effective, and the Authority checks deny.
4. SpiceDB is stopped, the datastore is restored from the dump (`pg_restore --clean`), and SpiceDB is restarted.
5. The Authority's checks now return **`allowed: true`** for the revoked auditor (`view_audit`) **and** the revoked designer (`create_form`), with `authorization_revision = projection_revision = 4` and gate `Ready`. Nothing signals the regression.

**Cause (from the code, §2.1):**
- The gate trusts only AuthorityDb (`projection_applied_revision`, the stored ZedToken).
- Checks run `AtLeastAsFresh` against the Authority-held ZedToken. SpiceDB's Postgres datastore marks deletions with a deleted-transaction id, so restored pre-revoke rows are live again and are visible at that token.
- The `projection_cursor` marker, which the restore also rolled back, is compared only by the projector's next write, never by a read.

**Scope (assessment):**
- **General, not specific to M4.** Every permission resolved through SpiceDB is exposed to the same rollback:
  * record discover, read, edit and share (record and share scopes, AC-5 area);
  * field items and form `design`, `create_record` and `query_fields` (AC-6 field search);
  * organization roles (AC-7, C-70, AC-8.1);
  * team-derived access (team membership edges; AC-9's outcome lookup reads AuthorityDb and is not itself affected).
- **Pre-existing** since Stage A. M4-1 did not introduce it.
- **Unknown:** how long the stale window lasts. Presumably until the next projection write for the organization fails its cursor compare-and-swap; that is not tested.

**Threat and severity:**
- A BusinessDb administrator cannot do this. `spicedb-db` is on the internal Authority network, inside the Authority's independently administered domain.
- The realistic causes are an operational restore of SpiceDB's datastore (a backup, a failover to a lagging replica, or a recovery mistake) or a compromised Authority-domain database.
- It breaks the recovery invariants of `COORDINATED_DISASTER_RECOVERY.md` (§4R items 1 and 5: never recover authorization from an old snapshot; invalidate ZedTokens after recovery) **silently**.
- **Severity: High** for recovery and production readiness. It is not exploitable from the business side.

**Remediation candidates (for a separate approval; nothing implemented):**

| ID | Change | Effect | Cost and limits |
|---|---|---|---|
| **S1 (recommended now)** | Before answering any check (single and batch), the Authority reads SpiceDB's `projection_cursor` marker fully consistently and requires the marker step to equal `projection_applied_revision`. On mismatch it fails closed (`AUTHORITY_NOT_READY`), sets integrity quarantine for the organization, and raises a security alert. | Detects a restored datastore whose marker is older than the Authority's accepted revision, which covers T16-S. | One extra SpiceDB read per check or batch; PB-09 latency to be measured. It cannot detect an attacker inside the Authority domain who also rewrites the marker (outside the Level 2 trust assumption). |
| S2 | The same comparison on SpiceDB reconnect or restart and periodically | Cheaper | Leaves a window between probes; complementary to S1, not a substitute. |
| S3 | An approved rebuild: after any SpiceDB restore, rebuild the organization's tuples from AuthorityDb desired state under the recovery control plane (new marker, new ZedToken, two-person approval) | Restores availability correctly | Part of G14 (T16-A); not a detection control. |

T16-S becomes the regression test for S1. The same test with an AC-7 designer revocation also covers AC-7's T16. After S1, the expected T16-S outcome is **denied, and the organization quarantined until an approved rebuild (S3)**.

## 3. RCF-1: rejected security commands keep the raw request (separate finding)

### 3.1 Finding

- `SecurityCommandService.SubmitAsync` creates every command with `CommandPayloadJson = request.CommandPayload.GetRawText()` and `CommandReasonText = request.ChangeReason`.
- On success the payload is replaced by the plan's normalized payload, which drops step-up evidence.
- **On rejection the raw body stays on the rejected `security_commands` row.** M4-1 sanitizes only `GrantSystemRole`/`RevokeSystemRole`.
- **Step-up-bearing kinds still affected:** `ApproveFieldSearch`, `GrantOrganizationRole`/`RevokeOrganizationRole`, `ProposeOrganizationOwnerChange`, `DisableIdentityBinding`/`EnableIdentityBinding` and the three membership-request kinds.
- **Other content on those rows:** free-text reasons, membership-request identity data (subject identifiers, display-name hints) and arbitrary caller-supplied JSON (bounded only by request limits).

### 3.2 Exposure

- **Where it is:**
  * AuthorityDb `security_commands` and its backups only, administered separately from BusinessDb;
  * not returned by any API (`OperationStatus` has no payload; AC-9 reads only team pair identifiers);
  * not journaled or archived (rejection audit entries carry fixed fields).
- **Who can read it:** AuthorityDb administrators, the Authority runtime, and anyone holding an AuthorityDb backup.
- **What the step-up evidence is today:** the synthetic Development/Testing marker, a constant with no secret value. In staging and production the step-up gate is unavailable and step-up commands are refused, so no real evidence exists anywhere.

### 3.3 Severity

- **Current: Low.** There is no real credential material; free text and identity data of the same kinds are already retained intentionally on *committed* commands.
- **Latent: High if unaddressed before PB-08.** If real step-up proofs are replayable bearer artefacts, every refused command would leave one in a long-lived, backed-up store.
- **Secondary concerns:** rejected rows let any caller that can reach the Authority write arbitrary JSON into the trusted store (bounded storage abuse), and caller-supplied text is kept without validation.

### 3.4 Containment and remediation (proposed; not part of M4-1)

1. **Containment, now:** record RCF-1 in the security gap register and make it a **PB-08 entry requirement**: no step-up evidence may be persisted anywhere, rejected rows included.
2. **Remediation (separate work item, D8-75):** generalize the M4-1 pattern.
   * On every rejection, store only the canonical request digest (already kept), the kind, the resource and kind-specific validated identifiers.
   * Never store `step_up_evidence`, free text or unvalidated values.
   * Cap the stored size.
3. **Migration:** a reviewed AuthorityDb migration that redacts `step_up_evidence` (and free text) from existing rejected rows, after confirming nothing reads them. Today only `ProtectionCommandResolver`/`CommandPlanner` read committed publication and declassification payloads, and AC-9 reads team pairs.
4. **Tests:**
   * a parameterized test over every kind accepting step-up: a rejected row contains no step-up marker or free text;
   * idempotent replay of a rejected command still yields `IDEMPOTENCY_CONFLICT` or the same status, since the digest is unchanged;
   * migration test on a seeded database.

## 4. Independent review package addendum: AC-8.1

To be added to the AC-5/AC-6/AC-7/AC-9 package. The reviewer is asked to confirm or reject each item.

| Area | Implementation | Evidence |
|---|---|---|
| Relation and permission | `organization#security_auditor`; `view_audit = security_auditor`; `RequestedAction.ViewAudit` | `authority.schema.zed`; `PermissionCheckService.MapCheck`; Stage B T01 (view_audit allowed; administer_security, manage_configuration and create_form denied) |
| Appointment and removal (A1–A9) | `CommandPlanner.SecurityAdministrators.cs` (`SecurityAuditorAsync`) | Authority unit `SecurityAuditorTests` (10); Stage B `SecurityAuditorTests` (7) |
| D8-62a self-change refusal (both roles; changes C-70) | `RefuseSelfRoleChange` | Unit `SecurityAdministratorTests`, Stage B `SecurityAdministratorTests.RemovalTakesEffectImmediatelyAndNobodyRemovesThemself`, T02/T14 |
| Reason codes (exact, direction-specific) | `SecurityAuditorReasonCodes` | Unit and Stage B (case variants, wrong direction, `Other`, mixed payload shapes) |
| Rejection evidence | `RoleCommandEvidence`, `SecurityAuditorCommandRejected` | Unit (field list exact; sentinels absent), Stage B (audit stream and rejected rows) |
| Business never stores the note | `SecurityAuditorsHandlers` | Stage B sentinel over `audit_events` |
| Revocation and disabled binding | existing pipeline, `ActorResolver` | Stage B T06, T13 |
| Forgery and cross-organization | — | Stage B T04, T07 |

- **Full results:** `docs/PHASE_C8_STATUS.md` §34 (shards 69/69 and 55/55 on fresh stacks, .NET 954/954, Vitest 450/450, Playwright 8/8).
- **The reviewer must explicitly address:**
  * **RN-1** (§1, note confidentiality): is option R1 acceptable as reuse of the approved envelope, or does it need the R2-style new-mechanism review?
  * **RCF-1** (§3): severity, and whether it must be fixed before any further Authority command work.
  * **T16** (§2): is the T16-B/T16-S/T16-A decomposition sound, and does T16-S reveal a gate defect?
  * **F-M4-1a:** the last-administrator rule is now unreachable through commands; is keeping it as defence in depth correct?
  * **F-M4-1b:** M4-1 changed what rejected role-command rows store. Is that storage-only change acceptable?
- **Not claimed:** independent approval, the recovery invariant (T16), note confidentiality, production readiness or Azure staging evidence.

## 5. Owner decisions requested

| ID | Decision | Recommendation |
|---|---|---|
| D8-71 | RN-1 remediation option | **R1** (business-sealed envelope with existing components); R0 as fallback |
| D8-72 | Interim containment for the note until RN-1 is implemented | Authority refuses any non-null note (a small change, needing authorization) |
| D8-73 | Who may decrypt notes | Nobody in-app for now (write-only); any read path is a separate reviewed design |
| D8-74 | T16 scope for M4 closure | T16-B and T16-S before M4 closure; T16-A under G14 before production |
| D8-75 | RCF-1 | Recorded now as a PB-08 entry requirement; generalized sanitization and redaction migration as a separate authorized work item |

### 5.1 Decided in C-113, and new decisions requested (C-114)

D8-71…D8-75 were decided in C-113. New requests:

| ID | Decision | Recommendation |
|---|---|---|
| D8-76 | R1 minimal design change (§1.5): M1, a separate `AesGcmAuditorNoteSealer` with its own AAD domain and seal-only port, the identical primitive and key provider, existing services unmodified; or M2, slot mapping in the existing field service | **M1** |
| D8-77 | T16S-1 remediation design (§2.4): S1 check-time cursor verification with quarantine and alert, now; S2 probe as a complement; S3 rebuild under G14 | **S1 now** (+ S2), S3 with G14; the T16-S test is the regression test |
| D8-78 | Status of the failing T16-S test until S1 is approved and implemented: keep it failing in the suite (honest red) or mark it as a known-defect skip with this reference | **Keep it failing**; the shard reports it explicitly |
