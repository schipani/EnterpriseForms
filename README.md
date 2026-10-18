# Enterprise Forms — Canonical Claude Build Handoff

**Approved pre-build decisions:** ADR-PB-001 through ADR-PB-014 (all approved 2026-10-17). The three key choices—encryption/search, mandatory security proof, and Canada-first residency—are incorporated as PB-004, PB-003 and PB-010 respectively.

**Package type:** version-consistent *documentation and reference-code snapshot*, not a Git repository clone, runnable system, deployed infrastructure or completed compliance certification. Commit the extracted contents into the **single private Git repository** approved under PB-001, along with real application code. Avoid extracting historical ZIP files over this snapshot.

## Read order for Claude Code

1. `CLAUDE.md` — core functionality, Level 2 security and feature delivery requirements.
2. `CLAUDE-DESIGN.MD` — UI architecture, 69-screen requirements and feature mapping.
3. `docs/decisions/PREBUILD_DECISION_REGISTER.md` — owner-approved PB-001...PB-014 decisions; use the individual ADRs to resolve scope.
4. `security_hardening/SECURITY_SIMPLIFICATION_STANDARD.md`, `SECURITY_THREAT_MODEL.md` and `SECURITY_GAP_ASSESSMENT.md`.
5. `trust_authority/` and `form_model/` — trust/consistency/cryptography/recovery/related-forms reference contracts.
6. `docs/ui/PB14_ENTERPRISE_DESIGN_VALIDATION_STANDARD.md`, the screen matrix, UI tests and representative-screen acceptance.

### Precedence and design constraints

- Binding Level 2 safety requirements in `CLAUDE.md` and security/trust protocols cannot be weakened by the UI, performance, API or shipping schedule. If normative documents conflict, report exact file/heading and request resolution instead of selecting an insecure interpretation.
- Use **feature-owned vertical slices**, independent Authorization Authority, React + Tailwind frontend and separate design-token package, .NET 10 Minimal APIs, generated `/api/v1` client, Docker Compose local and Azure Bicep + Container Apps staging/production.
- Default to approved near-black action buttons; organization branding/color/font tokens must pass accessibility validation with manual WCAG 2.2 AA-targeted review.
- Working sequence: Stage A Docker Level 2 security proof → Stage B complete Accused/AccusedAddress/documents workflow → PB-14 gallery + 3 representative UIs signoff before **broad screen expansion** → import/export/offboarding and remaining required features → Azure staging + independent security proof → production authorization.
- Internal sample code/SQL/test plans remain **references**, not evidence of executing, deploying or testing a working product.

## Phase 0 unresolved required engineering outputs

The current documentation references three **future generated or finalized artifacts that are NOT supplied as complete approved specifications**:

| Required path | Resolution gate |
|---|---|
| `docs/dynamic-forms/architecture.md` | Claude to create and review actual system architecture and data flows during Stage 0 repo inspection |
| `docs/dynamic-forms/row-integrity-format.md` | Approve language-independent deterministic typed-value canonicalization; do not rely on default JSON serializer or unreviewed placeholder |
| `trust_authority/merkle-v1.test-vectors.json` | Produce cross-language cryptographic test vectors from the reviewed canonical protocol, independently verify expected values, and then commit |

**Gate 0:** These missing deliverables must exist, be reviewed, and pass the specification reference validator in strict mode before Stage A security implementation is considered complete. Their absence is an explicit blocker, not an accidental omission. Other operational decisions—including region-specific Managed HSM recovery and real 15-minute RPO proof—remain deployment validation tasks, not waived by approved ADRs.

## Package integrity and repeatability

- `spec-files.json` enumerates the exact canonical snapshot inputs with SHA-256 hashes. `SHA256SUMS.txt` contains the same listed byte digests.
- `scripts/validate-specifications.py` checks file integrity, referenced paths, the 14 accepted ADRs, and the 69-screen ownership map. Run `python scripts/validate-specifications.py` (baseline status shows known Stage 0 missing artifacts); strict validation (`--strict`) must pass after the 3 required outputs are built and the manifest is regenerated under review.
- `scripts/generate-spec-manifest.py` is used to regenerate the checksums and manifests in the canonical repository. Run only after reviewed changes; do not automatically rewrite hashes on untrusted pull requests.
- Git commit and CI integration are **not** established by delivering a ZIP; these are mandatory tasks for Claude under PB-001/PB-011.
- ZIP archives from earlier conversations are excluded: they are superseded snapshots, not parallel sources of truth.
