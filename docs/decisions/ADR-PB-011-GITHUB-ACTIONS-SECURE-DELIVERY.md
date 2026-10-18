# ADR-PB-011 — Private GitHub, GitHub Actions, ACR and Secure Image Promotion

**Status:** ACCEPTED by product owner, October 17, 2026  
**Finding:** PB-11 — CI/CD, supply chain security, production approvals  
**Applies to:** the one canonical Git repository accepted in ADR-PB-001.  
**Supporting standard:** `docs/delivery/PB11_CICD_SUPPLY_CHAIN_RELEASE_STANDARD.md`

## Binding decision

Use a **private GitHub repository**, GitHub Actions for CI/CD, **Azure Container Registry (ACR)** for OCI images and related artifacts, **Bicep** for infrastructure, and independently deployed containers in Azure Container Apps (ADR-PB-005).

Every build must be reproducibly traceable to a reviewed commit, pass mandatory application, architecture, API, security and vulnerability controls, produce a software bill of materials (SBOM), and publish **signed/attested immutable image digests**. Promote *the same digest* through development, staging and production; never rebuild a new image for promotion or deploy via a mutable tag alone. Artifact sign/attestation and **verification prior to deployment** are both mandatory; registry storage of signatures by itself is not enforcement.

**Security authority changes are independently approved:** the Trust Authority API, SpiceDB schema, authority PostgreSQL, security journal, HSM/key policies, signing identities and recovery runbooks have security-owner review and a separately permissioned deployment workflow/identity. The business application's deployment principal cannot approve or modify Authority infrastructure, secrets or signing policies, and the Authority release process cannot be bypassed through a monorepo-wide workflow. Trust boundary is organizational and credential-based, not just separate container images.

## Required controls

1. Protect `main` and release branches with non-bypassable repository/ruleset requirements, independent review, CODEOWNERS for security and migrations, passing checks, blocked force-push and protected environments with required production approvers. The separation must survive compromise of an ordinary business API deploy credential.
2. GitHub Actions uses ephemeral **GitHub OIDC -> Microsoft Entra federated workload identity** (subject/issuer/audience strictly constrained to approved repository identity, protected environment and workflows). Use separate identities for build/push, business deploy, Authority deploy, Bicep permissions and migrations. No stored Azure client secret or registry password. Restrict `id-token: write` to jobs requiring federation.
3. Pin third-party GitHub Actions by immutable commit SHA; default `GITHUB_TOKEN` permissions to read-only, elevate only per job; restrict reusable workflows and PR event privilege. Untrusted forks/PRs receive no production credentials, privileged OIDC identity, signing capability or write access to protected artifacts.
4. For each change, run deterministic compilation and unit/integration tests, .NET and TypeScript lint/typecheck, feature-boundary and database naming checks, generated OpenAPI drift checks, secret and dependency scans, SAST, IaC scanning, API contract tests, accessibility tests and mandatory security gate tests at the correct phase. Define clear severity policy and controlled exceptions; **never accept critical exploitable findings or Level 2 control failures through a routine waiver**.
5. Build distinct hardened and non-root web, business API, business worker, and Trust Authority OCI images; minimize base images, pin base image digests, avoid shipping build tools/secrets, generate SBOM and vulnerability report per digest, attach provenance and OCI-compatible signature/attestation, and verify trusted issuer/identity, intended source ref, source SHA, workflow and digest. Define keyless trust or controlled signing identity and its rotation.
6. Store immutable release evidence (source SHA, image digest, signed provenance, SBOM, test evidence indexes, Bicep plan, schema migration approval, required reviewers, rollback path, deployment outcome) without placing customer records, file contents, tokens, or sensitive logs in GitHub-hosted runners or artifacts. Follow Canadian residency ADR-PB-010 for all customer-bearing data and security evidence. Use synthetic data in generic hosted CI; isolate real data tests to approved Canadian environments and runners.
7. Apply and inspect Bicep plans and policy checks before change; use production-safe migration practices (expand/contract, review and restore drills); separate restricted migrations from application runtime. Production requires explicit human approval and evidence verification. Destructive migrations and authority key/graph changes require elevated independent approval.
8. Dev -> staging -> production promotions must reference digest identifiers (not `:latest`). Block if signature, provenance, supported dependencies, required tests, residency, security proof or environment approvals cannot be verified. Include distinct release permissions for business services vs. Authority.
9. Runtime supply-chain check: confirm whether Azure Container Apps or the selected deployment admission mechanism actually enforces provenance/signatures. Until an enforcement feature is demonstrated, **pre-deployment verification is mandatory and its limitations documented**; no unsupported assumption of native image-policy admission control.
10. Use release-scoped rollback and feature flags for business code, and a separately verified recovery protocol for authority/stateful changes. Never roll back authoritative permission revocations, security journal checkpoints, schema or HSM key state by merely redeploying an old container digest.

## Scope and status

This ADR approves a release **design and implementation standard**, not an existing GitHub repo, configured Actions runs, cloud registry, signed image or proven security posture. GitHub ownership, branch protection rules, CI environments, service principals, scopes and independent reviewers must be deployed and tested. Software release risk owner/exception/evidence policy is decided under PB-12; approval here is not a production release authorization.

## References

- [Microsoft: GitHub Actions sign-in with OIDC](https://learn.microsoft.com/en-us/azure/developer/github/connect-from-azure-openid-connect)
- [GitHub Docs: OIDC in Azure](https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-in-azure)
- [GitHub Docs: artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)
- [Microsoft: OCI image signature with Notation and Artifact Signing](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-tutorial-sign-verify-notation-artifact-signing)
