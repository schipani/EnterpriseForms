# Indexed search (Option C): Tier 1 owner-review evidence (C-123)

- **C-124 update:** OR-1 and OR-2 remain **open** until the owner accepts design §18.1 and §18.2; OR-3 and OR-4 contracts are in §18.3 and §18.4. Current status and the readiness recommendation: `INDEXED_SEARCH_TIER1_READINESS_ASSESSMENT.md`.
- **Status:** evidence prepared for the project owner's **owner-led architectural and security review** (kind 1 in `docs/governance/REVIEW_AND_AUTHORIZATION_WORKFLOW.md`). **The owner's decisions on it are not yet recorded.**
- **Not** an independent third-party security assessment and not an attestation. Independent assessment remains an outstanding production-readiness requirement.
- **Prepared by:** Claude (Opus), the design author. The author reviewing its own design is a known limitation; the owner is the reviewer.
- **Evidence base:**
  * design revision 3 with C-122 updates (`INDEXED_SEARCH_PROJECTION_DESIGN.md`, "design"), R1, Amendment A2;
  * source inspection at `e830d08`. The branch `design/indexed-search-projection` has no code changes since then (`git diff --stat e830d08 HEAD -- src infrastructure trust_authority` is empty).
- **Nothing in this document was executed.** No test was run, nothing was measured. Every guarantee below is a **design claim**, not an experimentally verified property. "Code fact" means checked by reading the cited lines.
- **Scope:** J-1 to J-14 of the review package §6.2. Tier 2 items are covered only to state that they are **not accepted** (C-123 §4).

## 1. Summary

### 1.1 Owner-review findings (new in this review)

| ID | Finding | Kind | Blocks |
|---|---|---|---|
| **OR-1** | **The design overstates I-2.** It claims an Authority-domain change "that would add a match" is never served. But I-2 is unkeyed and compares against rows the runtime role can rewrite (leaves, head root). A writer holding the runtime database credential can rewrite posting, leaf digest and head root consistently and pass I-2. Population still comes from SpiceDB, so this is a wrong-result (integrity) issue, not disclosure of unauthorized records | Claim correction; optional design change | Tier 1 implementation (the claim must be corrected and a decision taken before IX-3) |
| **OR-2** | **I-1's revision equality would let one request quarantine the whole index.** `ContentIntegrityRevision` is advanced at **four** sites, not only on commit. One is a prepare-time integrity incident (`ContentService.cs` 1235) that a share-editor caller can trigger by sending a mismatched current snapshot. The design (R1 §C.1 step 5) updates the projection revision only on commit. So that incident makes the projection revision unequal, I-1 fails, and the organization's index is quarantined | **Design defect** | **Tier 1 implementation** (design fix before IX-1) |
| OR-3 | Commit derivation placement: `CommitAsync` commits the transaction on four abort paths before the head changes (lines 248, 260, 292, 321). Derivation must be on the success path only | Contract precision | Tier 1 implementation (contract clause, no design change) |
| OR-4 | R1 §B.2 says result snapshots are purged "like `search_catalogue_snapshots`". There is **no** catalogue-snapshot purge today: no delete code, and the runtime role has no `DELETE`. Catalogue snapshots are user-bound but **not session-bound** | Claim correction | Tier 1 implementation (purge and session binding are new work, to be costed) |

### 1.2 Recommended decisions and blocking

