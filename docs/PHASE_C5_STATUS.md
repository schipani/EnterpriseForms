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
| **U11 (new)** | Option catalog membership is validated by the business against unsigned BusinessDb catalog rows. A hostile DBA could add a selectable option for owners and contributors alike. | Not changed in C5: pre-existing and shared with owner writes. Severity assessed in §11.4: **Medium**, not a development-merge blocker, production Level 2 blocker. Tracked as **G21**. | **Open — outstanding security issue** |

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
* G21 (new: option catalog / form-definition metadata integrity, U11 — see §11.4);
* the C-12 and C-28 Authority reviews;
* the PB-14 manual accessibility reviews (nothing in C5 was manually reviewed);
* the My Records scalability concern;
* the Form Designer usability debt;
* PB-08 step-up;
* future requirements FR-C5-01…07 (not approved);
* every production Level 2 gate.

## 11. Owner acceptance preparation (2026-10-22)

### 11.1 CI test-result filename collision — fixed (C-41)

* **Problem.** `dotnet test --solution … --results-directory X --report-xunit-trx` put every executable's timestamp-named TRX file in one folder. Two executables finishing together wrote the same name, and one result was lost.
* **Fix.** `scripts/ci/run_dotnet_tests.py`:
  * discovers the solution's test projects;
  * empties the results root, so files from an earlier run can't be counted;
  * runs the projects **concurrently**;
  * has each write exactly `<results>/<Project>/<Project>.trx`.

  `assert_test_totals.py --expect-projects` then requires exactly one TRX per project and no other file, so a lost, overwritten, duplicated, stale or empty result fails the run. `ci.yml` and `scripts/dev/test.ps1` use it.
* **Proof.**
  * `scripts/ci/test_test_results.py`: 5 self-tests, run in CI, covering concurrent writers counted correctly, the PR #15 collision shape, missing, stale, duplicate, failed and empty files.
  * A local concurrent run: 4 projects, 4 files, **302 / 302**.

### 11.2 Local development stack prepared for acceptance (persistent services untouched)

* **What changed:** only the application containers `authority`, `api`, `worker` and `web` were rebuilt from the branch and recreated. Command: `docker compose … up -d --no-deps --build --wait authority api worker web`.
* **What didn't:** `vault`, `business-db`, `authority-db`, `spicedb`, `spicedb-db`, `azurite`, `clamav` and `archive-store` keep their original container IDs and creation times (recorded before and after). They weren't recreated, their volumes weren't touched, and the in-memory Vault and its development signing keys are intact. No migrations were needed (C5 has no schema change).
* **Switch.** Contributor editing is enabled **only** in this local Authority, by passing `AUTHORITY_SHARE_CONTRIBUTOR_WRITES_ENABLED=true` in the environment of that one compose command. Nothing was written to `.env` or committed. The committed default remains `false`, and the Production startup guard is unchanged. The Authority reports `Content__ShareContributorWritesEnabled=true`, `ASPNETCORE_ENVIRONMENT=Development`.

### 11.3 Acceptance and regression results on the development stack (synthetic data)

