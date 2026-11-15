# Staging preparation: indexed search (IX-1 to IX-7) on the merged main branch

- **Status (2026-11-11): prepared, not deployed.** Staging deployment is **not possible yet**: §1 lists the blockers. Each one needs separate implementation or owner authorization.
- **Scope:** the Authority search index (Tier 1, `authority_search`), the dark Tier 2 projection (`authority_search_access`), the IX-6 listing endpoint and the IX-7 Managed HSM seal provider, as merged into `main`. It also covers the C8 M4-1 S1 projection integrity and M1 work, which the indexed search depends on and which arrived in the same merge.
- **Not claimed:**
  * nothing here has run in Azure;
  * no staging, production, PERF-01, ST-PERF, G14 or independent-review result exists;
  * owner-led Development/Testing acceptance is not independent security attestation.
- **Companions:**
  * [PB05 deployment blueprint](PB05_DEPLOYMENT_BLUEPRINT.md);
  * [PB-10 residency](../privacy/PB10_CANADIAN_RESIDENCY_AND_PRIVACY_STANDARD.md);
  * [PB-11 delivery](../delivery/PB11_CICD_SUPPLY_CHAIN_RELEASE_STANDARD.md);
  * [IX-5/6/7 evidence](../architecture/INDEXED_SEARCH_IX5_IX7_COMPLETION_EVIDENCE.md);
  * [coordinated DR](../../trust_authority/COORDINATED_DISASTER_RECOVERY.md) and its [test plan](../../trust_authority/DISASTER_RECOVERY_TEST_PLAN.md).

## 1. Blockers to a staging deployment

These were verified against the code on `main` after the merge. The Authority and business API **refuse to start, or fail closed on every protected operation,** in a `Staging` environment until the platform blockers are resolved.

| Id | Blocker | Where enforced | Resolution (separate work) |
|---|---|---|---|
| SB-1 | **Row-integrity MAC provider for Staging does not exist.** `Signing:Provider=AzureManagedHsm` registers `UnavailableMacProvider("AzureManagedHsmProviderNotImplemented")`; VaultTransit and LocalDevelopment are refused in Staging. Every protected Authority operation fails closed | `SigningProviderPolicy.cs` | G04: an oct-HSM HMAC provider (SIG-01..SIG-14) |
| SB-2 | **No staging user identity.** `UserAuthentication:ProviderKind=EntraId` refuses to start ("not operable in Stage B"); `TestIdentityProvider` is Development/Testing only | Business API and Authority startup | PB-08: Entra OIDC BFF and the Authority's Entra assertion validator |
| SB-3 | **No staging document-encryption key provider.** `Encryption:KeyProvider=LocalDevelopment` is refused outside Development/Testing; Key Vault and Managed HSM providers are "not implemented in Stage B" | `DocumentProcessingHost.cs` | Key Vault or Managed HSM KEK provider |
| SB-4 | **No infrastructure as code or deployment pipeline.** No Bicep, no Container Apps or network definitions, no GitHub OIDC federation, no signed-digest promotion | Repository (absent) | PB-05 Bicep; PB-11 pipelines, separate Authority deploy identity |
| SB-5 | **Operator and step-up evidence is synthetic only** (`OperatorApprovals:Mode`, `PlatformOperators:Mode`, `FieldSearch:ApprovalStepUp=SyntheticDevelopment`). Entra transaction-bound step-up is "not implemented (PB-08)" | Authority startup and approval services | PB-08 |
| SB-6 | **The search index cannot run in Staging by design today.** `SearchIndex:Mode=Dark`, `SearchIndex:Query:QueryEnabled` and `SearchIndex:Access:Mode=Dark` are refused outside Development/Testing. With the safe default (`Disabled`/`Off`), ST-PERF and PERF-01 cannot measure the index | `SearchIndexComposition.cs`, `SearchAccessComposition.cs` | **Owner decision:** a reviewed change permitting Dark maintenance (and the Tier 1 query path for measurement) in Staging only, keeping it refused in Production. **Not made here; do not bypass it** (for example by running staging as `Testing`, which would also map Testing endpoints and synthetic identity) |
| SB-7 | **The Managed HSM seal provider is untested against a real Managed HSM** (fake endpoint only). The seal key does not exist | IX-7 | Managed HSM provisioning (§4.6), then ST-PERF-1/7 |
| SB-8 | **G14 coordinated recovery is not implemented** (recovery control plane, epochs, dual approval, journal replay) | `trust_authority/COORDINATED_DISASTER_RECOVERY.md` (reference only) | G14 implementation before its rehearsal (§10) |
| SB-9 | **Confidential Ledger anchoring and the production archive** are not verified in Azure. The archive provider is exercised against Azurite only | Authority archive | PB-12/G14 staging validation |
| SB-10 | **Staging authorization.** No staging deployment is authorized; PB-10 Canadian residency sign-off, PB-11 approvals and PB-12 evidence storage are required first | Owner, PB-10/11/12 | Owner authorization |