| Item | Subject | Recommended decision | Blocks |
|---|---|---|---|
| J-3 | Atomic commit boundary | Accept, with contract clauses OR-3 and OR-2's fix | Tier 1 implementation, until OR-2 is fixed |
| J-4 | Per-match integrity binding | Accept the corrected claim (OR-1); adopt per-page seal verification, pending PERF-01 | Tier 1 implementation, until the claim is corrected and the option decided |
| J-5 | Tier 1 SpiceDB and S1 consistency | Accept | None for implementation; RE-1 residual stays |
| J-9 | Result revocation and cross-instance invalidation | Accept, with OR-4 | Tier 1 implementation (OR-4 costing) |
| J-7 | Rollback and partial restore | Accept, conditional on OR-2 | Tier 1 implementation, until OR-2 is fixed |
| J-10 | Quarantine separation | Accept | None |
| J-8 | Sweep deadlines (C-123, UF-13) | Accept the C-123 contract (design §17.10) | Production (PERF-01 throughput) |
| J-1 | Separate search process (FD-6) | Not required for Development/Testing; decide before production | Production |
| J-2 | Plaintext-exposure inventory, R-D | Accept for Development/Testing; Azure verification required | Production |
| J-11 | DR and rollback with G14 unimplemented | Accept as a known gap | Production |
| J-12 | Implicit relaxations, ENC-SR text | Correct A2 wording (OR-1, OR-2); ENC-SR text unchanged until FD-5 | Tier 1 implementation (wording only) |
| J-13 | OBS-IX-1 | Keep open; owner chooses among options (OBS-IX-1 §6) | Production; not Tier 1 |
| J-6 | Tier 2 completeness | **Not accepted** (C-123 §4) | Tier 2 |
| J-14 | Overall | Tier 1: acceptable for an IX-1 authorization decision **after** OR-1 and OR-2 are dispositioned. Tier 2: not acceptable | — |

---

## 2. Priority items

### J-3. Atomic transaction boundary

1. **Claim.** Postings and the entry seal are derived inside the content-commit transaction, from the bytes that transaction verified, and commit or roll back with the head. A derivation or seal failure aborts the commit.
2. **Design clauses.** Design §3, §7 (R1 §C.1); IX-D1; A2 P-6.
3. **Code facts (`ContentService.cs`, `e830d08`):**
   - one scope per call (`BeginScopeAsync`, line 208); organization row locked `FOR UPDATE` (line 209; `AuthorityStore.cs` 54); gate open (211);
   - head locked `FOR UPDATE` (232; `AuthorityStore.cs` 164); revision compare-and-swap (257);
   - commit-time authorization against current canonical edges (284–286);
   - `VerifyStagedAgainstLeavesAsync` (302) checks, for every staged item: key set, header binding, `SHA256(bytes)` = candidate digest, signature equality, MAC verification, and the rebuilt root (940–1023);
   - manifest, leaves, head, revision, journal, checkpoint added (329–385); single final commit (387);
   - **early commits** of an aborted operation state at 248 (expired), 260 (revision conflict), 292 (permission revoked), 321 (record locked): the transaction is committed, then the call throws;
   - the head is written only at 358–363; quarantine flags at 732 and 898; registration creates heads at revision 0 (`SecurityCommandService.cs` 181–187).
4. **Security assumptions:**
   - the derivation reads only rows locked or written in this transaction (ReadCommitted is then sufficient);
   - the MAC provider is reachable for the seal (the commit already depends on it for verification);
   - no other code path writes heads (to be enforced by an architecture test).
5. **Failure scenarios and adversarial tests:**

   | Scenario | Expected | Test |
   |---|---|---|
   | Derivation throws after leaves are added | Whole commit rolls back; head unchanged | **New: IX-64** (fault injection before 387) |
   | Seal sign fails (provider unavailable) | Commit fails 503; head unchanged | **New: IX-65** |
   | Crash after 387 (`CrashAfterContentHeadCommit`) | Head and postings both present | **New: IX-66** (existing fault name) |
   | Abort path at 248/260/292/321 | **No** posting change committed | **New: IX-61** (OR-3) |
   | New head writer or revision increment added later without a hook | Architecture test fails | **New: IX-63** (OR-2) |
   | Builder backfill races a commit | Backfill's head CAS refuses (`HEAD_CHANGED`) | **New: IX-67** (R1 has no race test; IX-25/26 cover forgery and withholding) |

   R1's catalogue has no atomicity test identifiers; IX-61 and IX-63 to IX-67 are proposed here.
