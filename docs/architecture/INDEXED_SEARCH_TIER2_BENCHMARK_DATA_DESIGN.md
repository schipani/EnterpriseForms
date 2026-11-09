# 100K and 250K synthetic benchmark datasets: preparation design (C-138 IX5-D4; review annex C)

- **Status:** design only. **No dataset was seeded and no benchmark run** (C-138 §5). Prepared by Claude (Opus); not an independent assessment.
- **Constraints (C-138 §5):**
  * dataset creation must not bypass Authority authorization revisions, accepted-content verification, integrity sealing, projection or archiving;
  * dataset creation is separate from query measurement;
  * any bulk-registration mechanism stays gated under R-37;
  * synthetic data only.

## 1. Purpose

The §16 IX-5 exit gate requires 100K and 250K benchmarks. They also feed:
- the Option C/B decision (annex A §3.8);
- the LookupSubjects cost model (annex E);
- the sampling population (annex B §4.2);
- R-33/R-37 measurements in IX-7.

## 2. Phases

| Phase | Content | Output | Approval |
|---|---|---|---|
| **P0 pilot** | 1,000 records with the full dataset shape (§3) | Measured time per record and bytes per record per plane; validation of the generator and of the snapshot and restore procedure (§5) | One bounded run, ≤ 30 min |
| **P1 creation** | 100K, then 250K, records through the trusted path (§4) | A verified, quiesced dataset and its manifest (§5.2) | **Owner approval per run** (far over 30 min) |
| **P2 restore and verify** | Restore the full plane set onto a disposable benchmark stack; run the complete verifications (§5.3) | Verification evidence | Bounded; may exceed 30 min at 250K (deep sweep) |
| **M measurement** | Query and save benchmarks on the verified, restored dataset | Results per PB-09 metric | Each run ≤ 30 min unless approved |

Measurement never runs against a dataset that has not passed P2 in its current restore.

## 3. Dataset shape (proposed)

| Dimension | 100K | 250K |
|---|---|---|
| Organizations | 1 large organization, plus 2 small (1,000 each) for tenant isolation and the R-39 multi-organization sweep | Same |
| Users / teams | 500 users (PB-09 per organization); 50 teams of 5 to 100 members | Same |
| Forms | 1 parent form with 10 approved Internal fields (string, integer, decimal, date, date-time, boolean, single and multi-select option) and 5 non-approved fields; 1 child form | Same |
| Owners | 70 % user-owned, 30 % team-owned; Zipf-distributed across owners (some owners of more than 50K records, for large-\|D\| cases) | Same |
| Shares | 20 % of records shared: half whole-record, half partial (1 to 3 fields); 30 % of recipients are teams | Same |
| Children | 10 % of records with 1 to 5 children | Same |
| Value selectivity tiers | Per approved field: a unique value; 0.1 %, 1 %, 10 % and 100 % buckets; prefix families; `contains` tokens of controlled frequency | Same |
| Content revisions | 10 % of records edited once after creation | Same |

The generator is deterministic from a recorded seed, and every value is synthetic.

## 4. Creation through the trusted path, and feasibility

### 4.1 Today's path (public business API, one record at a time)

Measured (R-37, evidence §12.4): serial p50 327 ms at 100 records and 1,845 ms at 1,000 records, under the security-test sweep interval of 10 s. Concurrency adds nothing. Two costs grow with organization size:
- **Organization-lock waits.** At 1,000 records, mostly the sweep's 90 % duty cycle at the 10 s interval. At the production interval of 900 s, estimated at about 3 %.
- **Graph load per security command.** About 4 µs per edge (at least 2 edges per record): about 0.8 s at 100K records and about 2 s at 250K, per command.

| Size | Estimate, serial, production sweep interval | Estimate, serial, test interval |
|---|---|---|
| 100K | ≈ 30 to 60 h (≈ 0.35 s floor + graph load growing to 0.8 s + content path) | Longer; dominated by sweep contention |
| 250K | ≈ 120 to 200 h | Impractical |

The content path's own growth at large sizes (prepare and commit lock waits, any graph evaluation in the commit) is unmeasured; P0 measures it at 1,000. **Both sizes are infeasible within the 30-minute rule and need explicit owner approval in any case.**

### 4.2 Options (each separately gated; none implemented)

| # | Option | Effect on creation time | Gate |
|---|---|---|---|
| D4-1 | Today's path, long-running, under owner approval, with the **production** sweep interval on the dataset stack | 100K in days, 250K in a week or more | Owner approval of the run and of the sweep setting. C-137 §2 forbids changing intervals *to improve benchmarks*; using the production default for dataset creation is a configuration decision for the owner |
| D4-2 | R-37 option A: **bulk registration** (one security command registering many records, with the same revision CAS, projection and archive wait) | Removes the per-record command, Effective-wait and graph-load costs. Content commits remain per record | R-37 owner approval, its own review and tests |
| D4-3 | R-37 option B: **relationship-scoped graph loading** | Removes the O(organization) graph load from each command (and from content commits if they use it) | R-37 owner approval, review |
| D4-4 | In-process seeding harness calling the same business and Authority application services (no HTTP) | Saves the network and BFF overhead only (tens of ms of the floor) | A new test-only entry point; owner approval. Low value |

