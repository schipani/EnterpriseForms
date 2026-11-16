# E3-M2f — Owner Decisions and Contract Closure (Business Envelope Capability Floor)

**Status (2026-11-16):**

| Item | State |
|---|---|
| This document | Owner decision record and contract closure for E3-M2f (increment **E3-M2f-0**). **Documentation only.** It changes no code, test, migration, configuration, database or infrastructure. |
| E3-M2f implementation | **Not authorized.** E3-M2f-1, E3-M2f-2, E3-M2f-2G and E3-M2f-3 (§12) each need a separate owner authorization. |
| Envelope v2 acceptance | **Not activated anywhere.** Nothing here changes `E3_M2E_CONTRACT_CLOSURE.md` §4.3 or OA-M2e2-06. |
| Baseline | Branch `e3/m1-envelope-v2-core`, HEAD `0be5f522456acfa2102cf3e709ee9de1774a3d3a` (E3-M2e-2, signed), clean tree |
| Review | Owner-led. Not an independent security review and not a certification. V2-IND-01 is still required before production. |

**Inputs:** the E3-M2f readiness review (owner review, 2026-11-16); `E3_M2_AUTHORITY_DATABASE_CONTRACT.md` (the package) §7, §8, §10, §11, §12; `E3_M2B_IMPLEMENTATION_CONTRACT.md` §3, §10, §14, §15, §19–§22; `E3_M2E_CONTRACT_CLOSURE.md` (OD-M2e-01..16, AM-1..12, L-1..14, BD-1, BD-2); `E3_M2C_IMPLEMENTATION_RECORD.md` (header deferred to M2f); `E3_ENVELOPE_V2_KEY_HIERARCHY_DESIGN.md` §4, §6, §9.

**Precedence:**
1. This record governs E3-M2f. It supersedes the package §7.2, §7.3, §10 "E3-M2f" and §11 M2-FEN rows, and the M2b contract §10 and §15 "E3-M2f" rows, **only where it says so explicitly**. Dated inline notes at those places point here.
2. It amends `E3_M2E_CONTRACT_CLOSURE.md` in exactly one place: L-10 (AM-F1, §5). Every other E3-M2e decision, amendment and requirement is unchanged and binding.
3. Where this record is silent, the M2e closure, then the M2b contract, then the package govern, as before.

**Notation:**

| Tag | Meaning |
|---|---|
| [IMPLEMENTED] | Present in source at the baseline and owner-accepted for Development/Testing (not production evidence) |
| [DECIDED] | Approved by the owner on 2026-11-16 for E3-M2f, recorded here |
| [SPECIFIED] | Implementation detail specified by this record; binding on the named increment once that increment is authorized; not yet authorized |
| [DEFERRED] | Required, but its design is a separate later owner decision |
| [PREREQUISITE] | Must hold before any persistent use (closure §4.3 classes 2 and 3) |
| [OPEN] | Unresolved; blocks what is named |

**Identifiers introduced:** owner decisions OD-M2f-01 to OD-M2f-10; amendment AM-F1; requirements CAP-S1..S8 (signal), CAP-F1..F7 (floor), CAP-O1..O7 (ordering), CAP-E1..E4 (error), RE-1..RE-6 (refusal evidence), INV-1..INV-5 (inventory); invariant FB-1; findings G-1, G-2; risks R-M2f-1..R-M2f-10; tests M2F-01..M2F-40; increments E3-M2f-1, -2, -2G, -3.

---

## 1. Owner decisions [DECIDED]

| ID | Decision | Basis | Consequence |
|---|---|---|---|
| **OD-M2f-01** | **Scope.** E3-M2f covers: business-build envelope capability signalling; a per-organization minimum business capability; Authority enforcement before incompatible content verification; integration with the existing CARV / floor / Release B mechanisms; operational documentation and deployment prerequisites. | Readiness review §3 | **Outside E3-M2f:** organization cryptographic protection profiles (E-9); provider routing, the encryption-key catalog and provider-family restrictions (E3-M3); organization entitlements and any unrelated authorization functionality. Envelope version eligibility stays with the E3-M2e switch and E3-M4 `MinimumWriteEnvelopeVersion`. No M2f mechanism may act as a runtime toggle over an E-9 profile. |
| **OD-M2f-02** | **OD-M2-07 = F0.** Reuse CARV 3, floors 1–3 and the Release B transactional fence as the Authority rollback fence. No CARV 4, no floor 4, no new database trigger for M2f. | Package §7.2; M2b §10 | Rationale and residuals in §2. The package §7.2 trigger (F2) and F1 are not built. |
| **OD-M2f-03** | **OD-M2-08 = per organization.** Effective floor = `max(deployment capability floor, organization capability floor)`. The organization floor is 2 when any data-key binding exists for that organization, in any binding or operation state. The existence check runs under the organization lock, before verification. | Package §7.3 | §4; first-binding invariant FB-1 |
| **OD-M2f-04** | **Narrow L-10 amendment (AM-F1).** One organization-level binding-existence query, solely for capability enforcement. | Closure L-10; readiness conflict C-M2f-1 | §5. The analysis there concludes the exception preserves the security properties L-10 protects. |
| **OD-M2f-05** | **Mixed Authority fleet: deployment-inventory enforcement**, not CARV 4. Before any persistent use, verified proof is required that every Authority instance serving an affected organization enforces M2f. OA-FE1-2 remains a separate prerequisite for persistent floor raises. | Readiness conflict C-M2f-2 | §10 |
| **OD-M2f-06** | **Client coverage:** `AuthorityHttpClient`, `ProtectionVerifierHttpClient`, `SearchIndexBuilderHttpClient`. | Readiness finding G-1 | §3, §6 |
| **OD-M2f-07** | **Signal:** the contract-defined HTTP header `EnterpriseForms-Envelope-Capability`. **No workload-token claim in M2f.** The PB-08 workload-identity consideration is recorded (CAP-S8). | Package §7.3 | §3 |
| **OD-M2f-08** | **Enforcement ordering:** one shared Authority capability check in the transaction, after the organization lock, before any signed-content verification and before any step that can quarantine content or journal an integrity incident because of an unsupported client representation. It never bypasses CARV, floors, ledger protections, authorization or integrity checks. | Readiness finding G-2 | §6 |
| **OD-M2f-09** | **Refusal evidence:** durable, bounded refusal evidence is **required**. No throttling algorithm, table or journal schema is approved. The design is a separate implementation decision (E3-M2f-3) under R-CARV-12 / UQ-4. | Package §7.3 | §8 |
| **OD-M2f-10** | **Deployment floor governance:** PB-11-protected configuration and controlled deployment procedures as the initial direction. Configuration alone does not guarantee monotonicity across rollback or restore. No policy-state table without further owner approval. This does not close BD-1 and does not authorize persistent acceptance. | Package §8 | §9 |

