# Pre-Build Engineering Blockers and Proof Backlog

The 14 product/architecture pre-build choices are **all accepted**. This does not establish implementation or operational readiness.

| ID | Status | Action and required proof |
|---|---|---|
| BUILD-00 | Pending | Commit canonical handoff into **one private Git repository** with protected main and verified manifest CI (PB-001/011) |
| BUILD-01 | Pending | Create `docs/dynamic-forms/architecture.md`, refine module ownership and confirm actual source/repository and database migrations (PB-01/02) |
| BUILD-02 | Pending | Create `docs/dynamic-forms/row-integrity-format.md` and `trust_authority/merkle-v1.test-vectors.json`; independently test serialization parity before trusting signatures |
| BUILD-03 | Pending | Stage A real Docker business DB/authority DB/SpiceDB/Vault Transit journal, HMAC/Merkle, revocation, 12 adversarial test evidence (PB-03) |
| BUILD-04 | Pending | Confirm real Azure Managed HSM/Confidential Ledger/Canada East recovery availability and RTO 4h/RPO 15m with approved Canadian-only plan (PB-09/10) |
| BUILD-05 | Pending | Provide real CI provenance, signed artifact promotion, independent production release approval, authenticated evidence archive and PB-12 gate (PB-11/12) |
| BUILD-06 | Pending | Implement and review PB-14 design gallery, three functional representative flows, automated and manual accessibility proof; preserve role-based permission boundaries |
| BUILD-07 | Pending | Implement CSV/JSON import, full-fidelity authorized export and secure offboarding before real tenant production onboarding (PB-13) |

**All rows represent work not yet accomplished**. Static source/manifest validations verify packaging, not the implementation, regulatory compliance or demonstrated attacks. Require risk owners, real test results and independent signoff before production.
