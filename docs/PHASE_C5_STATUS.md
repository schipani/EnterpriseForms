# Phase C5 — Collaboration and field-scoped contributor writes: implementation status

Branch `phase-c/collaboration` from main `cf45a9e`. Architecture: `docs/architecture/PHASE_C5_COLLABORATION_CONTRIBUTOR_WRITES.md` (revision 2), approved for controlled development by the owner on 2026-10-22 (decision **C-34**, eight mandatory conditions). Implementation decisions are **C-35 … C-40** in `docs/decisions/PHASE_C_DECISION_LOG.md`.

**What this is not:**
* not an independent cryptographic or security review;
* not a production approval or Level 2 certification;
* not permission to enable contributor writes in production.

Contributor writes are **off by default in every configuration**, and a production Authority refuses to start with them on. All testing used synthetic data on disposable stacks. The owner's persistent development stack (Vault, PostgreSQL, SpiceDB, keys) was not touched; it still runs pre-C5 images.

## 1. What was built

| Milestone | Delivered |
|---|---|
| **A — security foundations** | Identity binding I1–I3 for every user and reprotection write (C5-F1); partial-scope writes no longer run the owner candidate builder, so nothing outside the editor's read scope is decrypted (C5-F2); one indistinguishable `403 FIELD_NOT_EDITABLE` for every key outside the editable projection (C5-F3); rule eligibility R1/R2, static and dynamic (C5-F4); default-off switch with a Production startup guard (D10). |
| **B — Authority contributor-write processing** | `ContentDeltaRules`: a column-class table (framing, identity, content, operation binding, storage key) for every supported entity kind and schema version; current-snapshot binding (D4); share-editor classification with strict field scope (D3, D14, D15); commit-time re-authorization against current desired edges under the organization lock; additive `readable_items`, `editable_items` and `contributor_writes_enabled` on `VerifyRecordProjection` (D7). Business `SharedFieldWriteBuilder` builds verbatim-preserving candidates. |
| **C — collaboration UX** | **Edit shared fields** on the record page; a shared-mode editor (only the shared fields, view-only fields marked Read-only with reasons, only changed values sent); honest role wording that follows the switch; a "View only" preview in the share dialog; **Can change values** column; a revocation confirmation notice; a **Sharing history** card; user-facing titles for the new error codes. |

## 2. U5 — commit-time authorization: verified in code, with a reliable guarantee

**Condition 5 asked whether every relevant permission change takes part in the same synchronization. It does.**
* All relationship edges (owner, share role, field-item, team membership, parent) and share lifecycles change only in `SecurityCommandService.ApplyPlan`. That runs inside a transaction that first takes `SELECT … FOR UPDATE` on the organization's `organization_authorization_states` row (`AuthorityStore.LockOrganizationAsync`, READ COMMITTED).
* `OrganizationBootstrapService` creates organizations; it does not change the edges of an existing one.
* No other code path writes `authority_relationship_edges` or share resources.

**Content commits take the same lock first.** In that same transaction they then:
1. read the current desired edges;
2. re-authorize every stored mutation (`AuthorizeMutations`, which also re-checks the switch);
3. CAS the head;
4. write the manifest, head, journal and checkpoint.

Under READ COMMITTED every statement after the lock sees all committed revocations. So a revocation either commits first, and the content commit aborts with `PERMISSION_REVOKED_BEFORE_COMMIT`, or commits after an edit that was authorized when it took effect.

**SpiceDB** is not consulted for content mutations. It only serves read/edit flags, and while it lags the organization gate is `AwaitingProjection`, so every protected operation fails closed.

Tests:
* `ContributorWriteProtocolTests` covers revocation, team-member removal and switch-off between prepare and commit, against the real `ContentService`.
* `ContributorWriteTests` (Docker) drives the same races deterministically with the Testing-only `PauseBeforeContentCommit` point, plus a load race in which no accepted contributor write may carry an authorization revision at or after the revocation.

**No escalation was needed.**

## 3. Condition-by-condition record (C-34)

