# Tier 2 index architecture: Options B1, B2 and C (C-138 IX5-D2, refined under C-139; review annex A)

- **Status:** design evaluation for the independent reviewer and the owner. **No Tier 2 table, index, migration or query exists. No architecture is selected or approved for implementation.** Prepared by Claude (Opus) under C-138 §3 and C-139 §2–§6. It is the design author's analysis, not an independent assessment.
- **C-139 (owner direction, owner-led, 2026-11-09):** Option B is preferred **for independent review**, with logically and physically separate access and searchable-value structures. Option C remains the comparison baseline and possible fallback. Option A remains rejected unless separately approved. This is an architecture direction, not implementation approval. §1–§5 below are the C-138 evaluation, retained unchanged for traceability. **§6–§15 are the C-139 refinement and take precedence where they differ** (corrections are listed in §6.2).
- **Decision context:** C-138 approved evaluation of Option C first, Option B second, and Option A only as a documented rejected or exceptional alternative. The final Tier 2 architecture is chosen only after the independent review and owner approval.
- **Inputs:** design §9.2–§9.5 and §17.1; `INDEXED_SEARCH_IX5_READINESS_ASSESSMENT.md` §5 (findings R38-1 to R38-4); design §22.4–§22.5 (PD-9, PostgreSQL 18 skip scan); `authority.schema.zed` at `1868e16`; the vendored SpiceDB v1.56.2 API.

## 1. The options as evaluated

All options keep the parts of §9.3 that make Tier 2 disclosure-safe:
- the S1 pin;
- the M1 fence (`access_verified_revision = authorization_revision`);
- SpiceDB deciding every disclosed result (bulk `form_record#discover`, `form_record#read`, `field_item#read`);
- the Tier 1 checks I-1 and I-2;
- the stored-result rules of FD-2.

The options differ only in how candidates are found.

| | Option C: access enumeration | Option B: split tables | Option A: §9.2 as written |
|---|---|---|---|
| Access rows | One row per (record, principal, access path, scope); scope is the whole record (owner, parent owner) or a share-covered field item. **No value column** | C's table, plus a value-keyed, principal-leading table: one row per (posting, principal) with a copy of the posting's typed value | One table: one row per (posting, principal) with the value; value-keyed indexes and a record maintenance index on the same table |
| Candidate step | Enumerate the caller's discoverable records `D` from rows with `principal = ANY(P)`, then run the **existing Tier 1 record-scoped probe** on `D` | Principal-bound value range scans on the value table, intersected per condition; parity on the record table | As B, on one table |
| Per-search SQL cost | O(\|D\|) probes per condition | O(caller-accessible postings that match) | Same as B if the planner keeps principal-leading plans |
| Replaces | The SpiceDB `LookupResources` enumeration and its 50,000 ceiling | The enumeration and the O(\|D\|) probing | Same as B |
| Status | **Evaluate first** | **Evaluate second** | **Rejected** (§5) |

**Variant C-S (structural C), proposed for the reviewer.** No record-leading index on the access table. Maintenance, which always knows from the canonical edges before and after a command which principals a record had and has, addresses rows by their **full principal-leading key**. M1 tests that expected rows are present by exact key. Rows that are present but unexpected (over-inclusion, which cannot leak) are found by M2's ordered full scan and by the per-result SpiceDB decision. Every index on the table then leads with `(organization_id, access_principal_entity_id)`, so no plan for any statement, caller-facing or not, can read the rows of principals outside the statement's principal set. B can adopt the same rule for both of its tables (§3.7).

## 2. Common facts that drive the evaluation

1. **Principal-leading plans are bounded by authorized data, whatever the planner chooses, as long as every caller-facing statement binds the principal.** A plan over an index that leads with `(organization_id, access_principal_entity_id)` and has `principal = ANY(P)` as an index condition can only read rows of principals in `P`. Rows exist for a principal only for records or items that principal can access, so the cost (including any skip scan over later key columns) depends only on data the caller is authorized for. That property comes from the key order, not from cost estimates. This is the PD-9 lesson applied in reverse: PD-9 showed that a value-keyed index on a table probed by record is unsafe, because the planner can skip-scan it.
2. **A record-leading index on a caller-facing table is the risk.** If an index such as `(organization_id, form_record_id, …)` exists on a table that a caller statement reads without a record condition, the planner can scan it, or skip-scan it, across the whole organization. The cost then depends on organization-wide row counts, which the caller is not authorized for. That dependency on aggregate size is less severe than PD-9's dependency on value distribution, but it is the same class (IX-D4).
3. **GIN does not bound cost by its leading column (finding UF-15).** A `btree_gin` index on (principal, trigram), as §9.2 proposes for `contains`, stores each column's keys separately and intersects their posting lists by heap TID. The trigram key's posting list covers every principal's rows in the organization, so the work, and the timing, of a `contains` search depends on how often the trigram occurs organization-wide. GIN is also used only through bitmap scans, which Tier 1 removes per statement. **Neither option can use a GIN index for `contains`.** C does not need one (the Tier 1 probe handles `contains` per record). In B, `contains` must be evaluated after principal-bound narrowing by another predicate, or fall back to C-style enumeration.
4. **Tier 1 postings no longer carry value-keyed indexes** (PD-9, C-135; architecture test `NoPostingIndexIsKeyedOnAValueColumn`). The Tier 1 record probe runs on `(organization_id, form_record_id, field_definition_id)`. Option C reuses this probe unchanged.
5. **Command catalogue.** The canonical edges that confer record or field access are written by:
   - `RegisterRecord` (owner, or parent for a child);
   - `TransferOwnership`;
   - `CreateShare` and `RevokeShare`;
   - `UnlinkChildRecord`.

   `UpdateShareScope` is declared in the contract but **not implemented** (the planner returns `COMMAND_KIND_NOT_IMPLEMENTED`). There is **no team-removal command**. `AddTeamMember` and `RemoveTeamMember` change only team edges. `DisableIdentityBinding` revokes sessions and changes no edges. The commands that change which fields are approved and indexed (`ApproveFieldSearch`, `WithdrawFieldSearch`, `ReclassifyField`, and the builder backfill and purge that follow) change no edges, but they change the set of postings. See the package, UF-14.

## 3. Assessment by criterion

### 3.1 Tenant isolation

| | C | B |
|---|---|---|
| Keys | `organization_id` first in every key; RLS on the table; read by `authority_search_reader` only | Same, on **two** tables |
| Principal resolution | `P` from the organization's canonical edges in the candidate transaction (§9.3 step 2) | Same |
| Cross-organization checks | S-A extended to the access table | S-A extended to both tables |
| Assessment | Equivalent | Equivalent, with twice the policy and sweep surface |

### 3.2 Authorization freshness

Both rely on M1: Tier 2 serves a search only when `access_verified_revision = authorization_revision` at the pin. Freshness is therefore set by **how quickly M1 clears pending records**, not by the index design.

