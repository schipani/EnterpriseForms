# Indexed search (Option C): Tier 1 implementation-readiness assessment (C-124)

- **Kind:** decision package for the owner's next approval (owner-led review; `docs/governance/REVIEW_AND_AUTHORIZATION_WORKFLOW.md`). **Not** an independent third-party assessment, which remains an outstanding production-readiness requirement.
- **Evidence:** design (`INDEXED_SEARCH_PROJECTION_DESIGN.md` §17.10, §18), owner-review evidence (`INDEXED_SEARCH_TIER1_OWNER_REVIEW_EVIDENCE.md`), remediation proposal (`../security/AUTHORITY_PRIVILEGE_REMEDIATION_PROPOSAL.md`); source inspection at `e830d08`.
- **No test has been executed and nothing has been measured.** Every guarantee is a design claim.

## 1. Owner-review findings

| ID | Subject | Status after C-124 | What closes it |
|---|---|---|---|
| OR-1 | Integrity binding of indexed results | Corrected claim **accepted**; per-page keyed seal check approved as a requirement, **not** as a complete mitigation. **Open** | Owner acceptance of the binding contract (design §18.1.4), including the separate seal key purpose |
| OR-2 | Revision synchronization | Correction approved in principle. **Open** | Owner acceptance of INV-R and its enforcement (§18.2), including the constraint-trigger decision |
| OR-3 | Commit failure paths | Contract documented (§18.3) | Owner acceptance; tests at IX-1 |
| OR-4 | Stored-result lifecycle | IX-3 requirements documented (§18.4) | Owner acceptance; tests at IX-3 |
| — | Deadline contract §17.10 | **Approved** with clarifications K1 to K9 (§17.10.5) | Closed as a design decision; tests at IX-1/IX-2 |
| OBS-IX-1 | Authority privilege exposure | **Provisional High**; remediation proposal prepared; open | Owner decisions in the proposal §6 |

## 2. Integrity trust boundary (revised)

```
                       ┌──────────────────────── Authority domain ────────────────────────┐
 Business PostgreSQL   │  Authority process  (AD-2: holds everything below)               │
 (AD-B)  ──── no path ─┼─►  ├── MAC provider (Managed HSM; keys non-exportable)            │
                       │    │     keyed checks: row HMAC, entry seal, (journal MAC, R6)    │
                       │    ├── SpiceDB (decides every permission; S1 pin and verify)      │
                       │    └── AuthorityDb  ◄── runtime DB credential (AD-1)               │
                       │          heads, leaves, journal (unkeyed chain), edges,           │
                       │          bindings, outbox  → AD-1 can INSERT/UPDATE              │
                       │          authority_search  → postings, entries (sealed)          │
                       └──────────────────────────────────┬────────────────────────────────┘
                                                          │ append-only delivery
                                         External evidence: archive (refuses a different
                                         body for a delivered revision); ledger (production)
```

| Check | Kind | Holds against |
|---|---|---|
| BusinessDb not on the search path | Structural | AD-B |
| I-1 scope guard, I-2 per-match digest and root | **Unkeyed consistency** | Defects, partial restore, posting-only edits. **Not** AD-1 or AD-3 |
| Entry seal on each displayed record (§18.1.4) | **Keyed** | AD-1 and AD-3, for identity, field, type, classification, value and approval at derivation. Not freshness |
| In-process predicate re-evaluation on verified values | Trusted code | Index damage that makes a non-match look like a match |
| SpiceDB decision under S1 | Authorization | Unauthorized disclosure, except AD-1 forging authorization history (remediation proposal AP-2, AP-3) |
| Archive reconciliation (R5) and keyed journal (R6) | External or keyed | Rewrites and forged history after the fact; **not implemented** |
| Nothing in-band | — | AD-2 |

## 3. Recommended integrity-verification architecture (§18.1.4)

**Option B, per-record entry-seal verification.**
- **Seal message:** `DynamicForms/SearchEntrySeal/v1`. It uses the existing HMAC-SHA-256 primitive and the RowHmac v1 encoding rules; only the message schema is new, and it needs vectors and explicit review. It binds:
  * the organization, form and record;
  * the committed operation, revision and manifest root;
  * for each posting: field, item, storage type, classification, protection mode, approval id and operator set, SHA-256 of the source canonical bytes, and the normalized value.
- **Key:** a separate key purpose (`SearchEntrySealV1`).
- **On every page:**
  1. I-1, then I-2 on all matches;
  2. seal verification of every displayed record;
  3. sealed fields compared with current state;
  4. the predicate re-evaluated on the verified value.

  Any non-`Valid` seal fails the whole page.
- **Rejected for now:**
  * Option A, row-HMAC per posting: it doesn't bind classification or approval, and costs more operations.
  * Option C, keyed index root: a new authenticated data structure.
  * Option D, batch verification: a transport optimization only.

