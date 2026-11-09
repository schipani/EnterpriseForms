# Tier 2 index architecture: Option C versus Option B (C-138 IX5-D2; review annex A)

- **Status:** design evaluation for the independent reviewer and the owner. **No Tier 2 table, index, migration or query exists. No option is selected.** Prepared by Claude (Opus) under C-138 §3. It is the design author's analysis, not an independent assessment.
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

**The design author's reading:**
- C (preferably C-S) is the safer design.
- B is justified only if measurement shows that C cannot meet the PB-09 search target for the expected `|D|` at 100K and 250K.
- Since C is a subset of B, the decision can follow measurement.

The open questions for the independent reviewer are package TQ-1 to TQ-12. **No option is selected.**

## 5. Option A (rejected unless independent evidence establishes its security)

A keeps one table with value-keyed indexes and a record maintenance index, and depends on the planner always choosing principal-leading plans for caller statements.

PD-9 demonstrated, on this project's PostgreSQL 18 stack, that the planner will use a skip scan over a value-keyed index for a record-scoped probe when statistics change, and that PostgreSQL 18 has no setting to prevent it. A also includes the §9.2 GIN index, which does not bound cost by principal (§2.3).

A could be reconsidered only with independent evidence that every caller-facing statement on the table is safe for every plan PostgreSQL 18 can produce, not merely for the plans observed. The design author does not know how such evidence could be produced. **Recorded as rejected.**
