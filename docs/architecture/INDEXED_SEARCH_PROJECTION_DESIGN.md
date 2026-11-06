# Indexed search projection (Option C): architecture and security design — revision 2

- **Status:** **Design for final owner approval. Nothing here is implemented.**
  * Revision 1 received **conditional architectural approval**, with conditions C1 to C4 (C-120, 2026-11-06).
  * Revision 2 answers those conditions. It adds a scalable permission tier (§9), per-query integrity checks for returned matches (§6.3) and the exact decision texts (§15).
  * Prepared by Claude (Opus). This is not an independent review.
- **Revision 1:** [INDEXED_SEARCH_PROJECTION_DESIGN_R1.md](INDEXED_SEARCH_PROJECTION_DESIGN_R1.md), cited here as "R1 §…". Its sections remain part of the design unless this revision changes them; where they differ, this revision governs.
- **Companion:** [ADR-PB-004 Amendment A2](../decisions/ADR-AMENDMENT-PB-004-A2-AUTHORITY-SEARCH-INDEX.md) (**Proposed**). It separates the preserved guarantees from the proposed changes.
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

**Pre-existing observation, outside this design's scope:** the tables the code calls "append-only" (`security_journal_entries`, `record_manifest_leaves`, …) also receive `UPDATE`. Append-only is therefore enforced by the absence of `DELETE` and by application code, not by privilege. Recorded as **OBS-IX-1** for the independent reviewer; nothing in this design depends on changing it.

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
| AuthorityDb administrator edits rows | Outside the Level 2 threat model (`CLAUDE.md` §4M: the Authority and its administrators are trusted). Still: the per-match check (§6.3) prevents false **additions**; seals and sweeps detect **omissions** (§6.4); object-level pgAudit alerts on human access (§5.3) |
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

**Effect:** a returned match is **cryptographically tied to the anchored accepted root** (root → content checkpoint → journal → ledger). No posting can add a match that the accepted content does not support.

### 6.4 Omission and addition: demonstration matrix

| Manipulation | Actor | Addition prevented by | Omission detected by | When |
|---|---|---|---|---|
| Delete, alter or inject business rows | business DBA | n/a: search does not read BusinessDb | n/a: search does not read BusinessDb; the record still matches on accepted content | Structural; damage is caught on open and by the deep sweep |
| Alter a posting value so a non-match matches | Authority domain | **I-2** (bytes, digest, root) | n/a | Every query |
| Alter or delete a posting so a match disappears | Authority domain | n/a | **Entry seal** (HMAC) mismatch in the cheap sweep; deep-sweep re-derivation | Sweep interval |
| Delete an entry | Authority domain | n/a | **I-1** (contentful head without entry) | Every query (Tier 1); sweep (Tier 2) |
| Replay old postings or entry | Authority domain | **I-1** (operation binding) and I-2 | I-1 | Every query |
| Restore only `authority_search` from an old backup | operator | I-1, I-2 | I-1 (revision) | Every query |
| Delete or forge Tier 2 access rows | Authority domain | **SpiceDB decision** on every result (fail closed on divergence) | Differential sweep against SpiceDB lookups (§9.4) | Sweep interval |
| Old Authority version commits without derivation | deployment error | I-1 (revision) | I-1 | Every query |

**Conclusion, stated precisely:**
- Against the **Level 2 adversary** (business database administrator or credential), indexed search can neither omit nor add eligible results: structurally, BusinessDb is not consulted.
- Against manipulation **inside the Authority domain** (outside the Level 2 model):
  * additions are prevented on every query;
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

---

## 9. C3: scaling beyond 50,000 discoverable records (Tier 2)

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

## 15. Decision matrix (exact texts, for final approval)

Each decision is proposed verbatim. "Recommend" is Claude's recommendation; the owner decides.

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

R1 §K's acceptance criteria 1–10 remain, extended to the new tests.

**Unchanged:** M4-1 stays open pending independent review; M4-2 unauthorized; no push, PR, merge or deploy.
