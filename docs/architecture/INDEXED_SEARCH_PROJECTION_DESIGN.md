# Indexed search projection (Option C): architecture and security design — revision 3

- **Status:** **Architecture approved by the owner as the selected direction (C-121, 2026-11-06), subject to mandatory security conditions and independent design review. Nothing here is implemented, and no implementation stage (IX-1 to IX-7) is authorized.**
  * Revision 1 received conditional architectural approval with conditions C1 to C4 (C-120).
  * Revision 2 answered C1 to C4.
  * Revision 3 records the owner decisions on IX-D1 to IX-D14 (§15) and answers the five C-121 mandatory conditions (§17). It is the version submitted for independent review: [INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md](INDEXED_SEARCH_INDEPENDENT_REVIEW_PACKAGE.md).
  * **C-122 (2026-11-06):** the owner decided FD-1 to FD-8 (§17.7). Their attached conditions are specified in §17.9. FD-6 and FD-7 are deferred to the reviewer. **No independent review has occurred.**
  * **C-124 (2026-11-06):** the owner approved the §17.10 deadline contract with clarifications (§17.10.5) and decided OR-1 to OR-4 and OBS-IX-1 (provisional High). The resulting contracts are in §18. OR-1 and OR-2 stay **open** until the owner accepts §18.1 and §18.2.
  * **C-123 (2026-11-06):** the owner acts as the designated owner reviewer (an owner-led review, **not** an independent third-party assessment; `docs/governance/REVIEW_AND_AUTHORIZATION_WORKFLOW.md`). The proposed sweep-deadline decision is in §17.10. The Tier 1 owner-review evidence is `INDEXED_SEARCH_TIER1_OWNER_REVIEW_EVIDENCE.md`; its findings OR-1 to OR-4 are marked where they correct this document.
  * **Tier 2 (§9) is approved for further review only.** Neither its implementation nor its enablement is authorized.
  * Prepared by Claude (Opus). This is not an independent review.
- **Revision 1:** [INDEXED_SEARCH_PROJECTION_DESIGN_R1.md](INDEXED_SEARCH_PROJECTION_DESIGN_R1.md), cited here as "R1 §…". Its sections remain part of the design unless this revision changes them; where they differ, this revision governs.
- **Companion:** [ADR-PB-004 Amendment A2](../decisions/ADR-AMENDMENT-PB-004-A2-AUTHORITY-SEARCH-INDEX.md). The owner approved it as the governing architectural amendment, subject to conditions and independent design review (C-121). It separates the preserved guarantees from the accepted relaxations.
- **Separate finding:** [OBS-IX-1](../security/OBS-IX-1_AUTHORITY_APPEND_ONLY_UPDATE_PRIVILEGE.md), the Authority runtime UPDATE privilege on tables described as append-only.
- **Restrictions observed:**
  * no schema, worker, API or UI change;
  * no separate Search Service or external engine;
  * M4-1 not expanded, M4-2 unauthorized;
  * no push, PR, merge or deploy.
- **Branch:** `design/indexed-search-projection`, local only. It was created from `phase-c8/m4-governance-audit` at `e830d08`, because it cites that branch's S1 and P-S1-1 documents.
- **Not claimed:** G06, G19 and G21 stay **OPEN**. No performance figure here is measured unless quoted from existing evidence. Nothing here is production Level 2 evidence.

---

## 1. Owner conditions and where they are answered

| Condition | Answer |
|---|---|
| C1: AuthorityDb security (privileges, plaintext readers, no arbitrary changes, backup, retention, logging, replication, administration) | §5 |
| C2: IX-D3 integrity semantics (what changes; full verified reads kept; reconciliation and quarantine kept; no silent omission or addition; ENC-SR-08/09 only after approval; UI wording) | §6, and A2 §3–§4 |
| C3: scaling beyond 50,000 discoverable records without per-search enumeration; Authority remains the sole decision-maker | §9 |
| C4: mapping against the existing ADR-PB-004 guarantees, with every relaxation named | §10, and A2 §2–§3 |
| Deliverable 1: exact texts of IX-D1 to IX-D12 with recommendations | §15 (adds IX-D13 and IX-D14) |
| Deliverable 2: ADR-PB-004 Amendment A2 | companion file |
| Deliverable 3: revised threat and security-boundary analysis | §11 |
| Deliverable 4: the 50,000-plus strategy | §9 |
| Deliverable 5: recovery, rebuild and rollback | §12 |
| Deliverable 6: stage plan and acceptance gates | §16 |

---

## 2. Architecture overview

```
 Browser ──► Business API (untrusted for search decisions)
              │  validates request, capability check, admission
              │  one private call per search / per page
              ▼
 Authority runtime (trusted) ────────────────────────────────────────────────┐
   Content commit (existing)           Search query (new)                    │
   ├ verify staged bytes (HMAC, root)  ├ policy: approval + coverage         │
   ├ head CAS, manifest, checkpoint    ├ S1 pin ─► SpiceDB decisions ◄─ S1 verify
   └ derive postings + entry seal ┐    ├ candidate SQL (reader role)         │
                                  ▼    ├ per-match integrity check (§6.3)    │
   AuthorityDb (separate cluster)      └ result snapshot + evidence          │
   ├ schema authority        (heads, leaves, journal, edges, approvals)      │
   └ schema authority_search (entries, postings, coverages, snapshots,       │
                              access postings [Tier 2])                      │
 Business PostgreSQL ── never read or written by the search path ────────────┘
 search-index-builder workload ── submits business bytes for backfill only;
                                   the Authority verifies and derives
```

**Unchanged:**
- record opening (full verified read);
- SpiceDB as the permission decision engine under S1;
- AC-6 approvals;
- the business API contract (§6.6 adds one optional field).

---

## 3. Verified transaction boundaries (C-120 direction 7)

Verified against the code at `e830d08`:

| Fact | Source | Consequence |
|---|---|---|
| A content commit runs in **one** `ReadCommitted` transaction (`AuthorityStore.BeginScopeAsync`) that takes `organization_authorization_states … FOR UPDATE` (the organization lock) and `record_content_heads … FOR UPDATE` | `ContentService.CommitAsync`; `AuthorityStore.cs` lines 23, 54, 164 | Postings written in that transaction commit or roll back with the head. Commits within an organization are serialized |
| The commit verifies the staged canonical bytes (`VerifyStagedAgainstLeavesAsync`) and decodes the header (`RowCanonicalDecoder.Decode`) before writing the manifest, leaves, head, checkpoint and journal entry | `ContentService.cs` 297–389 | The Authority holds verified bytes for exactly the leaves it accepts: the derivation input |
| Heads change only in `CommitAsync`. Quarantine flags are set in the verification paths (`ContentService.cs` 732, 898). Registration creates the head at revision 0 (`SecurityCommandService.cs` 186) | grep of every write to `CommittedOperationId` and `ContentHeadQuarantined` | Derivation hooks: commit (postings), registration (no posting), quarantine (read by the guard, no posting change) |
| A read-only `RepeatableRead` scope already exists (`BeginReadOnlySnapshotScopeAsync`) | `AuthorityStore.cs` line 30 | The candidate query runs in one consistent snapshot |
| Security commands add and remove `authority_relationship_edges` in the command transaction, and commit-time authorization evaluates the canonical edges in C# (`DesiredStateGraph`, CONS-14) | `ContentService.cs` 274–279; `AuthorityStore.GetActiveEdgesAsync` | Precedent for Tier 2: the Authority already decides from its canonical edges, not only from SpiceDB |
| `canonical_payload_digest` is **unkeyed SHA-256 over the exact canonical bytes** the HMAC authenticates | `trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md` line 57 and the kind-code paragraph after it | A posting carrying its source bytes can be tied to the accepted leaf and root with CPU only, no HSM (§6.3). The digest stays internal: never a search token |
| SpiceDB teams are flat (`team.participant = member`), and `form_record.read = effective_owner` | `authority.schema.zed` | The Tier 2 principal set is {user} ∪ the user's teams |

**To re-verify at IX-1, before any code:**
- every path that changes `record_content_heads`, `authority_resources.resource_lifecycle_state` for records, or AC-6 approval states, by an architecture test that fails when a new writer appears without a derivation hook;
- `ReadCommitted` plus the organization lock gives no anomaly for derivation. All derivation reads are of rows locked or written in the same transaction.

> **Owner review OR-2 (C-123), design defect, fix proposed, not yet accepted.** `organization_authorization_states.content_integrity_revision` is advanced at four sites, not only on commit (`ContentService.cs` 368 commit; 735 and 901 verified-read quarantine; 1235 share-editor `CurrentSnapshotMismatch` incident, with no head change). R1 §C.1 step 5 advances `projection_content_integrity_revision` only on commit. Under I-1's equality rule, one mismatched share-editor request would therefore quarantine the organization's whole index. **Proposed fix:** every increment site advances the projection revision in the same transaction (with no posting change at 735, 901 and 1235), and the IX-1 architecture test is keyed on the increment. Until the owner accepts a fix, this blocks IX-1.
>
> **Owner review OR-3 (C-123), contract clause.** `CommitAsync` commits the transaction on four abort paths before the head changes (lines 248, 260, 292, 321). Derivation and sealing run only between line 326 and the final commit at 387; no abort path writes `authority_search` rows (test IX-61).

---

## 4. Schema (Authority domain, schema `authority_search`)

The tables of R1 §B.2 are kept: `search_projection_states`, `search_field_coverages`, `search_record_entries`, the seven typed posting tables, `search_record_field_anomalies`, `search_form_states`, and `search_result_snapshots` with `search_result_snapshot_entries`.

**Changes in revision 2:**

1. **Every posting stores `source_canonical_bytes bytea`,** the exact verified canonical bytes of its leaf, together with its `manifest_item_key`.
   - The typed value column holds the **exact** value. For strings, `normalized_string_value` is kept *in addition*, for matching.
   - This enables the per-match integrity check (§6.3).
   - Cost: about the canonical item size (≈ 120 to 400 bytes) per posting.
   - It adds no new information: the same plaintext, in a second encoding.
2. **`search_record_entries.entry_seal_signature bytea`** and **`entry_seal_key_version integer`:**
   - an HMAC-SHA-256 over the entry's canonical sorted posting tuples, organization, record, operation and revision, under a distinct domain-separation label (`DynamicForms/SearchEntrySeal/v1`);
   - computed with the organization's existing integrity key through the MAC provider, **one sign operation per commit** (IX-D14);
   - verified by the sweeps, **not per query**.
3. **Tier 2 access postings, `search_access_postings`** (§9.2). Not created before IX-5.

Naming, indexes, partitioning and growth estimates follow R1 §B.3 to §B.5, with two changes:
- the posting-by-record index `(organization_id, form_record_id, field_definition_id)` becomes the **primary probe index** for Tier 1 (§8.2);
- the per-field trigram partial indexes are replaced by one shared GIN (§5.4).

---

## 5. C1: AuthorityDb security

### 5.1 Identities and privileges (current, then proposed)

**Current state at `e830d08`:**
- Cluster bootstrap (`infrastructure/docker/postgres/init-authority-roles.sh`) creates one LOGIN runtime role (`AUTHORITY_RUNTIME_USER`): `NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS`.
- `AuthorityRuntimeGrants.Apply` (run by the migration identity) grants it `SELECT, INSERT, UPDATE` on every model table. No `DELETE` anywhere. No DDL.
- The migration identity is the AuthorityDb administrator (`AUTHORITY_DB_ADMIN_USER`; `ConnectionStrings__AuthorityDbMigration`).
- No RLS in AuthorityDb.

**Pre-existing observation, outside this design's scope:** the tables the code calls "append-only" (`security_journal_entries`, `record_manifest_leaves`, …) also receive `UPDATE`. Append-only is therefore enforced by the absence of `DELETE` and by application code, not by privilege.
- It is recorded as the separate finding **OBS-IX-1** ([finding](../security/OBS-IX-1_AUTHORITY_APPEND_ONLY_UPDATE_PRIVILEGE.md)) and included in the independent review package.
- Nothing in this design depends on changing it.
- No remediation is authorized (C-121).

**Proposed, for `authority_search`:**

| Identity | Kind | `authority_search` privileges | Other | Reads indexed plaintext? |
|---|---|---|---|---|
| `authority_search_owner` | NOLOGIN, owns the schema and tables | owner | none | n/a (no login) |
| AuthorityDb administrator / migration identity (Azure: Entra admin, `azure_pg_admin` member, PIM/JIT only) | LOGIN, privileged | DDL through migrations | everything | **Yes**: privileged, audited (§5.3) |
| Authority runtime role (existing) | LOGIN | `SELECT, INSERT, DELETE` on postings, anomalies, entries and access postings; `SELECT, INSERT, UPDATE` on coverages, states, form states and result snapshots; `DELETE` on expired result snapshots | unchanged | **Yes** (it derives them) |
| **`authority_search_reader`** (new; separate connection string, used only by the query and page operations) | LOGIN | `SELECT` only on `authority_search`; `SELECT` on `authority.record_content_heads`, `authority_resources`, `record_manifest_commits`, `record_manifest_leaves`, `organization_authorization_states`, `field_search_approvals`, `authority_relationship_edges` | `default_transaction_read_only = on`; no parameter logging (§5.3) | **Yes** (it evaluates predicates) |
| Business API runtime, business DBA, business worker | other cluster | **none**: no network path, no credential (G08) | none | **No** |
| `search-index-builder`, `field-protection-verifier` workloads | Authority API clients, **not** database identities | none | none | No (they submit bytes; they receive counts only) |
| SpiceDB datastore role | its own datastore | none | none | No |

**Rules for the new grants:**
- The `DELETE` grant is new for the runtime role and is limited to derived tables. A posting replace is delete-then-insert inside the commit; a withdrawal is a purge.
- `DELETE` is **not** granted on any `authority` table.
- The `AuthorityRuntimeGrants` table list and its unit test are extended. A new `authority_search` table without a grant entry fails the test, exactly as today.
- The reader's result-snapshot and evidence writes happen in a **separate** runtime-role transaction after the read. The read is never a write.