- **C:** rows depend only on canonical edges and share-covered item identities. Approval changes touch no rows, so they create no pending verifications (to confirm: that a field item's identity is stable across content revisions; package TQ-6).
- **B:** additionally, every approval or withdrawal of a field changes value rows for **every record of the form**: an affected set of O(form records), and a long M1 lag, during which the whole organization falls back to Tier 1 (see LookupSubjects annex E §5).

**Fence starvation** (package Q5, UF-18) affects both options equally. Every security command advances `authorization_revision`, so Tier 2 is usable only between commands, once M1 has caught up. At the R-37 rates (about one command per record created), sustained ingestion keeps Tier 2 unused. Whether that is acceptable, and whether a command with an empty affected set may advance `access_verified_revision` in its own transaction, are reviewer questions.

### 3.3 Revocation

- Revocation safety is the same in both options and does not depend on the index. The revoking command removes the rows in its own transaction (§9.2), advances the revision (so the fence closes until M1 verifies), and SpiceDB still decides every result.
- A row left behind by a derivation defect over-includes; SpiceDB refuses the result, and the search fails as a divergence (fail closed, Critical alert, Tier 2 disabled).
- Stored results are invalidated by any authorization change (FD-2).
- **Neither option can disclose a revoked record.** The cost of a defect is availability: Tier 2 is disabled, the organization falls back to Tier 1 or gets `SEARCH_INDEX_NOT_READY`.

### 3.4 Completeness (missing authorized records)

| Source of a missing row | C | B |
|---|---|---|
| Derivation defect on a changed record | M1 before use | M1 before use |
| Affected-set defect (a changed record not reported to M1) | M2 | M2; **more affected-set classes**: content commits, approvals, withdrawals, reclassification and builder backfill change value rows |
| Authority-domain deletion of an access row after verification | M2 | M2 |
| Authority-domain change of a **value copy** in an access row (the value differs from the sealed posting) | Not applicable: C has no value copies, and values come from the Tier 1 postings covered by I-1, I-2, S-B and S-C | **Not detected by M1 or M2**, which compare principals, not values. Needs (a) re-evaluation of every predicate on the I-2-bound Tier 1 posting for every match (otherwise a tampered value could add a candidate that SpiceDB allows), and (b) a value-consistency sweep comparing value rows with sealed postings, or a seal on value rows (UF-16) |
| Coupling with the zed schema | Schema-hash guard | Same |

**Assessment:** C has strictly fewer completeness failure modes. Its derivation has fewer inputs (edges and share item scopes only), so M3, the affected-set property test, is smaller, and C adds no new value-integrity obligation.

### 3.5 Index integrity

- **C:** the new rows carry no plaintext and no seal. Tampering is limited to deleting rows (omission, caught by M2) or inserting them (addition, caught by SpiceDB per result, which fails the search). Plaintext exposure is unchanged from Tier 1 (R-D).
- **B:** the value table **copies approved plaintext once per principal**. This enlarges the R-D exposure inventory (WAL, backups, PITR, by a factor of the principal count) and needs the two value-integrity controls in §3.4. Rebuilding requires the Tier 1 postings, or verified BusinessDb bytes if postings are lost.

### 3.6 Quarantine

- Both use the same rules: a divergence first runs the S1 decisive verification (Q4); only if S1 is consistent is it treated as a Tier 2 failure.
- Neither option may ever suppress or downgrade an S1 quarantine (FD-4).
- The "Tier 2 disabled" state is **not yet specified** as a state machine, for either option (package UF-21). It needs to define:
  * who sets it;
  * how it is journaled;
  * how it relates to the search-index `Quarantined` and `Suspended` states;
  * that it is cleared only by a rebuild followed by verification.
- B has more ways to enter it (value-consistency failures).

### 3.7 PostgreSQL 18 planning and timing leakage

| Statement | C (C-S variant) | B |
|---|---|---|
| Caller enumeration or candidate scan | `principal = ANY(P)` on `(org, principal, form, record)`: bounded by authorized rows by key order (§2.1) | Value table: `(org, principal, field, value, record)` range scans with `principal = ANY(P)`: bounded by authorized rows by key order, including skip scans over value |
| Caller parity | Field-scope rows `(org, principal, record, field)`: principal-bound | Record table: principal-bound |
| Value predicates | Tier 1 record probe (no value index; PD-9 structure) | Value table, principal-bound; `contains` without GIN (§2.3) |
| Maintenance | Exact principal-leading keys (C-S); a record-leading index exists only in plain C, and is then the residual risk of §2.2 | Exact keys on both tables, or a record-leading index on the record table only (that table holds no values) |
| Verifier (M1, M2) | Exact keys (M1), ordered full scan (M2); neither caller-facing | Same |
| Structural test | Architecture test: every index on the access table leads with `(organization_id, access_principal_entity_id)`; no value column | The same for the value table; no value column on the record table; no GIN |
| Residual | Per-statement planner settings (no hash, merge, bitmap or sequential scan) are still needed for the Tier 1 probe, as today | Same, plus the planner's choice between per-condition scans and intersection order. The work stays bounded by authorized rows but varies with the caller's own distributions |

**Assessment:** both can be made **structural**, like PD-9, if the C-S keying rule is adopted, but only for C does that need no additional value-specific rule. A plan-shape test passing does not prove that every plan is safe (C-135 §2); the guarantee must rest on index structure.

### 3.8 Search complexity at 100K and 250K records

Let `|D|` be the caller's discoverable records, `k` the number of conditions, and `|M|` the authorized matches (at most the 10,000 result cap).

| | C | B |
|---|---|---|
| SQL | Enumeration O(\|D\|) index entries + k · \|D\| record probes | Σ over conditions of O(caller-accessible matching postings) + O(candidates) parity |
| SpiceDB | O(\|M\|) bulk checks (3 kinds), no `LookupResources` | Same |
| Example: owner of all 250K records, selective predicate (1 match) | 250K enumeration + 250K probes per condition | One short range scan |
| Example: 1,000-record caller | 1,000 probes (Tier 1 today: about 400 ms Authority-side p50 at 1,000 records, *including* `LookupResources` with page size 20, evidence §4) | Range scans |
| PB-09 "verified search under 3 s p95" | **Unknown for large `|D|`.** At a few to tens of µs per cached B-tree probe, 250K probes per condition is about 1 to 5 s **per condition**, likely over target for whole-organization callers. Probably within target for callers with \|D\| up to tens of thousands. **Unmeasured** | Bounded by matches, not by \|D\|; likely within target, except for non-selective `contains` |

**C is a strict subset of B.** B's record table plays the role of C's table, so implementing C first is not wasted if B is later needed: B adds the value table and its two integrity controls.

**Measurement needed before any choice:**
- the cost of the Tier 1 record probe at |D| of 10K, 50K, 100K and 250K, and of the enumeration scan;
- both with the per-statement planner settings in place;
- on PostgreSQL 18 with realistic statistics.

This needs the dataset of annex C. An earlier, cheaper signal is possible from a planner and cost microbenchmark on an isolated PostgreSQL 18 instance with synthetic rows. It would measure probe cost and plan shape only, would **not** go through the Authority, and could therefore **never** count as gate evidence. It needs owner approval as a measurement method (package TQ-9).

### 3.9 Migration and rollback

| | C | B |
|---|---|---|
| Schema | One additive table, forward-only migration (as PD-9) | Two additive tables |
| Trusted-path change | Derivation inside the five edge-writing commands, in their transactions, under the organization lock | The same, plus content commits, builder backfill and purge, and approval, withdrawal and reclassification |
| Kill switch | Stop serving Tier 2 (fallback). If derivation is switched off, the rows go stale; re-enabling requires a journaled rebuild and a full M2 before use | Same, for more derivation sites |
| Old Authority version | An old version without derivation would write edges without access rows or pending verifications: **must be fenced** (extend §12.4's minimum safe version to Tier 2; UF-20) | Same |
| Derivation failure | Aborts the security command: a Tier 2 defect then blocks security commands for the organization (an availability risk on the trusted path). The dark stage must measure it before enablement | Same, on more paths, including content commits (record saves) |

### 3.10 Operational cost

Illustrative and unmeasured: 250K records, 10 approved fields, 2.5 principals per record, 20 % of records with a partial share covering 3 fields, about 150 bytes per row with indexes.

| | C | B |
|---|---|---|
| Rows | ≈ 250K × 2.5 + 50K × 3 ≈ 0.8M (≈ 120 MB) | C, plus 250K × 10 × 2.5 ≈ 6.3M value rows (≈ 1 GB); more with multi-select |
| Writes per ownership transfer of a record with 50 descendants | ≈ 51 × principals | × fields as well |
| Writes per field approval | None | O(form records × principals) |
| Organization-lock hold added (R-37) | Small, per edge command | Larger, including content commits |
| M1 and M2 SpiceDB calls | Same in both: driven by the records verified (annex E) | Same, plus every record of the form on approval changes |

### 3.11 Tier 1 fallback and disablement

- Both: when Tier 2 is disabled, the organization uses Tier 1 within its 50,000 ceiling, or gets `SEARCH_INDEX_NOT_READY`.
- **C** uses the same probe as Tier 1 and changes only how the population is obtained. The differential check is therefore a **comparison of populations**, `D` from access rows against `LookupResources`, independent of predicates. This simplifies M4, online sampling (annex B) and the M2 user differential, and makes a difference easier to localize.
- **B** must also compare per-predicate candidate and result sets.

## 4. Summary for the reviewer (not a selection)

| Criterion | C (C-S) | B |
|---|---|---|
| Tenant isolation | = | = (twice the surface) |
| Freshness | Better: no pending verifications from approval changes | Approval changes put every record of the form behind the fence |
| Revocation | = | = |
| Completeness | Better: fewer derivation inputs, no value integrity | Needs predicate re-evaluation and a value-consistency control |
| Index integrity and plaintext | Better: no new plaintext | Plaintext copied once per principal |
| Quarantine | = | More entry paths |
| Timing (PostgreSQL 18) | Structural with one rule | Structural with three rules (principal-leading, no value on the record table, no GIN) |
| Complexity at 250K | **Weaker for large \|D\|** (O(\|D\|) probes); unmeasured | Better (O(matches)) |
| Migration and rollback | Smaller trusted-path change | Larger, including record saves |
| Operational cost | Lower | Higher |
| Fallback and differential | Simpler | Per-predicate comparison |

**The design author's reading (C-138; superseded as the review preference by the owner's C-139 direction, see §15; the analysis stands, subject to the corrections in §6.2):**
- C (preferably C-S) is the safer design.
- B is justified only if measurement shows that C cannot meet the PB-09 search target for the expected `|D|` at 100K and 250K.
- Since C is a subset of B, the decision can follow measurement.

The open questions for the independent reviewer are package TQ-1 to TQ-12. **No option is selected.**

## 5. Option A (rejected unless independent evidence establishes its security)

A keeps one table with value-keyed indexes and a record maintenance index, and depends on the planner always choosing principal-leading plans for caller statements.

PD-9 demonstrated, on this project's PostgreSQL 18 stack, that the planner will use a skip scan over a value-keyed index for a record-scoped probe when statistics change, and that PostgreSQL 18 has no setting to prevent it. A also includes the §9.2 GIN index, which does not bound cost by principal (§2.3).

A could be reconsidered only with independent evidence that every caller-facing statement on the table is safe for every plan PostgreSQL 18 can produce, not merely for the plans observed. The design author does not know how such evidence could be produced. **Recorded as rejected.**

---

# C-139 refinement

## 6. Scope, requirements and corrections

### 6.1 Requirements the architecture must meet (C-139 §1)

- Millions of records across the platform.
- Potentially hundreds of thousands of records in one organization.
- Large numbers of users and teams.
- Direct record sharing, team-based sharing and field-specific permissions.
- Multiple authorization paths to the same record or field item.
- Frequent sharing, membership and permission changes.
- Revocation without stale search authorization.

The PD-9 and R-38 security requirements, INV-T2-01 to INV-T2-11 and the existing 100K and 250K acceptance gates are unchanged. No new mandatory performance threshold is introduced (C-139 §4).

### 6.2 Corrections to the C-138 sections (found while refining)

| C-138 statement | Correction | Reason |
|---|---|---|
| §3.2, §3.10 and annex E §5: under B, a field approval or withdrawal puts **every record of the form** behind the M1 fence (O(form records) `LookupSubjects` calls) | **Not true for the split design (B2, §10).** Approvals change no access row. Value rows are verified by re-derivation from verified access rows and sealed Tier 1 postings, not by SpiceDB. The field becomes queryable through Tier 2 only when its value-row coverage is complete, as Tier 1 requires coverage `Complete` for postings. | The M1 oracle compares **principals**; value rows add no principal (finding UF-24) |
| §1, §3.10: B copies values "once per principal" with "2.5 principals per record" | Duplication is per **access path principal of each item** (effective owner, plus share recipients covering that item), with teams never expanded (§10.2). Cardinality model in §13 | Refined from the zed schema and `CommandPlanner` at `f7886b0` |
| §3.7: B's per-condition intersection order is a residual planner choice | Removed by rule SAFE-3 (§10.5): one single-table statement per condition, intersection in the application | Plan choice is the channel for statistics leakage (UF-22) |
| §2.5: command catalogue | Restated as a complete per-command matrix over all contract kinds (§8) | C-139 §3 |

## 7. The access index (common to B1, B2 and C)

The access index is C's table (§1): the same structure serves as Option C on its own and as the access half of B1 and B2.

**Rows** (proposed logical kinds; column names follow the multiword rule and are illustrative):

| Row kind | Meaning | Derived from (canonical edges, `authority.schema.zed`) |
|---|---|---|
| `EffectiveOwner` | Principal p is in `form_record#effective_owner` of record r: it discovers r and reads every item | `owner` (user or `team#participant`), or `parent → effective_owner` for a child |
| `ShareRecipient` | p discovers r through share s (`form_record#discover` through `shared_via → read`) | `form_record#shared_via`, `record_share#reader`, `#contributor` or `#manager` |
| `FieldItemRead` | p reads field item i of field f of r through share s (`field_item#read` through `shared_via → read`) | `field_item#shared_via` with the share's role edge |

**Columns (illustrative):**
- `organization_id`;
- `access_principal_entity_id`, `access_principal_type_name` (user or team);
- `form_definition_id`, `form_record_id`;
- `access_path_kind_name`;
- `record_share_id` (shares only);
- `field_definition_id` and `manifest_item_id` (item rows only);
- `access_derivation_version`, `derived_authorization_revision`.

**Index:** one, principal-leading: `(organization_id, access_principal_entity_id, form_definition_id, form_record_id, …)`. There is no record-leading index (variant C-S, §1). Maintenance addresses rows by full key, computed from the canonical edges before and after the command, and **never reads a Tier 2 table by record**.

**Principal model:**
- A principal is a user or a team. Teams are **never expanded per member**: `team#participant = member` is flat (the schema has no nested teams), so membership changes write no access rows.
- The caller's principal set `P` = {caller} ∪ {teams with a `member` edge to the caller} is read from canonical edges in the candidate transaction at the pinned revision (design §9.3 step 2).
- Organization roles (`organization_owner`, `system_administrator`, `designer`, `security_auditor`) and form relations (`designer`, `form_user`, `field_searcher`) confer **no** `discover` and produce no rows. `field_searcher` (`query_fields`) remains a per-search check at the pin.

**Authority:** the access index **never grants**. SpiceDB decides every disclosed result at the S1-verified pin (INV-T2-01). The index only nominates candidates, and the M1 fence (§12) makes Tier 2 usable only when the nomination is complete.

## 8. Command-impact matrix (contract enum at `f7886b0`)

Sources:
- `AuthorizationContracts.cs` (`SecurityCommandKind`), and `CommandPlanner*.cs`, which dispatches every kind except `UpdateShareScope`;
- the edges each handler adds or removes, checked by the design author.

No Authority path outside these handlers writes `owner`, `parent`, `shared_via`, a share role or `member` edges (search of `src/authority` at `f7886b0`; to be confirmed by the reviewer, E-2, and enforced by the INV-T2-03 architecture test).

| Command | Status | Edges written | Access rows | B2 value rows | B1 canonical rows | M1 affected set |
|---|---|---|---|---|---|---|
| `RegisterTeam` | Implemented | None (team resource) | None | None | None | ∅ |
| `AddTeamMember` | Implemented | `team#member` | **None** (team-level principals) | None | None | ∅ (`P` changes at the pin) |
| `RemoveTeamMember` | Implemented | removes `team#member` | **None** | None | None | ∅ (`P` changes at the pin; SpiceDB per result) |
| `RegisterFormDefinition` | Implemented | `organization`, `designer`, `form_user` | None | None | None | ∅ |
| `UpdateFormUsers` | Implemented | `form_user` (`create_record` only) | None | None | None | ∅ |
| `RegisterRecord` | Implemented | `organization`; `owner` (user or team) **or** `parent` | `EffectiveOwner` row(s) for r (a child inherits its ancestor's owner principal) | None (no content yet) | None | {r} |
| `TransferOwnership` | Implemented | removes and adds `owner` | Replace r's `EffectiveOwner` rows **and those of every descendant** (children derive `effective_owner` through `parent`) | Replace the old owner's value rows with the new owner's, for r and descendants, every approved item | None | {r} ∪ descendants |
| `CreateShare` | Implemented | `form_record#shared_via`, share role → user or `team#participant`, `field_item#shared_via` per item (explicit allow-list, 1–500 items of the **accepted** manifest) | + 1 `ShareRecipient` row; + 1 `FieldItemRead` row per item | + value rows for the shared items of approved fields | None | {r} |
| `UpdateShareScope` | **Declared, not implemented** (`COMMAND_KIND_NOT_IMPLEMENTED`) | — | Must have a rule **before** it is implemented (architecture test fails otherwise) | — | — | — |
| `RevokeShare` | Implemented | removes every edge of and to the share | − the share's `ShareRecipient` and `FieldItemRead` rows | − the share's value rows | None | {r} |
| `GrantSystemRole`, `RevokeSystemRole` | Implemented | `organization#system_administrator` or `#security_auditor` | None (no `discover`) | None | None | ∅ |
| `UpdateMandatoryPolicy` | Implemented | No access edges | None (policy is a per-search check) | None | None | ∅ |
| `PublishFormSecurityRevision` | Implemented | No edges (field protection requirement or release resources) | None | Follows the Tier 1 posting changes it causes (builder) | Same | ∅ |
| `UnlinkChildRecord` | Implemented | removes `parent`; adds `owner` = the acting user | Replace the subtree's `EffectiveOwner` rows (ancestor's owner → actor). The child's own shares are unaffected; the parent's shares never reached the child's fields (zed comment) | Replace the subtree's owner-path value rows | None | {child} ∪ child's descendants |
| `ReclassifyField` | Implemented | No edges (reclassification resource) | None | Builder purge or backfill of the field's value rows, every record of the form | Same | ∅ (coverage gating, §11 item 12) |
| `RegisterPublishedFormVersion` | Implemented | No edges | None | None | None | ∅ |
| `ApproveFieldSearch` | Implemented | No edges (approval resource) | None | Builder backfill: every record of the form × principals reading the item | Builder backfill × 1 | ∅ (coverage gating) |
| `WithdrawFieldSearch` | Implemented | No edges | None | Builder purge | Builder purge | ∅ |
| `UpdateFieldSearchers` | Implemented | `form_definition#field_searcher` | None (`query_fields` checked per search) | None | None | ∅ |
| `GrantOrganizationRole`, `RevokeOrganizationRole` | Implemented | `organization#organization_owner` or `#designer` | None (no `discover`) | None | None | ∅ |
| `ProposeOrganizationOwnerChange` | Implemented | None | None | None | None | ∅ |
| `DisableIdentityBinding`, `EnableIdentityBinding` | Implemented | None (binding state; sessions revoked; edges kept) | None. A disabled user cannot search (binding and session checks precede the search) | None | None | ∅ |
| `RequestOrganizationMembership`, `CancelOrganizationMembershipRequest`, `ReplaceOrganizationMembershipRequest` | Implemented | None | None | None | None | ∅ |

**Operations that are not security commands:**

| Operation | Effect | Access rows | B2 value rows | B1 canonical rows |
|---|---|---|---|---|
| Content commit (W1, `ContentRevisionCoordinator.AdvanceForCommitAsync`) | Tier 1 postings derived inline in the commit transaction. Owners may add or remove items; share editors only change bound existing items (`SHARE_ITEM_ADDED_OR_REMOVED`) | **None.** Share scopes reference manifest item ids fixed at share time. A removed item leaves a `FieldItemRead` row that matches nothing; a new item is uncovered by default (CLAUDE.md §4H) | Changed approved items × principals reading them; `field_item_count` (§10.3) updated on every row of a field whose item count changed | Changed items × 1 |
| Record lifecycle (Draft, Submitted, Archived) | Signed header content. **There is no record-deletion command, and no producer of a `Deleted` record status at this baseline** | None | None (status is a metadata filter on Tier 1 entries) | None |
| Builder backfill, purge and derivation-version rebuild | Postings | None | Follow the postings | Follow the postings |
| Organization (S1) or search-index quarantine and suspension | State only | None | None | None |
| SpiceDB schema change | Coupling guard disables Tier 2 | Rebuild | Rebuild | — |

**Consequences:**
1. Only the five edge-writing commands (`RegisterRecord`, `TransferOwnership`, `CreateShare`, `RevokeShare`, `UnlinkChildRecord`) change access rows. This restates UF-14 against the code and resolves it **as a proposal**: the affected-set rule is per command kind, and an architecture test fails when any implemented kind lacks one.
2. Every other implemented kind has an empty Tier 2 affected set. That includes the frequent membership, role and form-user changes. This is what makes the fence rule F-1 (§12) possible.
3. A future record deletion, purge (for example PB-13 retention or offboarding) or `UpdateShareScope` implementation needs its own rule before it ships.

## 9. Option B1: canonical value index with authorization-constrained execution

**Structure.** Approved values stored once per (record, field, item), with a value-keyed index `(organization_id, form_definition_id, field_definition_id, value, form_record_id)`. Candidates are then intersected with the access index.

Tier 1's postings already are canonical per-record, per-field values. B1 would add the value-keyed index on them, or on a copy. The execution strategies are:

| Strategy | Mechanism | Timing assessment | Result |
|---|---|---|---|
| **B1-V** value-first | Range-scan the value index across the organization, then keep the records found in the caller's access rows | The scan reads postings of records the caller cannot discover. Its work, and any limit or cap refusal, grows with the **organization-wide** number of matching values. That is the PD-9 class (value distribution of unauthorized records), and it breaks INV-T2-07 / IX-33 | **Unsafe** |
| **B1-J** join written access-first | One statement joining the access index to the value index, written to drive from the principal | With both a principal-leading and a value-leading index present, the planner may choose either side as outer, and PostgreSQL 18 may skip-scan the value index for record-driven probes (PD-9, observed on this stack). The plan, and therefore the timing, depends on statistics, not on the query text. No PostgreSQL 18 setting prevents skip scans | **Unsafe** (the structure permits value-driven plans) |
| **B1-R** record-driven | Enumerate `D` from the access index, then probe values per record on a record-leading key | Safe, but this is **Option C** using Tier 1's postings; the value-keyed index must not exist (PD-9) | Equivalent to C |
| **B1-G** access-class partitioning | Values keyed by `(organization, access class, field, value, record)`, where an access class is the exact set of principals reading an item; the caller scans every class containing any p ∈ P | Bounded by authorized rows only if every row of a scanned class is readable by the caller, so classes must be defined per item read set. The number of classes approaches the number of distinct share sets: up to \|D\| for a heavily shared organization, so the cost degenerates to C. Every share change moves the record's rows between classes. The class table is itself an access index, and class sizes are organization-wide statistics | Not recommended: the complexity of B2 with the cost of C at high sharing |
| Padded or constant-work execution | Fixed work budgets, dummy reads | Not achievable on PostgreSQL: buffer-cache state, visibility map, heap fetches and TOAST all vary | **Rejected** |

**Specific investigations requested by C-139 §2:**

| Concern | B1 finding |
|---|---|
| Unauthorized-value-dependent timing | Present in B1-V and B1-J |
| PostgreSQL 18 skip scans | B1-J: value-keyed index probed by record, the PD-9 failure |
| Planner choices involving unauthorized records | Join order and index choice are driven by organization-wide statistics |
| Cardinality and statistics leakage | Value histograms and most-common values cover unauthorized records (UF-22) |
| Cross-organization access paths | Keys lead with `organization_id`, so data is isolated, but table statistics span organizations unless partitioned (UF-22) |
| Shared or partially authorized fields | Canonical rows include items the caller cannot read; filtering them costs time proportional to their count |

**Conclusion (C-139 §2 instruction).** Canonical value indexing with a later authorization intersection **cannot satisfy** the approved timing-security invariants (PD-9, R-38, INV-T2-06, INV-T2-07) on PostgreSQL 18 in any value-first or mixed-index form. Its only safe execution, record-driven, is Option C. **B1 is explicitly rejected as a distinct architecture.** Canonical per-record, per-field values remain where they are, as Tier 1's sealed postings: the source of truth for both C and B2. The safer access-scoped alternative is B2.

## 10. Option B2: access index plus access-scoped value index (controlled duplication)

### 10.1 Structure

1. **Access index:** §7 (unchanged; it is C's table).
2. **Access-scoped value index:**
   - one table per Tier 1 value type (string, integer, decimal, date, date-time, boolean, option), mirroring the posting tables;
   - one row per **(principal p, record r, field f, item i)** for every approved-field posting item i that p reads through a single path (effective owner, or a share covering i);
   - columns (illustrative): `organization_id`, `access_principal_entity_id`, `form_definition_id`, `field_definition_id`, the typed value, `form_record_id`, `manifest_item_id`, `indexed_operation_id` (binds the row to the Tier 1 entry and posting), `field_item_count` (§10.3), `value_derivation_version`;
   - **one index per table:** `(organization_id, access_principal_entity_id, form_definition_id, field_definition_id, value, form_record_id, manifest_item_id)`;
   - no record-leading index, no other value-leading index, no GIN.
3. **Maintenance** derives value rows from:
   - the canonical edges, read by record on the edges table, which is never caller-facing;
   - the record's postings at the indexed operation.

   It writes them in the same transaction as the postings (W1 content commit, builder) or as the edge change (the five edge-writing commands). It never reads Tier 2 tables by record.

### 10.2 Controlled duplication rules

| # | Rule | Effect |
|---|---|---|
| D-1 | Teams are principals; members are never expanded | Membership changes write no value rows; a team of 500 costs one row per item, not 500 |
| D-2 | Approved searchable fields only (Public/Internal with an Active approval; PB-04). Confidential and Restricted values are never copied | Duplication bounded by approved items, typically far fewer than the 250-field maximum |
| D-3 | One row per **path principal of an item**: effective owner (one principal, or the ancestor's for a child) plus each share recipient covering the item | No rows for organization roles, `form_user` or `field_searcher` |
| D-4 | No value rows for a share that covers no item of an approved field | Discover-only shares cost access rows only |
| D-5 | **Fan-out cap (proposed, for the owner and reviewer):** if more than K principals read one item (K configured, for example 64), the record is marked *high-fan-out*. Its value rows are not written, and its candidates come from the C path: record-driven probes over the caller's high-fan-out records, enumerated principal-bound from the access index | Bounds storage for records shared with many principals; the cost of those records is O(caller's high-fan-out records), all authorized (§13.3) |
| D-6 | Per-organization partitions (LIST by `organization_id`) for both Tier 2 tables (proposed) | Statistics, visibility state, vacuum and rebuild per organization (UF-22); a whole-organization drop for PB-13 offboarding |

### 10.3 Evaluable parity under multiple paths (UF-23)

**Tier 1 rule (R1 §E.3).** A share-only record is in scope for a predicate on field f only if **every** posting item of f is readable by the caller.

**A naive B2 fails parity.** A caller may read item 1 of a multi-select through team A's share and item 2 through a direct share. No single principal reads the whole field, so "write value rows only when one principal reads the whole field" would **omit** a record that Tier 1 and SpiceDB (a union over paths) include.

**B2 rule:**
1. Value rows are written per (principal, item). Each row carries `field_item_count`, the number of posting items of f in r at the indexed operation.
2. For a candidate (r, f), if any p ∈ P has an `EffectiveOwner` row for r, the record is evaluable for f.
3. Otherwise, count the **distinct** items of f in r readable through P: exact-key, principal-bound reads of the `FieldItemRead` rows for (p, r, f), p ∈ P. Compare the count with `field_item_count`. If equal, the record is evaluable; if not, it is excluded.
4. The work per candidate is O(|P| + readable items). It never reads an item the caller cannot read, and it does not depend on predicate values.

This also removes, for Tier 2, the Tier 1 dependence on unreadable items noted in UF-25.

### 10.4 Query (B2)

1. Q1 (organization gate and S1), projection state, S1 pin, and predicate authorization at the pin (`query_fields`, policy, Active approvals, operators, coverage): **unchanged from Tier 1**.
2. `P` from canonical edges in the candidate transaction. The transaction requires `authorization_revision` = the pinned marker revision = `projection_applied_revision`.
3. The fence (§12). Not usable ⇒ Tier 1 within its ceiling, otherwise `SEARCH_INDEX_NOT_READY`.
4. Per condition: **one statement on one value table**, with `organization_id = @org`, `access_principal_entity_id = ANY(@P)`, form, field and the value predicate. It returns (record, item, principal), bounded by the caller's accessible rows.
5. Evaluable parity per candidate (§10.3).
6. Intersection of the conditions in application memory, bounded. Limit refusals depend only on caller-owned quantities.
7. Metadata filters and sort through Tier 1 entries, by explicit candidate record ids.
8. **Predicate re-evaluation on the I-2-bound Tier 1 posting** of every match (UF-16 (a)). A value row only nominates; the sealed posting decides. A mismatch is a divergence: the search fails, followed by Q4.
9. SpiceDB decisions on every match at the pin (design §9.3 step 4). A SQL-yes / SpiceDB-no divergence fails the search (`SEARCH_INDEX_INCONSISTENT`).
10. I-1, I-2, seals, S1 verify, snapshot and evidence, as in Tier 1.

**Operators.**
- `equals`, ranges, `before`/`after`/`between`, `is`, `includes`, `isTrue`/`isFalse`: B-tree ranges on the principal-leading index.
- `startsWith`: an ordinal prefix range.
- **`contains` has no safe index in any option** (UF-15). Proposed:
  * as a secondary condition, evaluated on the postings of candidates from other conditions;
  * as the only condition, by a principal-bound scan of the caller's value rows of that field with a substring filter, O(\|D_f\|) like C.

  A principal-leading B-tree trigram table, keyed `(organization, principal, field, trigram, record)`, is possible but multiplies storage by the value length. It is **deferred** (TQ-17).

### 10.5 Conditions under which B2 is safe

| # | Condition |
|---|---|
| SAFE-1 | Every Tier 2 table has exactly one index, which leads with `(organization_id, access_principal_entity_id)`. No record-leading, other value-leading or GIN index (architecture test, extending INV-T2-06) |
| SAFE-2 | Every caller-facing Tier 2 statement reads one table and binds `organization_id =` and `access_principal_entity_id = ANY(P)` as index conditions. Per-statement settings: no sequential, bitmap, hash or merge plans; no parallel workers; no JIT. Plan-shape tests across distributions and statistics serve as regression evidence only |
| SAFE-3 | One statement per condition; intersection in the application; no SQL join between Tier 2 tables in a caller statement. With one index and no join, the planner has no plan choice through which statistics could act (UF-22) |
| SAFE-4 | Per-organization partitions (D-6), or another measure the reviewer accepts, so one organization's statistics and visibility state never shape another organization's plans |
| SAFE-5 | Value rows never authorize: SpiceDB decides every result; predicate re-evaluation on the sealed posting; divergence fails closed |
| SAFE-6 | Parity through `field_item_count` and `FieldItemRead` rows (§10.3) |
| SAFE-7 | Value consistency: value rows are derived in the posting or edge transaction (M1-V: rows written equal the derivation), and a daily M2-V sweep re-derives them from sealed postings and verified access rows (no grace; annex D §4A) |
| SAFE-8 | Each value row binds `indexed_operation_id` to the Tier 1 entry. A row whose operation differs from the entry's is a divergence and is never used |
| SAFE-9 | The plaintext inventory (R-D) records the copies: approved Public/Internal values only, multiplied by the duplication factor; WAL, backup and PITR retention; Canadian residency (PB-10) |
| SAFE-10 | Kill switch, minimum-safe-version fencing (UF-20) and the Tier 2 disabled state machine (UF-21) cover value rows |
| SAFE-11 | The FD-5 boundary is decided by the owner: B2 adds writes inside Tier 1's writer transactions (W1, builder), although Tier 1 query behaviour is unchanged (UF-27) |

## 11. Sharing and authorization correctness (C-139 §3)

| # | Case | Representation | B2 | C | Residual |
|---|---|---|---|---|---|
| 1 | Direct record share | `ShareRecipient` (user) + `FieldItemRead` per shared item | + value rows for shared approved items | Rows only; values from postings | A share never confers `form_record#read` (zed), so share-only parity always applies |
| 2 | Team share | The same, keyed by the team | Same | Same | Members reach it through `P` at the pin |
| 3 | Several teams granting the same record | One row set per team | Duplicates removed by record; parity counts **distinct** items across `P` (§10.3) | Same | Union semantics, matching SpiceDB |
| 4 | Direct and team access overlapping | One row set per path | Revoking one path leaves the other's rows; access continues, as in SpiceDB | Same | — |
| 5 | Field-level grants and restrictions | Positive grants only: explicit item allow-lists, no deny rules in the schema. Restrictions are classification (unapproved fields are never indexed), mandatory policy and `query_fields`, all checked per search | Partially readable fields are excluded by parity | Same | — |
| 6 | Permission changes | No share-role change command (`UpdateShareScope` unimplemented). A role change is revoke plus create. A manager's delegation is a new bounded `CreateShare` | Rows per command | Same | A rule is needed before `UpdateShareScope` ships |
| 7 | Team membership changes | No rows change | None | None | Revocation through `P` at the pin; F-1 keeps Tier 2 available (§12) |
| 8 | Ownership transfer | Replace owner-path rows of r and descendants in the command transaction | + value rows | Rows only | Affected set includes descendants (M3) |
| 9 | Share revocation | Delete the share's rows in the command transaction; the fence closes; stored results are invalidated (FD-2) | + value rows deleted | Same | **Cannot disclose:** SpiceDB per result at the pin |
| 10 | Record creation and deletion | `RegisterRecord` rows; values follow content. No deletion command; Archived is a metadata filter | — | — | A future deletion or purge command needs a rule |
| 11 | Child unlinking | Subtree owner-path rows replaced (ancestor's owner → actor); the child's own shares unaffected | + value rows | Rows only | — |
| 12 | Approval and withdrawal | No access rows. B2 value rows backfilled or purged by the builder; the field is used by Tier 2 only at complete value coverage | Coverage gating, no SpiceDB fence (UF-24) | Nothing to do | The coverage state for value rows must mirror Tier 1's |
| 13 | Organization and index quarantine | No rows change | Tier 2 not served; S1 first (Q1, Q4) | Same | Unchanged from FS-15, FS-16 |
| 14 | SpiceDB schema change | Coupling guard (hash) | Tier 2 disabled; rebuild; full M2 (E, S, V) | Same, without V | — |

**Freshness.** No access or value row can authorize a search: SpiceDB decides every result at the S1-verified pin.
- **Over-inclusion** (a stale row) is caught per result and fails closed.
- **Under-inclusion** is prevented by the M1 fence before use, and bounded afterwards by M2.

## 12. Fence availability (UF-18): proposed refinements for the reviewer

| # | Rule | Safety argument | Effect |
|---|---|---|---|
| F-1 | A command with an **empty Tier 2 affected set** (§8) advances `access_verified_revision` in its own transaction, if and only if no pending verification exists for the organization | Its rows are unchanged; `P` is resolved at the pin; SpiceDB decides each result | Membership, role, form-user, searcher and policy changes no longer close the fence. **Recommended** |
| F-2 | Inline verification: after a command becomes Effective, the Authority verifies its affected records (annex E) at the command's revision before advancing `access_verified_revision`. A search finding the fence closed **waits a bounded time** (for example up to 1 s) before falling back | Same oracle as M1, earlier | Fence lag ≈ projection delay + 2(1 + F) lookups. The wait reveals that **some** security command occurred recently in the organization: an activity signal of the same class as today's organization-lock waits (R-33, R-37). Reviewer decision (TQ-15) |
| F-3 | Per-record exclusion: serve Tier 2 while pending records exist, by checking pending records through SpiceDB | Correct, but the cost grows with the **organization-wide** pending count: an activity channel, needing a cap | Not recommended without the reviewer's view (TQ-7) |

Without F-2 or F-3, sustained record creation or sharing still keeps Tier 2 unused between verifications. That is an availability residual (FS-08), not a disclosure risk.

## 13. Scaling model (C-139 §4)

**Nothing in this section is measured.** Measured inputs are named as such. Everything else is an assumption or an extrapolation, to be replaced by annex C and PERF-01 measurements.

### 13.1 Notation and assumptions

| Symbol | Meaning | Base (assumed) | Heavy sharing | High fields |
|---|---|---|---|---|
| N | Records per organization | 100K / 250K / 1M | — | — |
| F_a | Approved searchable fields per form | 10 | 10 | 50 |
| k̄ | Posting items per approved field (multi-select > 1) | 1.1 | 1.1 | 1.1 |
| s̄ | Shares per record (one recipient each) | 0.3 | 3 | 3 |
| φ | Fraction of approved items a share covers | 0.6 | 1.0 | 1.0 |
| Row size, with its single index | Access ≈ 0.15 KB; value ≈ 0.2 KB (assumption, not measured) | | | |

Measured references:
- 58 KB per record across all planes on the security-test stack (annex C §5.4);
- the R-37 command and lock costs (evidence §12.4).

### 13.2 Cardinality and storage per organization

Formulas:
- access rows A = N (1 + s̄) + N s̄ φ F_a k̄;
- B2 value rows V = N F_a k̄ (1 + s̄ φ);
- B1 canonical rows = N F_a k̄, which Tier 1 postings already hold;
- C adds A only.

| Case | Per record A / V | 100K: A / V | 250K: A / V | 1M: A / V |
|---|---|---|---|---|
| Base | 3.3 / 13.0 | 0.33M (≈ 50 MB) / 1.3M (≈ 0.26 GB) | 0.82M (≈ 0.12 GB) / 3.2M (≈ 0.65 GB) | 3.3M (≈ 0.5 GB) / 13M (≈ 2.6 GB) |
| Heavy sharing | 37 / 44 | 3.7M (≈ 0.55 GB) / 4.4M (≈ 0.9 GB) | 9.3M (≈ 1.4 GB) / 11M (≈ 2.2 GB) | 37M (≈ 5.6 GB) / 44M (≈ 8.8 GB) |
| High fields | 169 / 220 | 17M (≈ 2.5 GB) / 22M (≈ 4.4 GB) | 42M (≈ 6.3 GB) / 55M (≈ 11 GB) | 169M (≈ 25 GB) / 220M (≈ 44 GB) |

**Platform:** millions of records across organizations. For example, 25 organizations (PB-09) averaging 100K records each is 2.5M records; at the base case that is about 8M access and 32M value rows, ≈ 7.7 GB in total. Storage grows linearly in N, F_a and s̄. Teams add nothing per member (D-1).

**Without D-1,** a team of 100 members owning 30 % of records would multiply those rows by 100.

**The heavy and high-field cases** are where D-5 (fan-out cap) and D-6 (partitions) matter.

### 13.3 Update and permission-change cost (row writes per operation)

| Operation | C (access rows) | B2 (access + value rows) |
|---|---|---|
| `AddTeamMember` / `RemoveTeamMember` | 0 | 0 |
| `CreateShare` or `RevokeShare`, whole record, F_a = 10 | 12 | 23 |
| `TransferOwnership` with 50 descendants, F_a = 10 | ≈ 102 (delete + insert) | ≈ 1,224 |
| Content commit changing 3 approved items, 2 path principals per item | 0 | ≈ 6 (+ `field_item_count` updates if a count changes) |
| `ApproveFieldSearch` on a 250K-record form | 0 (Tier 1 backfills ≈ 275K postings in any case) | + ≈ 325K value rows (base), in bounded builder transactions |
| Content commit touching every approved item of a record readable through 201 path principals (owner + 200 whole-record shares; D-5 off) | 0 | ≈ 201 × F_a k̄ ≈ 2,200 |
| Creating those 200 whole-record shares | 200 × 12 = 2,400 | 200 × 23 = 4,600 |

All writes occur under the organization lock in their command or commit transaction (the R-37 interaction). B2 adds work to content commits, which C does not. Both must be measured at IX-5a before enablement.

### 13.4 Revocation propagation

- In every option, a revoking command removes its rows in its own transaction and advances `authorization_revision`. The fence closes until M1 verifies (sub-second with F-2; verifier lag otherwise).
- **Disclosure safety does not depend on propagation:** SpiceDB decides each result at the pin, and stored results are invalidated by any authorization change (FD-2).
- Team-membership revocation propagates immediately through `P` (no rows).

### 13.5 Query complexity

Let |P| be the caller's principals (1 + teams; assume ≤ 50, with a configured cap), |D| the discoverable records, and |M| the authorized matches (capped at 10,000).

| Caller | C | B2 |
|---|---|---|
| Small \|D\| (≤ 1,000) | ≤ 1,000 probes per condition: Tier 1 today ≈ 400 ms Authority-side p50 at 1,000 records, including `LookupResources` (measured, evidence §4) | \|P\| index descents + caller's matches: estimated in milliseconds |
| Medium \|D\| (10K–50K) | 10K–50K probes per condition: estimated 0.1–1 s | Bounded by matches |
| Very large \|D\| (250K) | Estimated 1–5 s **per condition** | Bounded by matches; a non-selective condition reads up to the caller's \|D_f\| rows |
| Whole-organization \|D\| (1M) | Estimated 4–20 s per condition: likely beyond the PB-09 3 s target (a target, not a new threshold) | Bounded by matches |
| `contains` only | O(\|D\|) probes | O(\|D_f\|) principal-bound scan (§10.4) |

SpiceDB per search, in both options: no `LookupResources`; `CheckBulk` on ≤ |M| matches (`discover`, `read`, and `field_item#read` for share-only matches); the S1 verify.

### 13.6 Integrity verification and sweeps

- **M1 (SpiceDB, access rows only, both options):** 2 (1 + F) lookups per affected record (annex E §2). F counts either every item (an independent oracle) or only the items with `shared_via` edges (edge-informed, TQ-8). Base case: F ≈ 11, or ≈ 2 edge-informed.
- **M1-V (B2):** none at run time. Value rows are derived in the writing transaction, and the M3 property tests and IX-5a golden vectors check the derivation.
- **M2-E (both):** re-derive access rows from edges. SQL only: A rows per cycle.
- **M2-V (B2):** re-derive value rows from postings and access rows. SQL only: V rows per cycle (13M at 1M records, base). Estimated at tens of minutes per day in bounded transactions; **unmeasured**.
- **M2-S (both):** N × 2 (1 + F) / (7 × 86,400) lookups/s.

| N | F = 2 (edge-informed) | F = 11 (independent) | F = 34 (heavy sharing) |
|---|---|---|---|
| 250K | ≈ 2.5/s | ≈ 10/s | ≈ 29/s |
| 1M | ≈ 10/s | ≈ 40/s | ≈ 116/s |

### 13.7 Partitioning

- **D-6 (LIST by organization)** is proposed for both Tier 2 tables, for statistics isolation (UF-22), per-organization rebuild and drop, and offboarding (PB-13).
- With a single index and runtime pruning on `organization_id`, generic plans prune at execution.
- Sub-partitioning (for example by hash of principal) would serve maintenance only (vacuum, sweep batches) in organizations with tens of millions of rows. It is not needed for query safety.
- Partition count follows the organization count; planning overhead at hundreds of partitions is to be measured.

## 14. Final comparison (C-139 §6)

| | **B1** canonical values + authorization intersection | **B2** access index + access-scoped values | **C** access only + Tier 1 probe |
|---|---|---|---|
| Security feasibility | **Not feasible** in value-first or mixed-index form (§9); safe only record-driven, where it becomes C | Feasible under SAFE-1 to SAFE-11 | Feasible under the C-S rule |
| Timing isolation | Fails: work depends on unauthorized values (PD-9 class) | Structural: one principal-leading index, one statement per condition; residual planner and statistics influence closed by SAFE-3 and SAFE-4 (UF-22) | Structural; the Tier 1 probe keeps its per-statement settings |
| Authorization correctness | n/a | SpiceDB per result; parity through `field_item_count` (§10.3) | SpiceDB per result; Tier 1 parity (with UF-25) |
| Completeness verification | n/a | M1 (SpiceDB) + M1-V/M2-V (re-derivation) + M2-E/M2-S; predicate re-evaluation | M1 + M2-E/M2-S |
| Revocation | n/a | Rows removed in the command; cannot disclose | Same |
| Storage growth | ≈ postings (no new values; the index adds ≈ 1×) | V = N F_a k̄ (1 + s̄ φ), plus A; bounded by D-1 to D-5 | A only |
| Query scaling | n/a | O(caller's matches) | O(\|D\|) per condition |
| Maintenance cost | Builder only | Edge commands, content commits, builder; M2-V | Edge commands only |
| Implementation complexity | n/a | Highest: two structures, the parity rule, value consistency, Tier 1 writer changes (UF-27) | Lowest; and it is B2's first half |

## 15. Recommendation (design author's reading, for the reviewer; not a decision)

1. **Preferred candidate for the independent review: B2.** This follows the owner's direction and is the only option that keeps search cost bounded by the caller's own matches at 250K and 1M with very large |D|. It is safe **only** under SAFE-1 to SAFE-11. The conditions most in need of the reviewer's judgment are:
   - SAFE-3 and SAFE-4 (statistics and plan choice, UF-22);
   - SAFE-6 (multi-path parity, UF-23);
   - SAFE-7 (value consistency);
   - SAFE-11 (the FD-5 boundary).
2. **C is the structural baseline and fallback.** B2's access index is C's table, so the proposed order of construction (each stage separately authorized) is the access index first, measured as C. The value index follows only if the reviewer accepts B2's conditions.
3. **B1 is rejected** as a distinct architecture (§9).
4. **Option A remains rejected** (§5).
5. **Not selected:** the final architecture is chosen by the owner after the independent review. No result here is a measurement.
