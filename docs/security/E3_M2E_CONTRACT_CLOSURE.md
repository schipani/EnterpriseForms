# E3-M2e — Owner Decisions and Contract Closure (Data-Key Ledger, CARV 3, Floor 3, Acceptance Switch)

**Status (2026-11-15):**

| Item | State |
|---|---|
| This document | Owner decision record and contract closure for E3-M2e. **Documentation only.** It changes no code, test, migration, configuration or database. |
| E3-M2e implementation | **Not authorized.** Neither E3-M2e-1 nor E3-M2e-2 (§6) may start without a separate owner authorization. *(2026-11-16: E3-M2e-1 was later authorized, implemented and owner-accepted for Development/Testing, with one signed local commit; see `E3_M2E1_IMPLEMENTATION_RECORD.md` §14. E3-M2e-2 remains not authorized.)* |
| Envelope v2 acceptance | **Not activated anywhere.** No persistent environment (Development/Testing included) may enable it under E3-M2e (§4, §5). |
| Baseline | Branch `e3/m1-envelope-v2-core`, HEAD `a1127a109b5596e65cd3ac7f66a4ae4b35df2eb7` (E3-M2d, signed), clean tree |
| Review | Owner-led. Not an independent security review and not a certification. V2-IND-01 is still required before production. |

**Inputs:** the E3-M2e readiness report (owner review, 2026-11-15); `E3_M2_AUTHORITY_DATABASE_CONTRACT.md` (the package); `E3_M2B_IMPLEMENTATION_CONTRACT.md` (supersedes the package where they differ); `E3_M2D_IMPLEMENTATION_RECORD.md` (OA-M2d-1..8); `E3_ENVELOPE_V2_KEY_HIERARCHY_DESIGN.md` (OD-E3-16).

**Precedence:** this record supersedes the package and the M2b contract **only** where it says so explicitly (§2). Dated inline notes in those two documents point here. Every other requirement of those documents is unchanged.

**Notation:**

