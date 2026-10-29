# Field-protection migration reconciliation (Phase C7 M4, G19)

- **Status:** implemented for development and the isolated security-test stack (decision C-73). **G19 stays OPEN** until the completeness evidence below is independently reviewed and reproduced on Azure staging. Not a Level 2 or production claim.
- **Owners:** Authority `Catalogue` feature (census, attestation, coverage); business `FormDesigner` feature (reconciliation job, status, request); Worker host (verifier credential, job loop).
- **Inputs:** C-56 D1(7) (privileged completeness verification for encryption migration), C-60 OD-1 (separate `field-protection-verifier` workload), OD-8 (Authority audit evidence), AC-5 migration catalogue (§6A of `docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md`), B-28 reprotection, ADR-PB-004 Amendment A1, `SECURITY_GAP_ASSESSMENT.md` G19.

## 1. Checkpoint: what existed and what was missing

| Capability | Before M4 | M4 |
|---|---|---|
| Executing plaintext → ciphertext (and A1 ciphertext → plaintext) | B-28 reprotection operations through the Authority (prepare → stage → read-back → commit, `ReprotectionRules`), leased resumable worker, idempotency keys per attempt, lost-acknowledgement repair | **Unchanged.** No second migration engine, no new write path. |
| Expected population | Business `form_records` list + business estimate (G19: a DBA can conceal records) | AC-5 migration catalogue: the Authority registry of the form, records no user can discover included, read by the verifier workload only |
| "Old form remaining" check | Counted from business rows of the Authority's accepted operation, unverified | Authority **census**: full verification of the accepted operation (complete leaf set, every HMAC, root, archive receipt), counts from the verified canonical bytes |
| Completion claim | Business-side post-check ("all listed records", caveat C-03) | Authority **attestation** computed under the organization lock from the registry, current heads and census results; journaled on the Authority audit stream |
| Organization-wide view | none | Authority **coverage**: every current protection decision with its latest attestation |

Protocol check before implementing (owner §2): the census and attestation are read/verify operations of the verifier workload. They do not sign, commit content, change a protection decision or register anything. The only state they change is the existing AC-4 rule — a record whose observed rows fail verification is quarantined and an incident journaled — applied unchanged. Conversion stays the approved B-28/A1 reprotection operation under the decision's command. No new trust contract was needed for the implemented part; the parts that would need one are listed in §9.

## 2. Identities and separation

| Step | Principal | Capability |
|---|---|---|
| Protection decision | Designer (strengthen) / organization administrators (A1 removal) | `PublishFormSecurityRevision` security command (unchanged) |
| Execution | Business workload, reprotection operations scoped by that command | prepare/commit of the paired replacement only (unchanged) |
| Privileged inventory, census, attestation, coverage | `field-protection-verifier` workload (separate HS256 credential, issuer and key distinct from the business workload and the operator; Worker host only) | read the complete population, verify, count, attest — **no write of content, no command** |
| Verify-only request | Same permission as retrying the field's migration (design for Encrypt; organization administration for Decrypt) | queues a job; never reads records |
| Completion attestation | Authority | computed from Authority state; journaled `ProtectionReconciliationAttested` |

* The business API never registers `IProtectionVerifierAuthority` (contract test `ProtectionVerifierBoundaryTests`), even if a verifier credential is placed in its configuration. The Worker refuses a partial credential or one reusing the business workload key or issuer; the Authority refuses the same at startup (C-62).
* Inventory access never authorizes a write: the verifier's routes cannot prepare or commit content, and the reprotection path still requires the effective command (`ProtectionCommandResolver`).
* Production: the verifier credential, like every Stage A workload credential, is HS256 from deployment configuration. Production needs an Entra workload identity with mTLS and its own deployment pipeline (PB-08/PB-11). Until then the verifier is configured only on the isolated security-test stack and, opt-in, on the persistent development stack.

## 3. Authority operations (verifier scheme, `/internal/v1/verifier`)

