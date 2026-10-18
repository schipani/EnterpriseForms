# ADR-PB-009 — Approved Initial Capacity, Performance and Recovery Targets

- **Status:** Accepted by product owner
- **Date approved:** 2026-10-17
- **Pre-build finding:** PB-09
- **Referenced contract:** `docs/performance/PB09_CAPACITY_SLO_STANDARD.md`
- **Proof status:** Requirements and engineering targets only; no load, uptime or recovery targets have been demonstrated.

## Decision

Approve the initial performance/capacity envelope from Finding 9 as a **first-release engineering target**. These are not claims of tested throughput, contractual customer service levels or permanent licensing ceilings.

### Capacity targets

| Area | Approved initial envelope |
|---|---:|
| Organizations | 25 |
| Registered users per organization | 500 |
| Simultaneously active users across platform | 500 |
| Primary business records per organization | 100,000 |
| Fields in one form definition | 250 |
| Related records in one child collection | 250 |
| Documents associated with a record | 100 |
| Standard single-document upload size | 500 MB |
| Explicitly enabled large-document upload size | 2 GB |
| Concurrent active uploads per user | 2 |
| Sustained aggregate business API requests | 50 requests/second |

### Latency, availability and disaster-recovery targets

| Metric | Approved engineering objective |
|---|---|
| Authorized record retrieval | p95 < 1 second |
| Authorized record creation/update | p95 < 2 seconds |
| Bounded independently verified search | p95 < 3 seconds |
| Independently loaded/refreshed dashboard card | p95 < 2 seconds |
| Initial visible application shell | p95 < 2 seconds |
| Platform monthly availability | 99.9% |
| Recovery Time Objective (RTO) | 4 hours |
| Business data Recovery Point Objective (RPO) | <= 15 minutes, with any missing acknowledged committed content explicitly detected and quarantined |
| Authorization rollback after recovery | **Zero accepted privilege rollback** |

### Interpretation

1. Targets apply at the approved load profile, with normal security controls **enabled**, not mocked or bypassed. Request and write latency include authoritative permission checks, cryptographic verification and commit barriers.
2. These bounds must be verified with repeatable p50/p95/p99 measurements in staging. Distinguish server API time from browser navigation and upload bandwidth, and report client experience for the visible shell separately.
3. The 500 MB ordinary / 2 GB special upload limits are configurable policy limits and must be enforced before and throughout chunk ingestion; choose an explicit bytes-vs-display-unit convention in the API implementation, document it, and test it. No silent file truncation or opportunistic bypass of scanning/encryption.
4. The 15-minute business RPO is a **target**, not proof that Azure geo-redundant PostgreSQL backups achieve it. The precise data-loss bounds of disaster modes and storage products must be independently verified; any strategy unable to meet the objective requires a revised design or explicit risk acceptance before production. Restore must never claim missing committed content is current.
5. Zero rollback applies to current authorized security state; if independently recoverable authority evidence is unavailable, affected reads/writes fail closed, and a recovered business database does not reinstate revoked grants.
6. Treat organization, record, field and upload counts as benchmark/load-engineering constraints and configurable product quotas where justified; do not hardwire arbitrary licensing limits into the domain model.
7. Verify isolation and fairness: 2-GB uploads, scanning, ledger checkpoints, authorization lag, high record-cardinality and expensive search must not starve interactive API lanes or weaken tenant boundaries.
8. Define reliability SLO measurement explicitly before rollout, including monitors, numerator/denominator, planned maintenance policy, incident reporting, escalation and error-budget ownership. A 99.9% target is not certification or an automatic provider SLA.
9. Re-baseline targets by measured workload and explicit owner approval; changing them is a tracked ADR and never implicitly relaxes Level 2 security.

## Scope and dependencies

- PB-01 through PB-08 decisions remain binding (single repository, first Accused vertical slice, adversarial security gate, encrypted-by-default verified search, Azure hosting, Minimal APIs, request coordination, Entra sign-in).
- PB-10 will determine region and residency policy and may restrict the DR architecture; PB-11 covers CI/CD and supply-chain controls.
- Do not use unverified targets to market actual production capacity.

## Required evidence

1. Repeatable realistic data generator and performance workloads; include 25 tenants, skewed tenant sizes, 100K-record tenant and up to 250 fields in representative forms.
2. Benchmarks of authenticated records, trusted verified search, background uploads, dashboard cards, authorized child collections, security command/revocation, cryptographic checks and HSM/ledger throughput.
3. Distributed failure and isolation tests showing no leakage, bypass, unauthorized caching, lost mandatory audit events or accepting partial searches under load.
4. DR rehearsals demonstrate 4-hour RTO target, 15-minute business RPO objective where actually achievable, zero authorization rollback and explicit quarantine/detection of irrecoverable committed content.
5. Measured results, hardware/container dimensions, test dataset, concurrency, test commit/image digest, regression thresholds and reviewer sign-off included in the release evidence register.