6. **Remaining risks:**
   - seal sign latency is added inside the organization lock: one MAC sign per commit, unmeasured (UF-3, IX-1 gate "commit p95 delta ≤ 20 ms");
   - OR-2 (revision increments outside commit) affects I-1, not atomicity, but must be handled in the same hook design.
7. **Recommended decision:** **accept**, with two binding contract clauses:
   - **(a) OR-3:** derivation and sealing run only between line 326 and the final commit at 387; no abort path may write `authority_search` rows;
   - **(b) OR-2:** the hook set is defined by the **`ContentIntegrityRevision` increment sites**, not only by head writers (see J-7).
8. **Blocks:** Tier 1 implementation until OR-2 is fixed in the design. Not Tier 2-specific.

### J-4. Per-match integrity binding (I-2)

1. **Claim (design §6.3, as written).** Every returned match's predicate-field posting hashes to the accepted leaf digest, and the record's leaves recompute the head root. "No posting can add a match that the accepted content does not support." §6.4 and §17.5: an Authority-domain change that would add a match is "never served".
2. **Design clauses.** §6.3, §6.4, §5.2 (row "AuthorityDb administrator edits rows"), §17.5; A2 R-A.
3. **Code facts:**
   - leaf hash = SHA-256 over `0x00 || "DynamicForms/MerkleLeaf/v1" || key || row signature || canonical payload digest || kind || revision || operation` (`MerkleManifestV1.cs` 52, 70–111). It is unkeyed;
   - `canonical_payload_digest` = `SHA256(canonical bytes)` (`ContentService.cs` 993; at prepare, 1103);
   - leaves (`record_manifest_leaves`) and manifest commits are "append-only" tables on which the runtime role has `UPDATE` and `INSERT`; heads (`record_content_heads`) are mutable (`AuthorityRuntimeGrants.cs` 12–24, 49–57);
   - the open path binds the head to its journal entry **by operation id only** (`FindContentJournalForHeadAsync`, 1350–1358) and checks it is `Anchored`. It does not compare the head root with the journal body.
4. **Security assumptions:**
   - Level 2 (`CLAUDE.md` §4M): the Authority runtime and its administrators are trusted;
   - the adversary of concern is the business DBA, who has no path to AuthorityDb (G08).
5. **Failure scenarios:**

   | Actor | Action | I-2 result | Detected by |
   |---|---|---|---|
   | Business DBA | Any BusinessDb change | Not on the path | Open path; S-C |
   | Authority-domain writer, posting only | Change a posting value so a non-match matches | **Refused** (digest mismatch) | Every query |
   | Authority-domain writer with runtime-role SQL, **coordinated** | Rewrite posting bytes, the leaf's digest **and** the head root (or insert a new manifest commit and repoint the head) | **Passes**: nothing keyed is checked | Entry seal (S-B, ≤ 24 h under C-123); open path (BusinessDb bytes no longer match the head root → record quarantine); G14 archive comparison (not implemented) |
   | Same, with the MAC provider | Also re-seal | Passes | Archive/ledger comparison only (G14) |

   **Impact of the coordinated case:** a record **the caller is already authorized to discover** (SpiceDB decides the population, Tier 1) appears as a false match. No unauthorized record is disclosed. The wrong result is an integrity failure.

   **Tests:** IX-34 covers the posting-only case. **New: IX-62**, coordinated posting + leaf + head-root rewrite as the runtime role: assert the design's corrected behaviour (served until S-B, or refused at page level if option (b) below is adopted).
6. **Remaining risks:**
   - I-2 is a consistency check against Authority-domain state, not a defence against a hostile writer in that domain;
   - the canonical decoding and normalization parity of I-2 step 2 (multi-select, string normalization) is unverified (IX-36).
