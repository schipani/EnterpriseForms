# Pre-Build Engineering Blockers and Proof Backlog

The 14 product/architecture pre-build choices are **all accepted**. This does not establish implementation or operational readiness. Phase 0 is complete (`docs/PHASE0_STATUS.md`); Stage A is implemented and Level A tested but **BLOCKED** on independent cryptographic review and human repository/identity administration (`docs/STAGE_A_STATUS.md`).

| ID | Status | Action and required proof |
|---|---|---|
| BUILD-00 | Partially done | Everything expressible in code is committed: CODEOWNERS, importable `main` ruleset (`.github/rulesets/main-branch-protection.json`), CI gates with minimum-test enforcement, Gate A workflow. **Pending (human/admin, cannot be done from code):** items 3.1–3.10 of `docs/delivery/BUILD00_REPOSITORY_PROTECTION.md` — private repo settings, real teams, ruleset import, environments with reviewers, Entra OIDC federation, secret/code scanning, signed commits. |
| BUILD-01 | Done (Phase 0) | `docs/dynamic-forms/architecture.md` created; module ownership refined in `docs/architecture/FEATURE_OWNERSHIP_MATRIX.md` and enforced by architecture tests. Database migrations do not exist yet (Phase 1). |
| BUILD-02 | Done pending independent review | Format, vectors and the Stage A production signed-column registry (form_records v2, field_string_values v1) with CI inventory exist; F-03 decided; real Vault Transit tags are produced and verified for every Stage A record. **Pending:** independent human cryptographic review (`docs/security/INDEPENDENT_CRYPTO_REVIEW_PACKAGE.md`), explicit same-key LocalHmac-vs-Vault vector (SIG-11). **Stage A is BLOCKED on this item.** |
| BUILD-03 | Level A executed locally; CI run pending | Independent Authority (own PostgreSQL, SpiceDB projection with marker CAS, Vault Transit signer, journal + archive), signed immutable staging with Merkle manifests, minimal record workflow and SEC-P01..SEC-P12 implemented; executed locally against the Docker stack with evidence manifest (`docs/STAGE_A_STATUS.md`). **Pending:** execution of `security-gate-a` on GitHub-hosted runners (BUILD-00), full CONS/MRK/SIG/PG/DR matrices, Azure Level B. |
| BUILD-04 | Pending | Confirm real Azure Managed HSM/Confidential Ledger/Canada East recovery availability and RTO 4h/RPO 15m with approved Canadian-only plan (PB-09/10) |
| BUILD-05 | Pending | Provide real CI provenance, signed artifact promotion, independent production release approval, authenticated evidence archive and PB-12 gate (PB-11/12). Only PR gates exist. |
| BUILD-06 | Pending | Implement and review PB-14 design gallery, three functional representative flows, automated and manual accessibility proof; preserve role-based permission boundaries. Only tokens + contrast validator exist. |
| BUILD-07 | Pending | Implement CSV/JSON import, full-fidelity authorized export and secure offboarding before real tenant production onboarding (PB-13) |

**All "Pending" rows represent work not yet accomplished**. Static source/manifest validations verify packaging, not the implementation, regulatory compliance or demonstrated attacks. Require risk owners, real test results and independent signoff before production.