---

## 2. F0: why the existing floors satisfy the rollback-fence requirement

**What the package fence was for (§7.1, §7.2).** An Authority build older than the v2 rules must not prepare or commit content in an organization that has Envelope v2 data, because it would bypass the v2 validation and the ledger.

**What already enforces that [IMPLEMENTED]:**

| Property | Mechanism | Source |
|---|---|---|
| A binding can only be created by a CARV ≥ 3 Prepare | P-1 binding guard: the referenced operation is `Prepared` with `prepare_acceptance_rule_version >= 3` | Closure L-6; ledger migration `20261116031706_AuthorityContentDataKeyBindings` |
| A New v2 group is accepted only where floor ≥ 3 is verified in the same transaction | AM-5: acceptance requires Release B `RequiredFloor = 3`; the Release B fence runs first in every Prepare and Commit | Closure AM-5; M2b §22; `ContentService.cs:88`, `:331` |
| Once floor 3 exists, no CARV ≤ 2 build can prepare in **any** organization | `ck_content_operation_rule_floor_v3` refuses every `Prepared` insert with P < 3 | Floor migration `20261116040000`; M2e-1 record §7 (E-35, E-36) |
| No operation is committed under rules other than those it was prepared with | Commit requires P = current version; the database requires C = P | M2b §19.4–§19.5 |
| A pre-v2 Authority reading v2 content does not misjudge it | Package F-04: a pre-M2 Authority verifies schema-2 rows correctly on read and refuses candidates that contain them | Package §1.5 |

**Why F2 adds no material protection.** F2 (a per-organization trigger refusing low-version Prepares where bindings exist) would only act when floor 3 is absent while bindings exist. Bindings can only be created in transactions that verified floor ≥ 3. So that state arises only if the floor is later removed (privileged DDL, or a restore of AuthorityDb to a point before the floor while the ledger survives). In both cases the same actor or restore also controls or removes the F2 trigger. F1 is subsumed by the CARV floor. Neither justifies a new migration, which would also expose the ordinary `--migrate` path to F-E2 and R-A1-3.

**Residual limitations (not closed by F0):**

| ID | Limitation | Status |
|---|---|---|
| R-M2f-1 | **Release A and legacy builds are not bound by the fence when a floor is missing** (R-CARV-21). After a restore below floor 3, a Release B build refuses writes, but a build without the Release B fence would write. | Open; persistent use requires Release B `RequiredFloor = 3` on every instance (closure §5.1 step 6) and the inventory (§10) |
| R-M2f-2 | **Privileged DDL** can drop floors or CHECKs (R-CARV-24, G08) | Outside the Level 2 model for the runtime; G08 monitoring |
| R-M2f-3 | **Floor 3 does not prove M2f enforcement.** M2f does not bump CARV, so a CARV-3 build without M2f (for example the baseline `0be5f52`) is admitted by floor 3 and does not enforce the capability floor | Handled by OD-M2f-05 inventory (§10) |
| R-M2f-4 | **Restore of AuthorityDb** reverts the ledger, so the organization capability floor can regress (§4.4); BD-1 is unchanged | Open (BD-1) |
| — | Azure PostgreSQL renderings of floor and ledger definitions (R-CARV-18/23) | Open; production |

---

## 3. Capability signal contract

| ID | Requirement | Tag |
|---|---|---|
| CAP-S1 | Header name `EnterpriseForms-Envelope-Capability` (one constant per side; the business constant and the Authority constant are cross-checked by an architecture or contract test). | [DECIDED] name; [SPECIFIED] constants |
| CAP-S2 | **Clients:** `AuthorityHttpClient`, `ProtectionVerifierHttpClient`, `SearchIndexBuilderHttpClient` send the header on **every** request to the Authority, not only on content calls (one rule, no per-route list on the client side). `TeamMembershipReconcilerHttpClient` is not covered: it submits no content (security-command outcome lookup only). | [DECIDED] |
| CAP-S3 | **Value grammar:** exactly one header instance whose value is exactly the ASCII string `1` or `2` (no whitespace, sign, leading zero or list). | [SPECIFIED] |
| CAP-S4 | **Levels.** `1`: the build cannot represent signed schema 2 and Envelope v2 faithfully. `2`: the build encodes signed schema 2 with fidelity (E3-M2b-2 S-1/S-2/S-3), opens Envelope v2 through the verified-read path (E3-M2c) and emits this header (E3-M2f-1). Level 2 says nothing about Envelope v2 **writing** (E3-M4). | [SPECIFIED] |
| CAP-S5 | **Missing header ⇒ level 1.** This is how every build before E3-M2f-1 is classified, **including E3-M2c to E3-M2e-2 builds**, which have the readers but do not signal. Consequence: for an organization with any binding, the minimum business build is the E3-M2f-1 build, not E3-M2c (supersedes package §7.1 "Minimum safe business reader = E3-M2c"). | [DECIDED] semantics |
| CAP-S6 | **Unknown or malformed value ⇒ level 1** (empty, repeated header, `02`, ` 2`, `3`, non-digits). Never higher. A future level is introduced Authority-first: the Authority must recognize a value before any business build sends it. | [SPECIFIED] |
| CAP-S7 | **Trust.** The header is asserted by the authenticated workload; it is not separately signed. It protects against **honest older builds** only. A compromised business runtime can send `2`; it already holds wrap/unwrap (A3), so this is stated, not hidden (package §7.3). The header is **not** part of the Prepare request digest (`CanonicalJson.Digest` over the body) or of any idempotency identity; it is never echoed, and logs carry at most the parsed level. | [DECIDED] |
| CAP-S8 | **PB-08 consideration.** When workload identity moves to Entra / mTLS, the signal remains a header asserted by an authenticated workload. A future change to a token claim, or binding the level to an attested image digest, is a separate decision; nothing in M2f precludes it. | [DECIDED] recorded |