7. **Recommended decision:**
   - **(a) Required:** correct the claim in design §5.2, §6.3, §6.4 and §17.5: additions are refused per query for posting-only tampering; coordinated Authority-domain tampering is detected by seals within the S-B deadline and on open. Done in this commit as a marked correction (OR-1).
   - **(b) Recommended design change, for owner decision:** **per-page seal verification.** Before a page is returned, verify the entry seal of every record on that page: at most one MAC verification per displayed record. This refuses displayed false additions against anyone who can write AuthorityDb but cannot use the MAC provider (for example a stolen database credential). Counts may still include a false match; they reveal nothing unauthorized. Cost must be confirmed in PERF-01 (UF-3).
   - **(c)** Option C of OBS-IX-1 (column grants plus triggers) narrows but does not close this. `INSERT` of a new manifest commit and repointing the mutable head remain possible.
8. **Blocks:** Tier 1 implementation until (a) is accepted and (b) is decided. (b) itself, if adopted, is part of IX-3.

### J-5. Tier 1 SpiceDB and S1 consistency

1. **Claim.** The population is decided by SpiceDB at an S1 pin. The AuthorityDb candidate read (REPEATABLE READ) and the pin are reconciled by S1 verify and the revision checks, so no result combines two authorization states.
2. **Design clauses.** §8.1; R1 §E.1–§E.3; IX-D4; A2 P-3, P-7.
3. **Code facts:**
   - S1 pin → decide → verify, with history-bound markers (`PROJECTION_INTEGRITY_S1_OPERATIONS.md` §1; `ProjectionIntegrityVerifier.cs`);
   - verify re-reads Authority state and compares `LatestSecurityJournalDigest` and revisions (`ProjectionIntegrityVerifier.cs` 218, 372);
   - read-only snapshot scope exists (`AuthorityStore.cs` 30–31, `SET TRANSACTION READ ONLY`);
   - catalogue validation re-reads authorization and projection revisions (`SearchCatalogueService.cs` 260–300);
   - the existing discover ceiling is 50,000 (`PermissionCheckService.MaximumDiscoverableRecords`).
4. **Security assumptions:** S1 invariants I1 and I3 (one SpiceDB writer; one datastore backend); RE-1 accepted (C-117).
5. **Failure scenarios and tests:**

   | Scenario | Expected | Test |
   |---|---|---|
   | Revocation committed between pin and candidate read | S1 verify sees a new revision → whole search fails | IX-20 |
   | Content commit between pin and candidate read | Candidate transaction sees a consistent heads/entries snapshot; I-1 runs in it | IX-22 |
   | SpiceDB rollback during the search | S1 quarantine (existing) | existing S1 RB suites; IX-43 |
   | Share-only record with an unreadable predicate field | Excluded before predicates are evaluated (value-independent) | IX-09, IX-10, IX-36 |
   | Limit refusal or count depends on unauthorized matches | Not possible: limits on caller-owned quantities | IX-08 to IX-12 |
   | Planner chooses a value-index plan | Plan-shape test fails | **New: IX-68** (§8.2 names the test, R1 gives it no id); timing report IX-13 |
6. **Remaining risks:**
   - RE-1 (datastore revert and return within the decide-to-verify window), RE-4 (revocation after release): unchanged;
   - the enforceability of a fixed scope-driven plan in PostgreSQL across versions is unproven until IX-3 (UF-7);
   - residual timing channels unmeasured.
7. **Recommended decision:** **accept.**
8. **Blocks:** nothing for implementation. The plan-shape and timing measurements are IX-3 exit criteria.

### J-9. Search-result revocation and cross-instance invalidation

1. **Claim.** Stored results are re-authorized on every page and invalidated on every Authority instance by revocation, session end, searchability change and organization quarantine (FD-2, design §17.3, §17.9.2).
2. **Design clauses.** §17.3 (steps 1–9, behaviour table), §17.9.2 (X1–X4); IX-D5.
3. **Code facts:**
   - catalogue snapshots are stored in AuthorityDb and validated by re-reading current state (expiry, gate, authorization revision, projection revision; `SearchCatalogueService.cs` 260–300). Lifetime clamped to 1–900 s (line 398);
   - they record `RequestingAuthorityUserId` but **no session id** (`SearchCatalogueSnapshot.cs` 19);
   - **no purge exists**: no delete code in the Authority, and no `DELETE` grant (OR-4);
   - session revocation is done inside the security-command transaction (`CommandPlanner.Membership.cs` 213; `SecurityCommandService.cs` 307).