| Route | Behaviour |
|---|---|
| `POST migration-catalogue` (AC-5, unchanged) | Complete population of the form scoped by the decision; pages now carry `protection_census_outcome` (the census the Authority already holds for exactly that record, operation and decision; a work-list hint) |
| `POST migration-catalogue/{snapshot}/census` | 1..50 entries (with an optional `reuse_since_snapshot_id`, see below) of the verifier's own, unexpired snapshot, each at the head the snapshot fixed. Per record: registry (form parent, not Revoked), quarantine, current head equals the entry (`HEAD_CHANGED` otherwise; nothing verified), archive receipt (`AWAITING_ARCHIVE`), verification key version (`KEY_UNAVAILABLE` for a retired key), then the shared verification (`VerifyStagedAgainstLeavesAsync`: count, keys, digests, signatures, root). On failure: head quarantined, incident journaled naming the workload and decision. On success the decision's field rows are counted from the verified bytes: occurrences (top level and repeating sections), rows still in the old form, target-form ciphertext rows with an unapproved envelope (algorithm, 96-bit nonce, 128-bit tag, non-empty ciphertext / wrapped key / key reference) or a repeated (wrapped key, nonce) pair; the signed header status (Archived records counted). A census already held for that (decision, record, operation) is returned as `Reused` only when it was verified since the reconciliation's first snapshot (below). Counts only. |
| `POST migration-catalogue/{snapshot}/attestation` | Under the organization lock: the decision must still be current (else 409); population re-read from the registry (Active, Quarantined, Registered of the form; Revoked counted as retired); heads re-read; census results joined on (record, **current** head operation). Report counts (§4) and `attestation_complete`; `protection_reconciliation_attestations` row (immutable to the runtime; CHECK constraint forbids a complete row with anything remaining) and audit journal entry in the same transaction. |
| `POST protection-coverage` | Every active `FieldProtectionRequirement`/`FieldProtectionRelease` of the organization (Authority resources, never the business form list) with its latest attestation; `organization_complete` only when each current decision has a complete attestation taken under that decision. |

Persistence (Authority migration `20261029122643_ProtectionReconciliation`, additive): `protection_census_results` (unique per decision, record, operation) and `protection_reconciliation_attestations`; runtime role SELECT/INSERT/UPDATE, no DELETE.

**Why the attestation does not depend on the snapshot's revisions.** The migration population depends only on registry rows and heads, not on SpiceDB. Instead of trusting that "nothing moved since the snapshot", the attestation re-derives the population and every head under the lock and requires a verified census for each **current** head. A census of an earlier head never counts for a newer one; a record registered after the snapshot shows up as not censused. The snapshot only identifies the decision and the verifier and supplies the work-list. User-scope snapshot rules (OD-5) are unchanged.

