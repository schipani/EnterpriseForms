# PB-09 — Capacity, Observability and Performance Validation Standard

**Decision:** ADR-PB-009. This document defines how Claude should implement and test the approved engineering target envelope.

## Budget configuration, not licenses

Configuration-owned bounds:

| Setting | Initial value | Validation |
|---|---:|---|
| `Capacity:TargetOrganizations` | 25 | Load scenario, not customer license |
| `Capacity:RegisteredUsersPerOrganization` | 500 | Identity/session/role distribution |
| `Capacity:ConcurrentUsersPlatform` | 500 | Measured simultaneous workload |
| `Capacity:PrimaryRecordsPerOrganization` | 100000 | Data generator, realistic values/versions |
| `Forms:FieldsPerDefinition` | 250 | Designers validate; evaluate policy-based hard cap |
| `RelatedForms:RecordsPerCollection` | 250 | Paginate, authorize each projection |
| `Documents:DocumentsPerRecord` | 100 | List paging and verified manifest |
| `Documents:StandardUploadMB` | 500 | Enforce policy and explicitly document unit convention |
| `Documents:ExceptionalUploadGB` | 2 | Explicit controlled enablement, scan and storage capacity |
| `Documents:ConcurrentUploadsPerUser` | 2 | Priority upload queue budget |
| `Api:TargetSustainedRequestsPerSecond` | 50 | Sustained aggregate benchmark, not automatic rate-limit |

Organization-level settings may lower applicable values for cost, availability, privacy or security. Raising beyond measured/protected bounds requires load and security requalification. Do not infer quotas for per-tenant total bytes, daily requests or API burst rates without an approved follow-up decision.

## Metric definitions

- **Authorized record read**: from API receipt through authority decision, verified content projection and HTTP completion, excluding last-mile client latency; p95 < 1 s under approved benchmark.
- **Authorized record write**: from API receipt through durable acknowledged commit, authority revision fences and HTTP completion; p95 < 2 s. For asynchronous writes `202` is *only accepted*, not completed; measure durable operation completion separately.
- **Trusted search**: p95 < 3 s for bounded, provably complete authorized query class under PB-04. A query exceeding proof/candidate limits must fail explicitly; do not substitute fast, incomplete results.
- **Dashboard card**: p95 < 2 s per independent data card from request start through completion. Each card has its own refresh, loading, failure, empty and authorization states.
- **Application shell**: p95 < 2 s to visible/usable shell in a specified browser/device/network profile. Record separate browser timing metrics; no claims based on server-only latency.
- **Availability**: 99.9% monthly objective. Implement a monitored user-journey/health measurement policy; define exclusions only with recorded owner approval.
- **RTO/RPO**: 4-hour coordinated recovery target, ≤15-minute business data loss objective, zero accepting stale authorization. Measure scenario-specific recovery after isolated service, availability-zone, regional and malicious-DB corruption failures. Service providers may not meet these targets by default.
- **Latency statistics**: capture p50/p95/p99, error rate, concurrent sessions, saturation, CPU, memory, DB connections/locks, IOPS, queue time, service-to-service calls, data size, throughput and audit evidence integrity.

## Workload design

1. Seed 25 organizations with skewed distribution and realistic authorization graphs; at least one has 100,000 primary records and child/document history. Include a 250-field form and 250 child items in relevant stress cases; do not build impossible Cartesian explosion for normal workload.
2. Generate realistic mix of Public/Internal searchable, encrypted Confidential/Restricted values, related records, signed manifests, audit and historical form revisions.
3. Simulate 500 active users at 50 sustained aggregate HTTP requests per second, with realistic think time and a mix of reads/writes/search, 500-MB and 2-GB transfers, and independent card refreshes. Test burst behavior separately without claiming an approved burst envelope.
4. Keep all permissions/integrity/ledger and encryption controls active; include off-nominal authority lag, invalid permissions, revocations, HSM outage, scan backlog and disaster-mode restores.
5. Compare response quality and visibility with baseline. Protected requests may fail closed on dependencies; such failures count against availability and must never be turned into successful insecure responses.
6. Include browser checks for keyboard/AODA, 200%+ text zoom, tenant-safe user cache, upload progress during navigation and cross-tab cancellations.
7. Retain evidence as deterministic CI artifact or staged run report, and tie it to test code, image digests, environment deployment commit and reviewer.

## Performance test release gate

- Performance pass requires enough repeated samples for stable p95; include distribution and confidence assessment, not a single optimistic run.
- Security and privacy assertions are absolute gates, not error-budget exceptions.
- If the RTO/RPO proof cannot be met, record actual measurements and rework DR architecture before production approval; do not mark a paper requirement as fulfilled.
- Capacity targets are approved but unverified until the actual services and load-test tools execute.