---

## 4. Capability floor

### 4.1 Evaluation

| ID | Requirement | Tag |
|---|---|---|
| CAP-F1 | `C` = client level (CAP-S3..S6). `D` = deployment capability floor (§9), values {1, 2}, default 1. `O` = organization capability floor: 2 if any row of `authority.content_data_key_bindings` has this `organization_id`, else 1. **Effective floor `E = max(D, O)`.** Refuse when `C < E`. | [DECIDED] |
| CAP-F2 | "Any binding" means any row, regardless of the state of its operation (Prepared, Committed, Aborted, expired). Reservations are permanent (OD-M2e-03), so `O` is monotonic in the live ledger. | [DECIDED] |
| CAP-F3 | **Short-circuit, mandatory.** If `C < D`: refuse without querying the ledger. If `C ≥ max(D, 2)`: admit without querying the ledger (2 is the highest organization floor). Only when `D ≤ C < 2` (today: `C = D = 1`) is the existence query executed. Level-2 traffic therefore never touches the ledger. | [SPECIFIED] |
| CAP-F4 | The existence query runs **in the entry point's transaction, while that transaction holds the organization row lock** (`LockOrganizationAsync`, `SELECT … FOR UPDATE`). | [DECIDED] |
| CAP-F5 | A query error (missing table, permission, timeout) **refuses** (fail closed, `503 AUTHORITY_NOT_READY`-class), never admits. | [SPECIFIED] |
| CAP-F6 | The capability decision can only **refuse**. It never admits a request that any later check would refuse, and it changes no state. | [DECIDED] |
| CAP-F7 | No per-organization, per-form, per-record, per-user or per-request setting can lower `E`. No business API, organization setting, form JSON, user role or operator command changes `O`. | [DECIDED] |

### 4.2 First-binding concurrency invariant (FB-1)

**FB-1.** For any organization, every capability decision is serialized with every binding insert, so a decision that observes "no binding" is never followed, inside its own transaction, by content that depends on a binding.

Argument:
1. Bindings are inserted only by DK-5, in the Prepare transaction, after `LockOrganizationAsync` (closure L-5; M2e-1 record §4.1). The row lock is held until that transaction commits or rolls back.
2. The capability check runs after `LockOrganizationAsync` in its own transaction (CAP-F4). Two transactions of the same organization therefore cannot interleave between lock and commit.
3. Hence either the check's transaction completes before the first binding's transaction acquires the lock (the check sees no binding), or it starts after that transaction committed (it sees the binding). A rolled-back Prepare leaves no binding and no content.
4. Schema-2 content can only reach BusinessDb staging after its Prepare committed (the business stages after Prepare returns), and can only become an accepted head after a Commit, which is itself a later locked transaction. So a transaction that saw "no binding" cannot be handling legitimate schema-2 content of that organization. Schema-2 rows that exist without a binding are forged (A1) and are rightly treated as tamper evidence.

**Obligation on BD-2.** Any future recovery-plane path that re-inserts bindings must take the same organization lock (or an equivalent exclusion), or FB-1 no longer holds. Recorded as a BD-2 design input.

### 4.3 Where the floor is not consulted

Endpoints that carry no signed content bytes are not enforced: content head and history reads, search catalogue and indexed search, sessions, security commands, the verifier catalogue / attestation / coverage calls, the builder work / population / attestation / deep-sweep-work calls, and the reconciler lookup. The header is still sent on them (CAP-S2).

### 4.4 Restore behaviour (stated, not solved)

An AuthorityDb restore to a point before an organization's first binding makes `O = 1` again while BusinessDb may still hold that organization's schema-2 rows. A level-1 build is then admitted and can reproduce G-2 (§6.3). The deployment floor `D = 2` is the only restore-independent backstop, and it is configuration (§9). This is R-M2f-4, part of the BD-1 problem; M2f does not close it.

---

## 5. AM-F1 — narrow amendment of closure L-10

**Original L-10:** "Reads, verification, search and history never consult the ledger."

**Amended L-10 (AM-F1):** Reads, verification, search and history never consult the ledger, **with one exception**: the capability check of §4 may execute **one organization-level binding-existence query** per evaluation, under these conditions:

