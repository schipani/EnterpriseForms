# PB-05 — Infrastructure implementation blueprint (normative handoff)

**Decision:** `../decisions/ADR-PB-005-DOCKER-AZURE-BICEP-DEPLOYMENT.md`.
**State:** Requirements and task blueprint only; environments not yet provisioned or tested.

## Repo locations (to be created by implementation)

```text
infrastructure/
  docker/
    compose.yaml                  # shared service declarations
    compose.dev.yaml              # development overrides, dev-only identity/emulators
    compose.integration.yaml      # disposable integration environment
    compose.security-test.yaml    # destructive tests, dedicated volumes and users
    env.example                   # no real secrets
  azure/
    main.bicep
    modules/                      # segmented web/business and authority resource groups/scopes
    environments/
      dev.bicepparam
      staging.bicepparam
      prod.bicepparam
  scripts/
    bootstrap-local.ps1
    verify-environment.ps1
    test-trust-isolation.ps1
```

These names are implementation guidance, not a claim that the files exist. Design Compose profiles/overrides so tests cannot accidentally erase developer data or reach production credentials.

## Deployment and administration map

| Component | Development | Staging/production | Administrative trust |
|---|---|---|---|
| Web | Vite/react container | Container Apps-hosted web artifact | Business app scope |
| Business API/BFF | .NET API container | Separate Container App, same-origin path through controlled gateway | Business app scope |
| Business Worker | .NET worker container | Independent Container App / job as appropriate | Business app scope, durable queue/outbox |
| Business PostgreSQL | PostgreSQL 18 instance A | Azure Database for PostgreSQL Flexible Server A | Business DB admins only |
| Authorization Authority | .NET Authority container | Internal ingress restricted Container App | Trusted/security admins only |
| Authority PostgreSQL | PostgreSQL 18 instance B | Separate Azure Database for PostgreSQL Flexible Server B | Trusted/security admins only |
| SpiceDB | SpiceDB container | Supported independently controlled deployment | Security relationship-write privileges only |
| SpiceDB datastore | Separate local DB, unless supported alternative selected | Independently controlled store as required | Trusted/security admins only |
| Signing | Vault Transit isolated container | Azure Managed HSM | Authority-managed HSM privilege only |
| Documents | Blob emulator + scanner test adapter | Azure Blob Storage + malware inspection pipeline | Scoped business storage; verified Authority release |
| Evidence | Archived local test journal + ledger double | Durable independent event archive + Azure Confidential Ledger | Trusted administrators |

## Build order / implementation gates

1. Establish isolated networks and nonoverlapping admin/service identities; secrets injected at deployment, never hard-coded.
2. Bring up both databases, SpiceDB, Vault Transit, and test doubles; run clean migrations using separate elevated migration identities.
3. Run Trust Authority, API, worker, and web with health checks and minimal authenticated read/write flow.
4. Execute PB-03 local `SEC-P01`–`SEC-P12` in a disposable test profile; verify no direct cross-boundary privilege.
5. Provision Azure staging with Bicep, valid workload identities, private network topology, Azure Managed HSM and Confidential Ledger; rerun cloud proof.
6. After PB-09/10/11, pin capacity/DR region/provisioning, sign and promote immutable images, deploy production only after security and privacy sign-off.

## Operational safety

- No production Docker Compose secrets, `latest` image tags, test signing providers or ledger mocks.
- Perform preview/what-if and reviewed Bicep deployment plans for any sensitive authority scope.
- Fail closed when a trust dependency is unreachable; degraded UI remains available only for explicitly unprotected operations.
- Cross-service timeout/retry operations require bounded idempotency and coherent revision barriers; never use container restarts as a consistency algorithm.
- Restrict authority-service egress/ingress and verify actual role assignment; isolation must be empirically tested, not diagrammed only.