### 5.2 No arbitrary changes, no policy bypass

| Path | Control |
|---|---|
| Business API writes an index entry | No database path. No Authority operation accepts postings, entries or access rows from a caller. Postings arise only from (a) bytes verified in a content commit, or (b) builder-submitted bytes verified against the current head (`HEAD_CHANGED` refused) |
| Business API runs a query outside AC-6 policy | The Authority re-checks `query_fields`, the approval id, operator, value type and coverage `Complete` for every condition. A forged request gets the same indistinguishable refusal as today |
| Builder forges or withholds | Bytes failing the HMAC, leaf-set or root check are refused, and the record is quarantined as in V1. Withholding leaves the coverage `Building`; `Complete` is set only by Authority attestation |
| Authority runtime bug writes a wrong posting | Golden derivation vectors; the per-match integrity check (§6.3) refuses a match whose posting does not hash to the accepted leaf; sweeps |
| AuthorityDb administrator edits rows | Outside the Level 2 threat model (`CLAUDE.md` §4M: the Authority and its administrators are trusted). Still: the per-match check (§6.3) prevents false additions **made by changing postings only**; a coordinated rewrite of postings, leaves and head root passes it and is detected by seals and the open path (correction OR-1, §6.3); seals and sweeps detect **omissions** (§6.4); object-level pgAudit alerts on human access (§5.3) |
| Old Authority version (rollback) commits without deriving | The projection revision stays behind the content revision; the query refuses; the projection goes `Rebuilding` (§12.4) |

### 5.3 Administrative access, backup, retention, replication, logging

| Concern | Requirement |
|---|---|
| Human administrative access | Entra PIM just-in-time for the AuthorityDb administrator, separate from BusinessDb administrators (G08). **pgAudit object audit on `authority_search` for every non-service role**: a `SELECT` by a human session raises an alert. Break-glass is recorded. Customer data in this schema is classified like the Public/Internal business plaintext it copies |
| Backups and PITR | AuthorityDb backups and geo-backups (Canada East, PB-10) now contain Public/Internal plaintext. The backup retention period becomes the **maximum persistence of withdrawn plaintext** (R-IX-3). Restores for testing are done only in approved Canadian environments, by the same restricted identities. The PB-10 register row for AuthorityDb is updated (IX-1 gate) |
| Retention and legal hold | Projection rows are derived and have no retention of their own. On withdrawal or offboarding they are deleted immediately; backups age out on the AuthorityDb schedule. A legal hold applies to the business record and its history, never to the index |
| Replication | HA standby and geo-backups only, in Canada (PB-10). No logical replication or publication of `authority_search`; a CI check refuses one |
| PostgreSQL logging | For `authority_search_reader` and the runtime role: `log_parameter_max_length = 0`, `log_parameter_max_length_on_error = 0`, `log_statement = none`; `auto_explain` disabled or `auto_explain.log_parameter_max_length = 0`; `pgaudit.log_parameter = off` for these roles. In Azure: server parameters plus role settings, verified by IX-28 (log scan) |
| Application logging | No predicate value in Authority logs, metrics, traces or problem details; EF Core sensitive-data logging off; OpenTelemetry `db.statement` without bind values |
| Vacuum and dead tuples | After a withdrawal purge, a forced `VACUUM` of the affected tables. WAL retention follows backup policy |
| Offboarding (PB-13) | Delete by `organization_id` in the disposal transaction; included in the disposal receipt as "derived search projection deleted" |

### 5.4 Index DDL

Partial trigram indexes per approved field would need runtime DDL. **Rejected.** Use one shared GIN `gin_trgm_ops` on `normalized_string_value`, created by migration, and measure its size. All DDL is migration-only, by the JIT migration identity.

---

## 6. C2: integrity semantics (IX-D3)

### 6.1 What changes, and what does not

| Guarantee | V1 (today) | V2 (indexed) | Change |
|---|---|---|---|
| Population comes from the Authority, never from BusinessDb | AC-5 catalogue | Tier 1: the same lookup at the S1 pin. Tier 2: Authority access postings, with every result confirmed by SpiceDB (§9) | Preserved (Tier 1); **changed enumeration mechanism** in Tier 2 (R-B) |
| Every returned match is evaluated on **Authority-accepted** content | HMAC plus leaf set plus root over BusinessDb bytes | Posting bytes → SHA-256 = accepted leaf digest → recomputed root = head root, **per match** (§6.3) | Preserved; mechanism changed (no HMAC and no BusinessDb at search time) |
| A BusinessDb actor cannot suppress or inject a match | The full scan detects | **Structurally impossible**: BusinessDb is not on the path | Strengthened |
| Search detects that BusinessDb bytes of a **matching** record were deleted or altered | Yes: the whole search fails (ENC-SR-08/09) | **No.** Detected when the record is opened (full verified read, quarantine) and by the deep sweep | **Relaxation R-A** |
| Search detects that bytes of a **non-matching** in-scope record were damaged | Yes: the whole search fails | No (same channels as above) | **Relaxation R-A** |
| Opening a record performs the full verified read | Yes | **Yes, unchanged** | Preserved |
| Independent reconciliation and quarantine on tamper | Verification paths and the G19 census | Unchanged, plus the deep sweep re-verifying BusinessDb bytes against heads and quarantining on mismatch (§6.5) | Preserved and extended |
| Snapshot consistency; revocation invalidates; no mixing of revisions | Catalogue snapshot | S1 pin and verify, authorization revision, `form_content_revision`, Authority-held result snapshots | Preserved |
| Whole query fails closed on any inconsistency | Yes | Yes (guard, per-match check, divergence) | Preserved |

### 6.2 Which "current integrity" a result means

**Defined statement** (API scope statement and UI, IX-D3):

> Results reflect the content the Authorization Authority last accepted for each record, at one Authority snapshot. Each returned record's searched values were checked against that accepted content. The business database copy is not checked by search; it is fully verified when you open a record.

The UI must **not** call indexed results "verified" or imply the business copy is intact (§6.6).

### 6.3 Per-query integrity checks (no HSM)

**I-1, scope guard.** Run on the in-scope set in Tier 1 and on the matched set in Tier 2, in the candidate transaction:
- the projection is `Active`;
- `projection_content_integrity_revision` equals the organization content revision;
- every contentful head has exactly one entry with `indexed_operation_id = committed_operation_id`;
- no quarantined head;
- no `EncryptedValuePresent` anomaly on a predicate field;
- coverage `Complete` for every predicate field.

Any failure fails the whole search and sets the projection to `Quarantined` (lock-confirmed, Critical alert).

**I-2, per-match binding.** For every returned match and every predicate-field posting:
1. `SHA-256(source_canonical_bytes)` equals `record_manifest_leaves.canonical_payload_digest` of the leaf with the same `manifest_item_key` in the head's current manifest commit;
2. decoding `source_canonical_bytes` yields the posting's organization, record, field, null group and the exact typed value (and the normalized value recomputes);
3. the record's leaf set recomputes the Merkle root equal to `record_content_heads.committed_manifest_root`, with `manifest_leaf_count` matching.

This is V1's Authority-side root check, run on the matched records. It costs CPU only: for 10,000 matches × 50 leaves, about 500,000 SHA-256 operations. Any mismatch fails the whole search and quarantines the projection.

**Effect:** a returned match is tied to the accepted root **as recorded in AuthorityDb**. No change to postings alone can add a match that the accepted content does not support.

> **Correction (C-123, owner review OR-1).** Revision 3 said the match is "cryptographically tied to the anchored accepted root" and that "no posting can add a match". That overstates I-2. I-2 is unkeyed and compares against `record_manifest_leaves` and `record_content_heads.committed_manifest_root`, which the runtime role can rewrite (OBS-IX-1). The read path binds a head to its journal entry by operation id only, and does not compare roots (`ContentService.cs` 1350–1358). A writer holding the runtime database credential can rewrite posting, leaf digest and head root consistently, and pass I-2.
> - **Impact:** a false match among records the caller is already authorized to discover (Tier 1 population is decided by SpiceDB). It is an integrity failure; no unauthorized record is disclosed.
> - **Detection:** entry seals (keyed; S-B deadline), and the open path (BusinessDb bytes no longer match the head root, so the record is quarantined).
> - **Proposed option, owner decision pending:** verify the entry seal of every record on a returned page before the page is released (at most one MAC verification per displayed record; PERF-01).

### 6.4 Omission and addition: demonstration matrix

| Manipulation | Actor | Addition prevented by | Omission detected by | When |
|---|---|---|---|---|
| Delete, alter or inject business rows | business DBA | n/a: search does not read BusinessDb | n/a: search does not read BusinessDb; the record still matches on accepted content | Structural; damage is caught on open and by the deep sweep |
| Alter a posting value so a non-match matches | Authority domain | **I-2** (bytes, digest, root) | n/a | Every query |
| Rewrite posting, leaf digest and head root consistently (OR-1) | Authority domain with the runtime credential | **Not by I-2.** Entry seal (S-B); per-page seal check if adopted | n/a | S-B deadline (or every page, if adopted) |
| Alter or delete a posting so a match disappears | Authority domain | n/a | **Entry seal** (HMAC) mismatch in the cheap sweep; deep-sweep re-derivation | Sweep interval |
| Delete an entry | Authority domain | n/a | **I-1** (contentful head without entry) | Every query (Tier 1); sweep (Tier 2) |
| Replay old postings or entry | Authority domain | **I-1** (operation binding) and I-2 | I-1 | Every query |
| Restore only `authority_search` from an old backup | operator | I-1, I-2 | I-1 (revision) | Every query |
| Delete or forge Tier 2 access rows | Authority domain | **SpiceDB decision** on every result (fail closed on divergence) | Differential sweep against SpiceDB lookups (§9.4) | Sweep interval |
| Old Authority version commits without derivation | deployment error | I-1 (revision) | I-1 | Every query |

**Conclusion, stated precisely:**
- Against the **Level 2 adversary** (business database administrator or credential), indexed search can neither omit nor add eligible results: structurally, BusinessDb is not consulted.
- Against manipulation **inside the Authority domain** (outside the Level 2 model):
  * additions made by changing postings alone are prevented on every query; a coordinated rewrite including leaves and head root is detected by seals within the S-B deadline (correction OR-1);
  * omissions are detected **within a sweep interval, not on every query**, except for entry deletion in Tier 1.
- V1 has a comparable exposure. An Authority-domain deletion of a registry row silently removes a record from V1's catalogue too.
- Per-query omission detection for value changes would need a per-query verification of all in-scope seals (IX-D14 option B).

### 6.5 Reconciliation and quarantine (preserved and extended)

- **Unchanged:**
  * the open path quarantines on any HMAC, leaf-set or root mismatch;
  * the G19 census and protection reconciliation;
  * S1 quarantine.
- **Cheap sweep** (hourly; Authority worker; no BusinessDb):
  * the I-1 checks for the whole organization;
  * entry seals verified (one MAC verify per entry, rate-limited);
  * the Tier 2 differential (§9.4).
- **Deep sweep** (daily, rate-limited; builder):
  * re-derive postings for a sample, and over each rolling period for every record, from BusinessDb bytes verified against heads;
  * a business-byte mismatch → **record quarantine** (as today);
  * a posting mismatch → **projection quarantine**.
- Every finding is journaled (counts only), shown on the Release Security Dashboard health, and raises a Critical alert.
- Sweep intervals, overdue handling and the resulting detection guarantees: §17.5.

### 6.6 UI and API representation

- `RecordListItemResponse.projectionVerified` is **`false`** for indexed results. It is `true` today for V1 field-search results, where bytes were verified. A new optional response field `resultBasis` = `AuthorityAcceptedContent` | `VerifiedScan` names the engine.
- The scope statement carries the §6.2 text. No "verified" badge or wording for indexed results.
- Opening a record keeps today's verified read and its error states.
- The PB-14 copy review covers the new text in English and French.
- The Playwright and Vitest assertions in IX-4 check that no indexed result is labelled as verified.

### 6.7 ENC-SR-08/09

- `security_hardening/SEARCH_SECURITY_ACCEPTANCE_TESTS.md` is **not changed** by this revision.
- The proposed replacement text is in A2 §4. It takes effect only if and when A2 is accepted.
- Until then, V1 remains the engine those tests describe.

---

## 7. Projection lifecycle

Unchanged from R1 §C, except as noted below.

- **R1 §C.1 Inline derivation.** Revision 2 adds two things:
  * the posting's `source_canonical_bytes`;
  * the entry seal: one MAC sign operation per commit, through the existing provider, in the commit transaction.

  A MAC-provider failure aborts the commit. That is fail closed, and it is the same dependency the commit already has for verification.
- **R1 §C.2 Approval changes** and the same-transaction purge on withdrawal or invalidation.
- **R1 §C.3 Backfill** by `search-index-builder`: bytes only, Authority verification and Authority attestation of `Complete`. Backfilled entries are sealed (one sign per record).
- **R1 §C.4 Ordering:** organization lock plus head CAS; builder idempotent per (record, operation).
- **R1 §C.5 Inconsistency:** the response is now **projection quarantine plus fallback** (§12).
- **R1 §C.6 Sweeps:** extended in §6.5.

---

## 8. Search execution: Tier 1 (exact, initial)

### 8.1 Flow

The flow is R1 §E.1: one Authority call per search.

1. Policy check.
2. S1 pin.
3. Population `D` = `LookupResources(form_record, discover)`, and `O` = `LookupResources(form_record, read)`.
4. Value-independent bulk `field_item#read` checks for share-only records (R1 §E.3).
5. Candidate SQL with **scope-driven probing** (§8.2).
6. I-1 and I-2.
7. First-page edit flags.
8. S1 verify.
9. Result snapshot and evidence in one transaction.
10. Return.

### 8.2 Scope-driven probing (timing without unauthorized dependence)