**When a held census is reused (C-74).** Accepted content of an operation is immutable and the census is keyed by (decision, record, operation). A held result is reused only when the request names an earlier snapshot of the **same** verifier, decision and form (`reuse_since_snapshot_id`: the reconciliation's first round) and the result was verified at or after that snapshot was issued (Authority clock); a re-verification refreshes that time. The first round names none, so every record is verified in every reconciliation, and a result from an earlier reconciliation never stands in for a fresh one (the first M4 version reused across reconciliations — caught by the rollback test, then again by review for records a catch-up round had not reached; both fixed). Rows are always sent.

## 4. Report categories (owner §5)

| Category | Source | Blocks completion |
|---|---|---|
| Expected records | registry population of the form (Active, Quarantined, Registered) | — |
| Accepted (has content) | head revision ≥ 1, not quarantined | — |
| Compliant (verified, target form) | census Verified, 0 old-form rows, ≥ 1 field row, no envelope issue | no |
| Not applicable (authenticated) | census Verified, no row of the field | no |
| Still in the old form | census Verified, old-form rows > 0 (record and occurrence counts) | **yes** |
| Quarantined | registry or head quarantined (missing, deleted, rolled-back, tampered content) | **yes** |
| Unverified | census failure, or a head signed with a retired verification key | **yes** |
| Not yet censused | current head without a census (changed or added during the run) | **yes** (new round) |
| Envelope issues | census envelope check | **yes** |
| Active search approvals of the field | AC-6 approvals | **yes** |
| Pending creations | registered without accepted content (revision 0) | no — no content exists; the protection barrier admits a first revision only in the required form |
| Archived | verified signed header status | no (included and checked) |
| Retired | registry Revoked | reported; content not migrated |
| Retained history | accepted manifest commits before each current head | reported; **never claimed** |
| Excluded by policy | none exist in V1 | — |

Worker-side additions, held in the business database and labelled untrusted: business rows of the form unknown to the Authority (forged/injected, ignored), Authority records not listed under the form by the business database (deleted or moved), ciphertext rows decrypt-checked (AEAD tag and the tenant/record/field/item AAD) and logical-type checks, and old-form rows the business database holds outside current versions (residual exposure). A reconciliation is **Complete** only when the Authority attestation is complete **and** every target-form ciphertext row the Authority counted was decrypt-checked without failure or type mismatch.

## 5. Worker job (`field_protection_reconciliations`, business migration `PhaseC7ProtectionReconciliation`)

States: `Queued → Running ⇄ Blocked → Complete | Incomplete | Unavailable | Superseded`. Modes: `MigrationCompletion` (created when a migration's business-side post-check passes; the migration is `Verifying` meanwhile) and `VerifyOnly` (authorized request; never converts).

1. **Round start:** read every catalogue page; compare with business `form_records` both ways (reported only); a full round resets the per-round counters (a resumed round keeps them); rounds are bounded (20) and a round that verifies nothing new ends the reconciliation (`RECONCILIATION_DID_NOT_CONVERGE`).
2. **Census pages:** one page per lease (≤ 1000 entries), processed 50 records at a time — rows read from the immutable staged copy, decrypt-checked (target-form ciphertext) and sent to the census — so progress starts immediately; records whose verified head still holds the old form are handed back to the migration as pending items (MigrationCompletion; an item wrongly marked completed is reopened). Counters, hand-back, the resume position and the offset commit together at each page end and when a snapshot expires mid-page (durable checkpoint). After an expiry the next round resumes after the last record this reconciliation verified.
3. **Attestation:** stored with its digest and journal revision. Not-yet-censused records (heads changed or records added) → a full new round (resume position cleared; this reconciliation's census results are reused). Otherwise Complete/Incomplete with the first reason.
4. **Migration outcome (MigrationCompletion only, migration still `Verifying`):** Complete → `Completed` (+ audit with the attestation id); old form remaining with pending items → `Running` again (bounded: 10 reconciliations per migration); anything else → `Failed` (`RECONCILIATION_INCOMPLETE:<reason>`). Verifier not configured → `Unavailable` and the migration completes with the pre-M4 caveat ("listed records", G19 not addressed).

Failure behaviour: Authority/verifier unreachable, gate closed or key provider down → `Blocked`, retried after 30 s, nothing claimed; snapshot expired → new round; decision superseded → `Superseded`; catalogue above `MaximumMigrationEntries` → `Incomplete` (`RECONCILIATION_LIMIT`), no partial certificate; worker crash or shutdown → lease expires or is released, the next step resumes from the stored offset; a lost census response → the Authority already holds the result, the retry gets it as `Reused`; reprotection lost acknowledgements remain handled by B-28 (S2/S4). No cross-database transaction is used: the Authority is the only writer of census and attestation, the business database only holds job state.

## 6. Status and UI

`GET …/fields/{field}/protection` adds `reconciliation` (job state, progress, the attestation copy, worker observations, basis text) and `authorityReconciledComplete` (latest reconciliation Complete under the decision the latest migration carries, in the field's current direction). The Edit field dialog shows "Authority-verified: every record's current version encrypted" only then; otherwise the pre-M4 "All listed records …" badge and caveat stay. The panel lists the Authority's counts, the worker's untrusted observations, the basis (current versions only; history, retired records, WAL, replicas and backups excluded) and, for permitted users after the migration finished, **Verify completeness…** (reason required, audited). No migration runs because a form was published or a policy changed: a migration starts only from the explicit, reasoned protection change (unchanged); a reconciliation starts only when that migration's post-check passes or on request.

## 7. Residual plaintext and historical exposure (owner §7)

| Representation | Migrated by M4? | Notes |
|---|---|---|
| Current accepted revision of every registered record | **Yes** (B-28) and verified by the census | the attested scope |
| Earlier accepted revisions (business staged rows of earlier operations) | **No** | immutable history (C6); counted (`retained_history_revision_count`, worker `retained_old_form_row_count`); reachable only through authorized historic reads; disposal needs DR-C7-4 |
| Abandoned candidates / pending creations' staged rows | No | never accepted, never served; counted in the worker's old-form row count |
| Business typed value indexes over those rows | No | indexes cover every staged row of the value tables; plaintext of history remains indexed and readable to a DBA |
| Field search approvals | Invalidated at strengthening (AC-6); the attestation blocks if one is active | — |
| Search catalogue snapshots, census and attestation rows | contain no values | — |
| Caches, job payloads | migration/reconciliation payloads carry ids and counts only; plaintext exists only in worker memory during conversion and the decrypt check | — |
| Logs and telemetry | values are never logged (integration test scans worker and Authority logs) | — |
| Retired records (registry Revoked) | No | counted, not served |
| PostgreSQL WAL, replicas, backups, exports already taken | No | operational retention/disposal process; never deleted automatically |
| Documents (File fields) | Out of scope | per-attachment protection is fixed at upload (always encrypted); File fields have no migration |

Old encryption/verification keys: a head signed with a key version below the minimum verification version is reported unverified, never replaced or skipped; a missing KEK blocks the decrypt check (`ENCRYPTION_UNAVAILABLE`, retried).

## 8. Operator runbook

**Isolated security-test stack:** configured (synthetic verifier credential on the Authority and the Worker only). Run `ProtectionReconciliationTests` (Stage B integration).

**Persistent development stack (owner's environment, :8091):** the verifier is **off** by default; existing data and existing migrations are never reconciled automatically.

1. `py scripts/devstack/devstack.py rebuild authority api worker web` — new images (data untouched).
2. `py scripts/devstack/devstack.py protection-verifier enable` — generates a per-environment development credential in the custody directory and recreates the Authority and Worker. No data changes, no migration starts.
3. In the browser use a **new synthetic form** (§10 of `docs/PHASE_C7_STATUS.md`); do not strengthen fields of existing forms unless you intend to migrate those records (that would be the owner's explicit operation; prepare a verify-only report first and approve the exact field).
4. Edit field → Security & Encryption → Strengthen protection; watch "migration verifying with the Authorization Authority", then the Authority attestation. "Verify completeness…" re-checks later without changing anything.

Reading the evidence: the attestation id, digest and Authority audit journal revision shown in the panel identify the Authority's own record (`protection_reconciliation_attestations`, audit stream `ProtectionReconciliationAttested`). The business copy is for display.

Production/staging: not enabled. Requires the verifier's production identity (§2), Azure staging evidence, independent review, and AUD-C7-01 archival durability.

## 9. Not implemented — decisions needed

* **DR-C7-4 — retained historical plaintext:** design in §12; unresolved pending an architectural decision.
* **DR-C7-5 — Security Administrator coverage view:** design in §13; not built.
* **DR-C7-6 — resumable rounds and limits for production:** §11; implemented for development, approval needed before production use.
* **DR-C7-1** (abandoned creations) is unchanged; pending creations are counted, never treated as content.
* **AUD-C7-01 / G24:** the attestation journal entry is archived by the existing asynchronous archive processor; mandatory-evidence archival durability is not verified.

## 10. Test map (owner §11)

| # | Scenario | Test |
|---|---|---|
| 1, 16, 17 | complete inventory and executed conversion of a form; type and AEAD | Stage B `AStrengtheningCompletesOnlyThroughTheAuthoritysAttestationOfTheCompletePopulation` |
| 2 | organization-wide scope across forms | Stage B `OrganizationCoverageIsCompleteOnlyWhenEveryDecisionAcrossFormsIsAttested`; unit `CoverageListsEveryCurrentDecisionAcrossForms…` |
| 3 | records invisible to normal users | Stage B complete-population test (reader and designer catalogues empty) |
| 4 | archived records | Stage B complete-population test |
| 5 | quarantined records | Stage B concealment test; unit `ThePopulationComesFromTheRegistry…` |
| 6 | pending creation counted apart | Stage B complete-population test; unit |
| 7, 8, 12 | missing / hidden / moved records | Stage B `ARecordConcealedFromEveryBusinessListingBlocksAFalseCompletionAndForgedRowsAreIgnored` |
| 9, 10 | rolled-back or tampered rows | Stage B `AVerifyOnlyReconciliationDetectsAPostCompletionRollbackAndNeverRepairsIt`; unit `TamperedMissingOrRolledBackRows…` |
| 11 | forged business records | Stage B concealment test (business-only row ignored) |
| 13 | historical form-version policy | Stage B complete-population test (v2 layout without the field) |
| 14, 15 | ineligible downgrade, organization floor | existing `FieldProtectionDowngradeTests`, `EncryptionTests` (re-run with M4) |
| 18 | historical revisions preserved | Stage B complete-population test (plaintext rows kept, history counted) |
| 19 | searchable plaintext not falsely compliant | unit `ARetiredVerificationKeyAndAnActiveSearchApproval…`; residual rows reported |
| 20–22 | worker interruption, lost response, duplicates | Stage B `OutagesAndRestartsResumeTheReconciliationAndHeldCensusResultsAreReused`, `ARecordChangedAfterTheAttestationIsVerifiedByTheNextReconciliation`; existing B-28 lost-acknowledgement test |
| 23 | policy change during migration | Stage B `ADecisionChangeDuringAReconciliationSupersedesItWhileAnOrganizationPolicyChangeDoesNot`; unit `ASupersededDecisionCanNeitherBeCensusedNorAttested` |
| 24 | record edits during reconciliation | Stage B `EditsDuringAReconciliationAreCaughtUpInANewRoundNotMixed`; unit `ACensusOfAnEarlierHead…` |
| 25 | retired or unavailable keys | unit (retired verification key); Stage B `AnUnavailableEncryptionKeyBlocksTheReconciliationAndNeverCompletesIt` |
| 26 | quota and limits; snapshot expiry | unit `TheMigrationCatalogueAcceptsExactly50000RecordsAndRefuses50001WithNothingPartial`; Stage B `TheCatalogueCeilingEndsIncompleteAndExpiredSnapshotsAreRenewedSafely` |
| 27 | no partial or misleading certificate | DB CHECK constraints on both sides; all failure tests |
| 28–30 | permissions, cross-organization, unauthorized execution | Stage B `OnlyAuthorizedPeopleRequestAVerification…`; contract `ProtectionVerifierBoundaryTests`; unit cross-organization |
| 31 | audit evidence; archival outage | Stage B complete-population test (journal entry); `AnArchiveOutageNeverYieldsACompletionWithoutAuthorityEvidence` (AUD-C7-01 stays open) |
| 32 | no plaintext in logs or reports | Stage B complete-population test (logs, audit metadata) |
| 33 | no regression | full Stage B, browser and SEC-P suites |

## 11. Catalogue lifetime and large forms (owner §5; DR-C7-6)

**Problem.** A migration snapshot lives at most 15 minutes. At the measured ≈ 30 verified records/s a single pass covers ≈ 27,000 records, below the 50,000-record catalogue ceiling.

**What the existing protocol already provides (no Authority protocol change needed):**

| Requirement | How it is met |
|---|---|
| Durable, bounded checkpoints | The Authority's census results, keyed by the immutable (decision, record, accepted operation); the worker's page offset, counters and resume position, committed at every page end and at a mid-page expiry |
| Independently verified resumable progress | Progress is the Authority's own census rows; the worker's numbers are only a work-list |
| Snapshot-safe catch-up | Each round takes a new snapshot; nothing is carried between snapshots except Authority census results of unchanged operations |
| Revalidation across renewal | Reuse only of results verified since this reconciliation's first snapshot (Authority clock, same verifier/decision/form); a re-verification refreshes the time |
| Trustworthy final attestation | Unchanged: computed under the organization lock from the registry, current heads and census of exactly those heads — a record changed after it was censused shows as uncensused and forces a full round |

**Implemented (C-74; worker policy plus one tightening of the Authority reuse rule):** after an expiry the next round resumes after the last record verified in this reconciliation; a round that verifies nothing new ends `Incomplete` (`RECONCILIATION_DID_NOT_CONVERGE`); at most 20 rounds; census batches start immediately (rows read per batch). Not done, on purpose: no longer snapshot lifetime, no higher ceiling, no weaker completeness rule.

**Limits that remain:** the 50,000-record ceiling (a larger form ends `Incomplete`, `RECONCILIATION_LIMIT`); 20 rounds × 15 minutes bounds a reconciliation to ≈ 5 hours of verification; a form whose heads change faster than they can be censused never completes (it stays Incomplete, by design). Measured on the isolated stack with a 2-second snapshot lifetime: `docs/PHASE_C7_STATUS.md` §32.

**Decision requested (DR-C7-6):** approve resumable rounds, the 20-round bound and the reuse rule for production use; or choose a different scaling path (parallel verifiers, a longer-lived catalogue with periodic revalidation — the latter is a protocol change and needs its own review).

## 12. DR-C7-4 — retained historical plaintext (design; unresolved)

**Exposure today.** After a strengthening, every earlier accepted revision of a record still holds the field in plaintext: the business database keeps those staged rows (and their value-table indexes), the Authority keeps their manifest commits and HMACs (digests only, no values), and backups, WAL, replicas and exports keep copies. Authorized full readers can open earlier revisions (C6 history; not a search surface). A business DBA can read the plaintext directly.

**Constraints.** Accepted revisions are immutable and Authority-committed (manifest root, leaf digests, row signatures, archived journal entries); history is used for authorized viewing and restore; legal holds and retention may require keeping it; DR must not roll back.

| Option | What it does | Security effect | Cost / risk |
|---|---|---|---|
| A. Keep and disclose (current) | Report retained revisions and rows; never claim them migrated | No change in exposure | None; honest |
| B. Restrict historical reads of strengthened fields | History views mask the field in revisions older than the strengthening unless the reader passes an extra step-up (PB-08) | Lowers application-level exposure; DBA exposure unchanged | Small; needs PB-08 for the step-up |
| C. Re-seal history (new "historic re-protection" transition) | For each earlier revision, write an encrypted copy and have the Authority accept a **superseding historic manifest** that references it, marking the plaintext revision "superseded for content, retained as evidence" | Removes plaintext from what the application serves; the old rows still exist until purged | New Authority transition, new manifest version, new archive events, restore semantics; independent review |
| D. Crypto-shred-style purge under retention | After C, delete the superseded plaintext staged rows through a privileged, dual-approved retention job that records digests and receipts; keep Authority digests | Removes plaintext from the live database; backups/WAL age out on their schedule | Deletes evidence bytes (the digests stay); legal-hold checks; irreversible |
| E. Per-record data keys from the start | Future fields encrypt every revision under per-record keys | Prevents the problem for new data only | Design change of the encryption model |

**Recommendation.** Now: A, plus B as soon as PB-08 step-up exists. Later, only with an approved retention policy: C then D for organizations that require it. Never: rewrite or delete accepted revisions in place, re-sign old rows, or report history as migrated.

**Immediately necessary restrictions — assessment.** Field-value search uses only current heads and refuses encrypted leaves, and approvals are invalidated at strengthening, so retained plaintext is **not reachable through search**. History is readable only by full readers (owners), who can already read the current decrypted value; share recipients, partial readers and Security Administrators cannot open history. No endpoint therefore exposes retained plaintext to anyone who could not already read the value; no immediate restriction is required. B remains recommended defence in depth. DR-C7-4 stays **unresolved**.

## 13. DR-C7-5 — Security Administrator coverage view (design; not built)

**Purpose.** A read-only organization page for `administer_security` holders showing whether each current protection decision is attested — without record discovery, read or decryption.

**Contract.** A new user-plane Authority read `POST /internal/v1/field-protection/coverage` (business workload + delegated user; `administer_security` of the organization, else 404), answered from the same Authority state as the verifier coverage — but it never calls the verifier routes and never enumerates records:

| Shown | Source |
|---|---|
| Form (registered title, OD-3) and field identity; decision direction and command time | protection resources, registered form versions |
| Migration status | business job state (labelled business-held) |
| Expected / accepted / compliant / pending / quarantined / unverified / not-censused counts | latest Authority attestation of the current decision |
| Retained-history and retired counts; coverage statement | attestation |
| Attestation id, journal revision, time; **Stale** when the attestation predates the decision's latest content-integrity change, **Missing** when the decision has none | attestation + organization state |

**Never shown:** record ids, field values, ciphertext, record titles or any per-record row. Counts are exact: they describe only the administrator's own organization, and a count reveals no identity.

**UI.** Organization → Security → "Encryption coverage": one row per decision, status badge, counts in a disclosure, "Request verification" only for users who already hold the field's design/administration permission (it queues the existing verify-only job; the administrator role alone does not grant it).

**Tests to add with it.** No record ids in any response; 404 for non-administrators and other organizations; stale/missing states; no SpiceDB record lookup is performed by the endpoint.