| # | Condition |
|---|---|
| 1 | **Shape.** The query is `EXISTS (SELECT 1 FROM authority.content_data_key_bindings WHERE organization_id = @organization)` through one new store-port method returning a **boolean only**. No binding column (identifier, record, operation, kind, commitment, count, time) is materialized, returned or logged. |
| 2 | **Sole caller.** The method is called only by the shared capability check. Architecture tests pin this (no other caller in Application, Api or Infrastructure). |
| 3 | **Allowed call sites.** The shared check, invoked from exactly the seven entry points of §6.1. |
| 4 | **Short-circuit.** Executed only when CAP-F3 requires it (`D ≤ C < 2`). |
| 5 | **Transaction.** Inside the entry point's transaction, under the organization lock (CAP-F4). |
| 6 | **Use.** The boolean feeds only the refusal decision. It is never used to admit, authorize, verify, select, count, rank, page or shape any result; never cached as "no binding" beyond the transaction (a cached "has binding" is monotonic and permitted); never exposed to the caller except through the fixed refusal code (§7). |
| 7 | **Failure.** Any error refuses (CAP-F5). |
| 8 | **Authorization boundary.** The query runs with the Authority runtime role, which already has `SELECT` on the ledger (closure L-8). No grant changes. The caller learns only what the refusal itself conveys (R-M2f-5). |
| 9 | **Not affected:** verification logic, search results, history, census, DK-4..DK-8, P-1, P-2, P-4, L-13. L-13 is **not** required before the existence query: it guards transactions that insert bindings or run P-4; the existence query neither inserts nor relies on uniqueness or immutability, and its only failure mode under a damaged ledger is refusal (or, after rows are removed by a privileged actor or a restore, regression covered by R-M2f-2 and R-M2f-4). |

**Required tests (in E3-M2f-2):** M2F-20..M2F-24 (§11).

**Security assessment.** L-10 exists so that the correctness and outcome of reads, verification, search and history never depend on the ledger. Under AM-F1:
- No read, verification, search or history result can change because of the ledger, except that a level-1 client is refused.
- The ledger can never cause content to be released, admitted or trusted.
- Ledger unavailability affects only level-1 clients (level-2 traffic never queries it, CAP-F3).
- No binding detail leaves the store.

The amendment therefore preserves the protected properties. If an implementation cannot keep all nine conditions, E3-M2f-2 must stop and report (§12.2) rather than widen the exception.

---

## 6. Enforcement ordering and coverage

### 6.1 Entry points (all seven submit signed content bytes)

| # | Entry point | Caller credential | Position of the capability check |
|---|---|---|---|
| 1 | `ContentService.PrepareCoreAsync` (`content-changes/prepare`) | business-api (user or reprotection) | Immediately after the Release B fence (`ContentService.cs:88`) |
| 2 | `ContentService.CommitCoreAsync` (`content-changes/{id}/commit`) | business-api | Immediately after the Release B fence (`:331`) |
| 3 | `ContentService.VerifyProjectionAsync` (`integrity/verify-record-projection`) | business-api | Immediately after `LockOrganizationAsync` (`:780`) |
| 4 | `ContentService.VerifyProjectionsAsync` (`integrity/verify-record-projections`) | business-api | Immediately after `LockOrganizationAsync` (`:842`) |
| 5 | `ProtectionReconciliationService.CensusAsync` (`verifier/migration-catalogue/{id}/census`) | field-protection-verifier | Immediately after `LockOrganizationAsync` (`ProtectionReconciliationService.cs:60`) |
| 6 | `SearchIndexBuilderService.BackfillAsync` (`search-index-builder/backfill`) | search-index-builder | Immediately after `LockOrganizationAsync` (`SearchIndexBuilderService.cs:120`) |
| 7 | `SearchIndexBuilderService.DeepVerifyAsync` (`search-index-builder/deep-sweep/verify`) | search-index-builder | Immediately after `LockOrganizationAsync` (`SearchIndexBuilderService.cs:331`) |

Line numbers are at the baseline and indicative only. The binding requirement is the order of CAP-O1..O7.

### 6.2 Ordering requirements

| ID | Requirement |
|---|---|
| CAP-O1 | Within the entry point's transaction. |
| CAP-O2 | After the organization lock; after the Release B fence where the entry point has one (the fence stays first, RB-CARV-1 §22). |
| CAP-O3 | Before actor resolution, the organization gate, idempotent replay (Prepare and Commit replays included), the protection barrier, snapshot binding (`BindAcceptedSnapshotAsync`), `PinAsync`, `VerifyStagedAgainstLeavesAsync`, `VerifyRecordCoreAsync`, `VerifyWorkloadObservedContentAsync` and every census or index computation. |
| CAP-O4 | Before any step that can quarantine a head, advance the content-integrity revision, or journal `IntegrityVerificationFailed` or `CurrentSnapshotMismatch`. |
| CAP-O5 | Does not replace or reorder any existing check relative to the others. For level-2 callers, the refusal precedence of every existing check is unchanged. For refused level-1 callers, the capability refusal precedes the existing refusals (organization gate, authorization, replay). |
| CAP-O6 | Writes nothing in E3-M2f-2 (no journal, no operation state change, no abort). Durable refusal evidence is E3-M2f-3 (§8). |
| CAP-O7 | One shared implementation (one Application component); the seven entry points call it, and an architecture test pins the call at the specified position in each. |

**Why before actor resolution (CAP-O3).** A single rule ("first call after lock/fence") is simpler to pin by architecture test and guarantees no quarantine-capable step precedes it. The cost is R-M2f-5: an authenticated level-1 workload learns, by refusal, that an organization has at least one binding before membership is resolved. This is an organization-level fact, visible only to callers that are themselves honest older builds of an authenticated workload, and it is accepted.

### 6.3 G-2: false quarantine by an honest older reader (the reason for CAP-O3/O4)

**Mechanism.** Business builds before E3-M2b-2 encode `field_encrypted_values` and `file_attachment_versions` with signed schema 1 regardless of the stored schema (`BusinessSignedSchemas.Encode`, verified at `66f0aa7`). For a schema-2 row they submit canonical bytes whose digest differs from the accepted leaf.

**Consequences without the capability check:**