- The candidate statement drives from the caller's readable-for-predicate scope (R1 §E.3), through a `LATERAL` probe on `(organization_id, form_record_id, field_definition_id)` per scope record.
- The value index is **not** used by the planner: the plan is fixed with an optimization fence (for example `OFFSET 0`) and per-transaction planner settings. An `EXPLAIN` plan-shape test in IX-3 enforces this.
- The work is then **O(|scope| × conditions)**, independent of how many records outside the caller's scope match.
- **This closes R1's timing residual R-IX-1 for Tier 1.** Expected cost: about 50,000 index probes per condition, low hundreds of milliseconds. To be measured.
- Value-index-driven plans (O(organization-wide matches)) are **prohibited** unless separately approved, because their cost depends on unauthorized data.

### 8.3 Limits

Tier 1 keeps:
- the existing 50,000 discover ceiling (**temporary safeguard**, C3);
- the field-item check ceiling on caller-owned quantities;
- a 10,000 result cap on authorized matches;
- `statement_timeout`;
- admission.

Every limit refusal depends only on caller-owned quantities.

### 8.4 Pagination

As R1 §E.5:
- Authority-held result snapshot;
- page validation on authorization revision, `form_content_revision` and projection `Active`;
- fresh edit flags at a new pin.

The complete stored-result specification (C-121 condition 3) is §17.3. It supersedes this summary where they differ.

---

## 9. C3: scaling beyond 50,000 discoverable records (Tier 2)

> **C-121:** the Tier 2 design is approved **for further review only**. Implementation and enablement are not authorized until its authorization-completeness guarantees (§17.1) have been independently reviewed and separately approved. R-B is conditionally accepted on that basis.

### 9.1 Why Tier 1 cannot scale, and the options

Tier 1 asks SpiceDB to enumerate everything the caller can discover, organization-wide, on every search: O(|D|) for the lookup. That is fine at hundreds to tens of thousands, but not at 100K, and not as a product limit.

| Approach | Per-search cost | Leaks via cost or limits? | Verdict |
|---|---|---|---|
| Lookup and probe (Tier 1) | O(\|D\|) in SpiceDB plus SQL | No (scope-driven) | Initial; the oracle for Tier 2 |
| Match-first: organization-wide index hits, then a SpiceDB check on each | O(organization-wide matches) | **Yes**: the cost and the limit reveal unauthorized matches | Rejected |
| SpiceDB-native filtered lookup | Not available in SpiceDB OSS. A commercial materialization would be an external dependency | — | Rejected |
| Authenticated index (Merkle B+-tree range proofs) | O(log n + organization-wide matches) | Yes, for the access-filter step | Rejected: novel construction, unreviewed, and does not compose with access filtering |
| **Access postings** (Authority-maintained, access-scoped, SpiceDB-decided) | **O(caller-authorized matches)** plus small principal expansion | **No**: cost depends only on what the caller may see | **Recommended Tier 2** |

### 9.2 Access postings

`search_access_postings`, one row per (posting, access principal):
- `search_access_posting_id` PK, `organization_id`, `form_definition_id`, `field_definition_id`, `form_record_id`, `manifest_item_id`, `indexed_operation_id`;
- `access_principal_kind_name` (`User` | `Team`), `access_principal_entity_id`;
- `access_path_name` (`Owner` | `ParentOwner` | `Share`), `record_share_id` (null unless `Share`);
- the typed value columns of the posting (nullable; a CHECK that exactly one is set, matching `indexed_value_type_name`).

**Indexes:**
- `(organization_id, access_principal_entity_id, field_definition_id, <value column>, form_record_id)`, partial per value type;
- a GIN (`btree_gin` plus `gin_trgm_ops`) for `contains` on (principal, normalized string);
- `(organization_id, form_record_id)` for maintenance.

**Derivation:** a pure, versioned function `AccessPostingDerivation/v1` mirrors `authority.schema.zed`:

| zed | Access rows |
|---|---|
| `form_record.effective_owner = owner + parent->effective_owner` | `Owner` rows for the owner principal on **all** postings of the record; `ParentOwner` rows for the ancestors' owner principals on descendants (bounded nesting) |
| `field_item.read = parent_record->effective_owner + shared_via->read` and `record_share.read = reader + contributor + manager` | `Share` rows for each recipient principal (user or team) **only on the postings whose `manifest_item_id` the share covers** |
| `team.participant = member` | Principal = the team; **not** expanded per member. Membership changes touch no rows |

It is maintained **in the same transaction** as:
- the security commands that add or remove those edges (registration, ownership transfer, share create, update or revoke, parent link or unlink, unregistration), recomputing the affected records;
- content commits, giving new postings the record's current principals.

Both run under the organization lock, so the rows are always consistent with the canonical edges at the committed authorization revision.

### 9.3 Tier 2 query

1. Policy check and S1 pin, as in Tier 1.
2. Principal set `P` = {caller} ∪ {teams with an active `member` edge to the caller}, read from `authority_relationship_edges` in the candidate transaction (index `ix_authority_edges_subject` exists). The transaction reads `authorization_revision` and requires it to equal the pinned marker revision and `projection_applied_revision`.
3. Candidate SQL: for each condition, access postings with `access_principal_entity_id = ANY(P)` and the value predicate. Records must satisfy all conditions. **Evaluable parity:** for share-only access, no predicate-field posting of the record may lack an access row for `P`. Then join entries for metadata filters and sort. **Cost:** O(caller-accessible postings that match), never organization-wide.
4. **SpiceDB decides every disclosed result.** For all matches up to the result cap:
   - a bulk `form_record#discover` check;
   - a bulk `form_record#read` check;
   - for those not allowed `read`, a bulk `field_item#read` check on every predicate-field posting item;
   - all at the pinned snapshot, with the O-1-hardened client.
   - Any SQL-yes / SpiceDB-no **divergence** fails the whole search (`SEARCH_INDEX_INCONSISTENT`), disables Tier 2 for the organization (falling back to Tier 1) and raises a Critical alert. Divergent results are never silently dropped.
5. I-1 on the matched records, I-2 per match, S1 verify, snapshot and evidence, as in Tier 1.

**Authority remains the sole decision-maker:**
- access postings are Authority-held, derived from the Authority's canonical edges, and used **only to narrow candidates**;
- no result is disclosed without a SpiceDB decision at the S1-verified snapshot;
- the access rows hold no decision a caller can rely on by itself.

### 9.4 The completeness risk of Tier 2, and its controls

**Over-inclusion** (a row that should not exist) cannot leak: SpiceDB rejects it, and the search fails.

**Under-inclusion** (a missing row) would omit an authorized result. It is not caught per query, because Tier 2 does not enumerate. Controls:

1. **Equivalence by construction.** One derivation function per zed relation, unit-tested relation by relation. A **schema coupling guard**: the derivation embeds the SHA-256 of `authority.schema.zed`; a mismatch at startup disables Tier 2 (fallback to Tier 1), and CI fails until the derivation version is bumped and the differential suite passes.
2. **Property-based differential tests** (IX-5): random edge graphs (owners, teams, parents, shares with partial field scopes, revocations, transfers). For every user, the Tier 2 candidate set must equal Tier 1's (SpiceDB lookup) set.
3. **Online differential sampling:** for a configured fraction of Tier 2 searches by callers with |D| ≤ 50,000, run Tier 1 in the background and compare ids and counts. Any difference → Critical alert, Tier 2 disabled for the organization.
4. **Sweep:** periodic per-user comparison of the access-posting principal closure with `LookupResources`, for all users of each organization over a rolling period, and a rebuild of access postings from canonical edges (no HSM, fast).
5. **Fallback:** Tier 2 disabled ⇒ Tier 1 within its ceiling, otherwise `SEARCH_INDEX_NOT_READY`.

**Explicit relaxation R-B** (A2 §3): in Tier 2, completeness of the candidate population relies on derivation equivalence plus differential detection, instead of a per-search SpiceDB enumeration. Disclosure safety does not change.

### 9.5 Ceilings after Tier 2

- The 50,000 discover ceiling no longer applies to field search.
- Remaining bounds:
  * the result cap (10,000 authorized matches, configurable, measured);
  * `statement_timeout`;
  * admission;
  * the SpiceDB decision volume, which is O(results).
- A query whose authorized matches exceed the cap returns `SEARCH_VERIFICATION_LIMIT` (`results`). That depends only on the caller's own authorized matches.

---

## 10. C4: mapping against ADR-PB-004

| ADR-PB-004 requirement | V2 | Status |
|---|---|---|
| §4: catalogue from the Authority, not BusinessDb | Tier 1: SpiceDB lookup at the pin. Tier 2: Authority access postings plus a SpiceDB decision per result | Preserved (T1); **R-B** (T2) |
| §4: apply predicate-field authorization before execution | AC-6 re-check plus item-level readability (`field_item#read`; value-independent in T1, per result in T2) before any result | Preserved |
| §4: "retrieve and verify every eligible current record/field value … complete only after exhaustive verification at a coherent snapshot" | Verification moved to commit (the same HMAC, leaf-set and root checks). Per query: I-1 over scope (T1) or matches (T2), I-2 per match. BusinessDb bytes not verified at search time | **R-A** (relaxation) |
| §4: "a BusinessDb index can accelerate fetching candidate bytes but cannot decide or prove the full candidate set" | No BusinessDb index; the Authority index decides | Preserved (stronger) |
| §4: fail closed if snapshot proof, enumeration or verification fails; no truncated list | I-1, I-2, divergence, S1 verify, limits: all whole-query failures, no partial results | Preserved |
| §5: bounded; typed refusal before exceeding a limit | Bounded by result cap, timeout and admission; Tier 1 discover ceiling temporary | Preserved; **ceiling semantics changed** (R-C) |
| §5: narrowing by an untrusted BusinessDb predicate does not establish completeness | No BusinessDb predicates | Preserved |
| §5: pagination is a snapshot-consistent partition of one verified result set | Authority-held result snapshot, validated per page | Preserved |
| §5: revocation invalidates pages and cursors; no combining of revisions | Authorization revision and `form_content_revision` checks per page | Preserved |
| §6: Confidential predicates denied server-side; no leak through timing, errors, counts, sorts, facets | Server-side refusal unchanged; counts only over authorized matches; no facets or value sort (D6); T1 scope-driven and T2 access-scoped costs depend only on caller-visible data | Preserved (R-IX-1 closed for T1; T2 timing is caller-scoped) |
| §2: only explicitly approved Public/Internal; no Confidential/Restricted search | Postings only for Active approvals, classification CHECK, encrypted leaves never indexed | Preserved |
| §3: residual exposure to BusinessDb readers | Unchanged; **plus AuthorityDb readers** (§5.1) | **R-D** (extended exposure) |
| §7: a V2 trusted index needs a separate ADR | A2 | Addressed by A2 |
| API: data returned only when every item is authorized and integrity-verified | Authorized (SpiceDB) and integrity-bound to the accepted root (I-2); BusinessDb copy not verified | **R-A** (wording in A2) |
| API: versioned contract, typed error codes, opaque cursor, audit without values | Unchanged codes, plus `SEARCH_INDEX_NOT_READY` and `SEARCH_INDEX_INCONSISTENT` as `unavailable_reason` values; evidence in the result transaction | Preserved |
| Versioning: strengthening invalidates search projections and purges plaintext replicas | Same-transaction purge on withdrawal or invalidation; backups age out (R-IX-3) | Preserved, with residual R-IX-3 |

**Relaxations, all explicit:**
- **R-A:** search-time verification of BusinessDb bytes;
- **R-B:** Tier 2 enumeration mechanism;
- **R-C:** ceiling semantics;
- **R-D:** AuthorityDb plaintext exposure.

Each is in A2 §3 for acceptance or rejection.

---

## 11. Revised threat and security-boundary analysis

### 11.1 Boundaries

| Zone | Trust | Holds | Search role |
|---|---|---|---|
| Browser | untrusted | nothing sensitive at rest (PB-07) | request |
| Business API and worker | untrusted for decisions (Level 2) | session, admission | forwards; never decides |
| BusinessDb | **hostile in the Level 2 model** | record bytes | **not on the search path** |
| Authority runtime | trusted | keys (via provider), decision logic | decides and derives |
| AuthorityDb (`authority`, `authority_search`) | trusted; administrators trusted but audited | heads, leaves, journal, edges, approvals, **projection with Public/Internal plaintext** | index and evaluation |
| SpiceDB | trusted under S1 (rollback detected) | relationship projection | decisions |
| MAC provider (Vault locally, Managed HSM in production) | trusted | integrity keys | commit and seal only; **none at query** |
| `search-index-builder` | narrow workload | credential | submits bytes |

### 11.2 Threat table (R1 IX-T01–T18, revised)

