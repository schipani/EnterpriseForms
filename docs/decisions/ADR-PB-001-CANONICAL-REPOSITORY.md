# ADR-PB-001 — One Canonical Git Repository for Code and Specifications

- **Status:** Accepted
- **Decision date:** 2026-10-17
- **Source:** Pre-build readiness audit, Finding PB-01; explicit user approval.
- **Scope:** Enterprise Forms platform and independent Authorization Authority code, infrastructure, docs, and tests.

## Decision

Use **one Git repository** as the authoritative, version-controlled source of truth for the entire system: all application source, independent Trust Authority source, infrastructure-as-code, deployment/test assets, `CLAUDE.md`, `CLAUDE-DESIGN.MD`, and every normative referenced security/product/design specification.

The repository must contain: `docs/architecture/`, `docs/security/`, `docs/trust-authority/`, `docs/form-model/`, `docs/api/`, `docs/decisions/`, `docs/testing/`, `src/backend/`, `src/frontend/`, `src/trust-authority/`, `tests/`, `infrastructure/docker/`, `infrastructure/azure/`, and `scripts/` (adapt paths to the existing repository without changing module/trust boundaries).

Generate downloadable ZIP archives only as snapshots of a *specific repository commit*. They are not sources of truth and must not overwrite newer reviewed decisions.

## Mandatory safeguards

1. Maintain a machine-readable normative-file manifest with repository-relative paths and SHA-256 digests. Validate required files, broken local links/references, duplicated competing copies, and checksums in CI. The manifest must be updated with reviewed specification changes; hashes do not replace Git review.
2. Require recorded ADRs for changes to primary technology, trust boundary, data protection, public contracts, persistence strategy, and deployment/security assumptions.
3. Establish explicit document ownership and precedence. `CLAUDE.md` owns core behaviour and security invariants; `CLAUDE-DESIGN.MD` owns UI and client architecture subject to those invariants; domain-specific normative security/trust documents govern their respective detailed protocols. On conflict, stop and report the exact conflicting passages for review; never silently select an unsafe interpretation.
4. Do not proceed past the pre-build Gate 0 until the full dependency tree, including referenced C#/SQL/OpenAPI examples, has been copied to the canonical repo and the manifest check passes. In particular, reconcile `security_hardening/SECURITY_CONTRACTS.reference.cs` and `security_hardening/audit-consolidation.reference.sql` with the latest requirements.
5. Use code review, protected main branch, pinned dependencies and reproducible builds. The repository's presence does not imply deployability, validated security, or authorization for production operations.

## Rationale

A single commit captures consistent code, deployment and security decisions. Separate unpinned ZIPs/specs risk incomplete or conflicting instructions for Claude Code and human engineers.

## Consequences

- **Positive:** Traceability, repeatable onboarding, coherent CI, fewer drift-related security defects.
- **Tradeoffs:** More files in one repository and need for strict isolation of production secrets, deployments and administrative credentials. Do not place real secrets, HSM keys, certificates, production environment values or confidential customer data in Git.
- **Explicitly not decided here:** PB-02 initial build scope and subsequent pre-build issues. Those will receive separate ADRs after user approval.

## Acceptance conditions

- All normative referenced paths exist within the same committed repository.
- Automated completeness/reference/checksum checks fail on a missing or stale dependency.
- A documented fresh-clone setup finds every required specification without external ZIPs.
- CI and human review can associate a deployed image with source commit and specification revision.

## Amendment A-21 (2026-10-18, approved by the product owner)

The existing GitHub repository `github.com/schipani/EnterpriseForms` **intentionally remains public during
development**. Making it private is **not** a prerequisite for development or for the Stage A gate. The production
source-control environment will be a **private Azure DevOps project inside an organization**; that migration is a
deferred infrastructure and governance milestone (see `docs/delivery/BUILD00_REPOSITORY_PROTECTION.md` §5).

Consequences for this ADR:

- Safeguard 5 ("code review, protected main branch, pinned dependencies") is applied with the controls a public,
  user-owned GitHub repository supports: active rulesets on `main` and `v*` tags, required status checks, pull-request
  review, signed commits, restricted and SHA-pinned Actions, secret scanning with push protection, Dependabot and CodeQL.
- Controls that **cannot** be enforced until the Azure DevOps milestone — organization teams and therefore independently
  authorized CODEOWNERS review, protected deployment environments with segregated approvers, Entra workload-identity
  federation — are recorded as deferred and must never be described as implemented or validated.
- The "no real secrets, HSM keys, certificates, production values or customer data in Git" tradeoff becomes a hard
  rule with history verification: the full object history was scanned on 2026-10-18 (487 blobs) and contains only
  labelled synthetic placeholders; secret scanning and push protection are enabled; any future finding is a Critical
  incident requiring history rewrite and credential rotation.
- The independent cryptographic review (Stage A requirement 11) is unaffected and remains mandatory.