| Tag | Meaning |
|---|---|
| [PRIOR] | Approved by the owner before this record (OD-E3-16, OD-M2-06/12/17..22/25, D-1..D-12 of E3-M2d, OA-M2d-1..8, RB-CARV-1 decisions) |
| [DECIDED] | Approved by the owner on 2026-11-15, recorded here (OD-M2e-01..16 and the owner's conditions) |
| [BLOCKING] | A design question this record leaves open on purpose; it blocks the activation stage named |

**Identifiers introduced:** owner decisions OD-M2e-01 to OD-M2e-16; amendments AM-1 to AM-12; ledger requirements L-1 to L-14; restore-barrier requirements RB2-1 to RB2-8; blocking decisions BD-1 and BD-2; risks R-M2e-1 to R-M2e-12; increments E3-M2e-1 and E3-M2e-2.

---

## 1. Owner decisions

### 1.1 Requirements that were already approved and are unchanged [PRIOR]

| Requirement | Source |
|---|---|
| OD-E3-16: a newly introduced v2 data-key id must not have appeared in an earlier accepted revision; unchanged carried-forward envelopes are exempt | E-3 design §11, §13.10 |
| Carried/New classification against the current accepted head only | E3-M2d D-7 |
| DK-1 classification, DK-2 batch consistency over **every** v2 row (OA-M2d-4), DK-3 evaluated before any new-v2 refusal (OA-M2d-5) | E3-M2d §4.3, §12 |
| v2 → v1 downgrade refused on every path | OD-M2-06, E3-M2d D-6 |
| v2 Commit acceptance only through an explicitly approved, ledger-backed E3-M2e policy; CARV never authorizes a bypass | OA-M2d-1 |
| M2-ENV-16 must pass end to end through Commit in E3-M2e | OA-M2d-2 |
| CARV mechanism: P stamped at Prepare and immutable, Commit requires P = V, the database requires C = P, the state guard, floors added NOT VALID, add-then-drop, never lowered | OD-M2-17/18/19/21, M2b §19, D-5 |
| Floors live only in `ContentAcceptanceFloorContext`; applied only by `--apply-content-acceptance-floor N`, one step at a time, never with `--migrate` | M2b §21.2, OD-M2-28, OA-M2d-6 |
| Release A / Release B policy and the transactional Release B write fence | RB-CARV-1, M2b §22, E3-M2d §6.3 |
| S-4 schema/envelope tie and S-9 signed-schema pinning are **permanent** | M2b §23.14–§23.15, E3-M2d §11.1 |
| Disposable PostgreSQL for constraint, grant and concurrency tests | OD-M2-12 |

### 1.2 Decisions approved on 2026-11-15 [DECIDED]

The owner approved decisions 1–16 of the readiness report as recommended, subject to the additional conditions in §1.3.

| ID | Decision | Contract basis | Approved alternative | Consequences | Implementation obligation |
|---|---|---|---|---|---|
| **OD-M2e-01** | Ledger uniqueness scope | OD-M2-04; package §4.2; M2b §9.4 | **Global**: one binding per `data_key_identifier` across every organization | A cross-organization reuse is refused outright; a cross-organization race is caught by the unique index | `uq_content_data_key_binding_identifier`; map SQLSTATE 23505 on it to `422 DATA_KEY_IDENTIFIER_REUSED` with full rollback (L-3) |
| **OD-M2e-02** | Registration timing | OD-M2-03; package §4.4 DK-5; M2b §7.1 | **Prepare**, in the Prepare transaction | A reused id is refused before its ciphertext is staged in BusinessDb. R-M1-8 is closed for id reuse; seed reuse under a fresh id stays a writer property (TH-M2-02) | DK-5 insert in the same transaction as the operation and candidate leaves (L-5) |
| **OD-M2e-03** | Ids of aborted or expired operations | package §4.5 | **Permanent reservation**; never released | Ids consumed by attempts that never commit stay unusable (harmless: UUIDv7 space). Reservations of never-committed operations are lost across an AuthorityDb restore (stated, §3.6) | No code path deletes or releases a binding (L-8) |
| **OD-M2e-04** | Database-enforced invariants | OD-M2-23; readiness P-1, P-2, P-3 | **P-1 binding guard** and **P-2 immutability**. **P-3 (wrapped-key digest) is not adopted in E3-M2e** | OD-M2-23 is resolved in the database (P-1), not by a composite FK. TH-M2-02 / R-M2d-4 (copied wrap under a fresh id) stay open | §3.3 L-6, L-7; G08 inventory; fence verification (L-13) |
| **OD-M2e-05** | CARV 3 rule boundary | M2b §3.3, §7.3; package DK-6/DK-7 | **DK-6 as written + P-4** carried-group verification + a Commit-time switch re-check for New groups | Commit can now refuse an unbound or foreign-record v2 id even in an operation not prepared by this build | §3.4 (P-4), §3.5 |
| **OD-M2e-06** | Floor-3 mechanism and prerequisites | M2b §9.4 vs §21.2 / OA-M2d-6 (conflict C-1); C-11; C-14 | **Separate floor-context migration**, applied only by `--apply-content-acceptance-floor 3`; the ledger migration recorded and the ledger objects present as a precondition; floor 2 installed first | The ordinary migration never installs a floor (AM-1) | Floor-3 SQL, reinstatement SQL, registry, policy, command, fence (§6.1) |
| **OD-M2e-07** | Acceptance switch behaviour | OD-M2-10; package §8; C-5, C-6, C-12 | (a) refuse to start when on in any environment other than Development/Testing, **Staging included**; (b) prerequisite **Release B with `RequiredFloor = 3`**, enforced transactionally (AM-5); (c) switch off refuses **New** groups only; carried groups remain subject to P-4; (d) the Testing override may enable only when the static prerequisites hold and may always disable | OD-M2-10 is decided in this form. With the switch off, a carried v2 group commits only if P-4 finds it bound to an accepted operation, which is impossible in an environment where the switch was never on (§5.3) | §6.1 switch items |
| **OD-M2e-08** | Mixed-fleet compatibility | package §7.5; M2b §10 | CARV-2 and CARV-3 instances may coexist before floor 3 while the switch is off. **Floor 2 must be installed before any v2 binding or acceptance** (AM-6) | CARV ≤ 2 builds cannot accept any v2 row. CARV-1 builds can accept a v2 → v1 downgrade, so floor 2 is mandatory | Gate evidence for C-14 (E3-M2e-2, §6.2) |
| **OD-M2e-09** | M2f dependency | package §7.3; M2b §15 | E3-M2e **without** M2f; acceptance only in isolated Development/Testing (disposable) | A mixed business fleet with pre-M2f-1 builds needs M2f before any activation there *(corrected 2026-11-16: originally "pre-M2c"; E3-M2c to E3-M2e-2 builds send no capability and count as level 1, `E3_M2F_CONTRACT_CLOSURE.md` CAP-S5)* | None in E3-M2e; M2f stays a separate increment. *(2026-11-16: M2f contract closed in `E3_M2F_CONTRACT_CLOSURE.md`; implementation not authorized.)* |
| **OD-M2e-10** | Recovery: archive content and restore enforcement | OD-M2-15; package §4.6; M2b §7.4; C-4, C-7 | Archive the **full binding tuple** (AM-3). The body field is **always present from CARV 3** (an empty array when there are no New groups). The G14 gate plus the runbook apply in Development/Testing only. **An enforceable restore barrier is required before any persistent activation** (AM-4, BD-1) | OD-M2-15 is amended. A G14 operator procedure alone does not satisfy persistent activation | §3.6, §4 |
| **OD-M2e-11** | DK-6 failure handling | package §4.7; C-9 | **Abort the operation and write a minimal content-stream incident**, using the existing integrity-incident path | Durable evidence of a ledger or operation inconsistency | The abort plus incident must commit; verify against the RB-CARV-1 catch-all (§6.1) |
| **OD-M2e-12** | DK-4 ordering | M2b §7.3 step 4 | **After authorization, before signing** (amends M2b §7.3; AM-8) | No existence oracle for unauthorized callers; still nothing is signed when refused | Order of calls in Prepare |
| **OD-M2e-13** | Policy-observed event storage | package §8; C-8 | **Platform journal** (`EnvelopeAcceptancePolicyObserved`) | Changes to the effective acceptance policy are durably recorded outside organization streams | Where the "last observed" state lives is an E3-M2e-1 design item (UQ-3) |
| **OD-M2e-14** | Business mapping of `DATA_KEY_IDENTIFIER_REUSED` | package §4.5; C-16 | **With M4** (the first v2 writer) | No business change in E3-M2e | None in E3-M2e |
| **OD-M2e-15** | Ledger disposition at organization offboarding | PB-13; C-13 | **Deferred**; recorded as a PB-13 input | Offboarding must later decide between permanent tombstones and another reuse-preventing form; the org FK is RESTRICT meanwhile | Record in the PB-13 design inputs (not done here) |
| **OD-M2e-16** | Activation authority | package §8 | **Separate owner authorization** is needed to enable the switch, even in a disposable environment (E3-M2e-2) | "Code present" and "acceptance on" are distinct gates | §5, §6 |

### 1.3 Additional owner security conditions (2026-11-15) [DECIDED]

1. C-1, C-3, C-4, C-7, C-12 and C-14 are resolved by the amendments in §2.
2. The ledger security contract (§3) is binding, including P-1, P-2, DK-4..DK-8, P-4 and the absence of runtime UPDATE/DELETE. P-3 is excluded from this increment.
3. Archived identifiers alone do not prove ledger correctness (§3.6).
4. A fail-closed restore barrier is required before persistent activation. A G14 operator procedure alone is not sufficient. The mechanism is not yet specified and is recorded as BD-1 (§4).
5. The activation sequence of §5 is mandatory. Outside Development/Testing, acceptance stays prohibited for E3-M2e. No persistent-environment activation is authorized.
6. E3-M2e is split into E3-M2e-1 and E3-M2e-2, each needing its own authorization (§6).

---

## 2. Contract amendments

Each amendment is also recorded as a dated inline note at the cited place.

| ID | Resolves | Amended text | Amendment |
|---|---|---|---|
| **AM-1** | C-1 | M2b §9.4 "Same migration: raise the CARV floor to 3" | **Superseded.** The ledger migration (`AuthorityContentDataKeyBindings`, ordinary context) installs no floor. Floor 3 is a separate migration in `ContentAcceptanceFloorContext` (`AuthorityContentAcceptanceRuleFloor3`), applied only by `--apply-content-acceptance-floor 3` with the migration identity, in a later step than the ledger migration (§5). Preconditions under the ACCESS EXCLUSIVE lock: the M2b-1 fence complete; floor 2 installed, recorded and the only floor; the ledger migration recorded and the ledger table, unique index, CHECKs, FKs and the P-1/P-2 triggers present exactly as created; no unexpired `Prepared` operation with P NULL or < 3; no connected writing session without a `carv-v` marker ≥ 3 (the LISTEN-only connection excepted). Then add `ck_content_operation_rule_floor_v3` NOT VALID and drop `ck_content_operation_rule_floor_v2`, in one transaction. Down always raises. Reinstatement SQL as for floor 2. No brace in any floor SQL. |
| **AM-2** | C-3 | Package §4.4 DK-7 | **Clarified.** DK-7 is satisfied by the Prepare classification (DK-1), CARV 3 (P = C = 3) and the head CAS; Commit cannot re-classify, because it holds only digests. **P-4** (§3.4) is an additional Commit-time verification from the ledger. It is not a re-classification and does not prove that a carried row comes from the current revision rather than an older revision of the same record. |
| **AM-3** | C-4 | Package §4.6 archive bullet; OD-M2-15 | **Amended.** The `ContentHeadCommitted` body carries the complete binding tuple of every New group of the commit (§3.6), not identifiers only. Present (possibly empty) on every commit by a CARV ≥ 3 build. |
| **AM-4** | C-7 | Package §4.6 "Until E3-M6 … v2 acceptance stays disabled (switch off) …"; M2b §7.4 step 4 | **Amended.** Switching the configuration off is not an enforcement mechanism: the switch is outside the restored database and may stay on. Persistent activation requires an Authority-enforced, fail-closed restore barrier meeting RB2-1..RB2-8 (§4.2). The mechanism is **[BLOCKING] BD-1**. Until BD-1 is decided, implemented and verified, Envelope v2 acceptance is limited to disposable environments. |
| **AM-5** | C-12 | M2b §10 "New precondition: the switch cannot be on unless the floor-3 migration id is present (startup guard)" | **Strengthened.** With the switch on, the Authority refuses to start unless: the environment is Development or Testing; the content acceptance policy is **Release B with `RequiredFloor = 3`**; the ledger migration is recorded. Release A with the switch on is a startup failure. Acceptance of a New group happens only inside a Prepare or Commit transaction whose Release B write fence has verified floor ≥ 3 in that transaction (the existing `RequireContentAcceptanceFloorAsync` entry check, extended by L-13). A startup check alone is not sufficient. |
| **AM-6** | C-14 | Package §7.1/§7.5; M2b §10 rollout order | **Added.** Floor 2 must be installed and verified before the first v2 binding or acceptance in any environment. Reason: CARV-1 builds (E3-M2b-1 to E3-M2c) have no downgrade guard, are admitted by floor 1, and would accept an owner save that rewrites a v2 item as v1. Floor 3 then gives the uniform invariant (every commit ran CARV ≥ 3 rules) and the F0 fence. The C-14 finding is from source analysis and must be proven in E3-M2e-2 (§6.2 item 11). |
| AM-7 | C-2 | Package §4.4 "`envelope_rule_version` … must equal 2" | **Superseded by CARV** (OD-M2-17): the CARV-3 reading is P = C = 3. |
| AM-8 | — | M2b §7.3 Prepare steps 4–6 | DK-4 runs after authorization and before signing (OD-M2e-12); the DK-5 insert follows signing, in the same transaction. |
| AM-9 | C-5 | Package §2.2, §8 | Wrap codes {1, 2} only in Development/Testing; the empty set elsewhere. The switch is refused everywhere except Development/Testing (OD-M2e-07). |
| AM-10 | C-6 | M2b §5.2 "Acceptance switch" row | Off refuses New groups at Prepare and at Commit; carried groups are judged by P-4 (OD-M2e-07). |
| AM-11 | C-9 | Package §4.7 DK-6 row | Abort plus a minimal content-stream incident (OD-M2e-11). |
| AM-12 | C-10, C-11 | Package §7.4; M2b §10 | The Authority required-migration guard includes the ledger migration id; floor 3 requires the ledger objects (AM-1). |

Not changed: RowHmac v1, Merkle v1 and every vector file; signed schemas 1 and 2; S-4; S-9; the downgrade guard; CARV equality; floor monotonicity; the Release A / Release B model; the business database.

---

## 3. Ledger security contract

### 3.1 Table

`authority.content_data_key_bindings`, as in M2b §9.4, with the additions of L-6 and L-7.

| Column | Type | Rule |
|---|---|---|
| `content_data_key_binding_id` | uuid | PK, UUIDv7 generated in .NET |
| `data_key_identifier` | uuid | UNIQUE globally (`uq_content_data_key_binding_identifier`); CHECK version nibble 7 and RFC 9562 variant (`ck_content_data_key_binding_identifier_v7`) |
| `organization_id` | uuid | FK → `organization_authorization_states` RESTRICT |
| `form_record_id` | uuid | The only record that may use the id |
| `envelope_payload_kind_code` | smallint | CHECK `IN (1, 2)`; 1 field batch (`field-value`), 2 document version (`document`) |
| `data_key_commitment_bytes` | bytea | CHECK `octet_length = 32` |
| `binding_item_count` | integer | CHECK `>= 1 AND (kind = 1 OR count = 1)` |
| `binding_content_change_operation_id` | uuid | FK → `content_change_operations` RESTRICT; the single operation allowed to introduce the id |
| `data_key_bound_on` | timestamptz | UTC, the Prepare time |

Indexes: `ix_content_data_key_bindings_record (organization_id, form_record_id)`, `ix_content_data_key_bindings_operation (binding_content_change_operation_id)`. All names follow the descriptive multiword snake_case rule.

### 3.2 Requirements

| ID | Requirement |
|---|---|
| L-1 | Identifiers are UUIDv7 with the RFC 9562 variant, validated by the Authority (v2 shape) and by the CHECK |
| L-2 | Uniqueness is global, across organizations and every operation state (OD-M2e-01) |
| L-3 | SQLSTATE 23505 on the unique index rolls back the whole Prepare transaction (operation, leaves, bindings) and is reported as `422 DATA_KEY_IDENTIFIER_REUSED`; no signature leaves the Authority |
| L-4 | The purpose is bound through the kind (field ↔ `field-value`, document ↔ `document`, enforced by the v2 shape rule). The organization is stored. The wrap code and the key reference are **not** bound (M5 Rewrap) |
| L-5 | DK-5: one binding per New group, inserted in the Prepare transaction (OD-M2e-02). The EF model declares the relationship to `ContentChangeOperation` so that the operation is inserted first |
| L-6 | **P-1 binding guard** (`BEFORE INSERT`, row level): the referenced operation exists, is `Prepared`, has the same `organization_id` and `form_record_id`, and has `prepare_acceptance_rule_version IS NOT NULL AND >= 3`; otherwise raise `check_violation` with a fixed, identifier-free message and a named constraint. Function: plpgsql, `SET search_path = pg_catalog, pg_temp`, not SECURITY DEFINER, `REVOKE ALL … FROM PUBLIC`, listed in the G08 inventory |
| L-7 | **P-2 immutability**: `BEFORE UPDATE OR DELETE` (row level) and `BEFORE TRUNCATE` (statement level) raise unconditionally; same function properties as L-6 |
| L-8 | Grants: the runtime role gets `SELECT, INSERT` only (`AuthorityRuntimeGrants.InsertOnlyTables`); no UPDATE, DELETE or TRUNCATE. No API, operator command or business path writes or removes a binding |
| L-9 | "Accepted" is derived (the binding's operation is `Committed`), never stored |
| L-10 | Reads, verification, search and history never consult the ledger. *(Amended 2026-11-16 by AM-F1, `E3_M2F_CONTRACT_CLOSURE.md` §5: the single exception is one organization-level binding-existence query returning a boolean, called only by the E3-M2f capability check at its seven entry points, under the organization lock, only when the client level is below 2 and not below the deployment floor, used only to refuse, failing closed on error. No binding detail is read or exposed.)* |
| L-11 | The store catch-all (`AuthorityStore`, R-CARV-26) treats a binding insert as a content-acceptance write |
| L-12 | A P-1 refusal is an invariant failure: fail closed, operational alert, never success, never `OPERATION_RULES_OUTDATED` |
| L-13 | Any transaction that inserts a binding or performs P-4 verifies, in that transaction, that the ledger's unique index and the P-1/P-2 triggers exist exactly as created (the same verification style as the Release B fence; PostgreSQL 18.0 renderings, R-CARV-23) |
| L-14 | No P-3 wrapped-key digest column or index in E3-M2e (OD-M2e-04) |

### 3.3 DK-4 to DK-8 (binding)

| Rule | Point | Requirement |
|---|---|---|
| DK-4 | Prepare, under the organization lock, after authorization, before signing | No New group's id exists in the ledger, in any organization or state; else `422 DATA_KEY_IDENTIFIER_REUSED`; the unique index is the backstop |
| DK-5 | Prepare, same transaction | L-5, guarded by P-1 |
| DK-6 | Commit, after the staged-bytes verification and the S-9, S-4 and v1/v2 shape and batch rules, before item-identity binding | The bindings of this operation equal exactly the New groups derived from the verified staged bytes (the set of ids, and for each the organization, record, kind, commitment and item count). Otherwise abort `DATA_KEY_BINDING_MISMATCH` and a minimal content-stream incident (OD-M2e-11) |
| DK-7 | — | AM-2 |
| DK-8 | Commit, same transaction | The binding tuples (§3.6) in the `ContentHeadCommitted` body |

### 3.4 P-4 carried-group verification (Commit)

For every v2 data-key id in the verified staged bytes that is **not** bound to this operation:
1. a binding must exist;
2. its operation must be `Committed`;
3. it must have the same organization, record, kind and commitment as the staged rows;
4. the number of staged rows with that id must be ≤ `binding_item_count`.

Otherwise abort with `DATA_KEY_BINDING_MISMATCH` and an incident.

**Limit:** P-4 does not prove that the carried rows equal the current head's rows. That is DK-1 at Prepare, covered by CARV 3 and the head CAS.

### 3.5 Switch re-check at Commit

If any New group exists (bindings of this operation) and the effective switch is now off, abort with `ENVELOPE_VERSION_NOT_ENABLED_AT_COMMIT` (tightening wins).

### 3.6 Archive tuple and reconstruction

**Body field:** `ContentCheckpointBody` gains one array that is present on every commit by a CARV ≥ 3 build. It holds one element per New group, sorted by the RFC 9562 byte order of the identifier. Each element contains:
- `content_data_key_binding_id`;
- `data_key_identifier`;
- `envelope_payload_kind_code`;
- `data_key_commitment_bytes` (encoding fixed in E3-M2e-1);
- `binding_item_count`;
- `data_key_bound_on`.

The organization comes from the journal stream, and the record and operation from the body's existing `form_record_id` and `committed_operation_id`. Together they make every column of an accepted binding deterministically reconstructible.

The body contains no secret:
- data-key ids are not secret (E3-D11);
- the commitment is an HKDF-Expand output that does not reveal the seed and is already stored in BusinessDb (package §2.1).

**Pre-CARV-3 commits** carry no field. They hold no accepted v2 binding by construction: E3-M2d Commit refuses every v2 row, and earlier builds refuse schema 2 candidates. Floors 2 and 3 keep that true for every later commit.

**What the archive alone does not prove** (owner condition 3):

| Property | Needs |
|---|---|
| The archive is complete (no `ContentHeadCommitted` entry missing) | The anchored journal chain (digest chain plus external receipts); a G14 / E3-M6 property |
| The archived tuple matches the committed rows (for example, the commitment really is the one in the signed rows) | Established at Commit by DK-6 from verified staged bytes. Proving it after the fact needs independently verified content bytes (verifier workload against the Authority leaves of that commit, package §6.3) |
| The commitment is correct for the seed and wrap | Never verifiable by the Authority (business opener, E3-M1) |
| Reservations of operations that never committed | Not archived and not reconstructible (OD-M2e-03, accepted loss) |
| The ledger's current contents are correct | Reconstruction equal to the archive **and** completeness of the archive. Identifiers alone prove neither |

---

## 4. Recovery and restore

### 4.1 Threat

After an AuthorityDb restore to an earlier point, or a partial ledger loss, bindings of commits after that point are missing. A New group that re-presents one of those ids passes DK-4 (TH-M2-16).

The configuration switch is outside the restored database. If it is still on, a CARV-3 instance would accept that group. Floors and Release B do not detect this: they verify the rule version, not ledger completeness.

### 4.2 Required barrier

| ID | Requirement |
|---|---|
| RB2-1 | **Authority-enforced and fail closed.** Unless the barrier is verified open, no Prepare and no Commit accepts a New v2 group and no binding is inserted, whatever the switch says |
| RB2-2 | **Independent of the switch and of instance memory.** A restart, a configuration change or the Testing override cannot open it |
| RB2-3 | **Restore-resistant.** Restoring AuthorityDb (fully or partly) to an earlier state cannot leave the barrier open: the evidence that opens it must be checked against state that the restore does not roll back (for example, externally anchored evidence), not only against rows in AuthorityDb |
| RB2-4 | **Transactional.** Checked inside the Prepare or Commit transaction that would accept a New group, like the Release B fence |
| RB2-5 | **Covers:** a full AuthorityDb restore; a partial ledger loss; an interrupted or incomplete ledger rebuild; an archive gap; a ledger that disagrees with the archive |
| RB2-6 | **Opened only by** a verified reconstruction (tuple equality with a complete archive, §3.6) through the recovery control plane, with two distinct authorized approvers; never by a business API, an organization setting or an ordinary operator command |
| RB2-7 | **Reads are unaffected.** v1 writes and v2 reads continue; P-4 carried-group commits follow the same barrier (they consult the ledger) |
| RB2-8 | **Observable.** A closed barrier is reported in readiness and alerts without identifiers |

**[BLOCKING] BD-1 — barrier mechanism.** The mechanism that satisfies RB2-1..RB2-8 is not specified and is not invented here. Its design must say:
- which state is anchored outside AuthorityDb;
- how it is compared transactionally;
- what it costs per write;
- how it interacts with the existing G14 organization gate and the anchored content-stream checkpoints. Whether either can serve is to be evaluated, not assumed.

BD-1 blocks persistent activation (§4.3). It does not block E3-M2e-1, which never opens acceptance.

**[BLOCKING] BD-2 — reconstruction insertion.** Re-inserting bindings after a restore is coupled to the recovery of the operations they reference:
- the operation FK is RESTRICT, and after a full restore the committed operations after the restore point are missing too;
- P-1 admits inserts only for `Prepared` operations with P ≥ 3, so a recovery-plane insert of a binding for a `Committed` operation would be refused.

The design must define, without weakening P-1 for the runtime, how the recovery control plane re-establishes operations and bindings (E3-M6 content-stream replay or an equivalent approved path). BD-2 blocks persistent activation. In E3-M2e-2, reconstruction is verified as a deterministic function and compared with the original ledger, without inserting into a live database.

### 4.3 Environment classes

| Class | Definition | Under E3-M2e | Prerequisites |
|---|---|---|---|
| 1. **Disposable Development/Testing acceptance** | A PostgreSQL container created and destroyed by one gate run, with no data outliving it; the Authority in-process or in the gate harness | **Only in E3-M2e-2**, with its own owner authorization (OD-M2e-16) | Floors 1 → 2 → 3 installed in that container; Release B `RequiredFloor = 3`; the switch on inside the run only. BD-1/BD-2 are not required, because restores are test subjects whose gaps are demonstrated, not hidden |
| 2. **Persistent Development/Testing activation** | Any environment whose AuthorityDb outlives one run: the developer stack, the security-test stack when its volumes are kept, any shared or long-lived instance | **Prohibited** | BD-1 and BD-2 decided, implemented and verified; a read-only v2-absence inventory of that environment; floors 2 and 3 installed there under their own approval; Release B `RequiredFloor = 3` in protected configuration; a homogeneous business fleet at E3-M2c+, or M2f; the §5 sequence; a separate owner authorization |
| 3. **Production (and Staging)** | Anything other than Development/Testing | **Prohibited**; the Authority refuses to start with the switch on (OD-M2e-07) | Everything in class 2, plus M2f, E3-M3, E3-M4, V2-IND-01, E-9, PB-03 Azure staging evidence, PB-11/PB-12 governance and protected configuration, RB-CARV-1 production closure, R-S4-4 inventory, Azure PostgreSQL verification (R-CARV-18/23), M2g census, dual control |

> **E3-M2f contract closure (2026-11-16; `E3_M2F_CONTRACT_CLOSURE.md` §11.5).** Where M2f is a prerequisite above, "M2f" means:
> - E3-M2f-1, E3-M2f-2, E3-M2f-2G and E3-M2f-3 accepted;
> - a verified Authority deployment inventory (INV-1/INV-2; CARV markers are insufficient);
> - OA-FE1-2 where a floor is raised.
>
> The alternative in class 2 ("a homogeneous business fleet at E3-M2c+") is narrowed: a homogeneous fleet at **E3-M2f-1+**, because E3-M2c..E3-M2e-2 builds send no capability and count as level 1. Nothing here changes any class or authorizes activation.

---

## 5. Activation safety

### 5.1 Mandatory sequence

| Step | Action | Verification |
|---|---|---|
| 1 | Install the ledger migration (ordinary context) | History row; table, unique index, CHECKs, FKs, P-1/P-2 triggers and grants exactly as created; a runtime UPDATE/DELETE/TRUNCATE is refused |
| 2 | Deploy CARV-3-capable Authority code, **switch off** | Readiness `v3`; marker `carv-v3`; Release A or Release B with `RequiredFloor` ≤ the installed floor |
| 3 | Establish **floor 2** before any v2 binding (AM-6) | `floor_v2` only, exact definition, recorded |
| 4 | Drain incompatible Authority instances (CARV ≤ 2) | Deployment inventory plus the `carv-v` marker precondition |
| 5 | Apply and verify **floor 3** (AM-1) | `floor_v3` only, exact definition, both floor history rows; a P = 2 insert is refused |
| 6 | Enforce **Release B `RequiredFloor = 3`** on every instance | Startup log and readiness `phase=ReleaseB requiredFloor=3`; writes refused on a floor-2 state |
| 7 | Verify ledger and recovery prerequisites | L-13 verification passes. Class 1: none further. Class 2/3: BD-1 barrier verified open and BD-2 procedure proven |
| 8 | Enable acceptance, **only under a separate owner authorization** | `EnvelopeAcceptancePolicyObserved` on the platform journal; startup prerequisites (AM-5) |

Outside Development/Testing, step 8 is impossible in E3-M2e: the Authority refuses to start. No persistent-environment activation is authorized.

### 5.2 Configuration drift

- The switch value is read once at startup.
- On without the AM-5 prerequisites is a startup failure, never "off and running".
- A Release A deployment with the switch on fails to start.
- A later loss of floor 3 refuses every write transactionally (Release B).
- A later ledger loss is BD-1's responsibility.
- Protection of the configuration itself is PB-11 (R-CARV-22 extended).

### 5.3 Carried groups with the switch off

OD-M2e-07(c) lets a carried v2 group commit with the switch off when P-4 finds a binding of an accepted operation. In any environment where the switch has never been on, no binding exists, so every carried v2 group is refused. The E3-M2d property (no v2 row is committed) therefore still holds in every environment where acceptance has never been enabled.

---

## 6. Increments

### 6.1 E3-M2e-1 — Dark ledger (separate authorization required)

**Scope (Authority and AuthorityDb only; no business change):**
- **Ordinary migration** `AuthorityContentDataKeyBindings`: §3.1; L-6, L-7; grants (L-8); guarded Down (refuses while any binding exists; Development/Testing only); `lock_timeout 5s`. Domain entity, EF configuration with the relationship to the operation, store port methods (lookup by ids, by operation, insert), and in-memory store emulation of the unique index and P-1.
- **Rules:**
  - `EnvelopeV2Rules.EnforceAgainstCurrent` returns the classification instead of refusing New rows (DK-3, the downgrade guard and the current-v2 wrap separation unchanged);
  - acceptance policy at Prepare (switch, environment, wrap codes);
  - DK-4 after authorization, before signing (AM-8); DK-5;
  - at Commit, `CommitViolation` is replaced by the switch re-check (§3.5), DK-6 and P-4;
  - DK-8 body field;
  - DK-6/P-4 abort and incident.
- **CARV 3:** `CurrentVersion = 3`; `RecognizedFloors = [1, 2, 3]`; marker `carv-v3`.
- **Floor-3 support:** floor-context migration and SQL (AM-1), reinstatement SQL, registry, write fence expected definition, policy `RequiredFloor ∈ {1, 2, 3}`, command `--apply-content-acceptance-floor 3`, readiness. **No floor installation outside disposable PostgreSQL.**
- **Switch:** `Content:EnvelopeV2AcceptanceEnabled`, default off. Fail-closed parse; refused outside Development/Testing; AM-5 startup prerequisites; bounded Testing override; `EnvelopeAcceptancePolicyObserved` on the platform journal.
- **Wrap codes by environment** (AM-9).
- **Migration guard:** required ledger migration id in readiness (AM-12). L-11 catch-all. L-12 and L-13.
- **Error mapping:** 23505 → `422 DATA_KEY_IDENTIFIER_REUSED`; `DATA_KEY_BINDING_MISMATCH`; P-1 refusal as an invariant failure. Private OpenAPI reference note updated.
- **Focused tests:**
  - Authority in-process: M2-DK-01..12, M2-IDM-01..05, M2-REP-01..03, M2-CARV-09, M2-AUZ-01..05, switch default-off and startup refusals (Production, Staging, Release A, `RequiredFloor` < 3, ledger migration missing), Testing-override bounds;
  - S-4/S-9/downgrade regressions with the switch on and off; committed (1,1)/(2,2) replay;
  - contract tests with the real M1 core;
  - architecture tests (catch-all coverage, no ledger update/delete path, DK-6 placement before identity binding, no business Infrastructure reference);
  - disposable-PostgreSQL checks of the migration, grants, P-1, P-2, EF insert order and the 23505 rollback;
  - enforcement-removal checks for every new control.
- **Documents:** implementation record, operations runbook update, G08 privilege inventory, spec manifest.

**Exit conditions:**
- Every existing suite is green.
- Acceptance stays disabled by default.
- With the switch off, no v2 row commits in any test that has no prior binding.
- Nothing is installed outside disposable PostgreSQL.
- No Stage B, Azure or persistent database is involved.

### 6.2 E3-M2e-2 — Controlled acceptance verification (separate authorization required; disposable PostgreSQL only)

1. Real M1-core fixtures (local provider, wrap code 1; Vault Transit wrap code 2 through the existing throwaway-Vault pattern), sealed by the business encoder and submitted to the real Authority Release build in a gate harness.
2. Floors 1 → 2 → 3 installed in the container; Release B `RequiredFloor = 3`; the switch on only inside the run.
3. Ledger-backed Prepare/Commit acceptance of new v2 field batches and document versions; **M2-ENV-16 end to end through Commit** (OA-M2d-2); carried batches across later commits; partial batches; removals.
4. Switch-off cases: New refused at Prepare and at Commit (M2-ENV-18); carried with P-4.
5. Concurrency: same-organization serialization; the cross-organization unique race on real PostgreSQL (M2-CON-01..04); floor-3 raise with concurrent writers.
6. Idempotency and crash: Prepare replay inserts nothing; crash after the Prepare commit leaves reservations; a retry with the same batch is refused.
7. Forged operations: seeded P = 3 operations with an unbound id, a foreign-record binding, a mismatched commitment or count → DK-6/P-4 abort and incident; P-1 refusals of forged inserts; P-2 refusals.
8. Floor enforcement: floor-3 preconditions (CARV-2 session, unexpired P = 2 operation, missing ledger objects, floor 1 still present); monotonicity; reinstatement; the D-M1..M7 equivalents for floor 3; Release B refusals after a floor-2 restore.
9. Mixed builds (`git archive a1127a1` CARV 2, `git archive 8915f77` CARV 1): cross-build operations refused; CARV-2 Prepare refused after floor 3.
10. Rollback and restore: tuple reconstruction from the archived bodies equals the original ledger (M2-RST-06, as a deterministic function per §4.2 BD-2); a demonstration that, without BD-1, a restored database with the switch on accepts a lost id (expected; this is the evidence for BD-1, not a pass condition to hide).
11. C-14 evidence: a CARV-1 owner downgrade of a v2 item is accepted on floor 1 and refused on floor 2.
12. Mutation (enforcement-removal) checks against the PostgreSQL gate for every database-level control.

**Exit conditions:**
- Runs of record ×2, containers removed.
- Evidence scrubbed of local paths.
- No persistent activation.

### 6.3 Not in E3-M2e

- M2f (capability header and floor, OD-M2-07/08; contract closed 2026-11-16 in `E3_M2F_CONTRACT_CLOSURE.md`, not implemented), M2g census, M3 catalog, M4 writers and the business mapping (OD-M2e-14), M5 Reseal/Rewrap, M6 content-stream replay.
- The BD-1 and BD-2 mechanisms (design decisions first).
- P-3, PB-13 disposition (OD-M2e-15), production configuration protection (PB-11).

---

## 7. Remaining unresolved design questions

| ID | Question | Blocks |
|---|---|---|
| **BD-1** | Restore barrier mechanism (§4.2) | Persistent activation (class 2 and 3) |
| **BD-2** | Recovery-plane re-insertion of operations and bindings without weakening P-1 (§4.2) | Persistent activation |
| UQ-1 | Exact JSON names and commitment encoding of the body element (existing serializer conventions) | Settled in the E3-M2e-1 implementation review |
| UQ-2 | Whether the Release B fence verifies the ledger objects on every content write or only in transactions that insert a binding or run P-4 (L-13 requires the latter as a minimum) | E3-M2e-1 review |
| UQ-3 | Where the "last observed" acceptance policy is kept for `EnvelopeAcceptancePolicyObserved`. If it needs a new table, that table is part of the E3-M2e-1 AuthorityDb migration approval | E3-M2e-1 authorization |
| UQ-4 | Whether DK-6/P-4 incidents and the switch-off Commit aborts are also journaled on the audit stream (R-CARV-12) *(Note 2026-11-17: `E3_M2F3_CONTRACT_CLOSURE.md` PD-11 decides durable audit-stream evidence for the capability refusal class only; UQ-4 is not answered and remains open.)* | Release gate, unchanged |
| UQ-5 | PostgreSQL 18.0 renderings of the floor-3 and ledger-trigger definitions on Azure PostgreSQL (R-CARV-23) | Production |
| UQ-6 | P-3 (wrapped-key digest) for a later increment | None in E3-M2e |

---

## 8. Risks and stop conditions

### 8.1 New risks

| ID | Risk | Status |
|---|---|---|
| R-M2e-1 | TH-M2-16: restore-time reuse of a lost id while the switch stays on | **Open; BD-1**; acceptance limited to disposable environments |
| R-M2e-2 | Reconstruction depends on archive completeness and on the recovery of operations (BD-2) | Open |
| R-M2e-3 | P-4 does not prove that a carried row comes from the current revision rather than an older revision of the same record | Accepted (CARV 3 + head CAS cover it; outside model for forged operations) |
| R-M2e-4 | Seed reuse, or a copied wrap under a fresh id, is undetected (TH-M2-02, R-M2d-4; P-3 not adopted) | Open, writer property |
| R-M2e-5 | C-14 is a source-analysis finding until E3-M2e-2 item 11 | Open until evidence exists |
| R-M2e-6 | Ledger growth (one row per New group) and the extra lookup and insert per write are not benchmarked | PB-09 |
| R-M2e-7 | Switch configuration protection is a deployment control (R-CARV-22 extended) | Open, PB-11 |
| R-M2e-8 | Floor-3 and ledger-trigger definitions are PostgreSQL 18.0 renderings (R-CARV-23 extended) | Open |
| R-M2e-9 | A Commit-time switch-off abort, and DK-6/P-4 aborts, add refusal classes to R-CARV-12 | Open (UQ-4) |
| R-M2e-10 | The in-memory store must emulate the unique index and P-1 faithfully; the real behaviour is proven only by disposable PostgreSQL | Mitigated by E3-M2e-1 PostgreSQL checks |
| R-M2e-11 | The ledger org FK is RESTRICT, which interacts with PB-13 offboarding | Deferred (OD-M2e-15) |
| R-M2e-12 | Operations in flight across the CARV 2 → 3 bump are refused and re-staged (R-CARV-7) | Accepted availability cost |

### 8.2 Carried forward (none closed by this record)

R-M2d-1..8; R-M2c-2, R-M2c-6, R-M2c-11; R-S4-1/-3/-4; R-M2b2-1..10; R-CARV-11, -12, -18, -21..-27; R-M1-1..8; R-M2a-1..6; V2-IND-01; PB-03 Azure staging evidence; PB-11/PB-12 governance; RB-CARV-1 production configuration controls; Azure PostgreSQL floor-definition verification.

### 8.3 Stop conditions (any implementation under E3-M2e)

Stop and report if:
- a New v2 group can be accepted with the switch off, outside Development/Testing, without Release B `RequiredFloor = 3`, without a binding to its operation, or while a restore barrier (once implemented) is closed;
- a v2 → v1 downgrade is accepted;
- S-4 or S-9 is weakened, or any v1 vector file or historical row changes;
- a floor can be lowered or skipped, or `--migrate` installs a floor;
- the runtime role can UPDATE, DELETE or TRUNCATE a binding;
- an operation with P ≠ 3 commits on a CARV-3 build;
- a carried v2 group commits without a binding of an accepted operation;
- the work needs a persistent database, Azure, Stage B, a business change, or M2f.

---

## 9. Not claimed

- No implementation of E3-M2e and no Envelope v2 acceptance in any environment.
- No floor 3 anywhere; floor 2 is installed only in disposable PostgreSQL (E3-M2d record).
- No restore barrier (BD-1) or reconstruction mechanism (BD-2).
- No Azure, staging or production evidence; no independent review; no compliance or production readiness.