4. **Security assumptions:** every page check reads AuthorityDb at page time; no instance-local authorization cache.
5. **Failure scenarios and tests:**

   | Scenario | Expected | Test |
   |---|---|---|
   | Revocation between pages, one instance | 409, snapshot deleted, no page | IX-21 |
   | Revocation on instance A, page on instance B | 409, snapshot deleted, no page | IX-54 |
   | Deletion step fails in the revoking transaction | Revocation fails (X2) | IX-55 |
   | Deletion skipped (fault-injected) | Page check still refuses | IX-54 |
   | Old cursor replayed after deletion; approval withdrawn between pages | No page; 409 `SEARCH_SNAPSHOT_CHANGED` | IX-47 to IX-50 |
6. **Remaining risks:**
   - RE-4: bytes of a page released before a revocation cannot be recalled;
   - OR-4: session binding, the purge job and the `DELETE` grant on result-snapshot tables are new. Today's catalogue snapshots accumulate without purge (a separate observation, outside this design).
7. **Recommended decision:** **accept**, recording OR-4 as a design correction (done in this commit) and costing the purge in IX-3.
8. **Blocks:** Tier 1 implementation (IX-3) until the purge and session binding are specified in the IX-3 contract. Not a blocker for IX-1.

### J-7. Rollback and partial restore

1. **Claim.** Stale, replayed or partially restored index state is refused per query by I-1 (operation binding and revision equality). An old Authority version is fenced because the projection revision falls behind.
2. **Design clauses.** §6.4, §12.2, §12.4; R1 §C.1 step 5.
3. **Code facts:**
   - S1 rollback fence `ck_legacy_projection_token_retired` (`PROJECTION_INTEGRITY_S1_OPERATIONS.md` §2);
   - **`ContentIntegrityRevision` is advanced at four sites** (`ContentService.cs`):
     | Line | Path | Head changed? |
     |---|---|---|
     | 368 | content commit | yes |
     | 735 | verified read quarantine | quarantine flag |
     | 901 | historic/verified read quarantine | quarantine flag |
     | 1235 | share-editor prepare: current snapshot mismatch (`CurrentSnapshotMismatch` incident) | **no** |
   - R1 §C.1 step 5 sets `projection_content_integrity_revision` only after a commit.
4. **Security assumptions:** every increment site updates the projection revision in the same transaction.
5. **Failure scenarios:**
   - **OR-2:** a share editor (or a business DBA altering the bytes a share editor reads) causes a `CurrentSnapshotMismatch` incident. The organization revision advances with no commit. Under R1 the projection revision stays behind, so **every** subsequent search fails I-1 and the index is quarantined until a rebuild. This lets an ordinary contributor deny indexed search to the whole organization. The same happens on every verified-read quarantine.
   - **Old Authority image** (pre-IX) commits: projection revision falls behind → refused (intended).
   - **`authority_search` restored alone:** revision behind → refused (intended).

   **Tests:** IX-35 (old image), IX-16/IX-17 (Authority-side tamper and partial restore), IX-18/IX-19 (replay; AuthorityDb restore). **New: IX-63**: each of the four increment sites advances the projection revision; a `CurrentSnapshotMismatch` incident does not quarantine the index.
6. **Remaining risks:** RE-3 (AuthorityDb and SpiceDB restored together) is G14, open.
7. **Recommended decision:** **accept, conditional on the OR-2 fix:**
   - every `ContentIntegrityRevision` increment advances `projection_content_integrity_revision` in the same transaction; at 735, 901 and 1235 with no posting change (the quarantine flag is read by I-1 directly);
   - the IX-1 architecture test is keyed on the increment, so a fifth site fails the build;
   - alternative, if preferred: I-1 compares a dedicated `search_projection_revision` advanced only by posting-changing events. Recommendation: the first option, because it keeps the stronger "no content event missed" property.