## 2. Deployment units and order

Units follow PB-05: one image per unit, promoted by digest, in separately administered scopes.

| Order | Unit | Notes |
|---|---|---|
| 1 | AuthorityDb (Azure Database for PostgreSQL 18, Canada Central) | Separate server, administrators and network from BusinessDb (G08). Roles in §4.1 |
| 2 | BusinessDb (Azure Database for PostgreSQL 18) | Separate administrative scope |
| 3 | SpiceDB v1.56.2 and its own datastore | Authority network only |
| 4 | Managed HSM: keys and role assignments (§4.6) | Separate provisioning identity |
| 5 | Archive storage (immutable Blob, Canada) and Confidential Ledger | Authority-only write identity |
| 6 | **Authority migration job** (`dotnet AuthorizationAuthority.Api.dll --migrate`) | JIT migration identity; §3 |
| 7 | **Business migration job** | Business migration identity |
| 8 | Authority (`AuthorizationAuthority.Api`) | Writes the SpiceDB schema at start; readiness at `/internal/v1/health/ready` |
| 9 | Business API, Worker (including `search-index-builder`) | Only after the Authority reports ready |
| 10 | Web, platform console | Last |

Rollback is by image digest in the reverse order. Migrations are forward-only (§9).

## 3. Database migrations and their order

**AuthorityDb.** The migration job applies every pending migration in timestamp order and then reapplies the runtime grants. The 13 migrations this merge adds over the previous `main`, in order:

1. `20261104231152_ProjectionIntegrityS1` (C8 M4-1 S1)
2. `20261106123244_AuthoritySearchIndexIx1`: `authority_search` schema, derived tables with forced RLS, INV-R constraint trigger
3. `20261107213255_AuthoritySearchIndexIx2`
4. `20261108003510_AuthoritySearchIndexIx2DeepExclusion`
5. `20261108042541_AuthoritySearchIndexIx3`: result snapshots and invalidation triggers
6. `20261108051053_AuthoritySearchIndexIx3PageServed`
7. `20261108133232_AuthoritySearchIndexIx4DropValueIndexes`: drops 7 value B-tree indexes and the trigram GIN (PD-9)
8. `20261110001213_AuthoritySearchIndexUf25FieldItemCounts`
9. `20261110161016_AuthorityJournalSearchIndexFormEvents`
10. `20261110185416_AuthoritySearchAccessIx5a`: `authority_search_access` schema, forced RLS
11. `20261111025341_AuthorityContentItemIdentitiesR53`
12. `20261111031500_AuthoritySearchAccessIx5b`
13. `20261112003342_AuthoritySearchAccessIx5Verification`: verification state and shadow samples, forced RLS

**BusinessDb:** no new migration in this merge (latest remains `20261103054837_PhaseC8M3RegionalPreferences`).

