# Pre-Build Engineering Blockers and Proof Backlog

The 14 product/architecture pre-build choices are **all accepted**. This does not establish implementation or operational readiness. Phase 0 (repository scaffold and Gate 0 artifacts) is complete as described in `docs/PHASE0_STATUS.md`; Stage A has not started.

| ID | Status | Action and required proof |
|---|---|---|
| BUILD-00 | Partially done | Repository, manifest CI (`ci.yml` strict spec validation), CODEOWNERS and Dependabot exist locally. **Pending:** push to the private GitHub repository, protected `main` rulesets, real reviewer teams in CODEOWNERS, Entra OIDC federation (PB-001/011). |
| BUILD-01 | Done (Phase 0) | `docs/dynamic-forms/architecture.md` created; module ownership refined in `docs/architecture/FEATURE_OWNERSHIP_MATRIX.md` and enforced by architecture tests. Database migrations do not exist yet (Phase 1). |
| BUILD-02 | Done pending independent review | `docs/dynamic-forms/row-integrity-format.md`, `trust_authority/merkle-v1.test-vectors.json` and `trust_authority/row-integrity-v1.test-vectors.json` created; serialization parity verified by Python, C# and Node implementations in CI. **Pending:** independent human cryptographic review, real Vault Transit HMAC vectors (SIG tests), signed-column registry for production tables. |
| BUILD-03 | Pending | Stage A real Docker business DB/authority DB/SpiceDB/Vault Transit journal, HMAC/Merkle, revocation, 12 adversarial test evidence (PB-03). Compose stack and `security-gate-a.yml` skeleton exist; the gate currently fails BLOCKED by design. |
| BUILD-04 | Pending | Confirm real Azure Managed HSM/Confidential Ledger/Canada East recovery availability and RTO 4h/RPO 15m with approved Canadian-only plan (PB-09/10) |
| BUILD-05 | Pending | Provide real CI provenance, signed artifact promotion, independent production release approval, authenticated evidence archive and PB-12 gate (PB-11/12). Only PR gates exist. |
| BUILD-06 | Pending | Implement and review PB-14 design gallery, three functional representative flows, automated and manual accessibility proof; preserve role-based permission boundaries. Only tokens + contrast validator exist. |
| BUILD-07 | Pending | Implement CSV/JSON import, full-fidelity authorized export and secure offboarding before real tenant production onboarding (PB-13) |

**All "Pending" rows represent work not yet accomplished**. Static source/manifest validations verify packaging, not the implementation, regulatory compliance or demonstrated attacks. Require risk owners, real test results and independent signoff before production.