| ID | Threat | Control (revision 2 changes in bold) | Tests |
|---|---|---|---|
| T01 | Cross-organization leakage | Organization from the DUA binding; leading key; **FORCE RLS on `authority_search` for the reader role**; snapshot binding | IX-01, IX-02 |
| T02 | Confidential exposure | Active approval with Authority-registered classification Public/Internal and Standard protection; CHECK; encrypted leaves produce an anomaly, never a posting; G21-safe input | IX-03–05 |
| T03 | Stale values after a classification change | Same-transaction purge; query requires Active and `Complete`; **forced VACUUM**; backups age out (R-IX-3) | IX-06, IX-07 |
| T04 | Existence disclosure through matches, counts, sort or paging | T1: caller's own sets at the pin, value-independent readability. **T2: access-scoped candidates plus a SpiceDB decision per result.** Counts only after decisions; limits on caller-owned quantities | IX-08–12, **IX-33** |
| T05 | Timing side channel | **T1: scope-driven plan, independent of unauthorized data (§8.2). T2: cost O(caller-accessible matches).** Value-driven plans prohibited | IX-13 |
| T06 | Business DBA tampering with the index | No path (G08) | IX-14, IX-15 |
| T07 | Authority-domain tampering or partial restore | I-1 per query; **I-2 per match**; **entry seals**; sweeps; projection quarantine; **pgAudit on human access** | IX-16, IX-17, **IX-34** |
| T08 | Replay or rollback | Operation binding (I-1); I-2; G14 for AuthorityDb; **projection revision fence for old Authority versions** | IX-18, IX-19, **IX-35** |
| T09 | Authorization change during a search | S1 pin and verify; revision checks; paging validation; **T2: candidate transaction revision = pinned marker = projection revision** | IX-20, IX-21 |
| T10 | Content change during a search | REPEATABLE READ; `form_content_revision` | IX-22 |
| T11 | Quarantined organization or record | Gate refusal; whole-search failure; projection quarantine scoped to search | IX-23, IX-24 |
| T12 | Compromised builder | Bytes only, verified; attestation by the Authority | IX-25, IX-26 |
| T13 | Builder or HSM unavailable | Coverage `Building`; resumable; fallback rules | IX-27 |
| T14 | Predicate values in logs | **Role-level PostgreSQL parameter-logging settings, pgAudit settings, application logging rules (§5.3)** | IX-28 |
| T15 | Expensive queries | Limits, timeouts, admission | IX-29 |
| T16 | Forged predicate | Authority re-check | IX-30 |
| T17 | Partial-share inference | Item-level Evaluable parity (T1 and T2) | IX-09, IX-10, **IX-36** |
| T18 | Indexed result taken as proof of business integrity | **§6.6 wording and contract (`projectionVerified=false`, `resultBasis`)**; verified open | IX-31, IX-32, **IX-37** |
| **T19** | **Tier 2 evaluator divergence** (missing or extra access rows) | Schema coupling guard, property-based differential, online sampling, sweep, SpiceDB decision per result, fail closed on divergence | **IX-38–41** |
| **T20** | **Runtime role abuse of the new `DELETE` grant** | `DELETE` only on derived `authority_search` tables; deleted entries detected by I-1 and seals; grants test | **IX-42** |

### 11.3 Residual risks

| ID | Risk | Status |
|---|---|---|
| R-IX-1 | Timing residual | **Closed for T1** (scope-driven); T2 cost is caller-scoped; any remaining residual measured in IX-13 |
| R-IX-2 | AuthorityDb readers see approved plaintext (administrators, runtime, reader role, backup restores) | Accept or reject: IX-D2 / R-D |
| R-IX-3 | Withdrawn plaintext persists in WAL and backups until expiry | Documented; forced VACUUM |
| R-IX-4 | BusinessDb tampering not detected by search | Accept or reject: IX-D3 / R-A |
| R-IX-5 | 50,000 discover ceiling | **Temporary (T1 only)**; removed by T2 |
| R-IX-6 | Authority-domain omission detected by sweep, not per query | Accept, or choose IX-D14 option B |
| R-IX-7 | T2 under-inclusion between sweeps if the evaluator diverges | Accept or reject: IX-D13 / R-B |
| From C7, unchanged | Delivered matches have been seen; the Authority runtime is trusted | — |

---

## 12. Recovery, rebuild and rollback

### 12.1 Sources of truth

Every `authority_search` row is **derived**:

| Rows | Rebuilt from | HSM needed |
|---|---|---|
| Postings and entries | BusinessDb bytes verified against the accepted heads | Yes: 8 MAC verifications per record (benchmark form) plus 1 seal sign per record |
| Access postings | Canonical `authority_relationship_edges` plus the postings | No |
| Coverages | Approvals plus attestation | No |
| Result snapshots | Disposable | No |

Losing the schema loses **search availability**, never data or authorization state.

### 12.2 Scenarios

| Scenario | Detection | Procedure | Search during recovery |
|---|---|---|---|
| Partial loss or corruption of `authority_search` | I-1, I-2 or sweep | Projection `Quarantined` → operator `SearchProjectionRebuild` command (journaled, security administrator, step-up when PB-08 exists) → `Rebuilding`: delete the organization's projection; coverages `Building`; access postings rebuilt from edges (fast); builder rounds; Authority attestation per coverage → `Active` | V1 fallback within V1 limits, else `SEARCH_INDEX_NOT_READY` |
| AuthorityDb PITR or restore, then G14 journal replay | Replay advances heads whose postings were not restored → I-1 | As above, automatically entering `Rebuilding` after recovery cutover. The recovery runbook gains a "search projection rebuilt" step **before** search is re-enabled | Same |
| BusinessDb restore (T16-B class) | Opens fail and quarantine records; deep sweep | Index unaffected and remains correct for accepted content; record quarantine as today | Indexed search continues; quarantined records fail any search whose scope contains them (D3 parity) |
| SpiceDB restore or rollback (T16-S class) | S1 quarantines the organization | G14 procedures; access postings unaffected (derived from AuthorityDb edges) | Organization gate closed → refused |
| Derivation defect found (wrong posting semantics) | Golden vectors, differential, sweep | New derivation version `v2`; full re-derivation through the builder; old postings never re-signed or "patched" | Fallback |
| Access-derivation defect or zed schema change | Coupling guard or differential | Tier 2 disabled; rebuild access postings with the new derivation version; differential suite | Tier 1 |
| Approval withdrawn | — | Same-transaction purge, forced VACUUM | Field no longer offered |
| Organization offboarded | — | Delete in the disposal transaction | — |

### 12.3 Feature rollback

- **Flags** (R1 §G.1, plus `SearchIndex:Tier2Enabled`):
  * engine back to `VerifiedScan` → V1 path unchanged; V1 code retained until V2 accepted;
  * `QueryEnabled=false` → no indexed query;
  * `DerivationEnabled=false` → derivation stops and the projection is marked `Rebuilding` at the next commit, so stale data can never be queried later.
- **Schema:** additive (expand only). Dropping `authority_search` loses only derived data.
- **Contract:** `resultBasis` is optional; old clients ignore it.

### 12.4 Authority version rollback (minimum safe version)

