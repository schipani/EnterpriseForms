# E3-M2f-3a — Contract Closure: Durable Capability-Refusal Evidence

**Status (2026-11-17, revised after the owner's decisions on B-M2f3-1..3 and the conditional selection of LV-1 for B-M2f3-4):**

| Item | State |
|---|---|
| This document | Owner decision record and contract closure for E3-M2f-3 (increment **E3-M2f-3a**). **Documentation only.** It changes no code, test, migration, configuration, database or infrastructure. Committed locally with the E3-M2f-3 closure (2026-11-17, §14); not pushed or merged. |
| Result | With the LV-1 partial index of §5A, the contract specifies both **safety** (at most one entry per key per authoritative UTC hour per AuthorityDb timeline, so at most seven per organization per hour, §5.4) and **liveness** (a mature organization's first qualifying refusal is decided by reading at most eight evidence index entries, with no scan of unrelated audit history, §5.6). A non-unique index is sufficient; no unique index is required. These are proofs over the specified protocol, **not test results**. |
| Remaining owner decisions | **OD-IX-2** (rollout in persistent environments) of §5A.9. OD-IX-1 was approved with conditions on 2026-11-17 (dedicated privileged command). |
| E3-M2f-3i | **Implemented and accepted for Development/Testing (owner, 2026-11-17):** `E3_M2F3I_IMPLEMENTATION_RECORD.md` (command `--provision-capability-refusal-evidence-index`; disposable PostgreSQL gate 40/40 in two runs of record). No persistent database changed. |
| E3-M2f-3b | **Implemented and accepted for Development/Testing (owner, 2026-11-17, final closure §14); D-3b-1, D-3b-2 and D-3b-3 approved and verified by E3-M2f-3G:** `E3_M2F3B_IMPLEMENTATION_RECORD.md`. Its store SQL has now run on PostgreSQL 18 (E3-M2f-3G). |
| E3-M2f-3G | **Executed and accepted for Development/Testing (owner, 2026-11-17, final closure §14):** `E3_M2F3G_VERIFICATION_RECORD.md` (runs of record on fresh disposable PostgreSQL 18.0 + Vault: run-1 and run-3 passed, run-2 had one environment failure before the capability check, analysed; PostgreSQL-backed mutations 7/7 detected, MZ-22 equivalent; regressions). No product code changed. Not executed: the binding-inserting part of M2F3-28 and M2F3-33 (bound organization: outside the authorization). |
| Envelope v2 acceptance | **Not activated anywhere.** Nothing here changes `E3_M2E_CONTRACT_CLOSURE.md` §4.3, OA-M2e2-06 or `E3_M2F_CONTRACT_CLOSURE.md` §11.5. |
| Baseline | Branch `e3/m1-envelope-v2-core`, HEAD `3009878f55dc4430cc90ef5092764ed01abeff80` (E3-M2f-2G), good SSH ED25519 signature. E3-M2f-3a documentation revised in place; committed locally with the E3-M2f-3 closure (2026-11-17) |
| Review | Owner-led. Not an independent security review and not a certification. V2-IND-01 is still required before production. |

**Inputs:** the owner-reviewed design proposal *E3-M2f-3 — Durable Capability-Refusal Evidence Design* (2026-11-17; held outside the repository, like the E3-M2f readiness review); the owner's E3-M2f-3a decisions (2026-11-17) and final decisions on B-M2f3-1..3 (2026-11-17); `E3_M2F_CONTRACT_CLOSURE.md` (OD-M2f-01..10, CAP-*, AM-F1, FB-1, RE-1..RE-6, §8, §12.4, OPEN-M2f-1); `E3_M2F2_IMPLEMENTATION_RECORD.md`; `E3_M2F2G_VERIFICATION_RECORD.md`; `E3_M2E_CONTRACT_CLOSURE.md` (UQ-4, R-M2e-9, BD-1, BD-2); `E3_M2B_IMPLEMENTATION_CONTRACT.md` §20.7–§20.9 (R-CARV-12), §22 (RB-CARV-1), §22.14 (A-1, R-A1-1..5); source inspection at the baseline (§3.6).

**Precedence:**
1. This record governs E3-M2f-3. It closes `E3_M2F_CONTRACT_CLOSURE.md` §8 (RE-1..RE-6 design) and OPEN-M2f-1 **only as far as it says so**; the blocker in §11.1 remains open.
2. Where this record and the design proposal differ, this record governs. The proposal's §7.1 G1–G4, §10.1 bounds, window lookup and `first_refused_on_utc` field are **superseded** by §4, §5 and §9.
3. It amends `E3_M2F_CONTRACT_CLOSURE.md` in exactly one place: the CAP-E1 documentation text (CC-2, §2). CAP-O6 and every other E3-M2f requirement are unchanged and binding.
4. Where this record is silent, `E3_M2F_CONTRACT_CLOSURE.md`, then the M2e closure, then the M2b contract govern, as before.

**Notation:**

| Tag | Meaning |
|---|---|
| [DECIDED] | Approved by the owner on 2026-11-17, recorded here |
| [CONDITIONAL] | Approved by the owner on 2026-11-17 **subject to the stated conditions**; not an unqualified approval. The decision lapses if a condition fails, and E3-M2f-3b must stop and report |
| [SPECIFIED] | Implementation detail specified by this record; binding on E3-M2f-3b once that increment is authorized |
| [OPEN] | Unresolved; blocks what is named |

**Identifiers introduced:** evidence requirements EV-1..EV-12 (adopted from the proposal, §3.2); T2 rules T2-1..T2-8; durability statements D-1..D-9; lookup rules LK-1..LK-9; invariant I-1; lemmas L-A..L-D; liveness options LV-1..LV-4; index candidates IX-A..IX-E; index-plan items IXP-1..IXP-12; migration-plan decisions OD-IX-1, OD-IX-2; increment E3-M2f-3i; blockers B-M2f3-1..B-M2f3-4; tests M2F3-01..M2F3-84; mutations MZ-01..MZ-36; risks R-M2f3-1..R-M2f3-19; technical questions TQ-1..TQ-23.

---

## 1. Owner decision register (PD-01..PD-15, B-M2f3-1..4)

### 1.1 Blocker decisions (2026-11-17)

| ID | Owner decision | Recorded effect |
|---|---|---|
| **B-M2f3-1** | **QC-1 conditionally selected.** Existing audit journal, bounded hour-scoped lookup, no new unique index. An inconclusive lookup (examination limit of 20,000 entries reached, timeout, error, or otherwise unable to prove absence) must **not** append; the original 409 is preserved and the approved operational evidence-loss signal is emitted. The authoritative clock and hourly bucket must be defined precisely (PostgreSQL time preferred, suitability verified); a five-minute skew assumption must not be used for uniqueness. The bound must survive restarts, multiple instances, cache eviction, ambiguous commits and retry; if that needs a schema change, stop and report | **[CONDITIONAL]**. §5 specifies the lookup (LK-1..LK-9) and the authoritative clock; §5.4 proves the bound without a schema change and without any clock-skew assumption. The proof exposes a **liveness** defect that QC-1 cannot remove without a schema or configuration change or a new always-on write (§5.6): **B-M2f3-4, reported, not decided** |
| **B-M2f3-2** | **RB-a approved.** Verify existing G14 archive-mismatch detection, organization isolation and evidence preservation; do not implement G14 recovery; keep G14 recovery, BD-1 and BD-2 open; never describe restore recovery as verified or completed | **[DECIDED]**. §8 |
| **B-M2f3-3** | **Accepted.** The audit append may update `audit_journal_revision`, `latest_audit_journal_digest` and `state_modified_on`, in addition to inserting its journal entry. No other organization, content, authorization, integrity, operation, ledger, projection or direct quarantine change is authorized | **[DECIDED]**. §3.3 |
| **B-M2f3-4** | **LV-1 conditionally selected** as the preferred solution; **LV-2, LV-3 and LV-4 rejected**. The design must give safety (at most one event per approved key and authoritative UTC hour, per AuthorityDb timeline) and liveness (a mature organization's first qualifying refusal recordable without an unbounded historical audit scan). E3-M2f-3b not authorized | **[CONDITIONAL]**. §5A specifies the smallest index (IX-B, partial, non-unique) and its lookup; §5.4 and §5.6 prove safety and liveness. Conditions: the migration plan of §5A (OD-IX-1, OD-IX-2), the separately authorized E3-M2f-3i increment, and the executed index tests of §10.7 |

### 1.2 PD-01..PD-15 (reconciled)

| ID | Decision | Disposition | Conditions / limits | Where |
|---|---|---|---|---|
| **PD-01** | Storage and delivery: **option C1**, bounded refusal evidence on the existing per-organization Authority audit stream, written in a separate transaction T2 after T1 has been disposed | **[CONDITIONAL]** | (a) T1/T2 separation as §3; (b) T2 change set exactly §3.3 (B-M2f3-3); (c) safety per §5.4 (B-M2f3-1, QC-1); (d) liveness per §5.6 through the IX-B partial index of §5A (B-M2f3-4, LV-1), which must exist and be valid before any E3-M2f-3b build records evidence; (e) the restore criteria of §8 (RB-a). Options C2, E, A, B and D are not selected | §3, §5, §5A, §8 |
| **PD-02** | Durability guarantee | **[DECIDED]** best-effort bounded evidence; **no per-request and no exactly-once guarantee** | D-1..D-9 (§4). An inconclusive lookup never appends (D-7). "At least once" only for the archive hop after a confirmed commit | §4 |
| **PD-03** | Failure policy and handler placement | **[CONDITIONAL]** preserve the original 409 in every evidence outcome, including an inconclusive lookup; endpoint-filter placement | Executed proof of disposal and mapping (M2F3-07, -17, -45, -46, -47); fallback per §6.4 | §6, §8.1 |
| **PD-04** | Event schema | **[DECIDED]** closed schema with RE-3 exclusions | Clarified timestamps (§9.2): `evidence_database_time_utc` (new, authoritative, PostgreSQL) and `refusal_observed_on_utc` (informational, instance clock). The added field is required by B-M2f3-1 | §9 |
| **PD-05** | Deduplication key and bucket | **[DECIDED]** (organization, workload, entry point, refusal code) per **PostgreSQL** UTC hour | **Not database-enforced.** At most one entry per key and bucket **per AuthorityDb timeline**, enforced by the T2 protocol under the organization lock (§5.4). It is not a guarantee against privileged database writers or across restores | §5 |
| **PD-06** | Quotas and bounds | **[CONDITIONAL]** fixed constants | The lookup reads **at most 8 evidence index entries** (`LIMIT 8` over IX-B), well inside the owner's 20,000-entry examination ceiling, and never examines unrelated audit entries; a lookup that cannot finish within its statement timeout is inconclusive. The bound of §5.4 is independent of the in-process limits, which are load controls only (§5.7) | §5, §5A |
| **PD-07** | Suppressed-repeat accounting | **[DECIDED]** metrics only; summary events deferred | | §4, §5 |
| **PD-08** | Retention | **[DECIDED]** existing permanent journal and archive retention | Volume per §5.5 | §5.5 |
| **PD-09** | Access control | **[DECIDED]** no new read path, permission or Security Auditor change | | §9.4 |
| **PD-10** | Restore behaviour | **[CONDITIONAL]** existing G14 archive-mismatch detection accepted; **RB-a**: verify detection, isolation and preservation | G14 recovery, BD-1 and BD-2 stay **open**; restore recovery is **not** verified by E3-M2f-3 | §8 |
| **PD-11** | Scope against R-CARV-12 / UQ-4 | **[DECIDED]** capability refusal only | | §2 (CC-4) |
| **PD-12** | Store-unavailable outcome (CAP-F5) | **[DECIDED]** operational only | | §9.1 |
| **PD-13** | Contract amendments | **[DECIDED]** clarify without weakening CAP-O6 | CC-1..CC-6 (§2); T2 footprint per B-M2f3-3 | §2, §3.3 |
| **PD-14** | Deployment requirements | **[DECIDED]** routing of 9253/9254 (§10.4) to monitoring is a deployment prerequisite beyond Development/Testing | | §12.5 |
| **PD-15** | Test and acceptance gates | **[DECIDED]** two independent disposable PostgreSQL runs of record, fault injection, architecture tests, mutation verification | | §10 |

---

## 2. Contract-conflict resolution (CC-1..CC-6)

| ID | Conflict | Resolution [DECIDED] |
|---|---|---|
| **CC-1** | Candidate fields (request correlation id, Authority instance/build id, effective required capability) versus RE-3 | **RE-3 unchanged.** The body records none of: floor cause, effective floor `E`, deployment floor `D`, organization floor `O`, binding information, raw header values, tokens or assertions, user identity, correlation identifiers, request or operation identifiers, build or instance identifiers (§9.3). The added PostgreSQL timestamp is a time, not an identifier |
| **CC-2** | CAP-E1 OpenAPI text: "the request was refused before any verification and nothing was written" | **CAP-E1 documentation text amended** (the only amendment): *"Conflict. Among other codes: `CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT` (not retryable): the declared envelope capability is below the effective floor. The refused request's transaction was ended before any verification and wrote nothing. Independently of that transaction, the Authority may record bounded refusal evidence on the organization's audit stream."* No wire change. The string lives in `EnvelopeCapabilityComposition` and the generated internal OpenAPI; changing them is E3-M2f-3b work |
| **CC-3** | Closure §12.4 "CAP-O6 relaxed only as decided" | **CAP-O6 is not relaxed.** The check and T1 still write nothing. A refused request has exactly one permitted durable side effect, outside T1: the T2 audit append of §3 |
| **CC-4** | R-CARV-12 / UQ-4 ask about a "durable audit-stream event" | Answered **for the capability refusal class only**. R-CARV-12 for its own class, R-M2b2-7, R-M2e-9 and UQ-4 remain open (PD-11) |
| **CC-5** | "A refusal must not change quarantine state" versus G14 archive-mismatch detection after a restore | **Accepted conditionally (PD-10, RB-a).** T2 never changes quarantine state; the existing `ArchiveProcessor`, in its own later transaction, may quarantine a restored organization whose re-used audit revision collides with the archive. Preserved unchanged (§8) |
| **CC-6** | "Must not advance an integrity revision" versus T2 advancing `audit_journal_revision` | **Confirmed:** `audit_journal_revision` is the audit stream's own sequence, not the content-integrity revision (INV-R) and not the authorization revision; it gates nothing (§3.4) |

---

## 3. Transaction separation

### 3.1 T1 — the refused transaction [unchanged; SPECIFIED as a contract]

T1 is the `IAuthorityScope` opened with `await using` by one of the seven entry points (`E3_M2F_CONTRACT_CLOSURE.md` §6.1): `LockOrganizationAsync`, for Prepare and Commit the Release B fence (A-1 order), then `EnvelopeCapabilityFloor.RequireAsync`. On refusal the check throws; T1 has written nothing (CAP-O6). Leaving the `await using` block runs `AuthorityScope.DisposeAsync`, which awaits `transaction.RollbackAsync()` (the scope did not commit), then disposes the transaction and the `DbContext`. All locks of T1 are released by that rollback. **T1 is fully disposed** when `DisposeAsync` has returned: before the exception leaves the public method (entry points 3–7) or `PrepareCoreAsync` / `CommitCoreAsync` (1–2).

### 3.2 T2 — the evidence transaction [SPECIFIED]

| # | Rule |
|---|---|
| T2-1 | T2 begins only after T1 is fully disposed. It is a new `IAuthorityScope` (new `DbContext`, connection lease and transaction). |
| T2-2 | T2 never starts while the same request holds any lock of T1 (guaranteed by T2-1). |
| T2-3 | T2 takes exactly one lock, the organization row (`LockOrganizationAsync`). It never calls the Release B fence, never locks `content_change_operations` or `ef_migrations_history`, never touches the platform journal head. |
| T2-4 | Right after the lock statement returns, T2 reads the authoritative time `t_db` (§5.2) in a separate statement. |
| T2-5 | T2 runs the lookup of §5.3. It appends exactly one `EnvelopeCapabilityRefused` entry and commits **only if the lookup is conclusive and proves the key absent** (LK-7); otherwise it writes nothing. |
| T2-6 | T2 uses its own bounded cancellation, linked to host shutdown, not the request's abort token. Expiry of the budget ends T2 without an append. |
| T2-7 | T2 never reads the data-key ledger (AM-F1 keeps exactly one caller, EV-11) and makes no archive call. |
| T2-8 | Before the lookup, T2 confirms that the IX-B index exists, is valid and ready, and has exactly the approved definition (IXP-8). If not, the lookup is inconclusive (`INDEX_UNAVAILABLE`) and nothing is appended. |

### 3.3 Permitted and forbidden T2 changes [DECIDED by B-M2f3-3]

**Permitted (exhaustive):**

| Object | Change |
|---|---|
| `authority.security_journal_entries` | **INSERT** one row: stream `audit`, kind `EnvelopeCapabilityRefused`, revision `audit_journal_revision + 1`, canonical body (§9), digest, previous digest, and the initial archive-delivery metadata set by `JournalChain.Append` (`Pending`, attempt count 0, no receipt, no error) |
| `authority.organization_authorization_states` (the locked row) | **UPDATE** of exactly `audit_journal_revision`, `latest_audit_journal_digest`, `state_modified_on` |

**Forbidden in T2:** every other change to the organization row; content heads, leaves, candidates, manifests, checkpoints, item identities; content operations and idempotency state; the data-key ledger (read or write); `authorization_revision`, `content_integrity_revision`, `projection_applied_revision`, the projection token; `integrity_quarantine_enabled` / reason (no direct quarantine); search projection, index, approvals, coverage, result snapshots; other journal streams; the platform journal; security commands; archive calls.

**Triggers (source inspection; verified by M2F3-54):** only `trg_organization_integrity_quarantine_is_permanent` (BEFORE UPDATE) fires, as a no-op. `trg_content_revision_search_synchronization` (UPDATE OF `content_integrity_revision`) and `trg_search_result_snapshots_organization_state` (UPDATE OF `authorization_revision`, `integrity_quarantine_enabled`) must not fire; this holds when the UPDATE's SET list contains only the three permitted columns.

**Release B:** the store catch-all (`HasContentAcceptanceWrites`) applies the fence only to content-acceptance entities; an audit-only T2 does not trigger it (source inspection; M2F3-28).

### 3.4 Existing side effects of a T2 commit (documented, not prevented)

| Side effect | Effect |
|---|---|
| Organization change marker (`AuditJournalRevision`) moves | One search-access maintenance `CatchUp` pass for that organization |
| New `Pending` journal row | `AuthorityWorkLoop` → `ArchiveProcessor` delivers it in its own transaction |
| `ProtectionReconciliationAttestation.AttestedAuditJournalRevision` | No invalidation by a later revision found (TQ-6; M2F3-56) |
| Organization gate | None (audit stream is non-gating) |

### 3.5 Lock order

T2 takes the organization row after T1 has released everything, the order of every audit append and of `ArchiveProcessor`. No inversion with the Release B fence, floor activation (A-1), binding-inserting Prepares or the platform journal (M2F3-28).

### 3.6 Source basis (baseline `3009878`)

`AuthorityStore.AuthorityScope` (`DisposeAsync`, `CommitAsync`, `SaveAsync`, `HasContentAcceptanceWrites`, `LockOrganizationAsync`); `AuditJournal.Append`; `JournalChain.Append` (envelope `CreatedOnUtc` and `JournalCreatedOn` come from the Authority's `TimeProvider`, **not** from PostgreSQL); `SecurityJournalEntryConfiguration` (indexes `uq_journal_stream_revision` (organization, stream, revision), `ix_journal_pending_archive`, `ix_journal_content_operation`; **no index on the event kind; no database default on `journal_created_on`**); `ArchiveProcessor`; `OrganizationGate`; `ContentEndpoints`, `CatalogueEndpoints`, `SearchIndexBuilderEndpoints`; `Program.cs` middleware order; `AuthorityExceptionHandler`; migrations `20261104231152_ProjectionIntegrityS1`, `20261106123244_AuthoritySearchIndexIx1`, `20261108042541_AuthoritySearchIndexIx3`. Source inspection is not test evidence.

---

## 4. Durability guarantees (corrected)

| ID | Statement [DECIDED by PD-02] |
|---|---|
| **D-1 Response** | Every refused request receives the unchanged 409, whatever happens to evidence (success, duplicate found, inconclusive lookup, failure, timeout, ambiguous commit, quota). A 409 does **not** imply that evidence exists. |
| **D-2 Durable after a confirmed T2 commit** | When T2's commit returns normally, the entry is durable in AuthorityDb, subject only to a later AuthorityDb restore to an earlier point (D-8). |
| **D-3 Independent archive delivery** | After a confirmed commit, delivery to the independent archive is **asynchronous and at least once**, because durable retry state exists (the row stays `Pending`; `ArchiveProcessor` retries with a deterministic key and identical bytes). Holds only while AuthorityDb is not restored to a point before delivery. No latency is guaranteed. |
| **D-4 Before a confirmed commit: in-memory best effort** | The synchronous attempt and any retries exist **only in process memory**. No durable retry state exists, so **no at-least-once claim** is made for uncommitted evidence. |
| **D-5 Crash windows and restarts** | Evidence is lost if the process crashes, stops or restarts after T1's rollback and before T2 commits, or while a key waits for retry. A later refusal of the same key, on any instance, is handled normally. |
| **D-6 Ambiguous commit** | If T2's commit outcome is unknown, the key is marked Ambiguous and nothing is assumed. No special reconciliation step is needed for safety: the next attempt for the key (any instance, after any restart) runs the full conclusive lookup of §5.3 under the organization lock, which sees the entry if the earlier commit took effect and does not if it did not. If that lookup is inconclusive, nothing is appended (D-7). An ambiguous commit can therefore never cause a duplicate (L-C). |
| **D-7 No append without proof** | A lookup that finds the IX-B index missing or invalid, times out, errors, finds an unverifiable body, detects a time regression or an order or bound inconsistency, or otherwise cannot prove absence **appends nothing**. The refusal is then not recorded durably; log 9254 with the reason and a counter are emitted. Pre-filter and load-limit suppression also leave no durable trace (PD-07). |
| **D-8 Restores** | *AuthorityDb restore:* undelivered entries are lost; delivered entries survive in the archive. The bound of §5.4 holds per AuthorityDb timeline: after a restore, keys already recorded in the lost interval may be recorded again. The organization's audit revision regresses; the next audit append collides with the archive and `ArchiveProcessor` quarantines that organization (`ARCHIVE_BODY_MISMATCH`); the colliding post-restore entry stays in AuthorityDb and never reaches the archive. *Archive restore or rollback:* anchored entries are not re-sent (existing G14 limitation). |
| **D-9 Not claimed** | No exactly-once delivery; no per-request durability; no "every 409 is evidence-backed"; no restore-independent evidence; no barrier role (BD-1); no liveness while the IX-B index is absent or invalid, while the AuthorityDb clock is behind the newest evidence entry, or while an unverifiable evidence body is among an organization's newest eight (§5.6). |

---

## 5. Deduplication, authoritative clock and the seven-event bound

### 5.1 Key

`K = (organization_id, workload_name, entry_point, refusal_code)`. `workload_name` is derived from the authenticated route policy, never from the `workload_name` claim (whose values are deployment-configured, for example `enterpriseforms-api`): entry points 1–4 ⇒ `business-api`, 5 ⇒ `field-protection-verifier`, 6–7 ⇒ `search-index-builder`; `other` stays in the approved enumeration and is unreachable. `workload_name` is thus a function of `entry_point`, and with one refusal code in scope **exactly seven keys exist per organization**. Refusals occur only for registered organizations. Limitation: business API and Worker share the `business-api` role (R-M2f3-10).

### 5.2 Authoritative clock and hourly bucket [SPECIFIED]

| Candidate | Behaviour (verified by source inspection or PostgreSQL semantics) | Verdict |
|---|---|---|
| Instance `TimeProvider` (used by `JournalChain` for `CreatedOnUtc` and `journal_created_on`) | One clock per Authority instance; instances can disagree; non-monotonic across instances | **Not authoritative.** Informational only (`refusal_observed_on_utc`) |
| `transaction_timestamp()` / `now()` | Fixed when the transaction starts, which is **before** T2 waits for the organization lock (whether the provider sends `BEGIN` eagerly or with the first command, TQ-15). Two T2s can acquire the lock in the reverse order of their start times; across an hour boundary their buckets would then decrease along the revision order | **Unsuitable** |
| **`clock_timestamp()` read in a statement executed after the lock statement has returned** | One clock (the AuthorityDb primary). Every earlier T2 of the organization committed before this T2 acquired the lock, so its reading was taken earlier on the same server clock, **provided that clock does not step backward** (NTP correction, failover to a standby host) | **Authoritative**, with that proviso **checked, not assumed** (I-1, LK-6) |

**Definitions:** `t_db` = the value of `clock_timestamp()` returned by the first T2 statement after `LockOrganizationAsync`, in UTC with microsecond precision. **Bucket** `H = date_trunc('hour', t_db)`, the half-open hour `[H, H + 1 h)`. `t_db` is recorded in the body as `evidence_database_time_utc`; `H` as `evidence_bucket_start_utc` (§9.2). A refusal observed at 10:59:59 on an instance and recorded by a T2 whose `t_db` is 11:00:00.2 belongs to bucket 11:00. A retried key is bucketed by the `t_db` of the T2 that records it.

### 5.3 Lookup protocol [SPECIFIED; revised for LV-1]

Executed in T2 after the organization lock, the index check (T2-8) and `t_db`:

| # | Rule |
|---|---|
| LK-1 | **Query** (exact text in IXP-6): the newest at most **8** rows of organization o with `journal_stream_name = 'audit'` and `journal_event_kind = 'EnvelopeCapabilityRefused'`, ordered by `journal_stream_revision` descending, served by the IX-B partial index. Unrelated audit entries are not examined. The previous 20,000-entry revision window is withdrawn. |
| LK-2 | The query runs under a statement timeout (IXP-7). Expiry ⇒ **inconclusive** (`TIMEOUT`). |
| LK-3 | Each returned body must verify against its stored digest and parse into the closed schema of §9.2, with `organization_id` equal to o. Any failure ⇒ **inconclusive** (`UNVERIFIABLE_BODY`). |
| LK-4 | Returned rows, newest first, must have non-increasing `evidence_database_time_utc`. Violation ⇒ **inconclusive** (`TIME_ORDER_VIOLATION`). |
| LK-5 | **Conclusive boundary:** fewer than 8 rows returned (every evidence entry of o has been read), **or** at least one returned row has `evidence_bucket_start_utc < H`. Eight rows all with bucket ≥ H ⇒ **inconclusive** (`BOUND_INCONSISTENT`, Critical alert: it would mean eight entries in one bucket, which §5.4 excludes). |
| LK-6 | **Time regression:** the newest returned row has `evidence_database_time_utc > t_db` ⇒ **inconclusive** (`CLOCK_REGRESSION`). |
| LK-7 | **Decision:** conclusive and no returned row with key K and bucket H ⇒ append. Conclusive and such a row exists ⇒ no append (already recorded). Conclusive and two rows share key K and bucket H ⇒ **inconclusive** (`BOUND_INCONSISTENT`). Inconclusive ⇒ no append. |
| LK-8 | Timeout, cancellation, lock-wait expiry, database error or a unique-key violation on insert ⇒ T2 rolls back, no append. |
| LK-9 | K must be one of the seven valid keys; anything else is an error (no append). |

### 5.4 Safety proof (seven-event bound; no unique index, no clock-skew assumption)

**Setting.** For organization o, `E_o` is the sequence of its `EnvelopeCapabilityRefused` entries ordered by audit revision. Each entry e has `τ(e)` = `evidence_database_time_utc`, bucket `β(e)` = `date_trunc('hour', τ(e))` and key `κ(e)`.

**Assumptions (each pinned by test or architecture check, not by clock behaviour):**
- A1. The recorder is the only producer of the event kind and always follows §5.3 (M2F3-38; MZ-15).
- A2. Every evidence append happens in a T2 that holds o's organization row lock from the lookup through commit (M2F3-61; MZ-24).
- A3. Audit revisions are unique per organization and stream (`uq_journal_stream_revision`); an insert at a revision already used fails (LK-8).
- A4. No privileged actor inserts, deletes, reorders or edits journal rows (outside the runtime model; G08). Edited bodies fail LK-3. *(Note 2026-11-17, C-155, correction D-1: this proof **assumption** A4 is what R-M2f3-6 and R-FU-1 refer to as "A4"; it is unrelated to attacker class A4 of `E3_ENVELOPE_V2_KEY_HIERARCHY_DESIGN.md` §7. The assumption can be violated by attacker classes J1 (Authority runtime database credential: insert and edit) and J2 (privileged administrator: also delete) of `SEC_BL_01_DR_CONTRACT_CLOSURE.md` §2; "Edited bodies fail LK-3" holds only when the digest is not recomputed. The theorem below remains conditional on A4.)*
- A5. The lookup's **result** is defined by SQL semantics, whatever access path the planner uses: it returns exactly the newest `min(8, |E_o|)` rows of `E_o`. IX-B only makes that cheap (§5.6); it is not a correctness assumption (L-D).

**Invariant I-1.** `E_o` is non-decreasing in τ.

*Proof by induction over appends.* Empty `E_o` satisfies it. Suppose I-1 holds and T2 appends e*. By A2 no other evidence append occurs between T2's lookup and its commit. If `E_o` is non-empty, its newest entry p is the first returned row (A5), and LK-6 guarantees `τ(p) ≤ t_db = τ(e*)`. Hence I-1 holds after the append. ∎ *(Simplified from the earlier revision-window version: the newest evidence entry is now always read, so no case analysis on the window is needed.)*

**Lemma L-A (completeness for the current bucket).** When the lookup is conclusive, it has returned every entry of `E_o` with bucket H.

*Proof.* If fewer than 8 rows were returned, they are all of `E_o` (A5). Otherwise some returned f has `β(f) < H`; by I-1 every entry with bucket H has `τ ≥ H > τ(f)`, hence a higher revision than f, hence is among the rows returned before f. ∎

**Lemma L-B (one per key).** For every bucket H, `E_o` contains at most one entry with key K and bucket H.

*Proof.* An append of (K, H) requires a conclusive lookup that returned no (K, H) row (LK-7); by L-A none exists; by A2 none can appear before the commit. Later attempts for (K, H) return it and do not append. ∎

**Theorem (safety).** For every organization o and every authoritative UTC hour H, per AuthorityDb timeline: at most **one** entry per approved key and at most **seven** entries in total. *Proof:* L-B and the seven keys of §5.1 (LK-9). ∎

**Lemma L-C (independence).** The proof uses only durable state read under the organization lock and the single AuthorityDb clock reading `t_db`:
- **Restarts and cache eviction:** in-memory state can only skip attempts; every append still requires a conclusive lookup.
- **Concurrent instances:** all T2s of o serialize on one row lock (A2).
- **Retry and ambiguous commit:** a retry is another T2; if an ambiguous commit took effect, its entry is the newest row and is read; if not, there is nothing to duplicate; an inconclusive lookup appends nothing.
- **Instance clock skew:** instance clocks appear only in `refusal_observed_on_utc`, which the proof does not use.
- **AuthorityDb clock step backward or failover:** LK-6 detects it against the newest entry, which is always read; the outcome is "no append" until `t_db` passes it, never a duplicate.

**Lemma L-D (index absence cannot break safety).** If IX-B is missing, invalid or not chosen by the planner, the query's result is unchanged (A5); only its cost changes. T2-8 additionally refuses to run the lookup without a valid IX-B, and LK-2 bounds the cost of a bad plan. So index absence, a rolling deployment or a restore without the index affects liveness only.

**Scope:** one AuthorityDb timeline (D-8), under A1–A5. It is a protocol guarantee, **not a database constraint** (PD-05). A unique index is not needed for it (§5A.2, IX-D).

### 5.5 Write bounds

O = registered organizations; `O_H` = organizations with at least one recorded refusal in hour H.

| Scope | Upper bound per authoritative hour H |
|---|---|
| One organization, all instances, any restarts | **7** |
| **Whole fleet, any number of instances, restarts, evictions, retries, ambiguous commits** | **7 × O_H** |

PB-09 envelope (O = 25): at most 175 entries per hour fleet-wide, 4,200 per day, 1,533,000 per year under a continuous flood of every key in every organization; one straggling business build in one organization writes at most 4 per hour. Retention is permanent (PD-08). IX-B grows by the same number of index entries (about 1,533,000 per year in that extreme case; zero in normal operation).

### 5.6 Liveness proof (B-M2f3-4, LV-1)

**Claim.** If IX-B is valid (T2-8), the lookup completes within its timeout, no evidence body among the organization's newest eight is unverifiable, and `t_db` is not earlier than the organization's newest evidence time, then the first qualifying refusal of key K in hour H is recorded by the first T2 that reaches the lookup, **however large the organization's audit history is**.

*Argument.* The lookup returns the newest `min(8, |E_o|)` evidence rows. If `|E_o| = 0` (an organization that has never recorded this evidence, including one with millions of other audit entries), it returns no rows: fewer than 8, conclusive (LK-5), K absent, append. If `|E_o| > 0`, by I-1 and the bound the rows of bucket H (at most 6 other keys, because K is absent) are the newest; if they are fewer than 8 rows in total the lookup is conclusive, and otherwise the 8 rows must include one older than H (at most 7 can belong to H), also conclusive. LK-3, LK-4 and LK-6 pass by hypothesis, so K is appended.

**Cost.** One B-tree descent of IX-B for o (IX-B contains only evidence rows) and at most 8 heap fetches, plus any dead index entries left by the `ArchiveProcessor` updates of those rows (each evidence row is updated about once on delivery; reclaimed by vacuum). No unrelated audit entry is examined. The previous design's worst case (20,000 heap fetches per attempt) and its permanent suppression of mature organizations are removed.

**Residual liveness losses (safety unaffected):**

| Condition | Effect | Recovery |
|---|---|---|
| IX-B missing or invalid (not yet applied, rolled back, restored from a backup without it, privileged drop) | Every lookup inconclusive (`INDEX_UNAVAILABLE`) | Apply or reinstate IX-B under §5A |
| AuthorityDb clock behind the newest evidence entry (step backward, failover) | Inconclusive (`CLOCK_REGRESSION`) | Automatic once `t_db` passes the newest entry |
| Unverifiable body among the newest eight (tampering, outside A4) | Inconclusive (`UNVERIFIABLE_BODY`), and it persists because no newer entry can be appended | Privileged investigation (G14 / incident); Critical alert |
| Lookup timeout (bad plan, contention) | Inconclusive (`TIMEOUT`) | Plan verification (IXP-10); retried on a later refusal |

### 5.7 In-process controls (load only; not part of either proof) [CONDITIONAL, PD-06]

| Control | Value | Purpose |
|---|---|---|
| Pre-filter | 4,096 keys, LRU | Skip T2 for a key recorded or found in its current bucket. Expiry is the bucket end converted to a **local monotonic deadline** (`H + 1 h − t_db` on the process stopwatch) |
| Negative cache | Same structure; 60 s for `INDEX_UNAVAILABLE`, `TIMEOUT` and errors, until the bucket deadline for `BOUND_INCONSISTENT` and `UNVERIFIABLE_BODY` | Avoid repeating a failing lookup on every refusal |
| Attempt limit | 256 T2 attempts per process per hour | Bounds organization-lock acquisitions under a flood; exceeding it emits 9254 `ATTEMPT_LIMIT` |
| Retry set | 1,024 keys; backoff 5 s → 5 min; abandoned after 1 h | Best-effort retry (D-4) |
| T2 budget | About 1 s lock wait, 2 s total (TQ-1); lookup statement timeout per IXP-7 | Bounded request latency |

At O = 25 the attempt limit (256) exceeds the 175 keys; beyond about 36 active organizations per instance per hour it can drop evidence (logged); it never adds writes.

---

## 5A. LV-1 index and migration plan [SPECIFIED; documentation only, nothing created or executed]

### 5A.1 What the journal looks like today (inspection at the baseline)

| Item | Fact |
|---|---|
| Table | `authority.security_journal_entries` (migration `20261018044514_InitialAuthorityStore`, later mapping in `SecurityJournalEntryConfiguration`) |
| Relevant columns | `organization_id uuid NOT NULL`, `journal_stream_name varchar(30) NOT NULL`, `journal_stream_revision bigint NOT NULL`, `journal_event_kind varchar(60) NOT NULL`, `journal_event_body_json text NOT NULL` (changed from `jsonb` to `text` so the canonical bytes stay exact, A-F12), `journal_event_digest bytea` (32 bytes), `archive_delivery_state varchar(30)`, `journal_created_on timestamptz` (instance clock, no default) |
| Indexes | `uq_journal_stream_revision` UNIQUE (organization_id, journal_stream_name, journal_stream_revision); `ix_journal_pending_archive` (organization_id, archive_delivery_state); `ix_journal_content_operation` (organization_id, journal_stream_name, content_change_operation_id); *(corrected 2026-11-17, E3-M2f-3i record §2: this row omitted)* `ix_journal_search_index_form_events`, a partial expression index (organization_id, journal_event_kind, body `form_definition_id`, journal_stream_revision DESC) WHERE kind IN ('SearchIndexCoverageAttested', 'SearchIndexPopulationIssued'), created by the ordinary migration `20261110161016_AuthorityJournalSearchIndexFormEvents` outside the EF model. Its predicate cannot serve IXP-6; the design is unchanged |
| Event-kind catalogue | None in AuthorityDb: the kind is a free `varchar(60)` set by code constants. The IX-B predicate literal must therefore equal the recorder's constant, pinned by a contract test (IXP-11) |
| Append protocol | `AuditJournal.Append` under the organization lock: revision `+1`, canonical body (`DynamicForms/SecurityJournal/v1`), digest chained to the previous audit entry; `ArchiveProcessor` later updates only archive-delivery fields |
| Writers | Runtime role `SELECT, INSERT, UPDATE` on the table (`AuthorityRuntimeGrants`); indexes carry no privileges |

### 5A.2 Candidate indexes

| ID | Index | Supports LK-1? | Size / write cost | Verdict |
|---|---|---|---|---|
| IX-A | `(journal_event_kind)` or `(organization_id, journal_event_kind)` | Not by itself: no revision order, so "newest 8" needs a sort of all of o's evidence entries (unbounded over years) or a scan across organizations | Every journal row | **Insufficient** (a simple event-kind index does not give bounded liveness) |
| IX-A2 | `(organization_id, journal_stream_name, journal_event_kind, journal_stream_revision)`, all rows | Yes | Indexes every journal row of every stream (millions); extra B-tree maintenance on every journal insert | Sufficient but not the smallest |
| **IX-B** | **Partial** `(organization_id, journal_stream_revision) WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'` | **Yes**: equality on the organization, backward scan on the revision, `LIMIT 8` | Only evidence rows (zero today); other inserts only evaluate the predicate | **Recommended** |
| IX-C | Partial **expression** index on the bucket and key parsed from the body (`(journal_event_body_json::jsonb #>> '{body,evidence_bucket_start_utc}')`, …) | Yes, and would let the lookup read bucket H directly without I-1 | Parses JSON in the index; ties the schema to body field names and timestamp rendering; any body-schema change becomes an index migration | Not needed; rejected as larger and more brittle |
| IX-D | IX-C made **UNIQUE** on (organization, workload, entry point, code, bucket) | Database-enforced uniqueness | As IX-C; a duplicate insert fails with 23505 (LK-8) | **Not required**: the non-unique IX-B gives safety by protocol (§5.4) and liveness (§5.6). Kept as an owner-reviewable hardening option only, not proposed |
| IX-E | Reusing an existing index through a synthetic `content_change_operation_id` | Technically | None | Rejected: overloads a content-operation field |

**Partial-index caveat (binding on E3-M2f-3b):** PostgreSQL uses a partial index only when it can prove the query's WHERE clause implies the index predicate at planning time. The stream and kind must therefore appear as **SQL literals**, not bind parameters: with parameters, a generic prepared plan cannot use IX-B. Only `organization_id` is a parameter (IXP-6, M2F3-76).

### 5A.3 Exact DDL (IXP-1..IXP-5)

| # | Item |
|---|---|
| IXP-1 | `CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_journal_capability_refusal_evidence ON authority.security_journal_entries (organization_id, journal_stream_revision) WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused';` |
| IXP-2 | Non-unique; B-tree; ascending (a backward scan serves `ORDER BY … DESC`). No `INCLUDE` columns: the body and digest are read from the heap for at most eight rows |
| IXP-3 | Name follows the existing `ix_journal_*` convention (38 characters, under the 63-byte limit). No column is added; the descriptive-column naming rule is unaffected |
| IXP-4 | Executed with the migration identity (schema/table owner), never the runtime role. No grant changes are needed, so `AuthorityRuntimeGrants.Apply` is **not** run |
| IXP-5 | `IF NOT EXISTS` also succeeds when a previous concurrent build left an **INVALID** index of that name, so the command must always verify afterwards (IXP-8) and stop if the index is invalid; cleanup is `DROP INDEX CONCURRENTLY` under the same approval, then a rerun |

### 5A.4 Lookup query and runtime checks (IXP-6..IXP-8)

| # | Item |
|---|---|
| IXP-6 | `SELECT security_journal_entry_id, journal_stream_revision, journal_event_body_json, journal_event_digest FROM authority.security_journal_entries WHERE organization_id = @organization_id AND journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused' ORDER BY journal_stream_revision DESC LIMIT 8;` (stream and kind as literals) |
| IXP-7 | Statement timeout for IXP-6 (proposed 500 ms, inside the T2 budget), set with `SET LOCAL` in T2 (TQ-1) |
| IXP-8 | Index check before IXP-6, in T2: `pg_index` / `pg_class` / `pg_namespace` row for `authority.ix_journal_capability_refusal_evidence` with `indisvalid AND indisready AND indislive`, and `pg_get_indexdef(indexrelid)` equal to the approved rendering. The exact rendering on PostgreSQL 18 (it includes `USING btree` and casts such as `(journal_stream_name)::text = 'audit'::text`) is captured in E3-M2f-3i and pinned; Azure rendering is verified later (TQ-19, R-CARV-18). Any mismatch ⇒ `INDEX_UNAVAILABLE` |

### 5A.5 Existing-table and existing-data compatibility

- No column, constraint, trigger, default or grant changes. Stored bodies, digests, chains, revisions and archive keys are untouched, so journal revision and archive integrity are unaffected.
- No `EnvelopeCapabilityRefused` rows exist before E3-M2f-3b, so the index starts empty; the build still reads the whole table.
- Builds before E3-M2f-3b never write rows matching the predicate, so the index costs them only a predicate check per insert. It is safe to apply before any E3-M2f-3b instance exists.
- The `ArchiveProcessor` update of `archive_delivery_state` (indexed in `ix_journal_pending_archive`) is not HOT-eligible, so it adds one IX-B entry per evidence row delivered; vacuum reclaims them.
- EF Core model: IX-B is outside the `AuthorityDbContext` model if OD-IX-1 selects the dedicated path. The ordinary migrations never see or drop it; an architecture check must prevent a second definition being added to the model (IXP-11, TQ-20).
- G08 inventories: the object-ownership and privilege inventories gain one index owned by the schema owner; their expected outputs are updated in E3-M2f-3i (TQ-21).

### 5A.6 Locking and operational impact

| Form | Lock on `security_journal_entries` | Effect on live writers |
|---|---|---|
| Plain `CREATE INDEX` | SHARE for the whole build | **Blocks every journal INSERT and UPDATE** (authorization, audit and content streams, archive delivery) for the build duration, which is proportional to table size; while blocked, writers hold their organization row lock, so all work of those organizations queues. **Not acceptable** on a populated database with writers |
| **`CREATE INDEX CONCURRENTLY`** (required) | SHARE UPDATE EXCLUSIVE | Inserts and updates continue. The build makes two table scans and waits for existing transactions at its phase changes; a long-running transaction delays it. It cannot run inside a transaction block. A failure or cancellation leaves an INVALID index (IXP-5) |

A1-style lock order: the concurrent build takes no organization row, no `ef_migrations_history` lock and no `content_change_operations` lock, so it cannot form the R-A1-3 cycle (organization row versus history) or the F-E1 inversion. Its waits are on other transactions' completion, not on locks those transactions wait for. A `lock_timeout` on the build session (proposed 5 s, as for floors) turns an unexpected conflicting lock into a clean failure (INVALID index, IXP-5).

### 5A.7 Migration-runner compatibility, F-E2 and R-A1-3

| Path | Transaction compatibility | F-E2 | R-A1-3 / A-1 | Assessment |
|---|---|---|---|---|
| Ordinary EF migration in `AuthorityDbContext`, applied by `--migrate` | EF runs migrations in a transaction; `CREATE INDEX CONCURRENTLY` needs `suppressTransaction: true`, whose interaction with the EF migration lock on `ef_migrations_history` must be verified (TQ-22) | **Exposed:** every `--migrate` re-applies the runtime grants after the migrations (`REVOKE ALL` window) | The EF migration lock on history is held while the index builds; Release B writers take history ACCESS SHARE first (A-1), so they queue behind the build for its whole duration | **Not recommended** |
| **Dedicated privileged command** (pattern of `--apply-content-acceptance-floor`): one-shot, migration identity, single autocommit statement IXP-1 with `lock_timeout`, then IXP-8 verification, exit codes for invalid or mismatched index | Native (no transaction) | **Not exposed:** no grant re-application | No history lock, no organization row; no cycle | **Recommended** (OD-IX-1) |

Neither path is created by this record. The dedicated command and its source are the scope of **E3-M2f-3i**.

### 5A.8 Index validation and query-plan verification (IXP-9..IXP-12)

| # | Item |
|---|---|
| IXP-9 | After IXP-1: IXP-8 must pass; otherwise the command exits non-zero and nothing else changes |
| IXP-10 | Plan verification on disposable PostgreSQL 18 with a large synthetic journal (millions of audit rows across many organizations, plus evidence rows): `EXPLAIN (ANALYZE, BUFFERS)` of IXP-6 shows an index scan (backward) on `ix_journal_capability_refusal_evidence` with `LIMIT 8`, at most 8 heap rows plus dead entries, and no scan of `uq_journal_stream_revision`; repeated with fresh and with `ANALYZE`d statistics, and with the statement **prepared** (generic plan) to prove the literal predicate still matches |
| IXP-11 | Contract test: the IX-B predicate literals equal the recorder's stream and kind constants; the approved `pg_get_indexdef` rendering is a pinned constant shared by the command and T2-8 |
| IXP-12 | Concurrency: the concurrent build is run while synthetic writers append to all three streams and the archive loop delivers; no writer fails, no deadlock, the resulting index is valid and contains exactly the matching rows |

### 5A.9 Rollout ordering, rollback and owner decisions

| Step | Action | Gate |
|---|---|---|
| R-1 | **E3-M2f-3i** (separately authorized): dedicated index command, pinned definition, IXP-8 check, tests M2F3-73..M2F3-84 on disposable PostgreSQL | Owner authorization |
| R-2 | **E3-M2f-3b**: recorder with T2-8, lookup IXP-6, everything else in this record | Owner authorization; E3-M2f-3i accepted |
| R-3 | **E3-M2f-3G**: both runs of record with IX-B applied, plus the index-absent and restore-without-index cases | Owner authorization |
| R-4 | Any persistent environment: apply IX-B (IXP-1, IXP-9) **before** deploying any E3-M2f-3b build. In the meantime, a 3b build without IX-B records nothing (`INDEX_UNAVAILABLE`, L-D) | OD-IX-2 |

**Rollback.** Rolling back an E3-M2f-3b binary leaves IX-B in place; it is harmless to every build. Dropping IX-B (`DROP INDEX CONCURRENTLY`) is a separately approved operation; while it is absent, 3b instances fail closed for evidence (`INDEX_UNAVAILABLE`), and safety is unaffected. A restore from a backup taken before IX-B existed removes it; the recovery runbook must re-run the command before evidence resumes.

**Live writers.** **No migration runs against live writers without separately approved safeguards.** Disposable Development/Testing databases (E3-M2f-3i, E3-M2f-3G) are exempt only in the sense that they are created and destroyed by the gate.

| ID | Owner decision required | Options | Recommendation |
|---|---|---|---|
| **OD-IX-1** | Application path for IX-B | Dedicated privileged command (§5A.7) or ordinary EF migration | **Dedicated command** (avoids F-E2, the EF history lock and the transaction problem) |
| **OD-IX-2** | Rollout in any persistent environment | (a) quiesced Authority writers (maintenance window), concurrent build, verification, restart; (b) live writers with separately approved safeguards (lock and wait timeouts, monitoring of long transactions, abort criteria, an INVALID-index cleanup procedure) | **(a)** for the first persistent application; (b) only after its safeguards are written up and approved. Not needed for E3-M2f-3i/3G, which use disposable databases only |

---

## 6. Handler placement

### 6.1 How the endpoint filter observes the typed refusal (source analysis at the baseline)

*(Note 2026-11-17, E3-M2f-3b record §2 D-3b-1, owner acceptance requested: `AuthorityException` is sealed and existing tests assert its exact type, so item 1 is implemented as the unchanged exception carrying a `CapabilityRefusalContext` in `Exception.Data`, attached only by the floor and never serialized. Items 2–6 are unchanged.)*

1. **Typed refusal.** `EnvelopeCapabilityFloor.Refuse` throws a sealed `EnvelopeCapabilityRefusal : AuthorityException` with the identical `ErrorCode`, `HttpStatusCode` (409), `Retryable` (false) and `Detail`, carrying the locked organization id and parsed level as non-serialized properties.
2. **Disposal before the filter.** The scope is declared with `await using` in the method that throws (3–7) or in its `Core` method (1–2). C# lowers `await using` to `try/finally { await DisposeAsync(); }`, so the rollback completes before the exception leaves that method and reaches the endpoint lambda.
3. **Filter position.** An endpoint filter on exactly the seven route builders wraps the handler delegate; `await next(context)` rethrows the handler's exception into the filter. It catches only `EnvelopeCapabilityRefusal`, runs T2, then `throw;` (same instance).
4. **Mapping after the filter.** `Program.cs` order: `ReconcilerLookupRefusalMiddleware` → `UseExceptionHandler()` (`AuthorityExceptionHandler`) → `UseStatusCodePages` → authentication → authorization → rate limiter → endpoint. The filter runs inside the endpoint, before `AuthorityExceptionHandler`, whose `case AuthorityException ex` writes `ErrorResponse(ex.ErrorCode, correlation, ex.Retryable, ex.Detail)` with `ex.HttpStatusCode`, byte-identical to E3-M2f-2.
5. **Workload.** The filter sets `workload_name` from its route's policy (§5.1).
6. **Callers.** The only production callers of the seven methods are the seven endpoint lambdas.

Source analysis only; PD-03 is conditional on the executed proof of §6.5.

### 6.2 Application wrapper alternative

Refactor entry points 3–7 into `XxxCoreAsync` (holding the scope) plus a public wrapper, as Prepare and Commit already are; the wrapper catches the typed refusal after `Core` returns.

### 6.3 Comparison

| Property | Endpoint filter | Application wrapper |
|---|---|---|
| T1 disposed before T2 | Same guarantee | Same |
| Exception mapping unchanged | Yes | Yes |
| Covers non-HTTP callers | No | **Yes** |
| Independent of the HTTP host | No | **Yes** |
| Touches entry-point bodies pinned by M2F-25 | No | **Yes** |
| Workload source | Route policy, directly | Must be passed in |

### 6.4 Selection [CONDITIONAL, PD-03]

**Preferred:** endpoint filter on exactly the seven routes. **Fallback:** Application wrapper, adopted (and reported) if (a) any of M2F3-07, -17, -45, -46, -47 fails; (b) a non-HTTP caller of a seven-route method exists or is added; (c) the filter cannot be pinned to exactly seven routes; or (d) the pipeline changes so the filter no longer runs before `AuthorityExceptionHandler`.

### 6.5 Placement acceptance tests

M2F3-07, M2F3-17, M2F3-45, M2F3-46, M2F3-47 (§10).

---

## 7. Final transaction sequence [SPECIFIED]

```
Request on one of the seven routes (authenticated; header → client level)
│
├─ T1 (unchanged E3-M2f-2)
│    BEGIN; lock organization row; [1,2: Release B fence]; capability check → throws EnvelopeCapabilityRefusal
│    leaving await-using → DisposeAsync → ROLLBACK (nothing written; locks released)               [§3.1]
│
├─ Endpoint filter catches EnvelopeCapabilityRefusal (after disposal)                                 [§6.1]
│    K from (organization, route-derived workload, entry point, code); observed = instance clock (informational)
│    pre-filter: K recorded/found in its current bucket  → count suppressed                → RESPOND
│                K inconclusive in its current bucket    → count suppressed                → RESPOND
│                attempt limit reached                   → 9254 ATTEMPT_LIMIT             → RESPOND
│
├─ T2 (new scope, own bounded token)                                                                  [§3.2, §3.3]
│    BEGIN; lock organization row
│    t_db := clock_timestamp()   (separate statement after the lock);  H := date_trunc('hour', t_db)   [§5.2]
│    IX-B present, valid, approved definition?  no → ROLLBACK; 9254 INDEX_UNAVAILABLE; negative-cache K   [T2-8]
│    lookup: newest ≤ 8 evidence rows of the organization via IX-B (statement timeout)                [§5.3]
│        inconclusive (regression, order, digest, bound, error, timeout) → ROLLBACK; 9254 <reason>; negative-cache K
│        conclusive, (K, H) present      → ROLLBACK; pre-filter K
│        conclusive, (K, H) absent       → AuditJournal.Append(EnvelopeCapabilityRefused, body); COMMIT
│    COMMIT confirmed                     → pre-filter K
│    COMMIT outcome unknown               → mark Ambiguous; 9253; retry runs the full lookup again (D-6)
│    error before commit                  → mark Pending; 9253; in-memory retry (D-4)
│
└─ RESPOND: rethrow → AuthorityExceptionHandler → unchanged 409                                        [D-1]

Later, existing ArchiveProcessor (own transaction): Pending → Anchored (D-3), or ARCHIVE_BODY_MISMATCH
quarantine of that organization after a restore (D-8). Hosted retry service: Pending/Ambiguous keys, best effort.
```

---

## 8. Restore and failure-policy acceptance criteria

### 8.1 Failure policy [CONDITIONAL, PD-03]

| # | Criterion | Test |
|---|---|---|
| F-1 | The 409 is unchanged when T2 appends, finds the key, is inconclusive, fails, times out, is ambiguous, or is cancelled by client abort | M2F3-07, -19, -47, -48, -62 |
| F-2 | Evidence failure never admits, never converts the status, never blocks beyond the T2 budget | M2F3-19, -36 |
| F-3 | Every inconclusive lookup, failure and suppression is observable (9253/9254 with reason, counters) | M2F3-19, -20, -58, -62..-66 |
| F-4 | No inconclusive lookup ever appends | M2F3-58, -62..-66, -78, -79, MZ-22, MZ-25..MZ-28, MZ-31, MZ-35 |

### 8.2 Restore criteria [DECIDED by B-M2f3-2, RB-a]

| # | Criterion | Test |
|---|---|---|
| RS-1 | T2 never sets the organization quarantine, including in a restored database whose next audit revision collides with the archive | M2F3-55 |
| RS-2 | T2 cannot bypass archive-integrity detection: it appends only through `AuditJournal.Append` to stream `audit`; it makes no archive call; delivery stays with `ArchiveProcessor` | M2F3-38, -57, MZ-15 |
| RS-3 | After a restore copy, `ArchiveProcessor` detects the collision on the first post-restore audit append (evidence) and quarantines **that organization only** (`ARCHIVE_BODY_MISMATCH`); other organizations on the same Authority are unaffected (**isolation**) | M2F3-57 |
| RS-4 | Pre-restore evidence in the archive is intact and readable; the post-restore evidence row is retained in AuthorityDb, not deleted, repaired or re-signed (**preservation**) | M2F3-57 |
| RS-5 | Nothing in E3-M2f-3 clears the quarantine (`trg_organization_integrity_quarantine_is_permanent`); recovery remains the separately reviewed G14 process | M2F3-57, MZ-16 |
| RS-6 | A restore below the first binding gives `O = 1`: level-1 requests are admitted, no refusal and no evidence occur (R-M2f-4 / BD-1, demonstrated, not fixed) | M2F3-33 |

**Not verified and not claimed:** G14 recovery of a quarantined organization, restore recovery in general, BD-1 and BD-2. All remain **open**. Expected-gap demonstrations are labelled `EXPECTED_GAP_RESTORE_EVIDENCE` and `EXPECTED_GAP_BD1_EVIDENCE` and are outside pass totals.

---

## 9. Final event schema [DECIDED by PD-04; SPECIFIED]

### 9.1 Event kind

`EnvelopeCapabilityRefused`, stream `audit`, inside the unchanged `DynamicForms/SecurityJournal/v1` envelope (which carries `organization_id`, stream revision, previous digest and `created_on_utc` from the instance clock). No summary event (PD-07). No event for the `unavailable` outcome (PD-12).

### 9.2 Body, `evidence_schema_version` 1 (closed; no other field)

| Field | Type / values | Semantics |
|---|---|---|
| `evidence_schema_version` | `1` | Body schema version |
| `refusal_code` | `"CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT"` | The only code in scope |
| `organization_id` | uuid | The organization locked in T1 and T2 |
| `workload_name` | `business-api` \| `field-protection-verifier` \| `search-index-builder` \| `other` | Approved enumeration; derived from the route policy; `other` unreachable |
| `entry_point` | `prepare` \| `commit` \| `verify` \| `verify-batch` \| `protection-census` \| `index-backfill` \| `index-deep-verify` | Approved enumeration; fixed by the route |
| `client_capability_level` | `1` | Parsed level; never the raw header |
| `evidence_database_time_utc` | timestamp, UTC, microseconds | **Authoritative.** `t_db`: AuthorityDb `clock_timestamp()` read in T2 after the organization lock (§5.2). Orders evidence entries (I-1) and defines the bucket |
| `evidence_bucket_start_utc` | timestamp, hour-aligned, UTC | `date_trunc('hour', evidence_database_time_utc)` |
| `refusal_observed_on_utc` | timestamp, UTC | **Informational.** The recording instance's `TimeProvider` reading when its refusal handler observed the refusal that led to this entry, after T1 was disposed. Not authoritative and not used by the bound; subject to instance clock skew; may fall in an earlier hour than the bucket (for example when the write was retried); **not** guaranteed to be the earliest refusal of the key in the bucket, the T1 start or the response time |

### 9.3 Excluded (RE-3, CC-1)

Content bodies, signed bytes, leaves, ciphertext; keys, HMAC material, key references; tokens and assertions; user identity; record, operation, idempotency key, request digest; binding information; raw header values; correlation identifiers; build, image or instance identifiers; floor cause, `E`, `D`, `O`.

### 9.4 Access

No API reads the audit stream; no permission changes (PD-09). Readers remain AuthorityDb and archive administrators under G08/PB-10 controls. M4-2 decides tenant visibility of this kind later.

*(Note 2026-11-20, C-165, PD-09 option B: the C8 M4-3 security-command timeline (AC-8.2) shows this kind to Security Auditors (view_audit) with `refusal_code`, `workload_name`, `entry_point`, `client_capability_level` and `evidence_database_time_utc` only, marked as recorded on a business-runtime report; `refusal_observed_on_utc` is withheld. The recorder remains the sole producer; no permission was added. `PHASE_C8_M4_AC8_SECURITY_CONTRACT.md` §5.2, amendment C-165.)*

---

## 10. Test and mutation matrix (to be executed only in authorized increments)

**Gate (PD-15):** §10.7 (M2F3-73..M2F3-82) green in E3-M2f-3i; §10.1, §10.2 and §10.5 green in E3-M2f-3b; §10.3, §10.4 and M2F3-83, -84 green in **two independent runs of record** on fresh disposable PostgreSQL 18 + Vault Transit (E3-M2f-3G; M2f-2G harness pattern; containers removed; evidence scrubbed); every MZ mutation **DETECTED** by its declared tests (driver built `--no-incremental`, `-VerifyRestored`, DETECTED / INCONCLUSIVE / SURVIVED). Expected-gap demonstrations are outside pass totals. **No test is executed in E3-M2f-3a.**

*(Note 2026-11-17, final closure: the gate is **not met as written** and was accepted by the owner within the verified scope; the reconciled planned/executed/passed/inconclusive/not-executed matrix is `E3_M2F3G_VERIFICATION_RECORD.md` §12, and the gaps are listed in §14.)*

### 10.1 Unit

| ID | Test |
|---|---|
| M2F3-01 | Body field set equals §9.2 exactly (serialization snapshot) |
| M2F3-02 | Excluded data absent (correlation id, raw header `" 2 "`, actor assertion, binding facts, `D`/`O` in context) |
| M2F3-03 | Route → `workload_name` mapping for all seven entry points |
| M2F3-04 | Bucket = `date_trunc('hour', t_db)` at boundaries (`HH:59:59.999999`, `HH:00:00.000000`) |
| M2F3-05 | Pre-filter and negative cache: states, monotonic deadline, LRU eviction, capacity |
| M2F3-06 | Attempt limit counts T2 attempts and never affects the lookup decision |
| M2F3-07 | Typed refusal maps to a 409 byte-identical to the E3-M2f-2 golden response on all seven routes |
| M2F3-67 | Lookup decision table LK-1..LK-9: 0..8 returned rows; boundary row present or absent; key present; 8 rows all in bucket H (`BOUND_INCONSISTENT`); duplicate (K, H) rows; regression; order violation; digest failure; invalid key |

### 10.2 In-process integration (in-memory store; WebApplicationFactory host)

| ID | Test |
|---|---|
| M2F3-08..14 | One per entry point: level-1 refusal in a bound organization ⇒ exactly one entry with correct `entry_point`/`workload_name`; unchanged response; M2F-10..16 invariants hold |
| M2F3-15 | `D = 2`, unbound organization: recorded identically (no cause leak) |
| M2F3-16 | T1 writes nothing (change tracker empty at disposal) |
| M2F3-17 | Order trace: T1 rollback, then T2 begin on a different context and connection |
| M2F3-18 | Repeat in bucket: no second append |
| M2F3-19 | T2 store failure: 409 unchanged; 9253; key Pending; retried |
| M2F3-20 | Attempt limit reached: no T2; 9254 `ATTEMPT_LIMIT` |
| M2F3-21 | Level-2 traffic and admitted level-1 traffic produce no evidence |
| M2F3-22 | Requests rejected before the floor produce no evidence |
| M2F3-23 | Historical clients (header absent, `1`, malformed) ⇒ level 1 recorded |
| M2F3-24 | Organization isolation of entries and lookups |
| M2F3-25 | No summary event is ever written |
| M2F3-68 | The in-memory store emulates IXP-6 (newest 8 evidence rows of one organization), the IXP-8 index check (present/absent/invalid) and `t_db` through a test clock port faithfully (TQ-7) |

### 10.3 Disposable PostgreSQL (E3-M2f-3G)

*(Note 2026-11-17: executed in E3-M2f-3G, `E3_M2F3G_VERIFICATION_RECORD.md` §3. M2F3-28 was executed without its binding-inserting Prepares and M2F3-33 was not executed (both need a bound organization, outside the authorization). M2F3-29 crossed one real hour boundary per run (two buckets). The host's own archive loop waits for SpiceDB readiness; M2F3-31 delivery was verified with the same, unchanged processor directly.)*

| ID | Test |
|---|---|
| M2F3-26 | Real legacy agents (pre-M2b-2, `0be5f52`) at all seven entry points: evidence rows exist; refused transactions committed nothing |
| M2F3-27 | **Multi-instance:** 2 and 3 Authority instances refusing all seven keys of one organization concurrently: exactly ≤ 1 entry per key per bucket, ≤ 7 per organization per bucket |
| M2F3-28 | No deadlock or inversion with Release B writers, floor activation (A-1), binding-inserting Prepares; T2 never triggers the fence |
| M2F3-29 | **Fleet flood:** many organizations, all keys, sustained over several buckets: fleet count ≤ 7 × O_H per bucket; level-2 latency within budget |
| M2F3-30 | Query plan of IXP-6 inside the recorder's real T2 on a mature synthetic journal: backward index scan on IX-B, `LIMIT 8`, ≤ 8 heap rows plus dead entries; lock hold time of T2 measured (TQ-14) |
| M2F3-31 | Archive delivery of evidence entries; archive outage does not block recording |
| M2F3-33 | Restore below the first binding: level 1 admitted, no evidence (`EXPECTED_GAP_BD1_EVIDENCE`) |
| M2F3-69 | **Clock-boundary ordering:** two T2s started across an hour boundary that acquire the lock in reverse start order: with `clock_timestamp()` after the lock, buckets are non-decreasing along revisions; a variant reading `transaction_timestamp()` is shown to violate it (demonstration of §5.2, outside pass totals) |
| M2F3-70 | **Liveness, mature organization:** an organization with millions of audit entries and no prior evidence records its first refusal on the first attempt; the T2 statement trace shows no read of unrelated audit entries (replaces the earlier `EXPECTED_GAP_LIVENESS` demonstration) |

### 10.4 Fault injection

*(Note 2026-11-17: executed on PostgreSQL in E3-M2f-3G, `E3_M2F3G_VERIFICATION_RECORD.md` §3.2–§3.5. Under the approved D-3b-2 classification the index-unavailable (M2F3-62) and clock-regression (M2F3-65) cases are transient: Deferred, a bounded retry is scheduled and 9253 is logged (a repeat is `SuppressedPending`), not the 9254/negative cache named below; the gate verified that behaviour. Server-side insert, update and commit failures are Ambiguous (D-3b-4), never an append.)*

| ID | Test |
|---|---|
| M2F3-34 | Crash after T1 rollback, before T2: no evidence; a later refusal records it |
| M2F3-35 | Crash after T2 commit, before response: entry present; the retry finds it, no append |
| M2F3-36 | T2 lock wait expiry with the row held: 409 within budget; no append |
| M2F3-37 | Retry service shutdown and restart: no duplicates (bound holds), pending keys either recorded later or logged abandoned |
| M2F3-48 | **Ambiguous commit, committed** (server-side commit, client-visible failure, TQ-11): the next attempt's lookup finds the entry; no second append |
| M2F3-49 | **Ambiguous commit, not committed:** the next attempt appends exactly once |
| M2F3-50 | **Restart while Ambiguous:** the new process's lookup decides; count ≤ 1 per key per bucket |
| M2F3-51 | **Restart loop:** R restarts within one bucket under a flood across instances: ≤ 1 per key, ≤ 7 per organization, independent of R |
| M2F3-52 | **Cache eviction:** pre-filter capacity reduced to 1 under a flood: attempts rise, entries stay ≤ 1 per key per bucket |
| M2F3-62 | **Index absent or invalid** (never applied; dropped; INVALID after an interrupted concurrent build; definition mismatch): no append, 9254 `INDEX_UNAVAILABLE`, negative cache set; safety oracle M2F3-72 holds |
| M2F3-63 | **Timeout during lookup** (statement or budget): no append; 409 unchanged |
| M2F3-64 | **Database error during lookup or insert** (including a unique violation from a concurrent non-recorder audit append at the same revision): no append |
| M2F3-65 | **Clock regression:** newest evidence entry carries a later `evidence_database_time_utc` than the current `t_db` (seeded by test SQL to emulate a backward step or failover): no append, 9254 `CLOCK_REGRESSION`; append resumes once `t_db` passes it |
| M2F3-66 | **Unverifiable body:** an evidence body in R altered so its digest fails: no append; existing tamper handling unchanged |

### 10.5 Security and architecture

| ID | Test |
|---|---|
| M2F3-38 | The recorder is the sole appender of `EnvelopeCapabilityRefused`, only through `AuditJournal.Append` (A1) |
| M2F3-39 | The recorder is invoked only by the refusal handler, the handler only for the typed refusal |
| M2F3-40 | The recorder does not reference the ledger port; AM-F1 caller count still one |
| M2F3-41 | No new endpoint, route or permission; OpenAPI diff limited to the CC-2 text |
| M2F3-42 | Mutation driver over MZ-01..MZ-36 |
| M2F3-43 | Evidence-removal: delete or alter a delivered evidence row ⇒ chain / archive verification detects it *(Note 2026-11-17, C-155: requirement unchanged; status FAILED; remediation contract `SEC_BL_01_DR_CONTRACT_CLOSURE.md` (design D-R), not implemented)* |
| M2F3-44 | Existing suites green without assertion changes for level-2 callers; TS-M2f-01 remains the recorded open failure |
| M2F3-45 | Filter registered on **exactly** the seven routes |
| M2F3-46 | Filter rethrows the same instance; other exceptions pass through unrecorded |
| M2F3-47 | Client abort does not cancel T2; host shutdown does |
| M2F3-54 | T2 change set and SQL: one INSERT and one UPDATE of the three permitted columns; only the quarantine-permanence trigger fires |
| M2F3-55 | No direct quarantine, including in a restored database about to collide |
| M2F3-56 | Audit-revision side effects: `audit_journal_revision` +1; content-integrity, authorization, projection revisions unchanged; result snapshots not invalidated; attestations valid; one `CatchUp` scheduled |
| M2F3-57 | Restore-triggered G14 detection (RB-a): restore copy, evidence append, `ArchiveProcessor` quarantines that organization only; others unaffected; archived pre-restore evidence intact; post-restore row retained; nothing cleared (`EXPECTED_GAP_RESTORE_EVIDENCE`) |
| M2F3-58 | Every inconclusive outcome leaves the organization row and journal unchanged (row counts and revision before/after) |
| M2F3-59 | A business credential with an arbitrary `workload_name` claim records `business-api`; `other` unreachable |
| M2F3-60 | `evidence_bucket_start_utc = date_trunc('hour', evidence_database_time_utc)`; `t_db` read after the lock statement (statement trace) |
| M2F3-61 | The recorder locks the organization row before the lookup and holds it through commit (A2; statement trace) |
| M2F3-71 | `t_db` is not taken from the instance clock or from `transaction_timestamp()` (source-shape and statement check) |
| M2F3-72 | Seven-event bound oracle: after every fault, concurrency and index test of §10.3, §10.4 and §10.7, a read-only SQL check over the organization's whole audit stream (not through IX-B) finds ≤ 1 entry per (key, bucket) and ≤ 7 per bucket |

### 10.6 Mutations (each must be DETECTED)

*(Note 2026-11-17, FU-M2f3-02, `E3_M2F3_FU_VERIFICATION_RECORD.md` §3: MZ-02, -13, -24, -34, -35, -36 executed and DETECTED; MZ-09 executed and **SURVIVED** in its success-path form (MZ-09a: no test drives an admitted request to a successful completion through the filter; M2F3-21 is therefore reduced in scope); MZ-03 is not executable as written (the refusal context exists only on the exception leaving `next`), its closest expressible variant was DETECTED and is not counted. M2F3-43 has a dedicated test since FU-M2f3-01 and fails as written (§10.5 entry; record §2.3).)*

*(Note 2026-11-17, follow-up, `E3_M2F3_FU_VERIFICATION_RECORD.md` §10: FU-M2f3-03 adds a success-path test for M2F3-21 that DETECTS MZ-09a, so MZ-09 is DETECTED. Owner dispositions: MZ-22 accepted as an equivalent survivor under the seven-key contract (reassess if the key space changes); MZ-03 not executable as written, MZ-03a accepted as supplementary evidence and not counted as MZ-03 detected. M2F3-43 is unchanged; its delivered-entry design investigation is `E3_M2F3_DELIVERED_ENTRY_VERIFICATION_DESIGN.md` (documentation only).)*

| ID | Mutation | Detected by |
|---|---|---|
| MZ-01 | Remove recording | M2F3-08..14, -26 |
| MZ-02 | Record inside T1 | M2F3-16, -17 |
| MZ-03 | Record before `next` returns | M2F3-17 |
| MZ-04 | Swallow the exception | M2F3-07, -46 |
| MZ-05 | Convert to 503 on evidence failure | M2F3-19 |
| MZ-06 | Use the request abort token for T2 | M2F3-47 |
| MZ-07 | Filter on six routes (each dropped in turn) | M2F3-45 |
| MZ-08 | Filter on an eighth route | M2F3-45 |
| MZ-09 | Record on admit | M2F3-21 |
| MZ-10 | Remove the pre-filter | M2F3-18, -29 |
| MZ-11 | Remove the attempt limit | M2F3-20 |
| MZ-12 | Pre-filter treated as proof of absence (append without lookup on a cache miss) | M2F3-51, -52, -72 |
| MZ-13 | Append again after an ambiguous commit without a lookup | M2F3-48, -72 |
| MZ-14 | Treat Ambiguous as recorded and stop retrying | M2F3-49 |
| MZ-15 | Append to another stream or call the archive directly | M2F3-38, -57 |
| MZ-16 | Set quarantine fields in T2 | M2F3-54, -55 |
| MZ-17 | Touch `content_integrity_revision` or `authorization_revision` | M2F3-54, -56 |
| MZ-18 | Derive `workload_name` from the claim | M2F3-59 |
| MZ-19 | Add the correlation id to the body | M2F3-01, -02 |
| MZ-20 | Add the floor cause / `E` / `D` | M2F3-01, -02, -15 |
| MZ-21 | Query the ledger from the recorder | M2F3-40 |
| MZ-22 | Treat 8 rows all in bucket H as conclusive (append) | M2F3-67, -72 |
| MZ-23 | Drop the per-key presence check | M2F3-18, -27, -72 |
| MZ-24 | Look up before taking the organization lock | M2F3-27, -61, -72 |
| MZ-25 | Use `transaction_timestamp()` / `now()` for `t_db` | M2F3-60, -69, -71 |
| MZ-26 | Use the instance clock for `t_db` | M2F3-71, -65 |
| MZ-27 | Drop the regression check (LK-6) | M2F3-65 |
| MZ-28 | Drop the order or digest checks (LK-3, LK-4) | M2F3-66, -67 |
| MZ-29 | Append on lookup timeout or error | M2F3-63, -64 |
| MZ-30 | Change `LIMIT 8` to a smaller value | M2F3-67, -72 |
| MZ-31 | Skip the IX-B check (T2-8) | M2F3-62, -78 |
| MZ-32 | Pass the stream or kind as bind parameters instead of literals | M2F3-76 |
| MZ-33 | Query without the organization filter or without `ORDER BY … DESC` | M2F3-67, -70, -72 |
| MZ-34 | Index command re-applies runtime grants or runs inside a transaction | M2F3-80, -81 |
| MZ-35 | Treat an INVALID index as present | M2F3-62, -79 |
| MZ-36 | Predicate literal differs from the recorder constant | M2F3-77 |

### 10.7 Index (LV-1) — E3-M2f-3i and E3-M2f-3G, disposable PostgreSQL 18 only

| ID | Test |
|---|---|
| M2F3-73 | The dedicated command creates IX-B with exactly IXP-1; re-running is a no-op; `pg_get_indexdef` equals the pinned rendering (IXP-11) |
| M2F3-74 | **Plan on a mature journal:** millions of audit rows across many organizations, one organization with no evidence and one with evidence across many hours: `EXPLAIN (ANALYZE, BUFFERS)` of IXP-6 shows a backward index scan on IX-B with `LIMIT 8`, ≤ 8 heap rows (plus dead entries), no use of `uq_journal_stream_revision`; with and without `ANALYZE` |
| M2F3-75 | **Plan stability:** the same after bulk inserts of unrelated audit rows, after many evidence rows have been archive-updated (dead entries) and after autovacuum |
| M2F3-76 | **Prepared statement:** IXP-6 executed as a prepared statement past the generic-plan threshold still uses IX-B (literal predicate); the parameterized-predicate variant is shown not to (demonstration, outside pass totals) |
| M2F3-77 | Contract: predicate literals equal the recorder's constants; the command and T2-8 share one pinned definition |
| M2F3-78 | T2-8 refuses each of: index absent, `indisvalid = false`, `indisready = false`, definition differing in column order, predicate or uniqueness |
| M2F3-79 | Interrupted concurrent build (cancelled mid-build) leaves an INVALID index; the command reports it and exits non-zero; T2-8 refuses it; cleanup with `DROP INDEX CONCURRENTLY` and a rerun yields a valid index |
| M2F3-80 | The command never calls `AuthorityRuntimeGrants.Apply` and changes no grant (privilege inventory before/after identical apart from the new index object) |
| M2F3-81 | The build statement runs outside any transaction block and with the configured `lock_timeout`; the command uses the migration identity, never the runtime credential |
| M2F3-82 | **Concurrent build with live synthetic writers** (all three journal streams, archive loop, Release B content writers): no writer fails, no deadlock, the index is valid and contains exactly the matching rows (IXP-12). This is a disposable-database test; it does not approve live-writer rollout (OD-IX-2) |
| M2F3-83 | **Rolling deployment:** a 3b build against a database without IX-B records nothing (`INDEX_UNAVAILABLE`) while serving refusals normally; after IX-B is applied, the next refusal is recorded; the safety oracle (M2F3-72) holds throughout |
| M2F3-84 | **Restore without the index:** restore copy from a point before IX-B existed; evidence recording stops (`INDEX_UNAVAILABLE`), G14 detection unchanged (M2F3-57); re-running the command restores liveness; nothing else changes |

---

## 11. Remaining blockers and technical questions

### 11.1 Blockers

| ID | State |
|---|---|
| B-M2f3-1 | **Decided (QC-1, conditional).** Bound proven (§5.4); conditions are the executed tests of §10 |
| B-M2f3-2 | **Decided (RB-a).** §8.2 |
| B-M2f3-3 | **Decided (accepted).** §3.3 |
| B-M2f3-4 | **Decided (LV-1, conditional; LV-2..LV-4 rejected).** Liveness and safety specified with the IX-B partial non-unique index (§5.4, §5.6, §5A). Conditions: OD-IX-1, OD-IX-2, the E3-M2f-3i increment and the executed tests of §10.7 |
| OD-IX-1 | **Decided 2026-11-17: approved with conditions** (dedicated privileged command). Implemented as E3-M2f-3i (`E3_M2F3I_IMPLEMENTATION_RECORD.md`), accepted for Development/Testing (owner, 2026-11-17) |
| **OD-IX-2** | **[OPEN] owner decision:** rollout in persistent environments (quiesced writers recommended, §5A.9). Blocks only persistent use, not E3-M2f-3i/3b/3G on disposable databases |

### 11.2 Technical questions (resolved in E3-M2f-3b/3G, not owner decisions)

| ID | Question | Status |
|---|---|---|
| TQ-1 | T2 lock-wait and budget mechanism (`SET LOCAL lock_timeout` / `statement_timeout` versus linked cancellation) | Implemented in E3-M2f-3b (D-3b-5: 1 s lock, 500 ms statements, 2 s budget); verified on PostgreSQL 18 in E3-M2f-3G (55P03 at ~1.03 s; 57014 at ~0.53 s at the catalogue check and at the lookup) |
| TQ-2 | Release B catch-all does not apply to an audit-only T2 | Resolved by inspection; executed by M2F3-28 |
| TQ-3 | Query plans on PostgreSQL 18 and later Azure (R-CARV-18) | PostgreSQL 18.0 verified in E3-M2f-3G (backward IX-B scan under Limit inside the real T2, with and without statistics, and as a generic prepared plan); Azure open |
| TQ-4 | Workload source | Resolved: route policy (§5.1) |
| TQ-5 | Audit append in a quarantined organization | Resolved by inspection (no quarantine check in `AuditJournal.Append`); executed by M2F3-55 |
| TQ-6 | Later audit revisions do not invalidate attestations or coverage | Open; inspection found none (M2F3-56) |
| TQ-7 | In-memory store emulation of the lookup and of `t_db` | Resolved (E3-M2f-3b: transactional, row-locked test double) |
| TQ-8 | Filter position relative to exception mapping | Resolved by inspection; executed by M2F3-07, -46 |
| TQ-9 | Retry service lifetime, shutdown, OpenAPI-generation guard | Implemented (E3-M2f-3b); shutdown loses pending retries (D-4) |
| TQ-10 | Search-access maintenance load at the bound | One CatchUp per audit revision verified (E3-M2f-3G T-02); load at the bound not measured beyond the boundary flood (B-03); open |
| TQ-11 | Fault-injection technique for a server-side commit with a client-visible failure (a throw in a post-execution EF interceptor breaks Npgsql) | Resolved in E3-M2f-3G: a post-commit EF **transaction** interceptor (committed, outcome lost) and backend termination before COMMIT (rolled back); a deferred constraint trigger for a server-side COMMIT failure |
| TQ-12 | Classification of commit-phase exceptions as Ambiguous | Resolved (E3-M2f-3b D-3b-4): every commit-phase exception is Ambiguous |
| TQ-14 | T2 lock hold time with IXP-6 under load; effect on that organization's content traffic | Disposable PostgreSQL (E3-M2f-3G): 28 ms on a 2M-entry organization; T2 p95 519 ms under 6-instance contention on one organization; 518 concurrent level-2 writes all succeeded next to T2s; Azure open |
| TQ-15 | When the provider sends `BEGIN` (eagerly or with the first command); documents why `transaction_timestamp()` is unsuitable (M2F3-69) | `BEGIN` not observable for the runtime role (product `log_statement = 'none'`); open. The unsuitability of `transaction_timestamp()` is demonstrated at a real boundary (E3-M2f-3G B-01d) and does not depend on it |
| TQ-16 | `clock_timestamp()` behaviour on Azure PostgreSQL Flexible Server, including HA failover (feeds LK-6 expectations) | Open (Azure, later) |
| TQ-17 | Architecture check that the recorder obtains its organization state only from `LockOrganizationAsync` in the same scope (A2) | Resolved (E3-M2f-3b architecture order test) |
| TQ-18 | Planner choice for IXP-6 versus a backward scan of `uq_journal_stream_revision` with a kind filter, with fresh and stale statistics; PostgreSQL offers no plan hints, so the guarantee is plan verification plus the statement timeout | PostgreSQL 18 disposable: IX-B in every variant (E3-M2f-3i I-25..I-29); Azure open |
| TQ-19 | Exact `pg_get_indexdef` rendering of IX-B on PostgreSQL 18 and on Azure PostgreSQL (R-CARV-18) | PostgreSQL 18.0 rendering pinned (E3-M2f-3i); Azure open |
| TQ-20 | How the dedicated command is built (raw SQL command or a separate EF context as for floors) and the architecture check preventing a second IX-B definition in the `AuthorityDbContext` model | Resolved (E3-M2f-3i): raw Npgsql command; architecture tests |
| TQ-21 | Expected-output updates of the G08 ownership and privilege inventories for the new index | Open (not updated by E3-M2f-3i) |
| TQ-22 | Whether EF Core's migration lock permits `suppressTransaction` operations (relevant only if OD-IX-1 selects the ordinary path) | Open (only if needed) |
| TQ-23 | `CREATE INDEX CONCURRENTLY` duration and waits on a large journal with long-running transactions present; lock_timeout behaviour | Disposable: about 1 s for 2M rows; both lock-timeout phases verified (E3-M2f-3i I-20, I-21, I-30); persistent scale open (OD-IX-2) |

(TQ-13, the clock-skew margin of the earlier QC-1 draft, is withdrawn: no skew margin is used.)

### 11.3 Risks

| ID | Risk | Status |
|---|---|---|
| R-M2f3-1 | Crash windows lose evidence for the affected refusal (D-5) | Accepted by PD-02 |
| R-M2f3-2 | AuthorityDb restore loses undelivered evidence; the bound restarts per timeline (D-8) | Existing G14 class |
| R-M2f3-3 | A post-restore evidence append can trigger organization quarantine (CC-5) | Accepted under RB-a |
| R-M2f3-4 | No evidence when a restore admits level-1 traffic (RS-6) | R-M2f-4 / BD-1, open |
| R-M2f3-5 | A compromised business runtime can cause bounded evidence naming any registered organization, or avoid evidence by claiming level 2 | Bounded (≤ 7 per organization per hour); CAP-S7 |
| R-M2f3-6 | Privileged AuthorityDb actor can remove an undelivered tail or forge entries (A4) | G08 residual. *(Note 2026-11-17, C-155, D-1/D-4: "A4" = proof assumption A4 of §5.4, not an attacker class; forging needs only J1 (runtime credential), removal J2 (`SEC_BL_01_DR_CONTRACT_CLOSURE.md` §2). Unchanged by D-R: = R-DR-1)* |
| R-M2f3-7 | Suppressed, inconclusive, dropped and abandoned refusals have no durable trace | Accepted by PD-02/PD-07 |
| R-M2f3-8 | Extra organization-row locks and examinations under a flood | Bounded by pre-filter, negative cache and attempt limit; TQ-14 |
| R-M2f3-9 | Future tenant exposure of the audit stream (M4-2) | PD-09 |
| R-M2f3-10 | Business API and Worker indistinguishable (shared role) | Accepted consequence of §5.1 |
| R-M2f3-11 | Liveness is lost while IX-B is missing or invalid (rolling deployment, restore from an older backup, privileged drop); safety holds (L-D) | Accepted under LV-1; rollout order R-4 and runbook |
| R-M2f3-12 | An AuthorityDb clock step backward or failover makes lookups inconclusive (evidence loss, never duplication) until the clock catches up | Accepted; LK-6 |
| R-M2f3-13 | T2 holds the organization lock for the index check, the 8-row lookup and the append; this delays that organization's other transactions briefly | Measured on disposable PostgreSQL (E3-M2f-3G, TQ-14); Azure open |
| R-M2f3-14 | The attempt limit can drop evidence beyond about 36 active organizations per instance per hour | Accepted; logged |
| R-M2f3-15 | An unverifiable body among an organization's newest eight blocks that organization's evidence permanently until investigated | Accepted (fail closed; outside A4); Critical alert |
| R-M2f3-16 | The planner may choose a worse plan (no hints in PostgreSQL); the outcome is a timeout and no append, never a wrong decision | Open; TQ-18. Not observed on PostgreSQL 18.0 (E3-M2f-3G); a mutated lookup that no longer matched IX-B was detected (PM-04) |
| R-M2f3-17 | An interrupted concurrent build leaves an INVALID index; until cleaned up, evidence is not recorded | Accepted; IXP-5 |
| R-M2f3-18 | The index ties the schema to one event-kind literal; adding refusal classes later (PD-11) needs a new or rebuilt index | Accepted; future decision |
| R-M2f3-19 | Building the index on a populated persistent database waits on long-running transactions and scans the table twice | Open; OD-IX-2 |
| R-FU-1 | *(added 2026-11-17, FU-M2f3-01)* A privileged AuthorityDb writer can delete or consistently forge a **delivered** evidence row without product detection; the archive keeps the delivered original but nothing reports the divergence; a forged row can suppress recording of its key for the rest of the bucket | **Open**; retained; tracked as SEC-BL-01 (`SECURITY_BACKLOG.md`); M2F3-43 FAILED. *(Note 2026-11-17, C-155: D-1: the privileged actor is attacker class J1 or J2 of `SEC_BL_01_DR_CONTRACT_CLOSURE.md` §2, not "A4". D-3: suppression is not bounded to the bucket; a forged entry with a future `evidence_database_time_utc` makes later lookups Inconclusive (LK-6) until the database clock passes it (source inspection; not executed). D-4: deletion needs J2; alteration and insertion need only J1. Remediation direction D-R approved, not implemented)* |

Carried forward unchanged: BD-1, BD-2 (FB-1 obligation), G14 recovery, F-E2, R-A1-1..5, R-M2f-1..R-M2f-10 (R-M2f-9 closes only when E3-M2f-3i, -3b and -3G are accepted; *2026-11-17: accepted, R-M2f-9 closed for Development/Testing only, §14*), R-CARV-12 for its own class, R-M2b2-7, R-M2e-9, UQ-4, TS-M2f-01, FU-M2E2-MUT-01, INV-1/INV-2, OA-FE1-2, V2-IND-01, RB-CARV-1 production closure, PB-08, PB-11, PB-12, Azure PostgreSQL verification.

---

## 12. Proposed E3-M2f-3b implementation boundary [SPECIFIED; not authorized]

### 12.1 Preconditions

Owner decisions OD-IX-1 (and OD-IX-2 before any persistent use); E3-M2f-3i authorized, implemented and accepted, so that IX-B and its pinned definition exist; owner authorization of E3-M2f-3b; baseline clean at the commit containing this record.

### 12.2 In scope

*(Note 2026-11-17: implemented as E3-M2f-3b. Log classification refined (D-3b-2): transient reasons that schedule a retry are 9253; 9254 means the refusal will not be recorded; 9256 reports a retry-loop failure; reason `RETRY_CAPACITY` added. In-flight coalescing added (D-3b-3).)*

| Area | Content |
|---|---|
| Application (`Features/Content`) | Sealed `EnvelopeCapabilityRefusal`; `CapabilityRefusalEvidenceRecorder` (key, pre-filter, negative cache, attempt limit, Pending/Ambiguous state, T2 per §3, lookup per §5.3); closed body type (§9.2) |
| Ports / Infrastructure | Read-only store-port methods: `t_db` (`clock_timestamp()` after the lock), the IXP-8 index check and the IXP-6 query (literal predicate), in the EF store and the in-memory store. **No schema change in E3-M2f-3b**: IX-B is created by E3-M2f-3i |
| Api | Endpoint filter on exactly the seven routes; hosted retry service; log events 9253 (deferred, ambiguous) and 9254 (inconclusive with reason `INDEX_UNAVAILABLE`, `BOUND_INCONSISTENT`, `CLOCK_REGRESSION`, `TIME_ORDER_VIOLATION`, `UNVERIFIABLE_BODY`, `TIMEOUT`, `ERROR`; `ATTEMPT_LIMIT`; abandoned) and counters on meter `AuthorizationAuthority.EnvelopeCapability`; CAP-E1 text (CC-2) and regenerated internal OpenAPI |
| Documentation | Implementation record; runbook section in `E3_M2B1_CARV_OPERATIONS.md` |
| Tests | §10.1, §10.2, §10.5 and the mutation driver |

### 12.3 Out of scope

Migrations, tables, columns, indexes, triggers; server or application configuration; new endpoints, read paths or permissions; summary events; other refusal classes; `unavailable` evidence; any change to the capability decision, its ordering or its response; CARV, floors, ledger, BD-1, BD-2, G14 recovery; Envelope v2 acceptance; Azure, Stage B, persistent databases; E3-M2f-3G execution (separate authorization).

### 12.4 Stop conditions

Any write in T1; any T2 change outside §3.3; any change to a wire byte of the 409; any ledger access by the recorder; any admit path; any append without a conclusive lookup; any use of the instance clock or transaction start time for the bucket; any lookup that examines unrelated audit entries or runs without a valid IX-B; any need for a schema or configuration change not separately approved; a failed placement test without switching to the fallback and reporting; a level-2 regression in an existing suite.

### 12.5 Deployment prerequisites (PD-14)

Before any deployment beyond Development/Testing: monitoring routes for 9253/9254 (with 9100/9250); PB-11/PB-12 Authority release approval; the INV-1/INV-2 inventory; every item of `E3_M2F_CONTRACT_CLOSURE.md` §11.5.

---

## 13. Not claimed

- No implementation, test execution or verification. Source statements are inspection results, and the proof of §5.4 is an argument over the specified protocol, not test evidence.
- *(2026-11-17: §14 records the final Development/Testing closure; the bullets below describe the E3-M2f-3a record as written and remain true for persistent use.)*
- No closure of RE-1 (pending E3-M2f-3i, -3b and -3G and their executed tests), OPEN-M2f-1, R-M2f-9, BD-1, BD-2, G14 recovery, F-E2, R-CARV-12 for other classes, UQ-4 or any production gate. Restore recovery is not verified.
- No unqualified approval of PD-01, PD-03, PD-06, PD-10 or B-M2f3-1: each is conditional as recorded.
- No persistent activation, Envelope v2 acceptance, Azure or staging evidence; no independent review; no compliance or production-readiness claim.

---

## 14. Final Development/Testing closure (2026-11-17)

**Owner decision, 2026-11-17.** E3-M2f-3a, -3i, -3b and -3G **accepted for Development/Testing within their verified scope**; final closure documentation, validation and one controlled local SSH-signed commit authorized. Not authorized: push, merge, deployment, Azure, persistent database change, Stage B.

| Item | State after the owner decision |
|---|---|
| E3-M2f-3a (this record) | Accepted. Decisions, conditions and open items as recorded above; nothing in this section expands the contract |
| E3-M2f-3i | Accepted for Development/Testing (`E3_M2F3I_IMPLEMENTATION_RECORD.md`) |
| E3-M2f-3b | Accepted for Development/Testing; D-3b-1, D-3b-2 and D-3b-3 verified as implemented by E3-M2f-3G (`E3_M2F3B_IMPLEMENTATION_RECORD.md`) |
| E3-M2f-3G | Accepted for Development/Testing: two passing runs of record (run-1 68/68, run-3 68/68); run-2 67/68 kept with its analysed environment failure (`E3_M2F3G_VERIFICATION_RECORD.md`) |
| PD-15 gate | **Not met as written**; accepted within the verified scope. Reconciled matrix: `E3_M2F3G_VERIFICATION_RECORD.md` §12. Gaps: FU-M2f3-01 (dedicated tests for M2F3-22, -25, -43), FU-M2f3-02 (MZ-02, -03, -09, -13, -24, -34, -35, -36), M2F3-28 binding inserts and M2F3-33 (bound organization, OPEN-M2f-4), M2F3-44 inconclusive (TS-M2f-01 and one unidentified contract failure), MZ-22 equivalent survivor. *Update 2026-11-17 (FU-M2f3-01/02 executed, uncommitted, owner review pending; `E3_M2F3_FU_VERIFICATION_RECORD.md`): M2F3-22 and M2F3-25 passed; M2F3-43 **failed as written** (deletion and unsent or out-of-window alteration of a delivered row are not detected; needs a product verifier or an owner amendment; new risk R-FU-1); MZ-09 survived (proposed FU-M2f3-03); MZ-03 not executable as written; PD-15 still **not met***. *Follow-up 2026-11-17 (FU record §10, uncommitted): FU-M2f3-03 passed and MZ-09 DETECTED, M2F3-21 Passed; MZ-22 and MZ-03 owner dispositions recorded; M2F3-43 design investigated (design D recommended, OD-DV-1..7), not implemented; matrix 74 passed, 5 reduced, 1 failed (M2F3-43), 1 inconclusive (M2F3-44), 1 not executed (M2F3-33); PD-15 still **not met***. |
| Conditional decisions (PD-01, PD-03, PD-06, PD-10, B-M2f3-1, B-M2f3-4) | Remain **[CONDITIONAL]** as recorded in §1. Their executed-test conditions are evidenced within the verified scope (`E3_M2F3G_VERIFICATION_RECORD.md` §12). Conditions that depend on OD-IX-2 (PD-01 (d), B-M2f3-4) or on recovery (PD-10: G14 recovery, BD-1, BD-2) remain unmet. No decision becomes unconditional, and nothing here covers persistent environments |
| RE-1..RE-6 (`E3_M2F_CONTRACT_CLOSURE.md` §8) | Satisfied **for the capability-refusal class in Development/Testing only**. **Not** satisfied for persistent use, which still requires OD-IX-2, PD-14 monitoring routes and every item of `E3_M2F_CONTRACT_CLOSURE.md` §11.5. R-CARV-12 for other classes is not addressed |
| R-M2f-9 | Closed for Development/Testing only. Persistent use remains blocked |
| OPEN-M2f-1 | Still open, pending OD-IX-2 |
| Envelope v2 acceptance | Not activated anywhere; no acceptance policy was requested in any 3G database (Z-02); no data-key binding exists (Z-03) |
| Still open | OD-IX-2; BD-1, BD-2 (FB-1), G14 recovery; TQ-6, TQ-10 (at the bound), TQ-15, TQ-16, TQ-18/19/23 (Azure), TQ-21, TQ-22; open risks R-M2f3-2 (G14 class), -4 (BD-1), -13 (Azure), -16, -19, with the accepted residual risks of §11.3 unchanged; TS-M2f-01; the unidentified contract failure; FU-M2f3-01, FU-M2f3-02; OPEN-M2f-4; F-E2; V2-IND-01 (independent review); PB-11, PB-12; Azure PostgreSQL verification. *(Note 2026-11-17, correction D-2: FU-M2f3-01 and FU-M2f3-02 were executed and accepted later the same day; this entry is superseded by §14.1. The remaining items are unchanged)* |
| Review | Owner-led. Not an independent security review, not a certification and not production evidence |

### 14.1 Final closure of the refusal-evidence implementation workstream (Option A, 2026-11-17)

**Owner decision, 2026-11-17 (Option A):** the E3-M2f-3 refusal-evidence **implementation workstream is closed and accepted for Development/Testing**. No further product functionality is implemented under E3-M2f-3. **The original PD-15 verification gate remains OPEN**; this decision neither waives nor claims satisfaction of any outstanding PD-15 requirement, and the PD-15 acceptance criteria (§10) are unchanged.

| Item | State |
|---|---|
| Implementation (E3-M2f-3a, -3i, -3b, -3G) | Complete; accepted for Development/Testing within its verified scope (§14). Persistent use stays blocked (OD-IX-2, PD-14, `E3_M2F_CONTRACT_CLOSURE.md` §11.5) |
| Follow-up FU-M2f3-01, -02, -03 | Accepted (`E3_M2F3_FU_VERIFICATION_RECORD.md` §2, §3, §10): M2F3-22 and M2F3-25 passed as dedicated tests; **M2F3-21 passed** (FU-M2f3-03 success-path test); **MZ-09 detected**; MZ-02, -13, -24, -34, -35, -36 detected; **MZ-22 accepted as an equivalent survivor** under the seven-key contract (reassess if the key space changes); **MZ-03 remains not executable as written** (MZ-03a is supplementary evidence only, not counted as MZ-03 detected) |
| Harness correction HC-12 | **Accepted for both affected gates** (`scripts/dev/e3-m2f3g-evidence-pg-gate.ps1`, `scripts/dev/e3-m2e2-acceptance-pg-gate.ps1`): generated evidence under `docs/evidence/` is excluded from the configuration preflight; all configuration files elsewhere are still scanned (safety check 36/36, FU record §10.3a) |
| **M2F3-43** | **FAILED under the original PD-15 contract; status preserved.** Moved to the security backlog as **SEC-BL-01** (`SECURITY_BACKLOG.md`), historical audit-integrity verification of delivered entries. **R-FU-1 retained** (§11.3) |
| Design D | Preserved as a **candidate architecture only** (`E3_M2F3_DELIVERED_ENTRY_VERIFICATION_DESIGN.md`); not approved, not implemented; OD-DV-1..7 open under SEC-BL-01 |
| PD-15 matrix (82 closure tests) | 74 passed; 5 passed in reduced form (M2F3-28 binding inserts, -29 two buckets, -31 host archive loop, -42 mutation driver resting on two owner dispositions, -78 runtime variants); 1 failed (M2F3-43); 1 inconclusive (M2F3-44); 1 not executed (M2F3-33); 0 missing dedicated coverage. Mutations MZ-01..36: 34 detected, 1 equivalent survivor accepted (MZ-22), 1 not executable as written (MZ-03) |
| Remaining verification gaps (preserved) | M2F3-43 (SEC-BL-01); M2F3-28 binding-inserting Prepares and M2F3-33 restore below the first binding (both need a bound organization, OPEN-M2f-4); M2F3-29, -31, -42, -78 reduced; M2F3-44 inconclusive; **TS-M2f-01** (timing-sensitive contract test, open, not fixed); **the unidentified historical contract failure** of the E3-M2f-3G contract run 1 (kept separate, not attributed) |
| Still open (unchanged) | PD-15; OD-IX-2; OPEN-M2f-1; OPEN-M2f-4; BD-1, BD-2, G14 recovery; R-FU-1 and the open risks of §11.3; TQ-6, TQ-10, TQ-15, TQ-16, TQ-18/19/23 (Azure), TQ-21, TQ-22; F-E2; V2-IND-01 (independent review); PB-11, PB-12; Azure PostgreSQL verification |
| Not claimed | No PD-15 pass; no detection of delivered-row deletion or forgery; no persistent, Azure or production evidence; no independent review or certification |

### 14.2 SEC-BL-01 design D-R and PD-15 dependencies (owner decision C-155, 2026-11-17)

**Owner decision, 2026-11-17 (C-155):** the D-R architectural dispositions for SEC-BL-01 were approved and WP-0 (documentation only) authorized. The authoritative contract is `SEC_BL_01_DR_CONTRACT_CLOSURE.md`. This section changes **no** requirement, status or historical outcome of this record.

| Item | State |
|---|---|
| M2F3-43 | **FAILED**, requirement unchanged. Remediation contract D-R; acceptance by AC-DR-01..AC-DR-14 (inverted gap tests EG-1..EG-3, ADV-01..ADV-20, MV-01..MV-18, two PostgreSQL runs of record). **Not implemented** |
| Regressions D-R must preserve | M2F3-31 (archive delivery and outage), M2F3-54 (T2 statement and trigger set unchanged; therefore M-6 v1 adds **no** INSERT trigger on the journal), M2F3-57 (restore collision, isolation, preservation), the T2 request-path footprint; SEC-P01..SEC-P12 when D+ or M-6 is implemented (contract §13.4) |
| PD-15 | **OPEN.** Closing SEC-BL-01 is necessary but **not sufficient**. Unchanged: M2F3-33 not executed (bound organization, OPEN-M2f-4; no prohibited data-key binding may be inserted to make a test pass); M2F3-28 binding inserts, -29, -31, -42, -78 reduced as recorded; M2F3-44 inconclusive (TS-M2f-01 open, the unidentified 3G contract failure unattributed); MZ-22 and MZ-03 owner dispositions as recorded (contract §15) |
| Documentation corrections D-1..D-5 | Applied as dated notes (§5.4 A4, §11.3 R-M2f3-6 and R-FU-1, §14 row; other documents per contract §16). Historical text unchanged |
| Not claimed | No verifier, no D+, no detection of the M2F3-43 cases, no PD-15 pass, no production assurance, no independent review |

### 14.3 M2F3-43 technical verification and SEC-BL-01 workstream conclusion (owner decision C-161, 2026-11-19)

**Owner decision, 2026-11-19 (C-161):** the SEC-BL-01 Development/Testing workstream (D-R, WP-1..WP-7A) is concluded and accepted for Development/Testing, and its outstanding requirements are deferred to the pre-production backlog PP-01..PP-15 (`SEC_BL_01_DEVELOPMENT_WORKSTREAM_CONSOLIDATION.md`; contract amendment A-10). This section changes **no** requirement, status or historical outcome of this record.

| Item | State |
|---|---|
| M2F3-43 technical verification | **Successful within its demonstrated Development/Testing scope:** deletion of a delivered evidence row (`REVISION_GAP`), a consistently re-digested row never re-sent (`LINK_MISMATCH`), an alteration outside the newest eight (`BODY_DIGEST_MISMATCH`) and ADV-01..ADV-10 on delivered rows of all three streams are detected, in-process and in two independent PostgreSQL 18 + Azurite runs of record (WP-7, WP-7A). Detection, not prevention; alert-only; latency `T_inc`/`T_full` plus sweep duration |
| M2F3-43 status | **FAILED (retained).** Its acceptance is "by AC-DR-01..AC-DR-14" (§14.2); AC-DR-11 is INCONCLUSIVE (deferred) and AC-DR-13 is conditional only. Outstanding: an executed 2M-row AC-DR-11 measurement (after the F-7A-5 performance remediation) and unconditional owner acceptance (consolidation record §3). The FU-M2f3-01 outcomes are not rewritten |
| PD-15 | **OPEN.** Unchanged: M2F3-33 not executed; M2F3-28, -29, -31, -42, -78 reduced; M2F3-44 inconclusive; OPEN-M2f-4, TS-M2f-01 and the unidentified 3G contract failure open; MZ-22 and MZ-03 dispositions as recorded |
| R-FU-1 | No longer holds for **delivered** rows in Development/Testing; residuals R-DR-1 (= R-M2f3-6, including J2 deletion of an entry before its W2-S commitment, accepted by C-161), R-DR-2, R-DR-4, CKR-3, J4 + J6, J2 + J5 and JP remain. No production assurance |
| Not claimed | No PD-15 pass; no reclassification of M2F3-43; no production, Azure or persistent evidence; no independent review |
