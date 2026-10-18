# ADR-PB-005 — Docker Compose Development and Azure Container Apps with Bicep

- **Status:** Accepted
- **Decision date:** 2026-10-17
- **Approval:** Product owner explicitly approved the recommended pre-build Finding PB-05 approach
- **Finding:** PB-05: deployment/infrastructure baseline
- **Companion:** `docs/infrastructure/PB05_DEPLOYMENT_BLUEPRINT.md`
- **Scope:** Infrastructure architecture decision, **not** an assertion that environments or tests have been deployed.

## Binding decision

1. **Local experience.** Deliver one reproducible Docker Compose stack with discrete `development`, `integration-test`, and `destructive-security-test` profiles (or equivalently named profiles documented in the runbook). Containers cover the React web app, .NET business API/BFF, durable .NET worker, independently deployed Authorization Authority, two isolated PostgreSQL 18 instances, SpiceDB and its separate datastore as required by its chosen supported topology, HashiCorp Vault Transit, blob emulator, and isolated test identity/scanning/audit-ledger adapters. Development mocks must be conspicuously labeled, fail closed when accidentally selected outside their allowlisted test environment, and never be promoted to production.
2. **Production hosting.** Use **Azure Container Apps** to host independently versioned and deployed web, business API, business background worker, and Authorization Authority container images. Business application remains a modular monolith; the Trust Authority is independent. Container Apps revision, ingress, network, scaling and job configuration shall be documented and tested. The worker must durably resume after container termination and never depend on an in-memory-only queue for committed work. SPA upload persistence applies to in-app navigation, not browser/tab shutdown.
3. **Infrastructure as code.** **Bicep** is the authoritative Azure infrastructure-as-code choice, with separately parameterized development, staging and production environments and a reproducible review/approval process. Avoid separate Terraform state tooling until a new ADR authorizes the change. Plan for Azure Container Registry and digest-based image promotion; select CI/CD provider under PB-11.
4. **Independently administered trust boundary.** BusinessDb and AuthorityDb must use **distinct PostgreSQL server/administration scopes**, identities, credentials, recovery/backup authority, privileged network paths and deployment approvals. The Authority, its security journal, SpiceDB relationship-write plane, signing HSM, and trusted checkpoints must not be administered by the same identities or pipelines whose compromise is within the BusinessDb DBA threat model. Separate Azure subscription/management boundary is recommended where necessary to *actually enforce* independence; separate containers alone or separate DBs on the same superuser-administered server are insufficient. Business runtime has no AuthorityDb admin, SpiceDB write, HSM signing, or ledger administration credentials.
5. **Cryptography.** Vault Transit is the only approved local remote-signing integration provider; production must use **Azure Managed HSM**, with key custody restricted to the independent Authority. No production fallback to Vault, local keys or mock signer. Production uses actual Azure Confidential Ledger and independently archived security events; local ledger/test-double only exercises interface behavior, not cloud trust guarantees.
6. **Networking and identity.** Restrict Authority API ingress to authenticated approved callers on a private path. Use separate workload managed identities and least-privilege RBAC for web/API/worker/authority; no secrets in Docker images, source control, public frontend or unredacted logs. Define secrets/identity bootstrap without placing shared admin keys in Compose. Apply private service access for databases/storage/HSM where supported and verify firewall/DNS/egress.
7. **Promotion and checks.** Environment flow: local dev → isolated Azure staging with actual Azure identities/HSM/ledger → Azure production. Build artifacts are immutable images promoted by digest after tests/approval, not rebuilt from source separately in production. Stage A security tests run against realistic Compose isolation; critical attacks rerun in staging before Level 2 production. Test production-equivalent backup/recovery, key-version availability, migrations and container restarts.
8. **Pending decisions not silently resolved.** Actual Azure region/replication, retention, encryption jurisdiction and data residency require PB-10; precise size, node/replica minima, throughput and RTO/RPO require PB-09; pipeline technology, artifact signing details, vulnerability scan gates and promotion approvals require PB-11; consolidated OpenAPI requires PB-06. Do **not** assume a region or make a claim of legal compliance before these approvals.

## Explicit rejection criteria

- A single privileged PostgreSQL administrator for both BusinessDb and AuthorityDb.
- A shared Azure workload identity with both business API and Authority HSM signing permissions.
- Trust decisions implemented in the business API in place of Authority calls.
- Vault Transit, mock ledger, development/test account or locally exportable signing keys in production.
- Development credentials committed to git, CI output, container image layers, or used against staging/production.
- A reliable-upload or reliable-job claim resting solely on SPA Worker memory / volatile background queue.

## Acceptance evidence before marking implemented

- `docker compose config` succeeds for every profile; health checks and setup/migration commands run from clean host volumes.
- Separate BusinessDb/AuthorityDb admin/runtime users and failed cross-plane auth tests recorded.
- Reproducible builds and Bicep validation/deployment to isolated staging; actual identity, private-network and HSM permission tests.
- Twelve PB-03 local adversarial security tests pass, followed by mandated Azure staging equivalents and independent review.
- Worker restart/resume, upload interruption/retry, failure handling and deployment rollback demonstrated.
- Environment configuration matrix, threat-boundary diagram, and evidence links maintained in repo.

**Status note:** Accepted architectural choice only. No claim that Docker stacks, Bicep modules, Azure resources or test suites are implemented.