8. **Blocks:** **Tier 1 implementation** until the owner accepts the fix (design note added in §3 of the design).

---

## 3. Remaining items

### J-10. Quarantine separation (FD-4)

1. **Claim.** S1 organization quarantine is evaluated first and always applies. A search-index quarantine or suspension never suppresses, delays or downgrades it. Signs of permission damage go through S1's decisive check first (Q4).
2. **Clauses.** §17.2 Q1–Q5, suspension row; §17.9.1 R7.
3. **Code facts:** S1 quarantine is checked on every security command (`SecurityCommandService.cs` 57–59), gate (`OrganizationGate`), and projector; persisted with a permanence trigger (`PROJECTION_INTEGRITY_S1_OPERATIONS.md` §4).
4. **Assumptions:** search code has no write path to `organization_authorization_states.integrity_quarantine_*`.
5. **Scenarios and tests:** IX-43 (S1 quarantine refuses index, fallback and pages), IX-44 (index quarantine leaves records and S1 untouched), IX-45 (rebuild refused under S1), IX-46 (Q4 escalation).
6. **Risks:** a Q4 escalation that itself fails (SpiceDB unavailable) must leave the search failed, not classified as an index failure. **Contract clause:** an inconclusive S1 check fails the search with 503 and records nothing.
7. **Recommendation:** accept, with that clause.
8. **Blocks:** none.

### J-8. Sweep deadlines (FD-1, UF-13, C-123)

1. **Claim (C-123 proposed).** Deadlines 1 h (S-A), 24 h (S-B), 30 days (S-C), with no grace. Overdue suspends; successful completion of every overdue scope clears; a failed verification quarantines and never auto-clears; a run never substitutes for per-scope success.
2. **Clauses.** Design §17.10 (new), superseding the thresholds in §17.5 and R5–R6 of §17.9.1 on confirmation.
3. **Code facts:** none (sweeps do not exist). The MAC provider interface distinguishes `IntegrityProviderUnavailableException` from an invalid result (`ContentService.cs` 1009–1016), which the contract relies on.
4. **Assumptions:** bounded clock skew between Authority instances; the scheduler runs well inside each deadline.
5. **Scenarios and tests:** §17.10.4 (IX-51, IX-53 revised; IX-57 to IX-60 new).
6. **Risks:**
   - **availability:** with no grace, a MAC-provider outage of a few hours can suspend indexed search across organizations; the V1 fallback is limited (UF-11);
   - **HSM throughput:** completing S-B within a 16 h scheduling target needs about 43 verifications/s at the PB-09 envelope (29/s at exactly 24 h). Unmeasured (UF-3).
7. **Recommendation:** accept the §17.10 contract.
8. **Blocks:** production (PERF-01). Not Tier 1 implementation.

### J-1. Separate search process (FD-6)

1. **Claim.** Reader-role separation limits what query-path SQL can do.
2. **Clauses.** §5.1, §17.4; UF-5.
3. **Code facts:** only the `authority` container holds the runtime credential (`infrastructure/docker/compose.yaml` 203–216); API, work loop and S1 monitor run in that one process (`Program.cs` 94, 97).
4. **Assumptions:** the query path has no dynamic SQL (all EF Core or interpolated parameters; the only `ExecuteSqlRaw` is the grants code with validated identifiers, `AuthorityRuntimeGrants.cs` 37–62).
5. **Scenarios:** remote code execution in the Authority process gives the runtime credential regardless of the reader role. A separate process would contain a compromise of query-path code only.
6. **Risks:** OR-1 shows the runtime credential, not the reader, is the high-value one.
7. **Recommendation:** not required for Development/Testing stages; decide before production, informed by the independent assessment.
8. **Blocks:** production only.

### J-2. Plaintext exposure and R-D