| # | Condition | How it is met | Evidence |
|---|---|---|---|
| 1 | D3 single-select validity at a trusted boundary, not on Business API assertions alone | The Authority enforces, from the signed bytes, that the field holds exactly one current item and that an update replaces it with exactly one item of the same field, item id, entity kind and top-level position. Multi-selections are refused. Plaintext and encrypted selections are covered alike. The business validates option membership, active state and dependent-option rules against the published catalog, as for owner writes. **The option catalog itself is not Authority-anchored** (a pre-existing gap shared with owner writes): recorded as U11 for the reviewer, not hidden. | `ContentDeltaRulesTests.SingleSelectionChangesAreUpdatesButMultiSelectionsAreRefused`, `PhaseC5SharedEditTests.ASingleSelectChangeIsValidatedAgainstTheActiveOptionCatalog` |
| 2 | D4 fingerprints only with proof of the complete accepted, verified state; fail closed on any uncertainty | `BindCurrentSnapshot` requires: <br>• exact key coverage and no duplicates; <br>• SHA-256 per item equal to the AuthorityDb digest; <br>• header organization and key version; <br>• leaf count equal to the manifest commit's; <br>• a Merkle root **and** leaf-index digest recomputed from the accepted signatures and observed digests, equal to the accepted head. <br>The head's commit row must agree with the head (id, revision, key version, root), and the key version must still be verifiable. Every accepted leaf was HMAC-verified when its head was committed. A mismatch journals `CurrentSnapshotMismatch` and signs nothing. | `ContentDeltaRulesTests.TheExactAcceptedSnapshotBinds`, `…IncompleteExtraDuplicatedOrAlteredSnapshotsDoNotBind`; `ContributorWriteProtocolTests.ATamperedCurrentSnapshotIsRefusedJournaledAndNothingIsSigned` |
| 3 | D14 every signed column of every supported type and schema classified; unknowns fail closed | `ContentDeltaRules.Rules` covers `form_records` v2/v3, seven plaintext value kinds, `field_encrypted_values`, `field_option_values`, `form_record_groups` and `file_attachment_versions`. An architecture test proves that the set of (entity kind, schema version) pairs and every column set equal `BusinessSignedSchemas.All`. Unknown kinds, schema versions or extra columns fail with `UNSUPPORTED_SIGNED_SCHEMA`, for owners too. `file_attachment_lifecycle_events` has no producer and no assignment, so it fails closed. | `SignedColumnRegistryTests.TheAuthorityColumnClassTableCoversEveryBusinessSignedSchemaExactly`; `ContentDeltaRulesTests.UnknownSchemasExtraColumnsAndWrongTenantsOrRecordsFailClosed` |
| 4 | D15 byte-for-byte preservation with new operation ids, revisions, unique row ids and signatures validated; no stale bindings | Current rows must carry exactly the head's operation id and revision. Candidate rows must carry exactly the new operation id and the next revision; anything else is `CANDIDATE_OPERATION_BINDING`. Surrogate keys must be fresh and unique. Identity keys must be equal. Every leaf is re-signed under the new operation. Business copies keep each row's signed schema version (a v2 header stays v2). | `ContentDeltaRulesTests.CandidateOperationBindingMustBeTheNewOperationAndRevision`, `…AReusedOrDuplicatedStorageKeyIsRefused`, `…PreservedCiphertextAndArtifactsAreUnchanged…`; `PhaseC5SharedEditTests.EveryUneditedRowIsCopiedVerbatim…`; Docker `AContributorChangesSharedValuesAndEveryOtherItemIsPreservedByteForByte` (SQL comparison of ciphertext and document rows across operations) |
| 5 | U5 commit-time authorization | §2 | §2 |
| 6 | U7 legacy rows fail closed, no bypass | Current rows whose operation binding is not the head's are refused (`CURRENT_SNAPSHOT_LEGACY_BINDING`). There is no compatibility path. See §6 for affected formats. | `ContentDeltaRulesTests.LegacyCurrentRowsWithAnotherOperationBindingFailClosed` |
| 7 | V1 limits (D2, D3, D5, D6) | Only existing top-level single-item values change. Clearing, filling empty fields, multi-select, entries, documents, child records, status, Restricted fields and rule-dependent fields are refused by both the business and the Authority, wherever the Authority can see the difference. FR-C5-01…07 are not implemented. | Business and Authority suites; UI copy |
| 8 | Production guard, default-off switch, Level 2 review | `Content:ShareContributorWritesEnabled` defaults to false in `appsettings.json` and every compose profile (`${AUTHORITY_SHARE_CONTRIBUTOR_WRITES_ENABLED:-false}`). Only the literal `true` enables it. It is enforced at prepare **and** commit. Production startup fails if it is on. The Testing-only override exists only in the Testing environment, behind the operator credential. The Level 2 review stays required (G20). | `ShareContributorWriteSwitchTests`; `ContributorWriteProtocolTests.TurningTheSwitchOff…`; Docker switch-race test |