**Verified locally:**
- the upgrade path from the previous `main` schema (16 migrations), then these 13 (29 in total), on a disposable PostgreSQL 18.0;
- a re-run is idempotent;
- `has-pending-model-changes` is clean for both contexts;
- a fresh database applies the full chain (security-test stack).

**RLS:**
- forced on every value-bearing search table;
- the state tables (`search_projection_states`, `search_form_states`, `search_field_coverages`) and the result-snapshot tables have no RLS by design (D-IX1-5): they hold no customer values, and INV-R reads the projection row regardless of binding.

**Staging procedure:**
1. Take a PITR-capable snapshot of both databases.
2. Run the Authority migration job with the JIT migration identity.
3. Verify `authority.ef_migrations_history` ends at `20261112003342_AuthoritySearchAccessIx5Verification`.
4. Run the privilege inventory (`trust_authority/postgres-privilege-inventory.sql`) and the PG-01..PG-20 checks.
5. Then start the Authority.

## 4. Configuration

Values are environment configuration held in Key Vault (Canada) or the deployment, **never in the repository**. Every secret below is a credential to create per environment. The security-test values in `compose.security-test.yaml` are synthetic and must not be reused.

### 4.1 AuthorityDb

| Setting | Staging value |
|---|---|
| `ConnectionStrings__AuthorityDb` | Runtime role (NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION **NOBYPASSRLS**, not table owner); Entra workload identity, TLS required |
| `ConnectionStrings__AuthorityDbMigration` | JIT migration identity, migration job only |
| `Database__RuntimeRoleName` | The runtime role name (grants target) |
| `ConnectionStrings__AuthoritySearchReader` | The `authority_search_reader` login: SELECT on the search schemas only, **distinct** from the runtime connection (enforced at startup). Needed only if the query path is enabled under SB-6 |

### 4.1a Content acceptance phase (RB-CARV-1; contract §22, operations runbook §2b)