| Entry point | Effect |
|---|---|
| Verify / batch Verify (`VerifyRecordCoreAsync`) | Treated as tampering: the head is **quarantined**, `IntegrityVerificationFailed` is journaled, and the content-integrity revision advances. Release needs privileged reconciliation (G14). |
| Prepare (`BindAcceptedSnapshotAsync`) | `CurrentSnapshotMismatch` is **journaled** and the content-integrity revision advances (W4) |
| Census, backfill, deep-sweep verify (`VerifyWorkloadObservedContentAsync`) | Head quarantined and incident journaled, naming the workload |

E3-M2c to E3-M2e-2 builds encode schema 2 correctly, but they are level 1 by CAP-S5 and are refused anyway.

**Required negative tests** (E3-M2f-2 in-process; E3-M2f-2G on real PostgreSQL): for each of the seven entry points, a level-1 request in an organization with a binding and a schema-2 head is refused with `CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`, and afterwards:
- the head is **not** quarantined;
- no `IntegrityVerificationFailed` or `CurrentSnapshotMismatch` entry exists;
- the content-integrity revision is unchanged;
- no operation row is created or changed.

These are M2F-10..M2F-16. A demonstration on the baseline Authority (no M2f) that the same request **does** quarantine is recorded as `EXPECTED_GAP_G2` in E3-M2f-2G.

### 6.4 What the check must never do

It must not:
- bypass, precede or replace the Release B fence;
- alter CARV stamps or floors;
- read binding details;
- substitute for authorization, the organization gate, quarantine checks, HMAC or Merkle verification, DK rules, P-4, S-4, S-9 or the downgrade guard;
- admit anything.

---

## 7. Error contract [SPECIFIED]

| ID | Requirement |
|---|---|
| CAP-E1 | `409 CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`, `retryable = false`, fixed detail text with no identifiers, levels or binding information. Added to the private Authority error codes and documented in the internal Authority OpenAPI (header parameter and response). |
| CAP-E2 | The same response for "below the deployment floor" and "below the organization floor" (no distinguishing signal). |
| CAP-E3 | **Business mapping.** Level-2 builds recognize the code as non-retryable. It must not be added to the retryable or fresh-operation sets (`IsAbandonedOperation`, `IsUnusableOperationKey`, `IsRetryableConflict`, `FieldProtectionMigrationService` retryable codes), so no retry or new-Prepare loop starts. It surfaces to the user as a service-unavailable condition without detail. Older builds see an unrecognized 409 (their existing behaviour). |
| CAP-E4 | Operational signal in E3-M2f-2: a structured log event and metric carrying the outcome, the parsed level and the organization identifier only (no record, user or binding data). This is **not** the durable refusal evidence required by OD-M2f-09. |

---

## 8. Refusal evidence [DEFERRED; required]

| ID | Requirement (approved) |
|---|---|
| RE-1 | Each refusal class (per organization and calling workload) leaves **durable** evidence that survives the refused request. |
| RE-2 | **Bounded:** a misbehaving or straggling client cannot cause unbounded durable writes (journal, archive or table growth proportional to request rate). |
| RE-3 | No identifiers beyond the organization and the workload name; no record, user, binding or content data. |
| RE-4 | Must not reorder or weaken CAP-O1..O7: the capability decision stays first and writes nothing in the refused transaction. |
| RE-5 | Must not depend on the refused transaction committing. |
| RE-6 | Interaction with R-CARV-12 and UQ-4 (audit stream versus operational alert) is decided together. |

