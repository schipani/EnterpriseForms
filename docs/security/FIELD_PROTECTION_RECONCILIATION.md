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
| `POST migration-catalogue/{snapshot}/census` | 1..50 entries (`reverify` flag, see below) of the verifier's own, unexpired snapshot, each at the head the snapshot fixed. Per record: registry (form parent, not Revoked), quarantine, current head equals the entry (`HEAD_CHANGED` otherwise; nothing verified), archive receipt (`AWAITING_ARCHIVE`), verification key version (`KEY_UNAVAILABLE` for a retired key), then the shared verification (`VerifyStagedAgainstLeavesAsync`: count, keys, digests, signatures, root). On failure: head quarantined, incident journaled naming the workload and decision. On success the decision's field rows are counted from the verified bytes: occurrences (top level and repeating sections), rows still in the old form, target-form ciphertext rows with an unapproved envelope (algorithm, 96-bit nonce, 128-bit tag, non-empty ciphertext / wrapped key / key reference) or a repeated (wrapped key, nonce) pair; the signed header status (Archived records counted). Without `reverify`, a census already held for that (decision, record, operation) is returned as `Reused` without re-verification. Counts only. |
| `POST migration-catalogue/{snapshot}/attestation` | Under the organization lock: the decision must still be current (else 409); population re-read from the registry (Active, Quarantined, Registered of the form; Revoked counted as retired); heads re-read; census results joined on (record, **current** head operation). Report counts (§4) and `attestation_complete`; `protection_reconciliation_attestations` row (immutable to the runtime; CHECK constraint forbids a complete row with anything remaining) and audit journal entry in the same transaction. |
| `POST protection-coverage` | Every active `FieldProtectionRequirement`/`FieldProtectionRelease` of the organization (Authority resources, never the business form list) with its latest attestation; `organization_complete` only when each current decision has a complete attestation taken under that decision. |

Persistence (Authority migration `20261029122643_ProtectionReconciliation`, additive): `protection_census_results` (unique per decision, record, operation) and `protection_reconciliation_attestations`; runtime role SELECT/INSERT/UPDATE, no DELETE.

**Why the attestation does not depend on the snapshot's revisions.** The migration population depends only on registry rows and heads, not on SpiceDB. Instead of trusting that "nothing moved since the snapshot", the attestation re-derives the population and every head under the lock and requires a verified census for each **current** head. A census of an earlier head never counts for a newer one; a record registered after the snapshot shows up as not censused. The snapshot only identifies the decision and the verifier and supplies the work-list. User-scope snapshot rules (OD-5) are unchanged.

**When a held census is reused.** Accepted content of an operation is immutable and the census is keyed by (decision, record, operation), so within **one** reconciliation a record whose head did not change is not verified twice: catch-up rounds send no rows for it and get the held result as `Reused`. Every reconciliation's **first round** sets `reverify`: all rows are sent and verified again, so a business copy tampered with after an earlier census (found in verification by the rollback test, C-73) is caught on an unchanged head instead of being vouched for by the earlier result.

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

1. **Round start:** read every catalogue page; compare with business `form_records` both ways (reported only); reset per-round counters; round count bounded (5).
2. **Census pages:** one page per lease (≤ 1000 entries): rows of each accepted, non-quarantined entry are read from the immutable staged copy and sent in batches of 50 (first round: all rows, `reverify`; catch-up rounds: entries with a census held for the same operation are sent without rows); the worker decrypt-checks target-form ciphertext; records whose verified head still holds the old form are handed back to the migration as pending items (MigrationCompletion; an item wrongly marked completed is reopened); offset, counters and hand-back commit together (durable checkpoint).
3. **Attestation:** stored with its digest and journal revision. Not-yet-censused records → a new round (held census carries over). Otherwise Complete/Incomplete with the first reason.
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

* **DR-C7-4 — retained-history disposal.** Earlier accepted revisions are immutable by design (C6 history, restore). Removing their plaintext would need a new Authority transition (re-sealing or purging history under retention/legal hold, with new manifests for historic revisions) and a retention policy. Not implemented; reported only.
* **DR-C7-5 — Security Administrator coverage view.** Coverage is available to the verifier workload only. A read-only view for `administer_security` holders (counts and decisions, no record ids) would need a user-plane Authority read; proposed, not built.
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
| 20–22 | worker interruption, lost response, duplicates | Stage B `OutagesAndRestartsResumeTheReconciliationAndHeldCensusResultsAreReused`; existing B-28 lost-acknowledgement test |
| 23 | policy change during migration | unit `ASupersededDecisionCanNeitherBeCensusedNorAttested` (integration: pending) |
| 24 | record edits during reconciliation | Stage B `EditsDuringAReconciliationAreCaughtUpInANewRoundNotMixed`; unit `ACensusOfAnEarlierHead…` |
| 25 | retired keys | unit (retired verification key); missing KEK: pending integration |
| 26 | quota and limits | Authority catalogue ceiling (M1 unit); worker `RECONCILIATION_LIMIT` path: pending integration |
| 27 | no partial or misleading certificate | DB CHECK constraints on both sides; all failure tests |
| 28–30 | permissions, cross-organization, unauthorized execution | Stage B `OnlyAuthorizedPeopleRequestAVerification…`; contract `ProtectionVerifierBoundaryTests`; unit cross-organization |
| 31 | audit evidence; archival outage | Stage B complete-population test (journal entry); archival outage: pending (AUD-C7-01) |
| 32 | no plaintext in logs or reports | Stage B complete-population test (logs, audit metadata) |
| 33 | no regression | full Stage B, browser and SEC-P suites |