| Setting | Staging value |
|---|---|
| `ContentAcceptance__EnforcementPhase` | **Mandatory, no default** (the image's `appsettings.json` value `Unconfigured` refuses the start). `ReleaseA` until the CARV floor is activated and verified; then `ReleaseB` |
| `ContentAcceptance__RequiredFloor` | Absent in Release A; exactly `1` in Release B |

Both values must come from the separately permissioned Authority release pipeline (PB-11), unchangeable by business deployers and the Authority runtime, with audited changes; that protection is not implemented in the repository (R-CARV-22). Verify on the staging PostgreSQL version that the Release B write fence's expected definitions match (R-CARV-23) before switching to `ReleaseB`.

### 4.2 BusinessDb

| Setting | Staging value |
|---|---|
| `ConnectionStrings__BusinessDb` | Business runtime identity; no route to AuthorityDb, SpiceDB, the HSM or the ledger |
| `FieldSearch__Engine` | `VerifiedScan` (default; `Indexed`/`Shadow` refused outside Development/Testing) |
| `FieldSearch__ExecutionEnabled` | Unset (development/test capability) |
| `RecordList__Engine` | `VerifiedScan` (default; `Indexed` refused outside Development/Testing) |
| `TrustAuthority__*` | Authority base address and the workload credential (Entra workload identity under PB-08/PB-11; the shared-key form is synthetic) |

### 4.3 SpiceDB

| Setting | Staging value |
|---|---|
| Version | **v1.56.2.** The `LookupSubjects`/`LookupResources` exact-snapshot, wildcard and limit behaviour was probed on it (IX5-CT-08/11/12); a different version needs the probes rerun |
| Schema | Written by the Authority. The IX-39 coupling guard pins SHA-256 `4ee4e26a5a81d369df2a310c526aba5c64e8710be32a3975445943ad7aae4c85` (LF-normalized `authority.schema.zed`). A different schema disables Tier 2 at startup (`SCHEMA_COUPLING_BROKEN`) |
| `SpiceDb__Endpoint`, `SpiceDb__PresharedKey` | Private endpoint; preshared key from Key Vault, Authority identity only |
| `SpiceDb__LookupPageSize` | Default unless PERF-01 sizes it |
| Datastore | Own PostgreSQL, Authority-administered, Canada |

### 4.4 Authority journal and archive

| Setting | Staging value |
|---|---|
| `SecurityArchive__Provider` | `Blob` with a managed identity (not an account key), immutable container (WORM, legal hold per PB-13), Canada Central |
| `AuthorityWork__CommitWakeupEnabled` | `true` (R56-B; the 250 ms poll is kept) |
| `ProjectionIntegrity__PeriodicCheckSeconds` | Production default (the security-test stack uses 10) |
| Confidential Ledger | Anchoring of archive checkpoints (SB-9) |

### 4.5 Tier 1 and Tier 2 projections

The values below are the safe merged defaults; staging keeps them until SB-6 is decided. They are verified at startup.

| Setting | Merged default | Staging |
|---|---|---|
| `SearchIndex__Mode` | `Disabled` | `Disabled` (Dark refused, SB-6) |
| `SearchIndex__Query__QueryEnabled` | `false` | `false` |
| `SearchIndex__Query__RecordListEnabled` | `false` (requires `QueryEnabled`) | `false` |
| `SearchIndex__Access__Mode` | `Off` | `Off` |
| `SearchIndex__Access__ShadowComparisonEnabled` | `false` (refused outside Development/Testing) | `false` |
| `SearchIndex__CheapSweepIntervalSeconds` / deadlines | 900 s; 1 h / 24 h / 30 days (C-124; changes refused outside Development/Testing) | Defaults |
| `SearchIndex__SealCheckTransactionSize` | 50 | 50 |
| `SearchIndex__Access__*` verification | 60 s step, full check 1 h, deadline 24 h; SpiceDB pass 100 records, 24 h interval, 7 d deadline; fence 2,000 records; 10,000 subjects | Defaults |
| Worker `SearchIndexBuilder__*` | Builder credential (Entra workload identity), rate from PERF-01 | Off while `Disabled` |

**There is no Tier 2 user-facing serving path in the code.** Enabling it needs separate owner approval and new implementation.

### 4.6 Managed HSM (seal and integrity keys)

**Seal key** (IX-7, `SearchIndex__Seal__Provider=AzureManagedHsm`; only meaningful once SB-6 is decided):

| Requirement | Value |
|---|---|
| Key | `search-entry-seal-v1`, type **EC-HSM, P-256**, operations `sign` (and `verify`); **not exportable, no release policy**, enabled. The provider refuses anything else at startup |
| Version mapping | `SearchIndex__Seal__ManagedHsm__KeyVersionIdentifiers__<n>` = the HSM version id, for the signing version and every pinned version |
| Pin | `SearchIndex__Seal__PinnedKeys__0__*` = SubjectPublicKeyInfo of that version (deployment configuration, never AuthorityDb). The startup gate compares it with the HSM public key |
| Endpoint | `SearchIndex__Seal__ManagedHsm__VaultBaseUrl` = `https://<name>.managedhsm.azure.net/` (HTTPS enforced) |
| Identity | The Authority's managed identity (`SearchIndex__Seal__ManagedHsm__ManagedIdentityClientId` for a user-assigned one). Local RBAC on that key only: **Managed HSM Crypto User** limited to sign/get; no create, import, export, backup or delete |
| Provisioning | A separate provisioning identity creates and rotates the key (two-person, PIM). It never runs in the Authority |
| Timeout | `SearchIndex__Seal__ManagedHsm__TimeoutSeconds` 10 (1–60) |

**Row-integrity keys** (`Signing__Provider=AzureManagedHsm`): oct-HSM per-organization HMAC keys. The provider does not exist yet (SB-1).

**Residency:** Canada Central; Canada East is not a Managed HSM extended region. The recovery path is the manual Canadian security-domain-quorum restore of PB-10.

## 5. Security and feature defaults after the merge (verified on main)

- Tier 2 user-facing search serving: **no code path exists**. Tier 2 maintenance is `Off` by default and Dark is refused outside Development/Testing.
- IX-6 indexed listing: `RecordList:Engine=VerifiedScan` and `RecordListEnabled=false` by default. `Indexed` is refused outside Development/Testing. The `X-Testing-Record-List-Engine` header is honoured only in `Testing`.
- Shadow comparison: off, and refused outside Development/Testing.
- Testing endpoints (`/internal/v1/testing/*`, the business testing routes) are mapped only in `Testing`. The seal-throughput harness refuses to run outside Development/Testing.
- No Confidential or encrypted value is indexed (approved Public/Internal fields only; ADR-PB-004).
- Every decision uses fresh SpiceDB checks at an S1 pin that must complete. Integrity checks (I-1, I-2, seals) and quarantine are active. Quarantine is cleared only by a rebuild.
- No production Managed HSM is activated: Dark is refused outside Development/Testing, and the provider is contacted by nothing.
- Revocation guarantees are unchanged (tested: next page 409, new listing excludes the record, a restored stale grant is never served).

## 6. Search readiness and quarantine checks (read-only)

The Tier 2 status endpoints are Testing-only, so staging uses the read-only observer connection and the logs. **Never write to the search schemas by hand;** recovery is by rebuild only.

```sql
-- Tier 1: projection state per organization (Active is servable; Quarantined, Rebuilding, ReconciliationRequired are not)
SELECT organization_id, projection_status_name, projection_status_reason_code FROM authority_search.search_projection_states;
-- Tier 1: coverages
SELECT organization_id, coverage_state_name, count(*) FROM authority_search.search_field_coverages GROUP BY 1, 2;
-- Tier 2: checkpoint at the organization's revisions (readiness A-3)
SELECT c.organization_id, c.access_projection_state_name, c.quarantine_reason_code,
       c.consumed_authorization_revision = o.authorization_revision AS auth_current,
       c.consumed_content_integrity_revision = o.content_integrity_revision AS content_current
  FROM authority_search_access.search_access_checkpoints c JOIN authority.organization_authorization_states o USING (organization_id);
-- Tier 2: verification (M1 fence and M2 deadlines)
SELECT organization_id, access_verified_revision, full_verification_completed_on, spice_db_sweep_completed_on, last_verification_outcome_code
  FROM authority_search_access.search_access_verification_states;
```

Readiness requires all of:
- Authority `/internal/v1/health/ready` is 200;
- S1 is not quarantined (`organization_authorization_states.integrity_quarantine_enabled = false`);
- for Tier 1, `Active` with no overdue deadline (`SEARCH_INDEX_VERIFICATION_OVERDUE` absent);
- for Tier 2 (not serving), a `Maintaining` checkpoint at both revisions, plus verification current (fence closed, full check within 24 h, SpiceDB pass within 7 d).

## 7. Logging, metrics and alerting

| Signal | Severity / level | Meaning, action |
|---|---|---|
| `SEARCH_INDEX_QUARANTINE` | Critical | Tier 1 integrity failure: investigate, then rebuild (`POST /api/v1/organization/search-index/rebuild`, Security Administrator) |
| `PROJECTION_SNAPSHOT_MISMATCH` | Critical | S1 projection integrity: organization quarantined; G14 path |
| `SEARCH_INDEX_VERIFICATION_OVERDUE` | High | §17.10 deadline missed; the index stops serving |
| `SEARCH_INDEX_RECONCILIATION_REQUIRED` | High | INV-R revision mismatch |
| `SEARCH_ACCESS_PROJECTION_QUARANTINED` | High | Tier 2 diverged: Tier 2 rebuild, then a complete M2 pass |
| `SEARCH_ACCESS_SHADOW_MISMATCH` | High | Shadow difference (Development/Testing only) |
| `SEARCH_INDEX_DERIVATION_UPGRADE`, `SEARCH_INDEX_DEEP_EXCLUSION_CHANGED`, `PROJECTION_SNAPSHOT_UNAVAILABLE` | Medium/Warning | Watch |
| Events 5301–5304 / 5322–5323 | Information–Error | Tier 2 maintenance and verification outcomes |
| Events 5310/5311 | Information/Warning | R56-B listener connected/lost (the poll continues) |
| Events 9305, 9310–9316, 9320/9321 | Information–Critical | Seal-sweep lock hold, builder, deep sweep, persistent builder failure, snapshot purge |
| Events 9333 / 9334 / 9335 | Information | Search fallback, R56-A retry, IX-6 engine choice (codes only) |

Logs carry identifiers, codes and durations only: no field values, search terms or secrets. Alerts must go to the independently administered security sink (G08). Metrics to derive:
- quarantine count by reason;
- deadline headroom;
- builder backlog;
- Tier 2 checkpoint lag;
- verification age;
- gate-closed fraction (R-56);
- HSM sign latency and throttling (ST-PERF-7).

## 8. Health checks

- **Authority:**
  * `/internal/v1/health/live` (process only);
  * `/internal/v1/health/ready` (signing provider, SpiceDB schema, dependencies). The container probe is `dotnet AuthorizationAuthority.Api.dll --probe <ready url>`.
- **Business API:** `/health/live`, and `/health/ready`, which includes Authority readiness.
- **Databases:** `pg_isready`. **SpiceDB:** `grpc_health_probe`. **ClamAV:** `clamdcheck.sh`.
- **Startup guards** fail the container on unsafe configuration (§5); treat a refusal as a deployment failure, never something to work around.

## 9. Recovery and rollback

- **Images:** roll back by digest.
- **Migrations are forward-only:**
  * do not run `Down` in staging;
  * every IX migration is additive except IX-4, which drops unused indexes;
  * an older image runs against the newer schema only where the expand/contract review allows; otherwise roll forward.
- **Search index (Tier 1):** an administrator rebuild returns it to `Active` (rehearsed locally in 4.2 s for four records: `Ix7SearchRecoveryRehearsalStackTests`). Quarantine clears only through a rebuild.
- **Tier 2:** a controlled rebuild, then a complete M2 pass. Readiness requires an Active Tier 1.
- **Revocations never roll back.** An older database snapshot must not reinstate grants. Restored projections are detected (rehearsed: Tier 1 quarantined in 3.0 s; Tier 2 caught up from the journal and was held not-Ready). A restored AuthorityDb is G14 territory (§10), not a projection rebuild.
- **S1 organization quarantine** is permanent by trigger. Clearance needs the privileged G14 path, which is not implemented.

## 10. G14 coordinated recovery rehearsal plan (blocked on SB-8)

**Prerequisites:**
- G14 implemented: recovery control plane, recovery epoch, dual approval, journal replay, key and archive recovery;
- staging deployed;
- Managed HSM security-domain quorum ready.

**Plan** (from `trust_authority/DISASTER_RECOVERY_TEST_PLAN.md` DR-01..DR-36 and `DISASTER_RECOVERY_OPERATOR_RUNBOOK.md`):
1. Baseline: synthetic organizations with covered forms, shares and documents. Record authorization and content revisions, archive receipts and ledger anchors.
2. Revoke a share at revision N. Restore BusinessDb, AuthorityDb and SpiceDB to N−1 on isolated servers (DR-01..DR-03).
3. Replay the archived security journal to N under the recovery epoch, with two approvers.
4. Rebuild SpiceDB deterministically with new tokens.
5. Projections:
   * the Tier 1 rebuild (administrator) must reach `Active` with every entry at its head;
   * the Tier 2 rebuild must pass a complete M2 (full equivalence plus SpiceDB pass);
   * the readiness checks of §6 must pass before any search is allowed.
6. Verify: the revoked user cannot discover, search, list, read or download (both engines); a missing accepted document quarantines its record, with no older substitute.
7. Record:
   * RTO (target 4 h, PERF-16) and business RPO (≤ 15 min, PERF-17);
   * PERF-18 (zero accepted authorization rollback);
   * the revision and receipt chain;
   * the key inventory and both approvals.

The local projection rehearsal (`Ix7SearchRecoveryRehearsalStackTests`) covers only step 5 on a projection-only restore. It is not a substitute.

## 11. Staging test data

- Synthetic identities and organizations only; no customer data (PB-10).
- Seed only through the public business API (real authorization, signing and derivation); never by direct database writes.
- PB-09 envelope:
  * 25 organizations × 500 registered users, 500 active;
  * up to 100K records per organization (PERF-02);
  * a 250-field form (PERF-03);
  * 250 child records per collection;
  * 100 documents per record.
- Approved Public/Internal searchable fields only; Confidential fields present but unindexed.
- The 100K/250K dataset design is annex C of the Tier 2 review package. **Running the 100K/250K datasets needs explicit owner authorization.**

## 12. ST-PERF-1..7 and PERF-01 execution (after SB-1..SB-7 are resolved and SB-6 decided)

**ST-PERF:** run the method of `INDEXED_SEARCH_IX1_EVIDENCE.md` §6.5:
- interleaved paired commits;
- at least 2,000 pairs per run, 5 runs on fresh stacks;
- 50-pair warm-up;
- a 10,000-resample stratified bootstrap.

**Pass:** the upper bound of the 95 % confidence interval of the pooled p95 difference is at most 20 ms, and no run's interval lies entirely above 20 ms. An inconclusive result is not a pass.

| Test | Execution |
|---|---|
| ST-PERF-1 | Seal signer = Azure Managed HSM (§4.6) through the gate |
| ST-PERF-2 | Server-side commit duration and lock waits from `pg_stat_statements` and lock statistics on the staging AuthorityDb |
| ST-PERF-3..5 | §6.5 paired design and pass criterion (closes or fails R-02) |
| ST-PERF-6 | Concurrent commits across organizations with S-A/S-B sweeps active, plus the PB-09 search load (requires the SB-6 decision) |
| ST-PERF-7 | `dotnet AuthorizationAuthority.Api.dll --measure-seal-throughput --count <N> --concurrency <C>` inside the staging Authority. This needs a Development/Testing-style measurement authorization, or an owner-approved measurement mode, because it refuses in Staging. Report ops/s, p50/p95/p99, throttling (429) and cost (C-125: 330/s P-256 figure) |

**PERF-01 (PB-09 matrix):**
- rows PERF-01 (25 × 500), PERF-02, PERF-09 (50 requests/s, full security), PERF-12 (verified search p95 < 3 s, fail closed), PERF-18 and PERF-20;
- record p50/p95/p99, errors, queue and HSM timings;
- capture SpiceDB `LookupResources` at 10K–50K discoverable records (R1 §E risk) and Tier 2 checkpoint lag.

**Evidence:** PB-12 schema, linked to commit, image digest and environment, in Canadian immutable storage.

## 13. Outstanding gates (not passed, not waived)

- IX-5 14-day sampling: **deferred by the owner** (not met).
- R-02 performance gate: **FAIL**.
- TQ-23 timing-leak decision.
- Tier 2 serving approval.
- Staging PERF-01 and ST-PERF-1..7.
- Managed HSM staging validation.
- G14 coordinated recovery and DR rehearsal.
- Independent implementation and security review: IX-1..IX-7, Tier 2, and C8 M4-1 S1/M1.
- Multi-instance Authority sweep-lease decision.
- Stage B release testing.
- Production certification: independent cryptographic attestation, Managed HSM, Confidential Ledger, Azure Level B.
- **Arrived with the merge, still open:**
  * C8 M4-1 independent review;
  * P-S1-1 (480-record verified-search budget);
  * the RB-13 Stage B test-isolation defect;
  * B-Q9 latency at 150 requests/s;
  * the M1 production key provider;
  * R-56, R-43 and F-150-3..8, F-151-4/5;
  * G06, G19 and G21.