**Unresolved design (to be returned as a separate owner decision before E3-M2f-3).** The open questions are:
- **Where the evidence lives:** the platform journal, an organization audit stream, or an independent alert sink. The platform journal (the precedent of `EnvelopeAcceptancePolicyObserved`) lives in AuthorityDb and is rolled back by a restore (O-5, M2e-2 record).
- **How it survives the refusal:** a separate short transaction after the refused one rolls back, taking the organization lock again or not, with the lock-order implications for A-1.
- **How it is bounded:**
  - a deduplication window keyed by (organization, workload, interval), which needs either state (an index or table, and so owner approval under OD-M2f-10's no-new-table rule) or a bounded journal scan (the 64-entry precedent, which can only over-record);
  - or rate-limited emission to an external sink.
- **Accounting for suppressed repeats:** counters, and where they are kept durably.

None of these is approved. Until E3-M2f-3 is implemented and accepted, RE-1..RE-6 are unmet, and persistent use is not permitted (§11.5).

---

## 9. Deployment capability floor governance

| Item | Content | Tag |
|---|---|---|
| Key | `Content:MinimumBusinessEnvelopeCapability` (Authority configuration) | [SPECIFIED] |
| Values | Absent ⇒ 1. Present ⇒ exactly `1` or `2`; anything else refuses Authority startup (fail closed). | [SPECIFIED] |
| Visibility | The effective value is reported in readiness and the startup log (no identifiers). | [SPECIFIED] |
| Protection | PB-11-protected deployment configuration, changed only through controlled deployment procedures. Raising it is a tightening (one approver); lowering it is a loosening (two distinct approvers outside Development/Testing, package §8). | [DECIDED] direction |
| Monotonicity | **Not mechanically guaranteed.** A configuration rollback, a redeployment with older settings, or an operator error can lower it; an AuthorityDb restore does not affect it (it is outside the database), but it is also not anchored anywhere the Authority can check. | [DECIDED] stated |
| State | No policy-state table and no journal record of the floor without further owner approval (OD-M2f-10). | [DECIDED] |
| Limits | It does not close BD-1, does not authorize persistent acceptance, and does not replace the organization floor. | [DECIDED] |

---

## 10. Mixed Authority fleet: deployment inventory

| ID | Requirement | Tag |
|---|---|---|
| INV-1 | Before any persistent use (closure §4.3 classes 2 and 3), a **verified deployment inventory** shows that every Authority instance able to serve an affected organization runs an E3-M2f-2-capable build. | [PREREQUISITE] |
| INV-2 | Evidence is the deployed image digest (PB-11) mapped to a source commit that contains E3-M2f-2, recorded with the deployment. Self-reported runtime signals are supporting evidence only. | [PREREQUISITE] |
| INV-3 | **CARV markers are insufficient:** M2f does not bump CARV, so `carv-v3` is reported by both the baseline `0be5f52` build (no capability check) and an M2f build. Floor 3 admits both (R-M2f-3). | [DECIDED] stated |
| INV-4 | E3-M2f-2 may add an M2f indicator to readiness (for example `capability floor enforced; deployment floor N`) to help operators. It is not proof (INV-2). | [SPECIFIED] optional |
| INV-5 | OA-FE1-2 (an A-1-capable inventory before any persistent floor raise) is **separate** and unchanged. Both inventories are required where both apply. | [DECIDED] |

**Business fleet.** No safety inventory is required for business builds: the floor refuses level-1 stragglers. Draining them is an availability matter. Before an organization's first binding in a persistent environment, all business API and Worker instances should run E3-M2f-1 or later, or they will be refused for that organization.

---

## 11. Consolidated status

### 11.1 Already implemented CARV / floor / ledger protections [IMPLEMENTED; Development/Testing]

- CARV stamp, commit equality, state guard (M2b-1).
- Floors 1–3 in the floor context, applied only by `--apply-content-acceptance-floor N` (M2b-1F, M2d, M2e-1).
- Release B transactional fence with A-1 lock order (RB-CARV-1, A-1).
- Required-migration guards for business and Authority, including the ledger id (M2b-1, M2b-2, AM-12).
- Business S-11 CHECKs and guarded Down (M2b-2).
- Ledger P-1, P-2, DK-4..DK-8, P-4, L-13 (M2e-1).
- Default-off acceptance switch with AM-5 prerequisites (M2e-1).
- Package M2-FEN-03, -05, -06, -07 and -08 are therefore met by earlier increments.
- M2-FEN-01/-02 (the F2 trigger) are withdrawn by OD-M2f-02.

### 11.2 Approved M2f requirements [DECIDED]

OD-M2f-01..10; CAP-S1, S2, S5, S7, S8; CAP-F1, F2, F4, F6, F7; FB-1; AM-F1; CAP-O1..O7; RE-1..RE-6 (as requirements); INV-1..INV-3, INV-5.

### 11.3 Specified, not yet authorized [SPECIFIED]

CAP-S3, S4, S6; CAP-F3, F5; CAP-E1..E4; §9 key and values; INV-4; the increment scopes of §12.

### 11.4 Deferred [DEFERRED]

- The refusal-evidence mechanism (E3-M2f-3; R-CARV-12, UQ-4).
- A token claim or attested-digest signal (CAP-S8).
- Mechanical monotonicity of the deployment floor (§9).

### 11.5 Persistent deployment prerequisites [PREREQUISITE]

In addition to everything in closure §4.3 classes 2 and 3 (unchanged):
- E3-M2f-1 and E3-M2f-2 implemented and accepted;
- E3-M2f-2G evidence accepted;
- E3-M2f-3 implemented and accepted (RE-1..RE-6);
- INV-1/INV-2 Authority inventory;
- OA-FE1-2 where a floor is raised;
- all business instances at E3-M2f-1+ before the first binding (availability);
- the deployment floor set under PB-11 procedures.

None of these authorizes persistent acceptance by itself.

### 11.6 Unresolved restore and rollback risks [OPEN]

R-M2f-1, R-M2f-2, R-M2f-4 (§2, §4.4); BD-1 and BD-2 (unchanged; FB-1 obligation added to BD-2); F-E2 (unchanged; not touched by F0); R-A1-1..R-A1-5 (unchanged; R-A1-3 avoided because F0 adds no ordinary migration).

---

## 12. Implementation increments [SPECIFIED; none authorized]

### 12.1 E3-M2f-1 — Capability emission (business only)

| Aspect | Content |
|---|---|
| Scope | The three clients send `EnterpriseForms-Envelope-Capability: 2` on every Authority request (CAP-S1..S4). One business constant. No Authority change: no Authority code reads the header today, so older and current Authorities ignore it. |
| Components | `EnterpriseForms.Infrastructure/Authority/AuthorityHttpClient.cs` (`SendAsync`), `ProtectionVerifierHttpClient.cs` (`SendAsync`), `SearchIndexBuilderHttpClient.cs` (`SendAsync`); one new constant file in the same folder; contract tests; one architecture test |
| Database / config | None |
| Preconditions | Owner authorization; baseline clean |
| Acceptance criteria | Every request of each of the three clients carries exactly one header with value `2` (recording handler, every public method); the reconciler client does not; idempotency keys, request bodies and Prepare request digests are byte-identical with and without the change; existing contract, architecture and integrity suites green |
| Tests | M2F-01..M2F-05 |
| Security invariants | No body or digest change; no new decrypt call site; no Authority behaviour change |
| Stop conditions | Any change to a request body or digest; any need to modify the Authority; any client that submits content but is not one of the three |
| Rollback | Binary rollback (the header is ignored) |

### 12.2 E3-M2f-2 — Authority enforcement, error mapping, configuration, focused tests

| Aspect | Content |
|---|---|
| Scope | Shared capability component (parse CAP-S3/S6, evaluate CAP-F1..F5); store-port method `OrganizationHasDataKeyBindingsAsync` (boolean, AM-F1) in the EF store and the in-memory store; calls at the seven entry points (§6.1) in CAP-O order; the endpoints (Content, Catalogue verifier, SearchIndex builder) pass the parsed header to the services; `Content:MinimumBusinessEnvelopeCapability` (§9) with fail-closed startup parse and readiness text; `CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT` (CAP-E1/E2); internal OpenAPI header and response documentation and regenerated `contracts/openapi/authorization-authority-internal-v1.json`; business mapping (CAP-E3); log event and metric (CAP-E4); runbook section in `E3_M2B1_CARV_OPERATIONS.md`; implementation record |
| Components | Authority Application `Features/Content` (new component), `ContentService.cs`, `Features/Catalogue/ProtectionReconciliationService.cs`, `Features/SearchIndex/SearchIndexBuilderService.cs`, `Common/Ports/IAuthorityStore.cs`, Infrastructure `Persistence/AuthorityStore.cs`, in-memory store, Api `Composition/InternalAuthentication.cs` (header helper), `Features/Content/ContentEndpoints.cs`, `Features/Catalogue/CatalogueEndpoints.cs`, `Features/SearchIndex/SearchIndexBuilderEndpoints.cs`, error codes, startup composition; business Application retry predicates; tests |
| Database | **None** (F0; AM-F1 uses the existing index `ix_content_data_key_bindings_record`). G08 inventory unchanged (no new object; record this in the implementation record). |
| Preconditions | E3-M2f-1 accepted (business-first order); owner authorization |
| Acceptance criteria | §6.3 negative results for all seven entry points; full floor matrix; short-circuit proven (level 2 never queries the ledger); AM-F1 conditions pinned by architecture tests; ordering pinned for all seven; deployment-floor parse; no change for level-2 callers (every existing Authority, contract, architecture and integrity suite green without assertion changes, apart from listed additions); enforcement-removal mutations all detected |
| Tests | M2F-06..M2F-33 |
| Security invariants | Release B fence first; CARV, floors, P-1, P-2, DK, P-4, L-13, S-4, S-9 and the downgrade guard untouched; the check can only refuse; no binding detail leaves the store |
| Stop conditions | The check cannot be placed before a quarantine-capable or incident-journaling step at any entry point; any AM-F1 condition cannot be kept; any need for a migration, table, trigger, CARV bump or floor; any admit path; a level-2 regression in an existing test; acceptance switch needed on outside a disposable gate |
| Rollback | Binary rollback to the E3-M2f-1 Authority (nothing persisted) |

### 12.3 E3-M2f-2G — Disposable PostgreSQL verification (acceptance on in-run)

| Aspect | Content |
|---|---|
| Scope | Gate harness on the E3-M2e-2 pattern: throwaway PostgreSQL 18; floors 1→2→3; Release B `RequiredFloor = 3`; acceptance switch on **only inside the run** (needs its own owner authorization, OD-M2e-16); real bindings created through Prepare; level-1 and level-2 clients via raw requests and legacy business agents (`git archive` of a pre-M2b-2 build for G-2, and of `0be5f52` for CAP-S5) |
| Checks | M2F-34..M2F-40; runs of record ×2; containers removed; evidence scrubbed of local paths |
| Expected-gap demonstrations (outside pass totals) | `EXPECTED_GAP_G2` (baseline Authority quarantines on a level-1 read); `EXPECTED_GAP_INVENTORY` (baseline CARV-3 Authority admits level 1, R-M2f-3); `EXPECTED_GAP_RESTORE` (organization floor regression after a `CREATE DATABASE … TEMPLATE` restore copy, with `D = 2` shown as the backstop, R-M2f-4) |
| Stop conditions | Any persistent database; Azure; Stage B; acceptance-on outside the container; a counted check that fails; an unexplained quarantine or incident |

### 12.4 E3-M2f-3 — Durable bounded refusal evidence

Starts only after a separate owner design decision on §8 (and R-CARV-12 / UQ-4). Scope, storage and migrations, if any, are defined by that decision. Required tests at minimum: survival of the refused transaction; bound under a request flood; no identifiers beyond organization and workload; ordering unchanged (CAP-O6 relaxed only as decided); behaviour under archive and journal outage.

---

## 13. Test traceability (to be executed in the named increments)

| ID | Requirement | Verification | Increment | Environment |
|---|---|---|---|---|
| M2F-01 | CAP-S2 AuthorityHttpClient | Recording handler over every public method | M2f-1 | Unit (Development/Testing) |
| M2F-02 | CAP-S2 verifier client | Same | M2f-1 | Unit |
| M2F-03 | CAP-S2 builder client | Same | M2f-1 | Unit |
| M2F-04 | CAP-S2 reconciler excluded | Same; asserts absence | M2f-1 | Unit |
| M2F-05 | CAP-S7 digest unchanged | Prepare digest and bodies byte-identical | M2f-1 | Unit |
| M2F-06 | CAP-S3/S5/S6 grammar | Table: absent, `1`, `2`, empty, `02`, ` 2`, `3`, `x`, duplicates | M2f-2 | Unit |
| M2F-07 | CAP-F1 matrix | C × D × O, all combinations | M2f-2 | In-process |
| M2F-08 | CAP-F2 any state | Bindings of Prepared, Committed, Aborted, expired operations each raise O | M2f-2 | In-process |
| M2F-09 | CAP-F7 isolation | A binding in organization A does not raise O for B | M2f-2 | In-process |
| M2F-10..16 | G-2 / CAP-O3/O4 | One per entry point: refusal; no quarantine, no incident, integrity revision and operations unchanged | M2f-2 | In-process |
| M2F-17 | CAP-O3 replay | Prepare replay and Commit replay by level 1 in a bound organization refused | M2f-2 | In-process |
| M2F-18 | CAP-O2 fence first | With the floor missing, Release B refusal precedes the capability refusal | M2f-2 | In-process |
| M2F-19 | CAP-O5 level 2 | Level-2 requests produce identical outcomes to the baseline across existing suites | M2f-2 | Existing suites |
| M2F-20 | AM-F1 #1 | Method returns bool; no binding columns materialized (store test) | M2f-2 | In-process + PostgreSQL |
| M2F-21 | AM-F1 #2/#3 | Architecture: sole caller is the shared check; called from exactly seven sites | M2f-2 | Architecture |
| M2F-22 | AM-F1 #4 / CAP-F3 | Counting store: level 2 and `C < D` never query | M2f-2 | In-process |
| M2F-23 | AM-F1 #6 | Architecture: no other ledger port used by read, verify, search or history paths | M2f-2 | Architecture |
| M2F-24 | AM-F1 #7 / CAP-F5 | Store throws ⇒ refusal, never admit | M2f-2 | In-process |
| M2F-25 | CAP-O7 position | Architecture: the call immediately follows the lock / fence at each site | M2f-2 | Architecture |
| M2F-26 | CAP-O6 | No write on refusal (scope change tracker empty) | M2f-2 | In-process |
| M2F-27 | CAP-E1/E2 | Response code, status, retryable and fixed text; deployment versus organization floor indistinguishable | M2f-2 | In-process |
| M2F-28 | CAP-E3 | Business predicates exclude the code; no retry or new Prepare | M2f-2 | Contract |
| M2F-29 | §9 parse | Startup refuses invalid values; absent ⇒ 1; readiness text | M2f-2 | Host |
| M2F-30 | CAP-E1 OpenAPI | Header and response present in the regenerated internal document | M2f-2 | Contract |
| M2F-31 | CAP-S1 constants | Business and Authority header names equal | M2f-2 | Architecture |
| M2F-32 | Non-content endpoints | Header absent or `1` is not refused on §4.3 endpoints | M2f-2 | In-process |
| M2F-33 | Mutations | Remove check per site, move it after verification, drop short-circuit, invert comparison, ignore D, ignore O, admit on store error: each detected | M2f-2 | Mutation driver |
| M2F-34 | FB-1 | Concurrent first Prepare (binding) and level-1 Verify in one organization: serialized; either refused or pre-binding | M2f-2G | Disposable PostgreSQL |
| M2F-35 | Real G-2 | Pre-M2b-2 legacy agent: refused, no quarantine (M2f); `EXPECTED_GAP_G2` on baseline | M2f-2G | Disposable PostgreSQL |
| M2F-36 | CAP-S5 | `0be5f52` business agent refused in a bound organization, admitted in an unbound one | M2f-2G | Disposable PostgreSQL |
| M2F-37 | Workload planes | Census, backfill and deep verify with level 1 refused on real bindings | M2f-2G | Disposable PostgreSQL |
| M2F-38 | D = 2 | Level 1 refused in an unbound organization | M2f-2G | Disposable PostgreSQL |
| M2F-39 | R-M2f-4 | Restore-copy demonstration with D backstop | M2f-2G | Disposable PostgreSQL (expected gap) |
| M2F-40 | R-M2f-3 | Baseline CARV-3 Authority admits level 1 | M2f-2G | Disposable PostgreSQL (expected gap) |

**Requires later authorization:** Azure PostgreSQL behaviour of the existence query and lock (R-CARV-18); PB-11 inventory and configuration evidence; PB-12 release evidence; staging.

---

## 14. Remaining open decisions

| ID | Question | Blocks |
|---|---|---|
| **OPEN-M2f-1** | Refusal-evidence design (§8; with R-CARV-12, UQ-4) | E3-M2f-3; persistent use |
| OPEN-M2f-2 | Whether the deployment floor should later be recorded or anchored for mechanical monotonicity (§9; needs owner approval for any state) | None now; production governance |
| OPEN-M2f-3 | Signal evolution under PB-08 (CAP-S8) | None now |
| OPEN-M2f-4 | Authorization of acceptance-on inside the E3-M2f-2G disposable run (OD-M2e-16) | E3-M2f-2G |
| — | BD-1, BD-2 (with the FB-1 obligation), F-E2, R-A1-1..5, V2-IND-01, RB-CARV-1 production closure, PB-11/PB-12, Azure PostgreSQL | Persistent activation and production (unchanged) |

---

## 15. Risks

| ID | Risk | Status |
|---|---|---|
| R-M2f-1 | Release A and legacy Authority builds unbound by a missing floor (R-CARV-21) | Open |
| R-M2f-2 | Privileged DDL removes floors or CHECKs | Outside the runtime model; G08 |
| R-M2f-3 | Floor 3 admits non-M2f CARV-3 Authorities | Inventory (OD-M2f-05) |
| R-M2f-4 | AuthorityDb restore lowers the organization floor | Open (BD-1); D backstop |
| R-M2f-5 | A refused level-1 workload learns, before membership resolution, that the organization has a binding | Accepted (§6.2) |
| R-M2f-6 | The header is self-asserted; a compromised business runtime can claim level 2 (A3) | Accepted by design (CAP-S7) |
| R-M2f-7 | Deployment floor monotonicity is procedural only | Open (OPEN-M2f-2) |
| R-M2f-8 | Business instances below E3-M2f-1 lose service for bound organizations | Accepted availability cost; drain before the first binding |
| R-M2f-9 | Until E3-M2f-3, refusals leave only non-durable operational signals | Open; blocks persistent use |
| R-M2f-10 | A future business level must be recognized by the Authority before it is sent (CAP-S6); a business-first rollout of a new level is refused | Accepted (fail closed) |

---

## 16. Not claimed

- No implementation of any E3-M2f increment; no test was written or run for this record.
- No closure of BD-1, BD-2, F-E2, R-CARV-21 or any production gate.
- No persistent activation, no Envelope v2 acceptance in any environment, no Azure or staging evidence.
- No independent review; no compliance or production-readiness claim.