**Combination likely needed for 250K:** D4-2 and D4-3, plus D4-1's production interval. Rough estimate after both: creation bounded by content commits at roughly 0.1 to 0.2 s each (unmeasured): about 3 to 6 h for 100K and 7 to 14 h for 250K.

**Rejected:**
- direct SQL inserts into any database;
- generated database dumps;
- fabricated seals, manifests or journal entries;
- bypassing the Effective gate or archive;
- turning off seal derivation or sweeps during creation.

Each of these would produce a dataset whose integrity properties were never exercised, which is the thing the benchmark must not assume.

### 4.3 Separation from measurement

- Creation runs with the timing diagnostics (`Diagnostics:OperationTiming`) to record its own throughput, but its numbers are reported as **ingestion** results, never as query results.
- Measurement starts only after P2 on a restored copy, so the creation workload and its warm caches do not affect query numbers.

## 5. Reusable dataset strategy

### 5.1 Snapshot of a quiesced system

After P1:
- stop the business workload;
- wait until no command is pending, `projection_applied_revision = authorization_revision`, the archive has caught up, and the builder has attested coverage;
- run one complete S-A, S-B and S-C cycle;
- stop all containers.

Capture **every plane together**:
- BusinessDb;
- AuthorityDb (including `authority_search`);
- the SpiceDB datastore;
- the archive store;
- Azurite (if any documents);
- the integrity and signing **key store**.

**Blocking prerequisite (R-43):** the stacks run Vault in **dev mode (in-memory)**. A restart loses the Transit keys, so restored seals, row HMACs and signatures could no longer be verified. A reusable dataset needs a benchmark-only compose overlay with a **persistent** Vault (file storage backend, Testing-only synthetic keys, auto-unseal with a local synthetic key), captured with the other planes. That overlay is new infrastructure and needs owner approval.

### 5.2 Manifest

For each plane, the manifest records:
- the SHA-256 of the captured volume archive;
- the image digests;
- the EF migration ids for both databases;
- the zed schema hash;
- `SearchPostingDerivation` and `AccessPostingDerivation` versions;
- the generator seed and version;
- record counts;
- the final authorization and content-integrity revisions.

Any change to a migration, derivation version, schema hash or image that affects storage **invalidates** the dataset. It is then rebuilt, or upgraded only through the product's own migrations followed by a full P2.

### 5.3 Restore and verify (P2)

- Restore all planes together onto a **dedicated, disposable** stack with its own compose project name, never onto the security-test or development stacks.
- A restore is a whole-system rollback **by construction**: the journal, archive, SpiceDB and keys all come from the same quiesced point. It is not a G14 recovery and is not evidence for G14.
- Before any measurement, P2 runs:
  * S1 decisive verification per organization;
  * S-A;
  * a full S-B pass;
  * the S-C deep sweep (full at 100K; a ≥ 1 % sample plus full on the measured forms at 250K, if the full sweep exceeds the approved time);
  * M2 when Tier 2 exists.
- Any Fail aborts the measurement and invalidates the dataset.

### 5.4 Storage estimate

Measured on the security-test stack on 2026-11-08, after the C-137 runs: 1,637 records of mixed test shapes, about 3.4 manifest leaves per record.

| Plane | Size | Per record |
|---|---|---|
| AuthorityDb | 59.4 MB (journal 22.7 MB) | ≈ 36 KB |
| BusinessDb | 19.6 MB | ≈ 12 KB |
| SpiceDB datastore | 16.8 MB | ≈ 10 KB |
| **Total** | 95.8 MB | **≈ 58 KB** (an upper bound at this size: includes other test activity and fixed overhead) |

Scaled to the §3 shape (≈ 15 leaves per record, 4 to 5 times the leaves; edges and shares as specified), estimated at 2 to 3 times per record:

| Size | Data | With WAL headroom during creation and one snapshot copy |
|---|---|---|
| 100K | ≈ 12 to 18 GB | ≈ 40 to 60 GB |
| 250K | ≈ 30 to 45 GB | ≈ 100 to 150 GB |

Tier 2 tables add about 0.1 GB (option C) or about 1 GB (option B) at 250K (annex A §3.10). P0 replaces these estimates with measured figures.

## 6. Approvals and prerequisites (summary)

| Item | Needed for |
|---|---|
| P0 pilot run (≤ 30 min) | Measured per-record time and storage |
| Persistent Vault benchmark overlay (R-43) | Any reusable dataset |
| Sweep interval for the dataset stack | P1 duration (D4-1) |
| R-37 options A and/or B | 250K in practical time |
| P1 runs (days) and disk (≈ 150 GB) | 100K and 250K |
| Measurement runs over 30 min | Large-\|D\| and sweep-concurrent measurements |