## 4. Findings C5-F1 … C5-F5

| ID | Status |
|---|---|
| C5-F1 identity not bound in updates | **Fixed**: I1–I3 for every write; K-class equality for share editors. |
| C5-F2 partial path decrypted out-of-scope values | **Fixed**: partial scope never calls `CandidateBuilder`. The switch is checked before any candidate is built. A decryption-spy test asserts nothing outside the read scope is decrypted. |
| C5-F3 field-key existence oracle | **Fixed** for the partial path: unknown, unshared, read-only and ineligible keys get the identical response, checked before any validation or decryption. Timing is not normalized (U6). |
| C5-F4 partial writes skipped rules | **Fixed**: R1/R2 static eligibility plus dynamic evaluation over readable values only; generic refusal for effects on other fields. |
| C5-F5 UI promised edit | **Fixed**: role copy follows the switch; View only reasons; share coverage explained; revocation notice. |

## 5. Deviations from the proposal (implementation choices, all fail-closed)

1. **Business switch.** There is no separate business configuration. The business reads `contributor_writes_enabled` from the Authority's verification and refuses before building a candidate when it is off. One switch, held by the Authority, decides; the business cannot be enabled on its own.
2. **D7 scope.** The verification returns both `readable_items` and `editable_items` for **every** partial projection (Read and Edit), not only Read. This costs one extra SpiceDB bulk check per partial verification.
3. **Production guard scope.** The guard covers the **Production** environment. Staging may enable the switch for the independent reviewer's tests, using synthetic data only.
4. **Testing-only hooks** (operator credential, Testing environment, `FaultInjection:Enabled`):
   * `POST /internal/v1/operator/testing/contributor-writes` (override);
   * `PauseBeforeContentCommit` (holds a commit before the organization lock, bounded at 60 s);
   * `GET /internal/v1/operator/fault-injection` (status).

   They follow the existing SEC-P09 fault-injection pattern and are refused in any other environment.
5. **Error mapping.** Authority scope refusals reach the user as `403 CHANGE_NOT_PERMITTED`. The Authority's reason codes contain no item ids and stay in the Authority response/journal.
6. **Collaboration history** reuses the record activity API (owner read permission). Shared edits are audited with a distinct description so they can be listed without exposing metadata or values.

## 6. U7 — legacy and synthetic formats affected (reported separately, as required)

* **Records whose current rows carry another operation binding.** This includes rows staged by any historical path that did not set the head's operation id on every row. Share-scoped writes fail closed (`409 CURRENT_SNAPSHOT_MISMATCH: CURRENT_SNAPSHOT_LEGACY_BINDING`). Every current business staging path (`RecordCandidateStager`, document processing → staging, reprotection) sets the head's operation and revision on every row, so this is expected only for hand-made fixtures. A real affected record recovers through an ordinary owner write, which re-stages every row.
* **`form_records` v2 headers (Stage A rows).** Supported, and preserved at v2 by share writes; not affected.
* **`file_attachment_lifecycle_events`.** No business producer exists. Any record that contained such an item would refuse share writes (and now owner writes too, by identity binding). None exist.
* **Synthetic fixtures.** The Stage A security harness candidates (SEC-P11) remain valid: the forged header still receives 403 and the cross-organization candidate 422. No existing fixture needed a bypass.

This could not be checked against the owner's persistent development data without touching it. Rows created by current code satisfy the invariants by construction.

## 7. Unresolved reviewer issues U1–U11 — disposition (no independent review has taken place)

