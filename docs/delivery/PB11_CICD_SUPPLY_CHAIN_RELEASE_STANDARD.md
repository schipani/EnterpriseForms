# PB-11 — Secure CI/CD and Supply-Chain Release Standard

**Controlling decision:** `docs/decisions/ADR-PB-011-GITHUB-ACTIONS-SECURE-DELIVERY.md`  
**Approval:** October 17, 2026. **Implementation:** Pending.  
**Precedence:** Existing Level 2 controls, ADR-PB-010 residency, ADR-PB-003 adversarial tests, and ADR-PB-002 Stage A -> Stage B.

## Repository and code ownership

- Canonical Git repository (public on GitHub during development per ADR-PB-011 amendment A-21; private Azure DevOps project for production) with branch rulesets, protected main, mandatory pull requests/peer review, security reviewer CODEOWNERS on `src/trust-authority/`, `docs/security/`, `docs/trust-authority/`, `security_hardening/`, `infrastructure/security/`, key policies, credentials and authority DB migrations.
- Require explicit review for schema migrations, IAM/role/network policy, container base images and signing workflows. Validate that a business app maintainer cannot self-merge a Trust Authority workflow/policy change or disable required checks.
- All required documentation remains in the repo. Validate file completeness, required references and architecture decisions in CI; zip bundles are releases, not parallel sources of truth.
- Artifact version inputs (SDK, Node, lockfiles, package registries, tooling images, Github Actions SHA) pinned and reviewed; use dependency update policy with release provenance.

## Job-based pipeline gates

| Phase | Required controls |
|---|---|
| Pull request (untrusted) | Format/lint, unit tests, architecture boundaries, .NET build, frontend typecheck, OpenAPI deterministic generation/diff, SBOM and package dependency scans, secret scans, SAST, IaC scans, synthetic contract/integration checks. No privileged tokens or protected cloud writes. |
| Stage A security qualification | Run real Docker BusinessDb/AuthorityDb, SpiceDB, Vault Transit, API/Authority and required `SEC-P01`–`SEC-P12`; store result, failure injection trace and signed evidence with code SHA; block protected Stage B until passed. |
| Build and sign | Build hardened non-root app, worker, web and Authority images once; pin base digests; generate SBOM per image; scan image digest; publish digest to ACR; attach OCI-compatible signed provenance and SBOM attestations; record digest->commit/workflow linkage. |
| Deployment dev | Use environment-constrained Entra federation, isolated deployment identity and Bicep validation; verify provenance/signatures and digest before deployment. |
| Staging | Promote same digest. Exercise real Azure Entra, Managed HSM, Confidential Ledger, isolation, recovery, service availability, threat cases, residency and PB-09 load; record per-case evidence. |
| Production business deployment | Independent reviewer gate, current security evidence, signed digest/provenance verification, approved regions, Bicep diff review, migration compatibility, rollback strategy and rollout monitors. |
| Production Authority deployment | Additional **independent security-owner approval** and separately scoped identity; privileged changes cannot be triggered/approved through business deployment workflow. Extra journal/HSM/ACL/recovery regression tests. |

## Images, attestation and trust boundaries

Use OCI digest pinning for all runtime images. Choose one documented, verified OCI-compatible signing path: e.g. Notation with Azure Artifact Signing/authorized signing key *or* another approved cryptographic provenance tool. Artifact Signing is a **software supply chain credential**, distinct from the Azure Managed HSM record-integrity MAC keys; do not mix their access. Define signing subject, issuer/workflow trust identity, allowed registry paths and signature verification policy. Verify artifact identity, expected source revision, signature, SBOM, vulnerabilities, environment scope, image digest and software origin just before deploy. **Signature existence is not authorization to deploy.**

For GitHub Actions, set `permissions: contents: read` by default, restrict `id-token: write`/`attestations: write` to authorized jobs, pin third-party actions to full commit SHA, use environment protection rules, and bind federated credentials narrowly. New repositories/renames should consider immutable repository identity GitHub OIDC subject features where available; validate actual Microsoft Entra matching configuration, not only documentation.

## Secrets, personal data, deployment isolation

Synthetic CI data only on general GitHub-hosted runners. Never ship production DB extracts, document attachments, sensitive signed journal event bodies, customer logs, protected keys, production HSM backup/security-domain artifacts or real user identifiers into GitHub Actions artifacts. Use private Canadian-region runners/agents for tests that need customer-bearing material (prefer synthetic regardless). Customer logs, evidence, backups and archives stay in approved locations. Signed provenance may include repository/workflow metadata; classify and privacy-review it before publishing.

Workload identity federation has different identities/role scopes for `acr-build-push`, `business-dev/staging/prod`, `authority-dev/staging/prod`, IaC and migration jobs. Azure Container Apps managed identities only get runtime read/pull/required services, not a deployment or signature issuance principal. Business operators must be technically unable to mutate AuthorityDb/SpiceDB/HSM controls, even with legitimate business-deployment credentials.

## Migration and rollback boundaries

Business DB schema migrations use explicit forward/rollback compatibility stages and dedicated short-lived role. Authority DB or SpiceDB schema changes require separate approval and state migration/backup validation. Treat authorization-state recovery as immutable-journal reconciliation; never rollback to an older ACL snapshot that undoes revocation. Use independent canary rollout and health alerts, but don't confuse successful runtime health with passed Level 2 proof.

## Severity and exception policy prerequisites for PB-12

Build blocks on unauthorized cross-boundary access, critical/known exploitable vulnerabilities, secrets exposure, unsigned/unverified deployments, stale or missing mandatory evidence, missing CBOR/HMAC/Merkle verification, direct protected access without Authority and forbidden region usage. High findings require documented independent review, remediation and verified compensating controls before any pilot/production consideration. PB-12 will define exception owner and evidence retention; **no business-only waiver of mandatory Level 2 guarantees**.

## Acceptance and audit evidence

Implement and execute all test identifiers in `docs/delivery/PB11_CICD_ACCEPTANCE_TESTS.csv`. Produce per-release evidence and a machine-readable manifest linking commit, image digest, Build ID, SBOM digest, attestations/signature reference, IaC plan, migrations, security tests, region checks, required approver identities, staging and production verification. Keep customer-bearing evidence only in approved Canadian custody. No status may be labeled implemented until the pipeline is installed and tested against adversarial unauthorized PR, tampered image, missing signature, stolen business deploy identity, and Authority change attempts.