- An Authority older than IX-1 commits content without deriving postings. `projection_content_integrity_revision` then lags. The first query refuses, and the projection goes `Rebuilding` when the current version starts again.
- The A2 operations note sets IX-1 as the **minimum Authority version** once `DerivationEnabled` has ever been on, as I3 does for S1. Re-enabling after a rollback always rebuilds.
- An optional hard fence (a `CHECK` that an older binary violates, as S1's `ck_legacy_projection_token_retired`) is evaluated at IX-1.

---

## 13. Performance (to be measured)

The R1 §F benchmark matrix applies, with these additions:
- **Tier 2:** organizations of 10K, 100K and 250K records; callers with |D| of 1K, 50K, 100K and 250K; result sizes 10, 1K and 10K.
- **I-2 cost** per match.
- **Access-posting maintenance cost** per security command (share of 1 to 250 fields; ownership transfer of a record with 250 fields; reparent with nesting).
- **Seal sign** added to commit p95.
- **Differential sampling overhead.**

**Expected per-search HSM operations:** 0. **Commit:** plus 1 MAC sign.

**Targets** (PB-09, unverified):
- verified search p95 < 3 s;
- write p95 < 2 s, with derivation plus seal ≤ +20 ms p95 (proposed, revised from +15 ms for the seal).

PERF-01 on Azure staging decides.

## 14. Alternatives

R1 §H is kept. Revision 2 adds:
- match-first checking (rejected, §9.1);
- authenticated index structures (rejected, §9.1);
- value-index-driven plans (prohibited without separate approval, §8.2);
- per-query seal verification of the whole scope (IX-D14 option B);
- a local, HSM-wrapped index sealing key, which would make per-query seal checks cheap (rejected for now: it introduces a new exportable key class).

---

## 15. Decision matrix

### 15.1 Owner decisions (C-121, 2026-11-06)

| # | Owner decision | Attached condition (binding) | Where answered |
|---|---|---|---|
| IX-D1 | **Approved** | — | — |
| IX-D2 | **Approved**, subject to the AuthorityDb plaintext-access restrictions | Strict separation of runtime, search-reader, administrator and business identities; exposure through backups, replication, diagnostics and administration documented | §5, §17.4 |
| IX-D3 | **Approved** under R-A and Amendment A2 | Full verified reads on open and independent reconciliation stay mandatory | §6 |
| IX-D4 | **Approved** as the initial Tier 1 approach | — | §8 |
| IX-D5 | **Approved** with bounded retention and authorization revalidation | Stored-result specification | §17.3 |
| IX-D6 | **Approved** | — | — |
| IX-D7 | **Approved** with strict separation between search-index quarantine and organization authorization quarantine | S1 organization quarantine always retained; fallback never bypasses it | §17.2 |
| IX-D8 | **Approved** only where the fallback preserves existing security guarantees and stays within documented limits | Fallback goes through every V1 and S1 gate | §17.2 |
| IX-D9 | **Approved** as the architectural decision record, subject to review conditions | — | A2 |
| IX-D10 | **Approved** | — | — |
| IX-D11 | **Confirmed** | — | — |
| IX-D12 | **Deferred** | — | — |
| IX-D13 | Tier 2 design **approved for further review only**; implementation not authorized | Detection of missing authorized records, not only unauthorized additions; independent review and separate approval before implementation or enablement | §17.1 |
| IX-D14 | **Approved**, subject to documented sweep intervals, failure handling and integrity guarantees | — | §17.5 |

**Relaxations:**
- R-A: accepted.
- R-B: conditionally accepted (independent assessment of Tier 2 completeness and SpiceDB equivalence before implementation).
- R-C: accepted for the Tier 2 design; caps, timeouts and admission must not disclose unauthorized information.
- R-D: accepted, for explicitly approved unencrypted Public/Internal values only.

### 15.2 Decision texts as proposed in revision 2

The owner's decisions in §15.1 apply to these texts. "Recommend" is Claude's revision 2 recommendation.

| # | Exact decision text | Recommend |
|---|---|---|
| **IX-D1** | *Placement.* The trusted search index is a set of tables in schema `authority_search` of the independently administered AuthorityDb. It is written only by the Authority runtime: (a) within the content-commit transaction that accepts the record's content, from the canonical bytes verified in that transaction, and (b) by Authority verification of builder-submitted bytes against the current head. No BusinessDb table, external engine or separate search service is part of the trusted index. | **Approve** |
| **IX-D2** | *Plaintext in AuthorityDb and privileges.* The owner accepts that AuthorityDb and its backups hold explicitly approved Public/Internal plaintext (R-D), readable by the AuthorityDb administrator (just-in-time, audited), the Authority runtime role and a new SELECT-only `authority_search_reader` role, and by no business identity. The privilege, logging, backup, retention and replication rules of §5 are binding, including the PB-10 register update before IX-1 completes. | **Approve** |
| **IX-D3** | *Integrity semantics.* Indexed search evaluates the content the Authority last accepted (R-A). Per query, the Authority enforces I-1 (scope guard) and I-2 (every returned match bound by SHA-256 to the accepted leaf and by Merkle root to the accepted head). Opening a record keeps the full verified read. Tampering with business bytes is detected at open and by the reconciliation sweeps, with record quarantine as today. Indexed results are never presented as verification of the business copy (§6.6). ENC-SR-08/09 are replaced by the A2 §4 text only when A2 is accepted. | **Approve, with A2** |
| **IX-D4** | *Tier 1 permission strategy.* For field search, the population is SpiceDB `discover` and `read` at the S1-pinned snapshot. Field readability for share-only records is checked for all of the caller's records independently of predicate values. Candidate SQL is scope-driven, and value-index-driven plans are prohibited. Every limit refusal depends only on caller-owned quantities. The 50,000 discover ceiling is a temporary safeguard. | **Approve** |
| **IX-D5** | *Pagination.* Results are held by the Authority as result snapshots (record ids and sort keys, capped at 10,000 authorized matches, expiring with the catalogue snapshot). Every page re-validates the authorization revision, `form_content_revision` and projection state, and computes edit flags at a fresh pinned snapshot. The page-invalidation rate is measured before production. | **Approve** |
| **IX-D6** | *Coverage and backfill.* A new approval starts coverage `Building`. A separate `search-index-builder` workload identity (OD-1 pattern) submits BusinessDb bytes only. The Authority verifies them against the current head and derives postings. Only the Authority's attestation, under the organization lock, sets `Complete`. Withdrawal or invalidation purges the field's postings in the same transaction. | **Approve** |
| **IX-D7** | *Failure handling.* A derivation or seal failure aborts the content commit. Any I-1, I-2, seal or divergence failure fails the whole search, quarantines **the search projection only** (not the organization gate) with a Critical alert, and is cleared only by a journaled `SearchProjectionRebuild` and a completed rebuild. | **Approve** |
| **IX-D8** | *Fallback.* When the index cannot answer (coverage `Building`, projection `Rebuilding` or `Quarantined`, Tier 2 disabled), V1 runs only if the scope is within V1's limits, and the response states the engine; otherwise `SEARCH_INDEX_NOT_READY`. One search never combines engines. | **Approve** |
| **IX-D9** | *ADR vehicle.* The decisions are recorded as ADR-PB-004 Amendment A2, which lists every preserved guarantee and the relaxations R-A to R-D for explicit acceptance. | **Approve** |
| **IX-D10** | *O-1 and O-2.* O-1's bulk-client hardening (echoed-request mapping; a per-item error fails the call) is implemented with IX-2 and used by V2. O-2's set-based reader is implemented with IX-2 for the builder and V1. The O-1 restructuring of `VerifyProjectionsAsync` and O-3/O-4 stay deferred. The P-S1-1 benchmark stays an open, known failure until V1 is fixed or retired. | **Approve** |
| **IX-D11** | *Exclusions.* V2 provides no suggestions, autocomplete, facets, aggregates, field-value sort, repeating-group, child-record or document search, and never indexes encrypted values, ciphertext-derived tokens, blind indexes or document content. Each would need its own decision. | **Confirm** |
| **IX-D12** | *My Records.* Moving My Records metadata listing to verified index entries (and to Tier 2 entry access) is a separate stage (IX-6) needing its own approval after IX-4. Until then My Records is unchanged. | **Defer** |
| **IX-D13** (new, C3) | *Tier 2.* For scale beyond Tier 1, the Authority maintains access postings derived from its canonical edges in the same transaction as the commands that change them. They are used only to narrow candidates. Every disclosed result is decided by SpiceDB at the S1-verified snapshot, and a divergence fails the search and disables Tier 2 for the organization. Under-inclusion is controlled by the schema coupling guard, property-based differential tests, online differential sampling and sweeps (R-B). Tier 2 is implemented in IX-5, enabled only after the differential exit criterion, and Tier 1 remains its oracle. | **Approve the design**; implementation per the IX-5 gate |
| **IX-D14** (new, C2) | *Entry seals.* Each entry carries an HMAC seal made with the organization's existing integrity key, at one MAC sign per commit, verified by the hourly sweep (option A). Option B, verifying all in-scope seals per query, is not adopted because it reintroduces per-search HSM load. Authority-domain omission is therefore detected within the sweep interval (R-IX-6). | **Approve option A** |

---

## 16. Implementation stages and acceptance gates (each stage needs its own authorization)

**No stage IX-1 to IX-7 is authorized (C-121, C-122).** IX-0 is in progress: owner decisions IX-D1 to IX-D14 (C-121) and FD-1 to FD-8 (C-122) are recorded, and the independent design review package is ready but not yet submitted (FD-8). C-123: the owner-led Tier 1 review is in progress; independent third-party assessment remains an outstanding production-readiness requirement. Tier 2 work (IX-5) additionally needs the separate approval required by §17.1.

| Stage | Content | Exit gate |
|---|---|---|
| **IX-0** | Owner decisions IX-D1–D14; A2 accepted; independent **design** review (package as for S1) | Owner approval; reviewer findings dispositioned |
| **IX-1** | Schema and migrations; roles, grants, RLS, logging settings; `SearchPostingDerivation/v1` (golden vectors); inline derivation **dark**; entry seals; coverage lifecycle and purge; cheap sweep; architecture test on head writers; PB-10 register update | IX-01–07, 14–17, 28, 34, 35, 42; commit p95 delta ≤ 20 ms (local, then staging); privilege inventory passes |
| **IX-2** | O-1 client hardening; O-2 reader; `search-index-builder` and attestation; deep sweep; `SearchProjectionRebuild` | IX-18, 19, 25–27; CT-7; backfill throughput recorded |
| **IX-3** | Tier 1 `indexed-query` and `page`; I-1, I-2; result snapshots; plan-shape test; business `Shadow` engine | Zero unexplained shadow differences across the Stage B search shards plus N synthetic searches; IX-08–13, 20–24, 33, 36 |
| **IX-4** | `Indexed` engine for Advanced Field Search (Development/Testing); fallback; UI wording (§6.6) and contract field; Stage B; local benchmarks to 50K | All IX tests through 37; owner functional and **copy** acceptance; P-S1-1 disposition recorded |
| **IX-5** | Tier 2 access postings (dark → differential → enabled); coupling guard | IX-38–41; property-based differential with 0 differences; online sampling clean over the agreed period; 100K/250K benchmarks |
| **IX-6** (optional) | My Records on entries / entry access | Separate approval (IX-D12) |
| **IX-7** | Azure staging PERF-01 (Managed HSM backfill and seal throughput, SpiceDB at scale), DR rehearsal including the projection rebuild after G14 recovery, independent **implementation** review | PB-12 evidence; production gate |

**New test ids** (revision 2):
- IX-33: Tier 2 counts and limits independent of unauthorized data;
- IX-34: Authority-domain posting alteration (I-2 and seal);
- IX-35: old-Authority commit fence;
- IX-36: multi-select partial share, Evaluable parity;
- IX-37: UI and contract do not represent indexed results as verified;
- IX-38–41: Tier 2 divergence, coupling guard, differential, sweep;
- IX-42: the runtime `DELETE` grant is confined to derived tables.

R1 §K's acceptance criteria 1–10 remain, extended to the new tests (and to IX-43 to IX-52 of §17.8 and IX-53 to IX-56 of §17.9.3).

**Unchanged:** M4-1 stays open pending independent review; M4-2 unauthorized; no push, PR, merge or deploy.

---

## 17. C-121 mandatory security conditions

### 17.1 Condition 1: Tier 2 completeness (detecting missing authorized records)

> **Status:** design for independent review only. Tier 2 is **not** to be implemented or enabled until this section has been independently reviewed and separately approved (C-121).

**Definition.** An authorized record is *missing* when SpiceDB, at the search's snapshot, would allow the caller `discover` on the record and `read` on every predicate-field item, and the record satisfies the predicates, but Tier 2 does not return it because an access row is absent.

- SpiceDB decides every Tier 2 result (§9.3), so **additions** cannot leak.
- **Missing records** are what this section addresses.

**M1, verified-revision fence (new in revision 3).** The core mechanism.

1. Per organization, `search_access_verification_states` holds:
   - `access_derived_revision`: updated inline with every derivation;
   - `access_verified_revision`.
2. Every security command that changes edges, and every content commit that changes postings of approved fields, records its **affected record set** in `search_access_pending_verifications`, in the same transaction. Each row holds the record id and the authorization revision. Affected sets are computed **conservatively**, as a superset:

   | Command | Affected records |
   |---|---|
   | Share change | the share's record |
   | Ownership transfer | the record and its descendants |
   | Parent link or unlink | the whole subtree |
   | Team removal | every record with a row for that team |
   | Team membership change | none (rows hold the team, not its members) |

3. An Authority verification worker processes pending records once the SpiceDB projection has reached their revision, at an S1-pinned snapshot. For each record it calls:
   - `LookupSubjects(form_record, discover)` for subject types `user` and `team#participant`;
   - `LookupSubjects(field_item, read)` for every approved-field posting item.

   It compares the results with the record's access rows. Team rows are compared at team level; user-level results are compared after expanding team rows through the canonical member edges.
4. **Equal:** the pending rows are removed. `access_verified_revision` advances to the highest revision with no pending record at or below it.
5. **Not equal:**
   - first, run the S1 decisive verification (§17.2 Q4);
   - if S1 is consistent: disable Tier 2 for the organization, rebuild its access postings from the canonical edges and verify them again;
   - a persisting difference is a derivation defect: Tier 2 stays disabled, a Critical alert is raised and a finding is recorded.
6. **Query precondition:** a Tier 2 search runs only if `access_verified_revision = authorization_revision` at the pinned snapshot. Otherwise Tier 2 is not used for that search (fallback, §17.2), or the result is `SEARCH_INDEX_NOT_READY`.

**Result of M1.** A Tier 2 result is never computed from access rows that have not been compared with SpiceDB for every record whose edges or postings changed since the last verification. Missing rows caused by a **derivation defect on a changed record** are therefore detected **before** any search can use them.

**M2, full rolling equivalence sweep.**
- Every record is verified as in M1 at least once per **7 days** (proposed), whether or not it changed.
- In addition, a user-oriented differential runs: `LookupResources(form_record, discover)` for a rotating set of users, compared with the access-posting principal closure.
- M2 bounds the remaining gaps:
  * an incorrect affected-set computation (a changed record that M1 was never told about);
  * Authority-domain deletion of access rows after verification.

**M3, affected-set correctness tests.** Property-based: for random command sequences, recomputing every record must give exactly the rows produced by recomputing only the declared affected set.

**M4–M6** (from §9.4): property-based differential against SpiceDB; online sampling against Tier 1 for callers with |D| ≤ 50,000; the schema coupling guard.

**Residual after M1–M6** (for the reviewer): missing rows caused by
- (a) an affected-set defect not caught by M3, or
- (b) Authority-domain row deletion after verification

are detected by M2 within 7 days, not per query. A divergence in which both SpiceDB and the derivation are wrong in the same way is not a completeness failure under the design's definition: SpiceDB is the decision engine.

**Cost (unmeasured):** `LookupSubjects` calls are proportional to changed records times approved-field items. A record with 250 approved fields changed by a share means about 250 field-item lookups. This is a PERF-01 item and a review question (package Q5).

### 17.2 Condition 2: search-index quarantine versus S1 organization quarantine

| State | Scope | Indexed search | V1 fallback | Page retrieval | Record read and write | Set by | Cleared by |
|---|---|---|---|---|---|---|---|
| **S1 organization quarantine** (`IntegrityQuarantineEnabled`, gate `Quarantined`) | Organization | **Refused** 423 `INTEGRITY_QUARANTINE` | **Refused** (the same gate) | **Refused** | Refused (existing S1) | S1 only | G14 recovery only (S1 rules, permanence trigger). **Never** by any search action |
| Organization gate closed (projection behind, archive pending) | Organization | Refused, retryable (as today) | Refused (the same gate) | Refused, retryable | As today | Existing | Existing |
| **Search-index quarantine** (`search_projection_states.projection_status_name = Quarantined`) | The organization's search index | Not served (503 `SEARCH_VERIFICATION_UNAVAILABLE`, reason `SEARCH_INDEX_INCONSISTENT`) | Allowed only through the complete V1 path and within V1 limits (IX-D8) | Refused; the snapshot is deleted | **Unaffected** | Search checks and sweeps | Journaled `SearchProjectionRebuild` and a completed rebuild |
| **Search-index suspension** (`projection_status_name = Suspended`, reason `INTEGRITY_SWEEP_OVERDUE`; C-122 FD-1, §17.9) | The organization's search index | Not served (503 `SEARCH_VERIFICATION_UNAVAILABLE`, reason `SEARCH_INDEX_VERIFICATION_OVERDUE`) | As for quarantine (IX-D8) | Refused; the snapshot is deleted | **Unaffected** | Overdue evaluation only | Successful completion of every overdue verification scope (§17.9). A sweep **finding** during that completion converts it to a quarantine |
| Record content quarantine | Record | Any search with the record in scope fails (D3 parity) | Same | Same | Record refused (existing) | Verification paths | Existing |

**Rules:**
- **Q1, evaluation order** on every search and every page:
  1. organization gate and S1 state;
  2. search projection state;
  3. coverage and approvals;
  4. query.

  A closed or quarantined organization never reaches step 2.
- **Q2, fallback never bypasses S1.** The V1 fallback is the complete, unmodified V1 path: catalogue under S1 pin and verify, AC-5 and per-record verification. It re-enters step 1 itself. No shortcut, cached decision or stored result is reused across engines.
- **Q3, independence.**
  * A search-index quarantine never clears, suppresses, delays or changes the reason of an S1 quarantine, and an S1 quarantine never clears a search-index quarantine.
  * They live in separate tables, are set and cleared by separate code paths, and raise separate alerts.
  * `SearchProjectionRebuild` is refused while the organization is S1-quarantined.
- **Q4, escalation.** A search-side failure that could indicate authorization-projection damage runs the **S1 decisive verification** under the organization lock before it is classified. Such failures are:
  * a Tier 2 divergence;
  * a SpiceDB decision that contradicts an access row;
  * an S1 verify mismatch during a search or a sweep.

  If S1 finds a mismatch, the **existing S1 organization quarantine** applies (same reason codes, journal event and Critical alert). Only when S1 state is consistent is the failure treated as a search-index failure.
- **Q5, distinct signals.**
  * Organization quarantine: 423 `INTEGRITY_QUARANTINE`, alert `PROJECTION_INTEGRITY_QUARANTINE` (event 9100).
  * Search-index quarantine: 503 with reason `SEARCH_INDEX_INCONSISTENT`, a separate alert id (`SEARCH_INDEX_QUARANTINE`, event id assigned at IX-1), **never 423**.

### 17.3 Condition 3: stored search results

**What is stored.**
- **Snapshot row:** organization, Authority user, session reference, form, engine and tier, authorization revision, content-integrity revision, `form_content_revision`, the approval id per predicate field, created and expiry times, entry count.
- **Query digest:** SHA-256 over a **per-snapshot random nonce** plus the request. The nonce is held only inside the business API's encrypted cursor, so a database reader cannot brute-force low-entropy predicate values from the digest.
- **Entries:** position, record id and sort keys.
- **No values and no predicate values are stored.**

**Expiry and cleanup.**

| Rule | Value (proposed; FD-2) |
|---|---|
| Lifetime | Default 5 minutes, maximum 15, absolute (paging does not extend it) |
| Purge job | Every minute, bounded batches: expired and invalidated snapshots and their entries |
| On access | An expired snapshot is refused and deleted |
| On invalidation (any failed page check below) | Deleted immediately |
| Session end (logout, revocation, tenant switch) | All of that session's snapshots deleted. The Authority holds sessions (`authenticated_sessions`), so the deletion is in the revoking transaction |
| Organization quarantine or offboarding | Not served; purged |

> **Correction (C-123, owner review OR-4).** R1 §B.2 says these snapshots are purged "like `search_catalogue_snapshots`". No catalogue-snapshot purge exists today: there is no delete code in the Authority and no `DELETE` grant. Catalogue snapshots are bound to the user but not to the session (`SearchCatalogueSnapshot.cs` 19). The purge job, the session binding and the `DELETE` grant on result-snapshot tables are **new work** for IX-3.

**Storage and resource limits.**

| Limit | Value (proposed; FD-2) | Refusal |
|---|---|---|
| Entries per snapshot | 10,000 (= result cap) | `SEARCH_VERIFICATION_LIMIT` (`results`): depends only on the caller's authorized matches |
| Live snapshots per user session | 3; a fourth deletes the oldest | none (eviction) |
| Live entries per Authority instance | 500,000 | `SEARCH_CAPACITY_EXCEEDED` (retryable), identical to today's admission refusal. It reveals aggregate load only, never record data; noted as residual |
| Footprint | about 60 bytes per entry plus index: about 50 MB at the instance limit | — |

**Page retrieval: reauthorization on every page.** Every step must succeed. Otherwise no page is returned, the snapshot is deleted unless the failure is retryable, and a typed error is returned. **There is never a partial or stale page.**

1. The business API decrypts and binds the cursor (user, session, organization, nonce, offset, expiry), as today.
2. The snapshot exists, is not expired, and belongs to the same Authority user, session and organization.
3. The organization gate is open and S1 is not quarantined (§17.2 Q1). S1 pin.
4. The authorization revision equals the snapshot's (OD-5: any security change invalidates).
5. **Searchability revalidation:**
   - every recorded approval id is still Active;
   - each operator is still approved;
   - coverage is `Complete`;
   - `query_fields` is still allowed for the caller at the pinned snapshot.

   This is checked explicitly, so the outcome does not depend on which revision counter an AC-6 command advances.
6. The projection is `Active`; its revision equals the content-integrity revision; `form_content_revision` is unchanged.
7. **The page's records are decided again by SpiceDB** at the pinned snapshot: `discover`, and `read` on the record or `field_item#read` on every predicate-field item. A denial invalidates the snapshot. Items are never dropped silently.
8. I-2 on the page's records.
9. Edit flags, then S1 verify, then the page is returned.

**Behaviour after a change.**

| Event between pages | Response | Snapshot |
|---|---|---|
| Permission revocation (any authorization change) | 409 `SEARCH_SNAPSHOT_CHANGED` | Deleted |
| Approval withdrawn, classification strengthened, operator narrowed | 409 `SEARCH_SNAPSHOT_CHANGED` (not "field not allowed", so the change is not revealed beyond "restart") | Deleted |
| Content change in the form | 409 `SEARCH_SNAPSHOT_CHANGED` | Deleted |
| S1 organization quarantine | 423 `INTEGRITY_QUARANTINE` | Purged |
| Organization gate closed (not ready) | 503, retryable | Kept until expiry |
| Search-index quarantine | 503 `SEARCH_INDEX_INCONSISTENT` | Deleted |
| A page record quarantined | 503 `RECORD_QUARANTINED` | Deleted |
| Session revoked or logged out | 401 | Deleted |
| Authority or SpiceDB unavailable | 503, retryable | Kept until expiry |

### 17.4 Condition 4: AuthorityDb identity separation and plaintext exposure

**Identity separation.**
- The four identity classes of §5.1 (runtime, search reader, administrator, business) use **distinct credentials**: separate secrets locally, and separate Entra workload identities or managed identities in Azure.
- Administrators use PIM just-in-time and are never used by an application.
- Business identities have no network route (private endpoint and NSG), and use separate Entra groups and pipelines (PB-11).

**Limit, recorded as UF-5.** The runtime and reader credentials are both held by the Authority process. Reader separation therefore limits what **SQL issued by the query path** can do, not what a compromised Authority process can do. Process-level separation is a reviewer question and owner decision FD-6: a separate Authority search container holding only the reader credential.

**Plaintext exposure inventory.**

| Channel | Approved plaintext present? | Control | Residual |
|---|---|---|---|
| Data files, tablespaces | Yes | Encryption at rest (service-managed or CMK); administrator access only | Administrators (R-D) |
| WAL | Yes | Same; retained per backup policy | Until WAL and backups age out (R-IX-3) |
| HA standby (zone-redundant, Canada Central) | Yes | Same server security boundary | — |
| Read replicas | Would be | **Prohibited for AuthorityDb** (Bicep policy and CI check) | — |
| Logical replication or publication | Would be | **Prohibited**; CI check | — |
| Automated backups and PITR (7 to 35 days) | Yes | Retention period is the persistence bound; restores only by Authority-domain administrators, in Canada; restored servers time-boxed and deleted with evidence | R-IX-3 |
| Geo-redundant backups (Canada East) | Yes | PB-10 register | R-IX-3 |
| Long-term-retention backups, `pg_dump` exports | Would be | **Prohibited** except in the DR runbook, under two-person control | — |
| Server logs and Azure diagnostic settings (Log Analytics) | Possible via bind parameters, constraint-violation `DETAIL` lines, slow-query logs | §5.3 parameter settings; **`log_error_verbosity = terse`** for the runtime and reader roles; no unique constraint on any value column; workspace in Canada with restricted access | Verified by IX-28 |
| `pg_stat_statements`, Azure Query Store, `pg_stat_activity` | No (parameterized text with `$n` placeholders) | Parameterized SQL only (§B.3 of R1) | Verified by IX-28 |
| Temporary files (sort and hash spill) | Transiently | Encrypted storage; `temp_file_limit` | — |
| `EXPLAIN` and `auto_explain` by administrators | Only if an administrator supplies values | Procedure: generic plans without values; `auto_explain` parameter logging off | Administrator discipline |
| Memory dumps, platform support access | Possible | Customer Lockbox for production; no crash-dump shipping | Platform operator |
| Authority application telemetry | No | §5.3 | IX-28 |
| Development and test stacks | No: synthetic data only | Never production copies | — |
| Offboarding | Deleted | Disposal transaction and receipt; backups age out | R-IX-3 |

### 17.5 IX-D14 condition: sweep intervals, failure handling and guarantees

| Sweep | Checks | Interval / coverage (approved, C-122 FD-1) | On a finding | On inability to run | Overdue rule |
|---|---|---|---|---|---|
| **S-A cheap** | I-1 for the whole organization; withdrawn-field residue; cross-organization rows; anomalies | Every organization, every hour | Search-index quarantine and Critical alert (after the Q4 escalation check where relevant) | Retry with backoff | Warning at 1.5 h; **search-index suspension** `INTEGRITY_SWEEP_OVERDUE` at 2 h since the last **successful complete** pass (§17.9) |
| **S-B seals** | One MAC verify per entry, ordered by `last_seal_verified_on` | Every entry verified **at least once per 24 h**, rate-limited to the HSM budget | Search-index quarantine, Critical | MAC provider unavailable: paused, retried | Warning at 36 h; suspension when any entry has had no successful verification for 48 h |
| **S-C deep** | Re-derive from BusinessDb bytes verified against heads | Every record at least once per **30 days**, plus a daily random sample (≥1% or 1,000 records) | Business mismatch: **record** quarantine (existing). Posting mismatch: search-index quarantine | Builder or HSM unavailable: paused | Warning at 45 days; suspension when any record has had no successful comparison for 60 days |
| **S-D Tier 2** | §17.1 M1 continuous; M2 full | M1 continuous; M2 every record within 7 days | §17.1 | Paused | Tier 2 disabled when M1 lags (fence) or M2 exceeds 14 days |

> **C-123:** the overdue thresholds in this table (2 h, 48 h, 60 days) are superseded, on the owner's confirmation, by the deadlines of §17.10 (1 h, 24 h, 30 days, no grace).

**Guarantees** (precise; they hold only while the index is neither suspended nor quarantined; when an overdue rule fires, the index stops serving).

> **Correction (C-122).** Revision 3 stated the S-B, S-A and S-C bounds as the scheduling targets (24 h, 1 h, 30 days). The index keeps serving until the overdue threshold fires, so the **guaranteed** bound is the threshold. The table now gives both. Whether the grace between target and threshold is acceptable is a reviewer question (package Q7).

| Manipulation | Detected: target | Detected: guaranteed bound |
|---|---|---|
| Authority-domain change to **postings only** that would **add** a match | Never served: I-2 on every query | Same |
| Coordinated Authority-domain rewrite of posting, leaf digest and head root (OR-1) | Within the S-B deadline; on open | Same (per page, if per-page seal verification is adopted) |
| Authority-domain change or deletion of a posting that would **remove** a match | Within 24 h (S-B) | Within 48 h (S-B threshold) |
| Entry deletion | Every query (Tier 1); within 1 h (S-A, Tier 2) | Every query (Tier 1); within 2 h (S-A threshold, Tier 2) |
| Stale or replayed entry, partial restore, old Authority commit | Every query (I-1) | Same |
| Derivation defect affecting postings | Within 30 days for every record (S-C), sooner by sample; golden vectors before release | Within 60 days (S-C threshold) |
| Business-byte tampering | On open (full verified read); within 30 days (S-C) | On open; within 60 days (S-C threshold) |

**Evidence:**
- every completed pass is journaled (counts, duration, oldest verified time) and shown on the Release Security Dashboard;
- no values are recorded.

**Cost (unmeasured; UF-3):** S-B at the PB-09 envelope is up to 2.5M entries/day, about 29 MAC verifications/s on average, against Managed HSM. PERF-01 must confirm it, or the intervals must be revised by the owner.

### 17.6 Condition 5: OBS-IX-1

Recorded as a separate finding: [OBS-IX-1](../security/OBS-IX-1_AUTHORITY_APPEND_ONLY_UPDATE_PRIVILEGE.md). It is included in the review package (question 12). No remediation is implemented or authorized.

### 17.7 Owner decisions FD-1 to FD-8 (C-122, 2026-11-06)

| # | Subject | Owner decision (C-122) | Attached condition (binding) | Where answered |
|---|---|---|---|---|
| FD-1 | Sweep intervals and overdue rules of §17.5 | **Approved with conditions**: hourly integrity-check scheduling, complete seal verification within 24 h, BusinessDb comparison of every record within 30 days | Completion tracked per required verification scope; interrupted, failed or incomplete checks never reset a deadline; overdue verification disables indexed search until the required checks complete successfully; the reviewer assesses whether the intervals address the integrity threat model | §17.5, §17.9.1; package Q7 |
| FD-2 | Stored-result limits of §17.3 (lifetime 5/15 min, 3 per session, 10,000 per snapshot, 500,000 per instance) and per-page SpiceDB re-decision | **Approved** | Cross-instance invalidation for revocation, session termination, searchability changes and organization quarantine; whole-page fail-closed behaviour preserved | §17.3, §17.9.2; package Q8 |
| FD-3 | The verified-revision fence (§17.1 M1) as the Tier 2 completeness mechanism | **Approved for review only** | The reviewer assesses omitted affected records, missed events, projection divergence and the 7-day comparison interval. Tier 2 implementation stays unauthorized | §17.1; package Q5 |
| FD-4 | The Q4 escalation rule | **Approved** | Any indication of permission-integrity damage invokes S1 verification first; a search-index quarantine or suspension never suppresses or downgrades an organization authorization quarantine | §17.2 |
| FD-5 | When the A2 §4 ENC-SR text is applied | **Approved**: only after independent review acceptance **and** separate authorization to begin IX-3 | The existing acceptance tests are not modified now | A2 §4 |
| FD-6 | A separate Authority search process with its own read-only database credential | **Deferred** | The reviewer assesses whether it is necessary; not implemented | package Q1 |
| FD-7 | OBS-IX-1 severity and remediation | **Deferred** | The reviewer establishes the severity and recommends remediation. The proposed Medium is **not** accepted | package Q12; OBS-IX-1 |
| FD-8 | Reviewer selection and submission | **External action required** | The package is ready for a qualified human reviewer independent of the implementation. No review has occurred; no attestation is created or implied | package §2, §9 |

### 17.8 New tests (revision 3)

- IX-43: an S1 quarantine refuses indexed search, the V1 fallback and page retrieval;
- IX-44: a search-index quarantine leaves record reads and writes working and leaves the S1 state untouched;
- IX-45: `SearchProjectionRebuild` is refused while the organization is S1-quarantined;
- IX-46: the Q4 escalation (an injected SpiceDB mismatch during a search results in the S1 quarantine, not only the index quarantine);
- IX-47 to IX-50: stored results (expiry and purge; per-session eviction; every page-check failure in §17.3, each with its specified code and no partial page; nonce-protected digest);
- IX-51: overdue sweeps suspend the index (C-122: suspension, not quarantine; see §17.9.1 and IX-53);
- IX-52 (only if Tier 2 is approved later): fence, affected-set property test and M2 sweep.

### 17.9 C-122 conditions

#### 17.9.1 FD-1: completion tracking per verification scope

> **C-123:** the deadlines in this table and rules R5 to R7 are superseded, on the owner's confirmation, by §17.10.

**Verification scopes.** A deadline belongs to a scope, never to a sweep run.

| Sweep | Scope unit | Recorded on success only | Deadline |
|---|---|---|---|
| S-A | Organization | `last_successful_cheap_pass_on`, written only when every check of the pass completed and passed for the whole organization | 2 h after it |
| S-B | Each `search_record_entries` row | `last_seal_verified_on` of the entry, written only after the MAC verification of **that** entry returned `Valid` | 48 h after the oldest value in the organization |
| S-C | Each record | `last_deep_verified_on` of the record, written only after its re-derivation matched | 60 days after the oldest value in the organization |
| S-D (Tier 2, review only) | Each record (M2); organization (M1 fence) | `last_access_verified_on` of the record | §17.1 |

**Rules.**
- **R1.** A timestamp is written only for a unit whose check completed with a passing result, in the same transaction as that result. Nothing else writes it: not scheduling, not a run start, not a retry, not a batch that ended early.
- **R2.** An interrupted run (crash, cancellation, timeout, lease loss), a failed run (provider unavailable, error) or an incomplete run (rate-limited, partial batch) leaves untouched every timestamp it did not earn under R1. **A deadline is never reset by such a run.**
- **R3.** A run that is unable to verify a unit (for example `IntegrityServiceUnavailable`) records the attempt for observability only (`last_attempted_on`, outcome code). Attempts never count as completion.
- **R4.** A newly created or re-derived entry starts with the timestamp of the commit or attestation that sealed it (a successful verification under the organization lock).
- **R5.** The overdue evaluation runs independently of the sweeps (its own schedule, every 5 minutes, and before every indexed search through the projection-state check). It sets `Suspended` with reason `INTEGRITY_SWEEP_OVERDUE` and the overdue scope names, journals the transition and raises a High alert.
- **R6.** `Suspended` is cleared only when **every** overdue scope has had a successful completion under R1. The deadline is recomputed from the recorded timestamps, never from a run summary. The clearing is journaled.
- **R7.** A finding during a catch-up run sets `Quarantined` (§17.2), which takes precedence over `Suspended` and is cleared only by a rebuild (IX-D7).
- **R8.** While the index is suspended, the V1 fallback follows IX-D8 and §17.2 (full V1 and S1 path, V1 limits).

**Evidence.** Per scope, the dashboard shows the oldest successful timestamp, the deadline and the last attempt outcome. No values.

**Change from revision 3.** An overdue threshold previously set a search-index **quarantine**, which only a rebuild clears (IX-D7). C-122 requires indexed search to be disabled "until the required integrity checks complete successfully", so overdue now sets the separate `Suspended` state (R5, R6). Findings still quarantine.

**For the reviewer (package Q7):**
- whether the targets (1 h, 24 h, 30 days) and thresholds (2 h, 48 h, 60 days) adequately address the integrity threat model;
- whether the grace between target and threshold is acceptable, or the threshold should equal the target;
- whether automatic clearing of `Suspended` on successful completion is safe.

#### 17.9.2 FD-2: cross-instance invalidation of stored results

**Principle.** Every fact a page check depends on is read from AuthorityDb at page time. No Authority instance holds a cache of snapshot existence, session validity, authorization revision, approval state, quarantine state or SpiceDB decisions that a page check consults. So an invalidation committed through any instance is effective on every instance at its next page request.

| Invalidating event | Committed by | Deleted in the same transaction | Also refused at page time by |
|---|---|---|---|
| Permission revocation (any authorization change) | Security-command transaction (any instance) | The organization's snapshots whose authorization revision is below the new revision | Page check 4 (revision) and check 7 (fresh SpiceDB decision) |
| Session termination (logout, revocation, tenant switch, expiry) | Session transaction on `authenticated_sessions` | All snapshots of that session | Page check 2 (session must be live) |
| Searchability change (AC-6 approval withdrawn, classification strengthened, operator narrowed, coverage leaving `Complete`) | The AC-6 command or coverage transaction | All snapshots of the affected form | Page check 5 (approval revalidation) |
| S1 organization quarantine | S1 quarantine transaction | All snapshots of the organization (purge) | Page check 3 (organization gate and S1 state) |
| Search-index quarantine or suspension | Projection-state transaction | All snapshots of the organization | Page check 6 (projection state) |

**Rules.**
- **X1.** Deletion is the first line; the page check is the second. Each event is covered by both, so a missed or failed deletion still cannot produce a page.
- **X2.** If a deletion fails, the invalidating transaction fails. A revocation is never committed with its snapshots left in place.
- **X3.** The Authority's in-process admission counters (R1 §E, "per instance") are capacity controls only. They never authorize a page.
- **X4.** Whole-page fail-closed behaviour of §17.3 is unchanged: any failed check returns no page.

**Clarification.** The "500,000 live entries per Authority instance" limit of §17.3 is a capacity guard. It is counted from AuthorityDb rows tagged with the creating instance; it plays no part in invalidation.

#### 17.9.3 New tests (C-122)

- IX-53: interrupted, failed, rate-limited and partial sweep runs do not move any deadline; overdue suspends; suspension clears only after successful completion of every overdue scope; a finding during catch-up quarantines.
- IX-54: two Authority instances. Each FD-2 invalidating event committed through instance A refuses the next page on instance B, with and without the deletion step (fault-injected), and never returns a partial page.
- IX-55: a failed snapshot deletion aborts the revoking transaction (X2).
- IX-56: no page check reads an instance-local cache (architecture test).

### 17.10 C-123 proposed decision: sweep deadlines without grace (UF-13)

> **Status:** **approved by the owner with clarifications (C-124)**; the clarifications are binding (§17.10.5). It supersedes the overdue thresholds of §17.5 and rules R5 to R7 of §17.9.1. Nothing is implemented.

#### 17.10.1 Decision as stated by the owner

- Hourly integrity verification: enforce the **1-hour** deadline.
- Seal verification: enforce the **24-hour** deadline.
- BusinessDb record comparison: enforce the **30-day** deadline.
- The grace that extended these to 2 h, 48 h and 60 days is removed.
- An overdue required verification scope suspends indexed search.
- Successful completion of every overdue scope may clear the suspension automatically.
- A **failed** integrity verification quarantines the search index and does **not** clear automatically.
- A completed sweep run never substitutes for successful verification of its individual required scopes.

#### 17.10.2 Implementation contract

**Scopes and deadlines.**

| Sweep | Scope unit | Completion timestamp (written only on a passing result for that unit) | Deadline | Scheduling target (must be inside the deadline) | Warning |
|---|---|---|---|---|---|
| S-A | Organization pass (all S-A checks for the whole organization) | `last_successful_cheap_pass_on` | **1 h** after it | A new pass starts at least every **15 minutes** | 45 min |
| S-B | Each `search_record_entries` row | `last_seal_verified_on` | **24 h** after the entry's timestamp | Every entry verified within **16 h** | Oldest entry over 18 h |
| S-C | Each record | `last_deep_verified_on` | **30 days** | Every record within **20 days** | Oldest record over 25 days |
| S-D (Tier 2) | — | — | Not part of this decision; Tier 2 is design-only | — | — |

**Outcome classes.** Every unit check ends in exactly one class:

| Class | Meaning | Examples | Effect |
|---|---|---|---|
| **Pass** | The check completed and the evidence matched | Seal `Valid`; re-derived postings equal stored; I-1 holds | Write that unit's timestamp, in the same transaction as the result |
| **Fail** (failed integrity verification) | The check completed and the evidence did **not** match | Seal `Invalid`; I-1 violation; posting mismatch; withdrawn-field residue; cross-organization row; anomaly on a predicate field | **Quarantine** the index (after the Q4 escalation where it applies; §17.2). Never cleared automatically: only a journaled `SearchProjectionRebuild` and a completed rebuild (IX-D7) |
| **Inconclusive** | The check could not reach a result | MAC provider unavailable (`IntegrityProviderUnavailableException`), timeout, cancellation, lease loss, database error, builder unavailable, rate limit reached | No timestamp. Record `last_attempted_on` and the outcome code for observability. The deadline keeps running |

A business-byte mismatch found by S-C remains a **record** quarantine (existing behaviour), in addition to the index outcome for the postings.

**Rules.**
- **D1, no substitution.** Only a Pass writes a scope timestamp. A run record (start, end, counts) is observability only; no code path reads a run record to compute a deadline or to clear a suspension. An S-A pass is a Pass only when every S-A check passed for the whole organization; a partial S-A pass writes nothing.
- **D2, deadline evaluation.** `overdue` = (now − oldest scope timestamp) > deadline, evaluated per sweep kind over all units of the organization. There is **no grace**: the comparison uses the deadline itself.
- **D3, suspension.** The evaluation runs every 5 minutes and before every indexed search and page (through the projection-state check). Overdue sets `Suspended` with reason `INTEGRITY_SWEEP_OVERDUE` and the overdue sweep kinds; journaled; High alert.
- **D4, clearing a suspension.** Automatic, only when D2 no longer holds for **every** overdue kind, recomputed from the unit timestamps. Journaled.
- **D5, precedence.** `Quarantined` takes precedence over `Suspended`. A Fail during catch-up converts the state to `Quarantined`. A later successful pass never clears a quarantine.
- **D6, new and rebuilt entries.** An entry sealed in a commit or a verified backfill under the organization lock starts with that time as its S-B timestamp. **C-124:** this counts as the initial verification only after the seal was generated successfully **and** the transaction committed durably; the timestamp is written in that same transaction, so a rolled-back transaction leaves no timestamp. A rebuild sets every entry's timestamp the same way.
- **D7, clock.** Timestamps and evaluation use the Authority clock abstraction; instance clocks must be NTP-synchronized. **C-124:** a verification timestamp more than 1 minute in the future is Inconclusive (the unit is treated as never verified for deadline purposes). Clock anomalies never extend a deadline and never clear a suspension (§17.10.5).
- **D8, fallback.** While suspended or quarantined, IX-D8 applies (complete V1 and S1 path, V1 limits).

#### 17.10.3 Consequences to accept

- **Availability.** Without grace, an outage of the MAC provider or the Authority worker longer than the remaining slack suspends indexed search. Slack is 45 minutes for S-A and 8 hours for S-B at the scheduling targets. The V1 fallback is limited (UF-11: its 480-record benchmark fails), so for large forms a suspension means search is unavailable.
- **HSM throughput.** S-B within 16 h at the PB-09 envelope (up to 2.5M entries) needs about **43 MAC verifications/s** on average (29/s at exactly 24 h). Unmeasured; PERF-01 (UF-3). If PERF-01 cannot meet it, the deadline or the envelope must be revisited by the owner.
- **Guarantees.** The detection bounds of §17.5 become 1 h (S-A), 24 h (S-B) and 30 days (S-C), equal to the deadlines.

#### 17.10.4 Test implications

| Test | Change |
|---|---|
| IX-51 | Revised: overdue **suspends** (not quarantine) at exactly the deadline, for each sweep kind |
| IX-53 | Revised: interrupted, inconclusive, rate-limited and partial runs write no timestamp and move no deadline; suspension clears only after every overdue scope passes; a Fail during catch-up quarantines |
| **IX-57** (new) | Boundaries with a controlled clock: deadline − 1 s serves; deadline + 1 s suspends; no grace, for S-A, S-B and S-C |
| **IX-58** (new) | Classification: MAC provider unavailable → Inconclusive → eventual suspension, never quarantine; seal `Invalid` → quarantine immediately |
| **IX-59** (new) | A quarantine is not cleared by any number of later successful passes; only by `SearchProjectionRebuild` |
| **IX-60** (new) | Steady state: a compressed-clock soak run at the scheduling targets never suspends; one entry left unverified suspends the whole organization index at 24 h |
| PERF-01 | Adds the S-B throughput at the 16 h scheduling target (about 43/s at the envelope) |

#### 17.10.5 C-124 clarifications (binding)

| # | Clarification | Contract effect |
|---|---|---|
| K1 | Pass records successful verification for the **exact** required scope | A Pass writes only the timestamp of the unit it verified (one entry, one record, or one complete organization pass). No aggregate or neighbouring unit is touched |
| K2 | Fail triggers search-index quarantine | As §17.10.2; after the Q4 escalation where it applies |
| K3 | Inconclusive does not advance the deadline | No timestamp; attempt recorded for observability only |
| K4 | Overdue scopes suspend indexed search | D3 |
| K5 | Suspension clears automatically only after **every** overdue scope passes | D4 |
| K6 | Quarantine cannot clear automatically | D5; only `SearchProjectionRebuild` |
| K7 | A freshly sealed entry counts as its initial verification only after successful seal generation and durable commit | D6 |
| K8 | A verification timestamp more than 1 minute in the future is Inconclusive | D7 |
| K9 | Clock anomalies must not extend deadlines or clear suspension | (a) The evaluation keeps, per organization, the highest time it has observed (`last_evaluation_on`, monotonic) and evaluates deadlines against `max(now, last_evaluation_on)`. A backward clock step therefore never makes an overdue scope current. (b) A forward step can only make scopes overdue sooner (suspend), never clear. (c) Future timestamps are K8. (d) Clearing a suspension also requires `now ≥ last_evaluation_on` |

Tests added for K7 to K9: **IX-69** (seal generated, transaction rolled back → no timestamp; entry not counted), **IX-70** (future timestamp → Inconclusive; suspension not cleared), **IX-71** (backward clock step does not clear a suspension or restore an overdue scope; forward step suspends, never clears).

---

## 18. C-124 contracts (OR-1 to OR-4)

### 18.1 OR-1: integrity-binding contract (open until the owner accepts it)

#### 18.1.1 Corrected claim (accepted, C-124)

Unkeyed hashes over mutable Authority rows (I-1, I-2) are **consistency checks**. They are never represented as protection against compromise of the Authority runtime credential. Only a keyed verification (a MAC checked through the MAC provider) or an external anchor (archive, ledger) can bind data against a writer of AuthorityDb, and only against a writer **without** access to the MAC provider.

#### 18.1.2 Adversaries

| Id | Adversary | Capabilities |
|---|---|---|
| AD-B | Business DBA or business credential | No path to AuthorityDb (G08) |
| AD-1 | Holder of the Authority runtime **database** credential only (stolen credential, or SQL execution as that role) | `SELECT, INSERT, UPDATE` (+ `DELETE` on derived search tables) on AuthorityDb; **no** MAC provider, no SpiceDB key |
| AD-2 | Compromise of the Authority **process** | Everything AD-1 has, plus the MAC provider (managed identity) and SpiceDB |
| AD-3 | AuthorityDb administrator | Everything in the database, including DDL; no MAC provider |

#### 18.1.3 Options evaluated

| Option | Mechanism | Binds properties 1–5 | HSM operations | New cryptographic construction? | Assessment |
|---|---|---|---|---|---|
| **A. Per-posting row-HMAC verification** | For every predicate-field posting of every displayed record, verify its `source_canonical_bytes` against the leaf `row_signature` through the MAC provider | 1 org and record; 2 operation id (signed column); 3 field id, type (entity kind) and value. **Not** classification, approval, normalized value | Page size P × predicate items per record (≥ 1 per condition; multi-select adds one per option) | **No**: `DynamicForms/RowHmac/v1`, existing vectors | Sound but incomplete (no 3-classification, no 5) and the most HSM operations |
| **B. Per-record entry-seal verification** | Verify the entry seal (§4 item 2) of every displayed record through the MAC provider; the seal message binds the full derivation (§18.1.4) | 1, 2, 3, 4 (sealed value and normalized value; predicate re-evaluated in process on the verified value), 5 (approval id and operator set at derivation) | **P per page** (one per displayed record) | **Yes, a new signed message schema** under the existing HMAC-SHA-256 primitive and the existing RowHmac v1 encoding rules. It needs a written specification, vectors and explicit review | **Recommended** |
| C. Keyed index root | An authenticated tree over all entries of a form, its root sealed at each commit; per query, one seal verification and inclusion proofs | Same as B, plus membership of **all** matches (counts) | 1 per query (+1 per page recheck); 1 sign per commit, O(log n) node writes | **Yes, a new authenticated data structure** | Better per-query cost and binds counts, but a new scheme needing dedicated cryptographic review, plus write amplification. **Not for IX-1 to IX-4**; possible later, separately approved |
| D. Batch verification | Submit many MAC verifications in one provider call | Transport for A or B | Same number of operations, fewer round trips | No | Vault Transit has a batch form; **no batch verify is assumed for Azure Managed HSM** (to be confirmed in PERF-01). Use only as a transport optimization; parallel requests otherwise |

#### 18.1.4 Recommended contract (B), proposed

**Seal message `DynamicForms/SearchEntrySeal/v1`.** Encoded with the RowHmac v1 encoding rules (length-prefixed, typed, explicit nulls; `docs/dynamic-forms/row-integrity-format.md` §3). Only the message schema is new. Fields, in order:

1. domain label, seal schema version, MAC key purpose and key version;
2. organization id, form definition id, form record id;
3. committed operation id, committed content revision, committed manifest root;
4. derivation algorithm version;
5. for each posting, sorted by (field definition id, manifest item key):
   - field definition id, manifest item key, entity kind (storage type);
   - Authority-registered classification and protection mode at derivation;
   - search approval id and its approved operator set;
   - SHA-256 of `source_canonical_bytes` (this binds the exact typed value through the existing canonical encoding);
   - the normalized value for strings;
6. anomaly markers (for example `EncryptedValuePresent`) per predicate-eligible field, and the posting count.

**Key.** A **separate MAC key purpose** per organization (`SearchEntrySealV1`), not the row-integrity key. This follows the purpose-separation rule of `CLAUDE.md` §4L.3 and §4P. Cost: one more non-exportable key per organization, with rotation. *Owner decision.*

**Verification on every page** (before any record of the page is released):
1. I-1 for the search (unchanged) and I-2 for every match (CPU; unchanged);
2. for every record on the page, verify its entry seal through the MAC provider. Any result other than `Valid` fails the whole page:
   - `Invalid` is a Fail (quarantine, after Q4);
   - provider unavailable is a 503 with no page;
3. check the sealed fields against the current state:
   - sealed operation and revision equal the head (as read in the candidate snapshot);
   - every predicate field's sealed approval id equals the Active approval;
   - the request operator ∈ the sealed operator set;
   - sealed classification and protection equal the current Authority-registered values;
4. **re-evaluate the request predicate in process** on the sealed (verified) value. The SQL match is a candidate only; a record whose verified value does not satisfy the predicate fails the page (it indicates index damage).

**What B proves, by adversary:**

| Property | AD-B | AD-1 | AD-2 | AD-3 |
|---|---|---|---|---|
| 1 Organization and record identity | Structural | **Bound** (seal) | Not bound | Bound (no MAC) |
| 2 Accepted content version | Structural | Bound to **an** accepted operation. Rolling back the head and the entry together to an older sealed state is **not** detected per query: anti-rollback needs external state | Not bound | As AD-1 |
| 3 Field id, type, classification, value | Structural | **Bound** | Not bound | Bound |
| 4 Predicate and matching value | Structural | **Bound** (sealed value + in-process evaluation) | Not bound | Bound |
| 5 Active approval | Structural | Approval id **at derivation** bound. "Still Active now" is read from mutable state. Reactivating a withdrawn approval **and** replaying sealed entries saved from before the withdrawal is not detected per query | Not bound | As AD-1 |

**Not bound per query, and how they are covered:**
- freshness (rollback of head and entry together) and current approval state, against AD-1 and AD-3: by the **independent archive reconciliation** (option E of the remediation proposal) comparing heads and approval state with the anchored journal;
- everything, against AD-2: only by external evidence (archive and ledger) and its reconciliation;
- **matches not on a displayed page:** they contribute to the count but are checked by I-2 only. Under AD-1 the count can include false matches among records the caller may discover. It discloses no unauthorized record. The count is described as "matches in the Authority index" (§6.6). Option C is the only option that would bind it.

**Optional, for review:** an in-process cache of seal verification results per Authority instance, keyed by (entry id, seal bytes, SHA-256 of the sealed message as re-read), lifetime ≤ the snapshot lifetime. A changed row produces a different digest and therefore a cache miss; a replayed valid seal verifies with or without the cache. It reduces repeated operations across pages and queries and is never shared between instances.

#### 18.1.5 HSM operations (estimates, not measured)

| Event | Operations (B) | Notes |
|---|---|---|
| Content commit | 1 MAC **sign** (seal) | In addition to the existing per-item verifications at commit |
| First page of a query | P MAC **verify** (P = 10, 25, 50 or 100; `FieldSearchLimits.PageSizes`) | I-2 on all matches is CPU only |
| Each further page | P verify | Same snapshot; page re-authorization of §17.3 |
| S-B sweep | about 29 verify/s at 24 h, **about 43/s** at the 16 h scheduling target, at the PB-09 envelope | §17.10.3 |
| Option A instead | P × predicate items | For example 25 records × 3 conditions = 75 per page |
| V1 today, for comparison | One verify per leaf of every in-scope record (≤ 500 records × leaves) | For example 500 × 50 = 25,000 per search |

Illustration: if 10% of the 50 requests/s PB-09 envelope were search pages at P = 25, that is about 125 verify/s for pages, plus 43/s for S-B, plus one sign per commit. This is an illustration, not a forecast.

**PERF-01 must measure:**
- MAC verify latency p50/p95/p99 against Azure Managed HSM, single and in parallel groups of 10, 25, 50 and 100;
- sustained verify throughput per HSM and per Authority instance, and the throttling behaviour (which must surface as 503 with no page, never as a Pass);
- whether a batch verify exists for the provider in use;
- commit p95 delta with the seal sign inside the organization lock (IX-1 gate ≤ 20 ms);
- page latency at each page size with seal verification, with and without the optional cache;
- S-B at 43/s running concurrently with interactive traffic;
- recovery after an HSM outage (deadline slack, §17.10.3).

#### 18.1.6 Status

> **C-125:** the per-record HMAC seal is **not final**. A cost and cryptographic reassessment compares five alternatives: `INDEXED_SEARCH_INTEGRITY_COST_REASSESSMENT.md`. It recommends ES256 seals verified locally with a pinned public key (about 2 remote HSM operations/s instead of about 170/s), plus signed external checkpoints. IX-1 does not begin until the owner decides.

OR-1 is **open** until the owner accepts §18.1.4, including the separate key purpose. The seal message schema needs written vectors (generated at IX-1) and is added to the independent cryptographic review scope (package Q7). Per-page seal verification is a design requirement (C-124); it is **not** a complete mitigation (AD-2; freshness under AD-1 and AD-3).

### 18.2 OR-2: revision-synchronization contract (open until the owner accepts it)

#### 18.2.1 Inventory at `e830d08`

Every write to `organization_authorization_states.content_integrity_revision`, and the journal entry it accompanies:

| # | Site | Operation | Journal event | Head change | Required projection action |
|---|---|---|---|---|---|
| W1 | `ContentService.cs` 368 | Content commit | `ContentHeadCommitted` (370) | New head | **Atomic advance** with the derived postings, entry and seal |
| W2 | `ContentService.cs` 735 | Verified read finds tampered business bytes | `IntegrityVerificationFailed` (737) | Head quarantined | **Atomic advance**, no posting change. The quarantined head is read by I-1 (a search with the record in scope fails, D3 parity). **Not** an index quarantine |
| W3 | `ContentService.cs` 901 | Historic or projection verified read finds tampering | `IntegrityVerificationFailed` (903) | Head quarantined | As W2 |
| W4 | `ContentService.cs` 1235 | Share-editor prepare: submitted current snapshot does not match the accepted head | `CurrentSnapshotMismatch` (1237) | **None** | **Atomic advance**, no posting change, no quarantine of anything |
| W0 | `OrganizationBootstrapService.cs` 77 | Organization creation | — | — | Initial value 0; the projection state starts at 0 |

No other writer exists (grep of all assignments and increments). For comparison, V1 treats any change of this revision during a search as "snapshot changed, retry" (`SearchCatalogueService.cs` 369), never as tampering.

#### 18.2.2 Transaction invariant INV-R

> For every committed AuthorityDb transaction that changes `content_integrity_revision` of an organization from *r* to *r′*, the **same** transaction either:
> - **(a) advances** `search_projection_states.projection_content_integrity_revision` to *r′*, with exactly the posting changes derived from the content accepted in that transaction (none for W2 to W4); or
> - **(b) sets `ReconciliationRequired`**, recording *r* as the last synchronized revision.
>
> No transaction may change the revision without (a) or (b). A revision mismatch on its own is **never** classified as tampering.

- **`ReconciliationRequired`** is a new projection state, distinct from `Quarantined` and `Suspended`:
  * not served (503, reason `SEARCH_INDEX_RECONCILING`), fallback per IX-D8;
  * High alert, not Critical;
  * cleared automatically by an Authority reconciliation that re-derives or re-verifies the records whose content checkpoints lie between *r* and the current revision, then advances the projection revision;
  * a Fail found during reconciliation converts it to `Quarantined`.

  All four current sites use (a); (b) exists for future paths (for example G14 replay) that cannot derive atomically.
- **I-1 correction.** If the projection is `Active` and its revision differs from the organization's, INV-R was bypassed (an old image, out-of-band SQL, a defect). The search fails and the projection is set to `ReconciliationRequired`, **not** `Quarantined`. `Quarantined` is set only by a Fail: mismatching evidence such as a digest, seal, root or posting.

#### 18.2.3 Enforcement

1. **Single code path.** `OrganizationAuthorizationState.ContentIntegrityRevision` loses its public setter. The only way to change it is a domain method that takes a mandatory projection action (advance with derivation result, advance without postings, or reconciliation-required). A new site cannot compile without choosing one.
2. **Architecture test.** Fails if any code outside that method writes the property, and lists the known call sites (W1 to W4) so an added site is reviewed.
3. **Database constraint (recommended, defence in depth).** A `DEFERRABLE INITIALLY DEFERRED` constraint trigger on `organization_authorization_states`, owned by the `authority_search` migration: at commit, a changed `content_integrity_revision` requires the projection state of that organization to be either at the new revision or `ReconciliationRequired`. It applies only to organizations that have a projection state row.
   - **Effect on an old Authority image** (§12.4): its content commits are **refused** at commit (fail closed), instead of silently diverging. This replaces "projection behind, quarantine" in IX-35.
   - *Owner decision:* accept this availability consequence.

#### 18.2.4 Tests (regression, transactional)

| Test | Assertion |
|---|---|
| **IX-63** (revised) | For each of W1 to W4: after commit, projection revision = organization revision; W1 postings present; W2/W3 head quarantined, index `Active`; W4 index `Active`, a subsequent search succeeds |
| **IX-72** | Legitimate mismatch submissions (W4) repeated many times never quarantine or suspend the index |
| **IX-73** | Fault injection after the revision change and before commit, at each site: neither revision advances |
| **IX-74** | Raw SQL as the runtime role changing the revision without the projection update is refused at commit by the constraint trigger |
| **IX-75** | Architecture test: a new write outside the domain method fails the build; the call-site list matches W1 to W4 |
| **IX-76** | Active projection with a revision mismatch (simulated bypass) → `ReconciliationRequired`, never `Quarantined`; reconciliation clears it; a Fail during reconciliation quarantines |
| IX-35 (revised) | Old image: content commit refused by the constraint trigger |

### 18.3 OR-3: commit failure paths (contract, accepted direction C-124)

- Index writes happen **only** on the successful content-acceptance path of `CommitAsync`, after staged-byte verification (302) and the share-editor recheck (308–324), and before the final commit (387). All `authority_search` writes, the seal sign, the entry timestamp (K7) and the W1 projection advance are in that transaction.
- The abort paths commit the operation state only: expired (248), revision conflict (260), permission revoked (292), record locked (321). They must not write `authority_search` rows or change the projection revision. They do not change `content_integrity_revision` today.
- A failed or rejected commit publishes no searchable value and does not advance any accepted index state.

**Tests:** IX-61 (each abort path writes nothing in `authority_search`), IX-64 (exception after derivation, before commit → full rollback: no postings, no entry, no seal, no `form_content_revision` change), IX-65 (seal sign failure → commit fails, head unchanged), IX-66 (crash after commit → head and index consistent), **IX-77** (a staged-byte verification failure (`CandidateMismatch`, 303–306) writes nothing).

### 18.4 OR-4: stored-result lifecycle (IX-3 requirements, C-124)

Existing catalogue-snapshot cleanup is **not** relied on (none exists). New requirements for IX-3:

| Requirement | Specification |
|---|---|
| Session binding | Snapshot row stores the Authority `authenticated_session_id`; page check 2 requires that session to be unrevoked, unexpired (`session_expires_on`) and within its idle timeout at page time |
| Expiration | Absolute snapshot expiry (§17.3); session expiry and idle timeout are checked at page time because they are time-based, not transactional |
| Cross-instance invalidation | §17.9.2 (X1 to X4) |
| Cleanup | New purge job every minute, bounded batches; new `DELETE` grant limited to result-snapshot tables; deletion in revoking transactions (X2) |
| Concurrency | Page requests on one snapshot are serialized per snapshot (`FOR UPDATE` on the snapshot row in the runtime-role page transaction) so a deletion and a page cannot interleave into a partial page |

**Tests (IX-3 exit gate):** **IX-78** revocation between pages and during a page; **IX-79** logout and session revocation; **IX-80** session expiry and idle timeout at page time, with no revoking transaction; **IX-81** classification strengthened, approval withdrawn and operator narrowed between pages; **IX-82** concurrent page requests on one snapshot, and a page request concurrent with purge, eviction (fourth snapshot) and revocation: never a partial or mixed page.

### 18.5 New test identifiers (C-124)

IX-69 to IX-82 as above; IX-35 and IX-63 revised.