| Check | Result |
|---|---|
| `e2e/c5-owner-acceptance.spec.ts` | **5 / 5 passed**. <br>• Owner creates a record (Case title, Internal notes, Priority dropdown) and shares Case title + Priority with the clerk for editing. <br>• The clerk sees no Internal notes label or value in the UI or API, and a write to it is refused exactly like an unknown field. <br>• The clerk edits both shared fields and saves; the owner sees the changes. <br>• **Stale edit:** the owner saves while the clerk edits; the clerk's save is refused as a conflict, the clerk reapplies onto the latest version (the owner's title is kept) and saves. <br>• Owner revokes; the clerk can no longer open, read or edit (UI and API 403/404). Axe scan clean. |
| `e2e/contributor-writes.spec.ts` | **4 / 4 passed** (switch reported on, asserted) |
| Full Playwright suite against the dev stack | **54 passed, 1 failed, 1 not run** (11.2 min). The failure is `form-management.spec.ts` "designer edits an existing field and the document collection…" (not a C5 area); the "not run" test is the dependent next test in that serial file. Re-running that file: 3/3 passed, then 9/9 with `--repeat-each=3`; it passed in both hosted browser runs. The original error detail was overwritten by the re-runs, so it is recorded as an **unexplained intermittent failure**. |
| Unshared encrypted value: non-disclosure | API projection and PATCH responses for the clerk contain neither the `internal_notes` key nor its value (asserted). |
| Unshared encrypted value: preservation (read-only SQL on the dev database) | Envelope digest of Internal notes is **identical** in the revision the clerk created (rev 2 = rev 1). In the clerk's reapply revision, both Internal notes and Case title are identical to the owner's revision 3. The owner's write re-sealed Internal notes, which is the owner path's normal behaviour. No plaintext row exists for either encrypted text field. The `SharedFieldWriteBuilder` decryption-spy unit test proves nothing outside the read scope is decrypted. |
| .NET CI-equivalent (new runner) | **302 / 302**, 4 per-project TRX files |
| Result-accounting self-tests | **5 / 5** |
| vitest / typecheck | **166 / 166**; clean |

### 11.4 U11 — option catalog integrity: assessment (outstanding security issue, G21)

**What it is:**
* Dropdown values are stored as stable option ids (plaintext) or option keys (inside ciphertext). The Authority signs the row, and for share editors enforces single-item structure (C-36).
* **Which options are allowed, and what they are called**, comes from `field_options` and the published layout snapshot in `form_versions`, which are business PostgreSQL rows. Neither is row-signed, part of a record manifest, or known to the Authority.

**What a hostile business-database administrator could do:**
* insert a new option, or reactivate a deactivated one, for a field. Owners **and** contributors could then select it, the API would accept it, and the Authority would sign it as part of an otherwise authorized write;
* relabel existing options, so every record shows a different meaning for a stored choice without any write at all. This is stronger, needs no user action, and is independent of C5.

**What it does not do:**
* grant access;
* reveal or decrypt protected values;
* bypass Authority authorization, revocation, the content CAS or manifest verification. The signed value is exactly what an authorized user submitted.

**Severity: Medium.** It is an integrity and meaning problem under the Level 2 hostile-DBA threat, with no confidentiality or authorization impact. It is a specific case of the wider unsigned form-definition metadata gap (form versions, layouts, option catalogs; related to G03/G18).

**Assessment:**
* **Not a development-merge blocker.** It pre-exists C5, affects owner writes identically, is not widened in capability by contributor writes (the administrator can already relabel), and development uses synthetic data only.
* **A production Level 2 blocker** unless resolved or explicitly accepted after independent review. Possible directions, **not implemented and not designed here**:
  * an Authority-anchored signed form-version snapshot that includes the option catalog, with write validation against that snapshot;
  * binding the option key and label into the signed value.

  Either changes the trust model and needs its own design review.

No change was made to the Authority or the trust model for U11.

### 11.5 Browser acceptance instructions (owner)