| ID | Issue | Disposition in this implementation | Status |
|---|---|---|---|
| U1 | Digest binding vs HMAC re-verification of current items | Implemented as digest binding, **strengthened**: the Merkle root and leaf-index digest are also recomputed from the accepted signatures (D4, condition 2). Zero extra MAC calls. | Awaiting independent review |
| U2 | Correctness of the column-class table | Implemented as in proposal §5.3; cross-checked against the business registry by an architecture test; unknown kinds, versions and columns fail closed. | Awaiting independent review |
| U3 | Ciphertext preservation (AEAD/AAD, envelope allow-list, nonce/DEK-differ) | AAD analysed in code (§6 of the proposal: value AAD = tenant, record, field, item, logical type; document AAD = envelope version, tenant, record, attachment, version number, segment). The allow-list is AES-256-GCM, envelope v1. Updates must change both the nonce and the wrapped DEK. | Awaiting independent review |
| U4 | Form rules enforced by the business only | Implemented R1/R2 in the business; the Authority does not evaluate form rules, for any writer. | Awaiting independent review |
| U5 | Commit-time authorization | Verified in code and tested (§2). | Implemented; awaiting independent review |
| U6 | Timing/error-shape side channels | Error shape unified; timing not normalized. | Open |
| U7 | Legacy rows | Fail closed, no bypass (§6). | Implemented; awaiting independent review |
| U8 | Delegation chains and team shares at commit | The commit uses the same `DesiredStateGraph` as share commands; team-member removal tested. | Awaiting independent review (relates to C-12) |
| U9 | Snapshot mismatch journals but does not quarantine | As proposed: journal incident, no quarantine. | Awaiting independent review |
| U10 | Single-select updates | Implemented with the Authority single-item rule (condition 1). | Awaiting independent review |
| **U11 (new)** | Option catalog membership is validated by the business against unsigned BusinessDb catalog rows. A hostile DBA could add a selectable option for owners and contributors alike. | Not changed in C5 (pre-existing, shared with owner writes; relates to G03 form-definition integrity). | Open, for the reviewer and the owner |

## 8. Verification record (local, synthetic)

| Suite | Result |
|---|---|
| .NET, CI-equivalent (`EnterpriseForms.All.slnx`, Release, `--minimum-expected-tests 10`, `assert_test_totals --min-total 130 --min-files 4`) | **302 / 302 passed** (242 before C5): Authority 91, contract 111, architecture 66, integrity 34 |
| Frontend unit (vitest) | **166 / 166 passed** (163 before C5) |
| Frontend typecheck / ESLint | clean |
| Specification validator (`--strict`) | pass |
| Docker integration (`ContributorWriteTests` + updated `RulesTests`), Playwright (`contributor-writes.spec.ts`), SEC-P01…SEC-P12 | **Hosted only** — they cannot run beside the owner's persistent dev stack. See §9. |

## 9. Hosted verification (isolated, disposable GitHub runners; draft PR #15)

| Commit | Workflow | Run | Result |
|---|---|---|---|
| `ff9711d` (implementation + docs) | ci | 39740725730 | success |
| | stage-b-integration — integration | 39740725594 | **65 / 65**, all seven `ContributorWriteTests` passed by name; `RulesTests` passed |
| | stage-b-integration — browser | 39740725594 | **51 / 51**, including the four `contributor-writes.spec.ts` tests |
| | security-gate-a (manual dispatch) | 39740734569 | **13 / 13**; evidence SEC-P01…SEC-P12 = PASS, bound to `ff9711d` |
| | level3-script | 39740725917 | success |
| `fcdb52a` (test hardening: CI asserts the switch mode; the load race revokes only after a write lands) | ci | 39744229937 | success on re-run (see note) — test floor 4 files, **302 / 302** |
| | stage-b-integration — integration | 39744230036 | **65 / 65**, all seven `ContributorWriteTests` passed |
| | stage-b-integration — browser | 39744230036 | **51 / 51**. The spec now **asserts** that the Authority reported contributor writes enabled (`EXPECT_CONTRIBUTOR_WRITES=true`), so the editing path itself was exercised: contributor edit, owner sees the change and the sharing history, revocation notice, axe scans |
| | security-gate-a (manual dispatch) | 39744222709 | **13 / 13**; SEC-P01…SEC-P12 = PASS, bound to `fcdb52a` |
| | level3-script | 39744229841 | success |

**Note — the first `ci` attempt on `fcdb52a`:**
* The .NET test step itself passed: 302 tests, including the Authority executable.
* The solution-wide floor then found only 3 of 4 TRX files (211 tests), because two test executables wrote timestamp-named TRX files at the same instant and one overwrote the other.
* This is a pre-existing CI fragility unrelated to C5. A re-run of the failed job passed (4 files, 302/302).
* It is left as is: making TRX names unique is a CI change for its own commit.

These are development results on synthetic data. They are not Azure staging evidence, not an independent review, and not a Level 2 claim.

## 10. Still open (unchanged by C5)

* G06 and G19;
* G20 (new: independent review of this Authority change);
* the C-12 and C-28 Authority reviews;
* the PB-14 manual accessibility reviews (nothing in C5 was manually reviewed);
* the My Records scalability concern;
* the Form Designer usability debt;
* PB-08 step-up;
* future requirements FR-C5-01…07 (not approved);
* every production Level 2 gate.