1. **Claim.** Approved Public/Internal plaintext in AuthorityDb is exposed only through the inventoried channels (§17.4).
2. **Clauses.** §5.3, §17.4; A2 R-D; IX-D2.
3. **Code facts:** none for the new schema. Existing AuthorityDb has no customer plaintext today.
4. **Assumptions:** Azure Flexible Server supports the per-role logging settings (UF-4, unverified).
5. **Tests:** IX-28 (log scan), pgAudit object-audit test in staging.
6. **Risks:** backup retention bounds withdrawn plaintext (UF-9); PB-10 retention decision pending.
7. **Recommendation:** accept for Development/Testing (synthetic data only); Azure verification is a production gate.
8. **Blocks:** production.

### J-11. Disaster recovery

1. **Claim.** All index data is derived; loss affects availability only; rebuild from verified bytes.
2. **Clauses.** §12; R1 §G.
3. **Code facts:** G14 recovery plane not implemented (`CLAUDE.md` §4R; S1 operations §7).
4. **Assumptions:** rebuild runs only after G14 recovery is complete and S1 is clear (§17.2 Q3).
5. **Tests:** IX-45; DR rehearsal is IX-7.
6. **Risks:** UF-10; rebuild HSM cost unmeasured.
7. **Recommendation:** accept as a known gap.
8. **Blocks:** production.

### J-12. Implicit relaxations and ENC-SR text

1. **Claim.** R-A to R-D name every changed guarantee.
2. **Clauses.** A2 §2–§4.
3. **Evidence:** this review found two unnamed effects:
   - OR-1 weakens the stated strength of "additions prevented" inside the Authority domain (not a Level 2 relaxation, but a wording overstatement);
   - OR-2 is an availability effect, not a guarantee relaxation.

   Neither changes P-1 to P-13 as worded.
4. **Assumptions:** none.
5. **Tests:** n/a.
6. **Risks:** none beyond OR-1 and OR-2.
7. **Recommendation:** add an A2 note recording OR-1's corrected wording (done in this commit). ENC-SR text unchanged until FD-5.
8. **Blocks:** Tier 1 implementation (wording only; done).

### J-13. OBS-IX-1

See `docs/security/OBS-IX-1_AUTHORITY_APPEND_ONLY_UPDATE_PRIVILEGE.md` §6 (new in this commit): actual privilege exposure, exploitation paths, detection per alteration and remediation alternatives. **No final severity is assigned.**
- **Recommendation:** keep open; the owner chooses an option.
- **Blocks:** production; not Tier 1 implementation. The design does not depend on it, but OR-1 and J-4 show it widens the coordinated-tampering case.

### J-6. Tier 2 completeness

**Not accepted** (C-123 §4). The verification watermark M1 and the 7-day M2 sweep are not yet accepted as sufficient proof of completeness. Nothing in this review changes that. Evidence still needed before any acceptance:
- an affected-set argument for every security command type, checked against `CommandPlanner`;
- proof that every edge-changing path writes pending verifications (architecture test);
- a measured `LookupSubjects` cost (UF-12);
- the independent assessment.

**Blocks:** Tier 2.

### J-14. Overall

- **Tier 1:** acceptable for an owner decision on IX-1 authorization **after**:
  * OR-1: the corrected I-2 claim is accepted and per-page seal verification is decided;
  * OR-2: the revision-hook fix is accepted.

  OR-3 and OR-4 are contract corrections already recorded.
- **Tier 2:** not acceptable; design-only.
- **Independent third-party assessment:** outstanding production-readiness requirement.

## 4. New test identifiers (this review)

- IX-61: no abort path of `CommitAsync` writes `authority_search` rows (OR-3).
- IX-64 to IX-67: derivation failure rolls back the commit; seal-sign failure aborts it; crash after commit leaves head and postings consistent; builder backfill race refused (J-3).
- IX-68: plan-shape test (no value-index-driven plan) (J-5).
- IX-62: coordinated posting + leaf + head-root rewrite as the runtime role; corrected behaviour asserted (OR-1).
- IX-63: every `ContentIntegrityRevision` increment advances the projection revision; a `CurrentSnapshotMismatch` incident does not quarantine the index (OR-2).
- IX-57 to IX-60: sweep deadlines (design §17.10.4).