1. Use the rebuilt local stack (above). Start the web app as usual (`npm run dev -w apps/web`, http://localhost:5173), or reuse your running Vite.
2. **Sign in as Avery Owner (owner).** Create or pick a form with two text fields and a single-choice dropdown, and create a record. Or use the "C5 Acceptance …" form the automated run published (Case title, Internal notes, Priority).
3. Record page → **Sharing and access** → **Share fields**:
   * Person **Casey Clerk**; Role **Contributor**. The wording says "change their values".
   * Tick **only** one text field and the dropdown. Fields a contributor could not change show a "View only" badge.
   * **Share**. The table shows "Can change values: Yes".
4. Sign out. **Sign in as Casey Clerk (clerk)** and open the record from My Records:
   * "Partial view (shared fields only)" appears and the unshared text field is absent;
   * click **Edit shared fields**: the banner says you can change 2 shared fields;
   * change both; **Save changes** → "Changes saved".
5. **Stale edit:** as the clerk, open **Edit shared fields** and change the dropdown, but don't save. In another browser (or a private window), sign in as the owner and change the shared text field. Back as the clerk, **Save changes**:
   * "Someone else saved this record while you were editing" appears;
   * choose **Review and reapply my changes**, check the values, save.
6. As the owner: the record shows the permitted changes, the unshared field is unchanged, and **Sharing history** lists the shared edits. **Revoke** the clerk's share → "Access revoked for Casey Clerk".
7. As the clerk: the record and its edit page now show not found or no permission.

### 11.6 Returning the local feature switch to disabled after acceptance

Recreate only the Authority with the committed default (no variable set):

```bash
cd infrastructure/docker
docker compose -f compose.yaml -f compose.dev.yaml --env-file .env up -d --no-deps --wait authority
docker inspect enterpriseforms-authority-1 --format '{{range .Config.Env}}{{println .}}{{end}}' | grep Content__
#   expect: Content__ShareContributorWritesEnabled=false
```

This recreates only the Authority container (no rebuild, no other service). Persistent data, Vault and keys are untouched. Any later compose command run without the variable also leaves the switch off. To turn it on again, prefix the command with `AUTHORITY_SHARE_CONTRIBUTOR_WRITES_ENABLED=true`. Never put the variable in a committed file; a Production Authority refuses to start with it on.

## 12. Closure preparation (owner acceptance, 2026-10-22)

### 12.1 Owner functional acceptance (decision C-42)

The project owner personally tested Phase C5 and reports that contributor editing works very well. The owner accepts, **for continued development**:
* sharing individual record fields;
* contributor editing of authorized fields;
* protection of unshared fields;
* concurrent editing and conflict resolution;
* access revocation;
* sharing history.

This is an owner functional review. It is **not** an independent security review or certification, and **not** approval for production use. Contributor editing stays disabled by default and is refused in Production until G20 is closed.

### 12.2 Hosted verification on `1fc1393` (latest code commit; later commits are documentation only)

| Workflow | Run | Result |
|---|---|---|
| ci — the four **required** checks (Specifications/manifest/vectors; .NET build, architecture, contract tests; Frontend lint/typecheck/tests/build/client drift; Docker Compose profiles) | 39765425192 | **all success**. The new runner: 5 accounting self-tests; 4 projects run concurrently, 4 fixed-path TRX files, **302 / 302**. |
| security-gate-a (manual dispatch) | 39765416531 | **13 / 13**; evidence SEC-P01…SEC-P12 = **PASS, bound to `1fc1393`**. SEC-P also passed on `fcdb52a` (39744222709) and `ff9711d` (39740734569). |
| stage-b-integration — browser | 39765425164 | **56 / 56**, none skipped, including all five `c5-owner-acceptance.spec.ts` steps with contributor editing asserted on |
| stage-b-integration — integration | 39765425164 (attempts 1 and 2) | **64 / 65 both times — not green.** Attempt 1 failed `FieldProtectionDowngradeTests.InterruptedAndTamperedMigrations…` (legacy-encrypted count 2, expected 1). Attempt 2 failed `FieldProtectionMigrationTests.ConcurrentUserEditsAndAWorkerRestart…` (migration Completed 24/24, but legacy-plaintext count 1). All seven `ContributorWriteTests` passed in both attempts. See §12.3. |
| level3-script | 39765425172 | success |

### 12.3 Known pre-existing intermittent integration failure (not fixed in C5; not a required check)

* **Same family as Phase C1.** Both failures belong to the field-protection migration test family that Phase C1 documented as flaky and **unresolved** (`docs/PHASE_C1_UX_STATUS.md`). Both are an off-by-one in a **business-side status count** (`BusinessStore.CurrentOperationsAsync`, used for the legacy plaintext and legacy encrypted record counts) after the Authority or the worker is interrupted mid-migration.
* **Likely cause (from code reading, not proven by these runs' logs).** When the record's accepted-operation pointer has not yet been repaired, the count falls back to "highest staged revision". That is ambiguous when an abandoned candidate shares a revision with the committed one.
* **What still held.** The Authority-backed checks in those tests behaved correctly: the tampered record failed closed; `fullyPlaintext` and `fullyEncrypted` were not claimed while the counts disagreed.
* **Not caused by C5.** C5 does not change this code. The same backend code passed the whole suite on `ff9711d`, `fcdb52a` and `a59749e`; `1fc1393` differs only in CI scripts, an end-to-end spec and documents, none of which the integration job uses.
* **Disposition.** Recorded as an open defect needing its own fix: make the status count use the Authority-accepted head, or disambiguate same-revision candidates. Do not paper over it with retries. It does not block the required checks.

### 12.4 Local development switch restored to disabled (persistent data preserved)

* Only the Authority container was recreated, with no rebuild and no other service: `docker compose … up -d --no-deps --wait authority`, without the variable. Before/after container IDs show `vault`, `business-db`, `authority-db`, `spicedb`, `spicedb-db`, `azurite`, `clamav`, `archive-store`, `api`, `worker` and `web` unchanged. No data, volume or Vault signing key was touched.
* **Configuration:** the running Authority shows `Content__ShareContributorWritesEnabled=false`, `ASPNETCORE_ENVIRONMENT=Development`.
* **Behaviour:** the C5 browser specs run against the dev stack with `EXPECT_CONTRIBUTOR_WRITES=false` gave **5 passed, 4 skipped**. The Authority reported contributor writes disabled; the UI offered no "Edit shared fields" and showed "Changing shared fields is not enabled"; the acceptance editing steps were skipped.
* **Committed defaults are unchanged:** `appsettings.json` `"ShareContributorWritesEnabled": false`; `compose.yaml` `${AUTHORITY_SHARE_CONTRIBUTOR_WRITES_ENABLED:-false}`.

### 12.5 Production startup guard — verified by starting the real binary

* `ASPNETCORE_ENVIRONMENT=Production Content__ShareContributorWritesEnabled=true dotnet AuthorizationAuthority.Api.dll` aborts at startup: `InvalidOperationException: Content:ShareContributorWritesEnabled must not be enabled in Production: field-scoped contributor writes await the independent Level 2 security review (Phase C5, C-34).`
* With the switch off, the same binary passes this guard and stops at the next, unrelated Production guard (`Signing:Provider is not configured`). That shows the C5 guard is enforced first and independently.
* Unit coverage: `ShareContributorWriteSwitchTests`.

### 12.6 Still open (unchanged)

* G20 (independent review of the C5 Authority change) and G21 (U11, option-catalog integrity);
* U1–U11;
* G06 and G19;
* the C-12 and C-28 Authority reviews;
* PB-14 manual accessibility reviews;
* the My Records scalability concern;
* the Form Designer usability debt;
* PB-08 step-up;
* FR-C5-01…07 (not approved);
* every production Level 2 gate;
* the unexplained intermittent Playwright failure (§11.3);
* the field-protection status-count intermittent defect (§12.3).

### 12.7 Merge readiness (PR #15)

* **Branch protection on `main`:** the four `ci` checks above are required, with strict up-to-date (the branch is based on current `main`, `cf45a9e`); signed commits and linear history (satisfied by a GitHub squash merge); a pull request with no required approvals.
* **Required checks:** pass on `1fc1393`, and must pass again on the documentation-only closure head.
* **Not merged.** The merge is the owner's decision. The red, non-required Stage B integration run (§12.3) is disclosed for that decision.