**Stated limits:**
- per query, freshness (an older sealed state rolled back together with its head) and the "still Active" state of an approval are not bound against AD-1 or AD-3: those need R5;
- counts are I-2 level only;
- nothing holds against AD-2.

## 4. OR-2 transaction invariant (INV-R, §18.2.2)

> Every committed AuthorityDb transaction that changes `content_integrity_revision` from *r* to *r′* either advances the search-projection revision to *r′* in the same transaction, with exactly the derived posting changes, or sets `ReconciliationRequired` from *r*. A revision mismatch alone is never classified as tampering.

| Site | Operation | Action |
|---|---|---|
| W1 (368) | Content commit | Advance with derived postings |
| W2 (735), W3 (901) | Verified-read quarantine of a record | Advance, no posting change; I-1 reads the quarantined head |
| W4 (1235) | Share-editor `CurrentSnapshotMismatch` | Advance, no posting change, no quarantine |

**Enforcement:**
- a single domain method with a mandatory projection action;
- an architecture test on its call sites;
- a recommended deferred constraint trigger, so an old Authority image fails closed at commit;
- tests IX-63 and IX-72 to IX-76.

## 5. HSM performance implications (estimates)

| Event | MAC operations (option B) |
|---|---|
| Content commit | +1 sign |
| Each result page | P verifies (P = 10, 25, 50 or 100) |
| S-B sweep | about 29/s (24 h) to **about 43/s** (16 h target) at the PB-09 envelope |
| V1 today, for comparison | about one verify per leaf in scope (for example 25,000 per search) |

**PERF-01 must measure:**
- verify latency p50/p95/p99, single and in parallel groups;
- sustained throughput per HSM and per instance, and throttling surfacing as 503;
- whether batch verify is available;
- commit p95 delta (≤ 20 ms gate);
- page latency per page size;
- S-B concurrent with interactive load;
- behaviour after an HSM outage against the deadline slack (45 min for S-A, 8 h for S-B).

## 6. OBS-IX-1 security implications

- **The runtime database credential alone (AD-1) appears equivalent to full authorization authority** (read, not executed):
  * identity substitution (AP-1);
  * forged edges (AP-2);
  * forged, archived and projected command history (AP-3; the journal chain is unkeyed and the projector applies the outbox delta);
  * reactivation (AP-4);
  * in-place history rewrite (AP-5).
- Options C and E together close AP-4 and AP-5 and detect AP-2, but **not AP-3**. Keyed journal authentication with a bound outbox (R6) is needed for that.
- **Blocking:**
  * not IX-1 to IX-4 (Development/Testing, synthetic data, no new privilege on Authority tables);
  * **yes** for any real-data environment and for production Level 2, until remediation is authorized or the risk is explicitly accepted under the Level 2 trust assumption.

## 7. Outstanding IX-1 blockers

| # | Blocker | Owner action |
|---|---|---|
| **B1** | OR-1 binding contract (§18.1.4) not yet accepted, including the seal message schema and the separate key purpose. IX-1 builds the entry seal, so its schema must be fixed first | Accept, amend or reject §18.1.4 |
| **B2** | OR-2 contract (§18.2) not yet accepted, including the constraint trigger and its availability effect on old images | Accept, amend or reject §18.2 |
| B3 | OR-3 contract (§18.3) acceptance | Accept |

**Not IX-1 blockers:**
- OR-4 (IX-3);
- OBS-IX-1 (real-data and production gates);
- PERF-01 (IX-7);
- the independent third-party assessment (production);
- the seal vectors (produced during IX-1 and reviewed before IX-1 exits).

## 8. Recommendation

**Hold IX-1 now.** OR-1 and OR-2 are open by your instruction until their contracts are accepted.

No further design iteration is needed before that acceptance. **If you accept §18.1.4, §18.2 and §18.3 as written, authorizing IX-1 in the same decision is recommended**, with these conditions:
1. Development/Testing only, synthetic data only, derivation **dark** (no query path);
2. IX-1 exit tests include:
   - IX-01 to 07, 14 to 17, 28, 34, 35 (revised), 42;
   - IX-61, 63 to 66, 72 to 77;
   - the sweep tests for the cheap sweep and seal verification built in IX-1: IX-51, 53, 57 to 60, 69 to 71 (the deep-sweep cases at IX-2);
   - the PB-09 commit p95 delta.

   None has been run yet;
3. the seal message vectors are produced in IX-1 and owner-reviewed before IX-1 exits; they are added to the independent cryptographic review scope;
4. no change to M4-1 or to any `authority` table grant; OBS-IX-1 remediation stays separately authorized;
5. independent third-party assessment remains outstanding for production.
