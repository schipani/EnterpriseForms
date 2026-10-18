#!/usr/bin/env python3
"""Generate docs/dynamic-forms/control-traceability.csv — the executable Phase 0 checklist.

Tracks SEC-01..SEC-42 (SECURITY_THREAT_MODEL.md), G01..G18 (SECURITY_GAP_ASSESSMENT.md), SIM-01..SIM-42
(security_hardening/SECURITY_ACCEPTANCE_TESTS.md) and the six ADR-SEC-2026-10-16-01 decisions.

Status vocabulary (CLAUDE.md §0): Designed | Implemented | Tested | Waived. A control is "Tested" only when an
executable test in this repository exercises it and passes in CI; "Designed" means the normative text exists.
Phase 0 evidence is limited to the integrity format/vectors, provider policy guards and architecture tests.
"""
from __future__ import annotations

import csv
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "docs" / "dynamic-forms" / "control-traceability.csv"

# Controls with Phase 0 evidence beyond the normative text. Everything else is Designed.
PHASE0_EVIDENCE: dict[str, tuple[str, str, str]] = {
    # id: (status, evidence, note)
    "SEC-38": ("Tested", "docs/dynamic-forms/row-integrity-format.md; trust_authority/row-integrity-v1.test-vectors.json; tests/integrity; scripts/integrity/verify_vectors.py; scripts/integrity/verify-vectors.mjs",
               "Canonical format frozen; NULL/decimal/timestamptz/Unicode/UUID/JSON vectors pass in Python, C# and Node. Registry coverage of production tables is Phase 1A1."),
    "SEC-42": ("Designed", "src/backend/EnterpriseForms.Infrastructure/Persistence/Conventions; tests/architecture/DatabaseNamingTests.cs",
               "Naming inventory executes over the (empty) EF model; signed-column inventory test arrives with the first protected table."),
    "G03": ("Designed", "trust_authority/merkle-v1.test-vectors.json; src/shared/EnterpriseForms.Integrity/Merkle; tests/integrity/MerkleV1VectorTests.cs",
            "Merkle v1 wire format frozen and cross-language verified (MRK-23 partial). Authority leaf index, commit protocol and MRK-01..32 remain open."),
    "G04": ("Designed", "src/authority/AuthorizationAuthority.Api/Composition/SigningProviderPolicy.cs; tests/features/AuthoritySigningPolicyTests.cs",
            "Provider selection by deployment config only; Production refuses non-HSM providers at host start (tested). Providers themselves and SIG-01..14 are Stage A."),
    "G05": ("Designed", "docs/dynamic-forms/architecture.md §1",
            "Hostile-business-DB-writer tier selected for protected workloads; blocked until Stage A/B evidence."),
    "G08": ("Designed", "infrastructure/docker/compose.yaml; infrastructure/docker/postgres/pg_hba.*.conf; docs/PHASE0_STATUS.md §4.2",
            "Local Compose: separate clusters/credentials, internal authority network and pg_hba gateway rejection demonstrated live. Roles, RLS, Azure isolation and PG-01..20 remain open."),
    "G15": ("Designed", ".github/workflows/ci.yml; .github/dependabot.yml; Directory.Packages.props; .github/CODEOWNERS",
            "PR gates, pinned actions/packages and Dependabot scaffolded; SBOM/signing/SAST/secret-scan jobs pending PB-11 implementation."),
    "G18": ("Designed", "docs/dynamic-forms/row-integrity-format.md §3.1; tests/integrity/RowIntegrityV1VectorTests.cs",
            "Versioned signed-column registry with absent≠NULL semantics and unregistered-column rejection tested."),
    "SIM-38": ("Designed", "docs/dynamic-forms/row-integrity-format.md §1",
               "Format documents that HMAC provides no confidentiality; no unkeyed digest of plaintext is used anywhere."),
}

# Stage A (2026-10-18) evidence: real Docker Level A stack (ADR-PB-003), SEC-P01..SEC-P12 executed by
# tests/security/EnterpriseForms.SecurityTests with artifacts/security/gate-a-evidence.json. "Tested" here means
# Level A (Docker) only; Level B (Azure) and independent cryptographic review remain open (docs/STAGE_A_STATUS.md).
GATE_A = "tests/security/EnterpriseForms.SecurityTests; artifacts/security/gate-a-evidence.json (security-gate-a)"
STAGE_A_EVIDENCE: dict[str, tuple[str, str, str]] = {
    "SEC-01": ("Tested", GATE_A + " SEC-P01", "Forged owner_user_id/owner_team_id in BusinessDb never grants; authority owner edge unchanged (Level A)."),
    "SEC-02": ("Tested", GATE_A + " SEC-P02, SEC-P03", "Forged record_shares/record_share_fields/team_memberships rows never grant; authority commands are the only grant path (Level A)."),
    "SEC-06": ("Tested", GATE_A + " SEC-P04, SEC-P05, SEC-P10", "Mutation, signature flip, deletion, replay and whole-DB restore fail closed against the authority head/manifest (Level A)."),
    "SEC-38": ("Tested", "docs/dynamic-forms/row-integrity-format.md; tests/integrity; scripts/integrity; tests/architecture/SignedColumnRegistryTests.cs",
               "Canonical format frozen and cross-verified; Stage A production registry (form_records v2, field_string_values v1) inventoried in CI."),
    "SEC-42": ("Tested", "tests/architecture/SignedColumnRegistryTests.cs; tests/architecture/DatabaseNamingTests.cs",
               "Signed-column inventory and naming rules run over the compiled Stage A EF model in CI."),
    "G01": ("Implemented", "src/authority/*; " + GATE_A, "Independent Authorization Authority with its own PostgreSQL store, SpiceDB projection, journal and operator plane; Level A tests pass. Level B/IAM proof open."),
    "G02": ("Implemented", "src/authority/AuthorizationAuthority.Application/Features/{SecurityCommands,Projection,Journal}; " + GATE_A + " SEC-P06, SEC-P07, SEC-P09, SEC-P12",
            "Per-organization gate, ordered projection with SpiceDB marker CAS, archive-before-effective, idempotent crash recovery demonstrated in Docker. CONS-01..30 full matrix and Azure staging remain open."),
    "G03": ("Implemented", "src/authority/AuthorizationAuthority.Application/Features/Content/ContentService.cs; " + GATE_A + " SEC-P04, SEC-P05",
            "Authority-held complete leaf index, prepare/commit CAS, VerifyRecordProjection with full-set equality; MRK-01..32 full matrix open."),
    "G04": ("Implemented", "src/authority/AuthorizationAuthority.Infrastructure/Signing; " + GATE_A + " SEC-P08, SEC-P11",
            "Vault Transit provider with per-organization non-exportable keys behind the command-validated coordinator; no sign-any-bytes endpoint. Azure Managed HSM provider NOT implemented; SIG-11 same-key vectors not executed; independent review open."),
    "G05": ("Designed", "docs/dynamic-forms/architecture.md §1; docs/STAGE_A_STATUS.md", "Hostile-business-DB-writer tier selected; Level A evidence exists, Level B and production approval do not."),
    "G08": ("Implemented", "infrastructure/docker/postgres/*; src/*/Persistence/*RuntimeGrants.cs; " + GATE_A + " SEC-P11",
            "Separate clusters/admins, runtime roles without DDL/DELETE on signed tables, authority-net internal; PG-01..20 and Azure isolation open."),
    "G11": ("Designed", "src/authority/AuthorizationAuthority.Infrastructure/Archive; " + GATE_A + " SEC-P12",
            "Append-only archive of full journal bodies gates effectiveness (Azurite test double). WORM/ledger and SIEM delivery open."),
    "G14": ("Designed", "src/authority/AuthorizationAuthority.Application/Features/Journal; " + GATE_A + " SEC-P09, SEC-P10",
            "Replayable journal bodies archived before Effective; restore of BusinessDb proven harmless. Authority restore/epoch fencing and DR-01..36 open."),
    "G15": ("Designed", ".github/workflows/*.yml; .github/rulesets/main-branch-protection.json; docs/delivery/BUILD00_REPOSITORY_PROTECTION.md",
            "Gate A workflow with minimum-expected-tests; branch ruleset authored; repository settings and SBOM/signing pending BUILD-00/05."),
    "G18": ("Tested", "tests/architecture/SignedColumnRegistryTests.cs; docs/dynamic-forms/row-integrity-format.md §7", "Registry per (entity_kind, schema_version); unsigned columns must be justified; F-03 decided (integer-only JSON v1)."),
    "SIM-04": ("Tested", GATE_A + " SEC-P12", "Archive outage keeps commands non-effective and content unreleased (Level A)."),
    "SIM-06": ("Tested", GATE_A + " SEC-P08, SEC-P09", "Idempotent retries yield one effect / one command id after outage and crash (Level A)."),
    "SIM-07": ("Tested", GATE_A + " SEC-P01, SEC-P06, SEC-P10", "Revocation effective at the authority barrier regardless of BusinessDb state (Level A)."),
    "SIM-30": ("Tested", GATE_A + " SEC-P06", "Cached session token cannot start protected reads after effective revocation (Level A)."),
    "SIM-35": ("Tested", GATE_A + " SEC-P04", "Modified signed row rejected (Level A)."),
    "SIM-36": ("Tested", GATE_A + " SEC-P05", "Deleted leaf detected by complete manifest (Level A)."),
    "SIM-37": ("Tested", GATE_A + " SEC-P05, SEC-P10", "Replayed old valid row/snapshot rejected by head/revision (Level A)."),
    "SIM-38": ("Designed", "docs/dynamic-forms/row-integrity-format.md §1", "No unkeyed digest of plaintext is used anywhere."),
    "SIM-40": ("Tested", GATE_A + " SEC-P08", "Vault Transit outage fails closed (writes and verified reads); HSM variant open (Level B)."),
    "SIM-42": ("Tested", GATE_A + " SEC-P10", "BusinessDb restore with stale share/content rejected; authority/search archive restore and epoch fencing open."),
}

ADR_SEC_DECISIONS = [
    ("ADR-SEC-D1", "Canonical audit event ownership; consolidate duplicate audit tables"),
    ("ADR-SEC-D2", "Organization data-classification and encryption floor"),
    ("ADR-SEC-D3", "Trusted search and completeness"),
    ("ADR-SEC-D4", "Per-user FIDO2 step-up, optional owner passphrase"),
    ("ADR-SEC-D5", "HMAC per row and Merkle manifests: keep until proof"),
    ("ADR-SEC-D6", "Performance, reliability, and operational gates"),
]

GATES = {
    "SEC": "Stage A (SEC-P01..P12) / Level 2 release",
    "G": "Per SECURITY_GAP_ASSESSMENT.md priority",
    "SIM": "security_hardening S0–S6 / Level 2 release",
    "ADR": "ADR-SEC-2026-10-16-01",
}


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def sec_controls() -> list[tuple[str, str]]:
    text = read("SECURITY_THREAT_MODEL.md")
    rows = re.findall(r"^\| (SEC-\d{2}) \| \*\*(P\d)\*\* \| ([^|]+)\|", text, re.M)
    return [(sid, f"[{prio}] {desc.strip()}") for sid, prio, desc in rows]


def gap_controls() -> list[tuple[str, str]]:
    text = read("SECURITY_GAP_ASSESSMENT.md")
    rows = re.findall(r"^\| (G\d{2}): ([^|]+)\| (P\d) \|", text, re.M)
    return [(gid, f"[{prio}] {title.strip()}") for gid, title, prio in rows]


def sim_controls() -> list[tuple[str, str]]:
    text = read("security_hardening/SECURITY_ACCEPTANCE_TESTS.md")
    rows = re.findall(r"^\| (SIM-\d{2}) \| ([^|]+)\|", text, re.M)
    return [(sid, desc.strip()) for sid, desc in rows]


def main() -> int:
    controls = sec_controls() + gap_controls() + sim_controls() + ADR_SEC_DECISIONS
    assert len([c for c in controls if c[0].startswith("SEC-")]) == 42, "expected 42 SEC controls"
    assert len([c for c in controls if c[0].startswith("G")]) == 18, "expected 18 gaps"
    assert len([c for c in controls if c[0].startswith("SIM-")]) == 42, "expected 42 SIM tests"

    with OUT.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle, lineterminator="\n")
        writer.writerow(["ControlId", "Title", "Status", "Owner", "Evidence", "Gate", "Note"])
        for cid, title in controls:
            status, evidence, note = STAGE_A_EVIDENCE.get(cid) or PHASE0_EVIDENCE.get(cid, ("Designed", "", "Normative text only; no executable evidence yet (Stage A scope did not reach this control)."))
            family = "ADR" if cid.startswith("ADR") else cid.split("-")[0] if "-" in cid else "G"
            owner = "security-owners" if family in {"SEC", "G", "SIM", "ADR"} else "maintainers"
            writer.writerow([cid, title, status, owner, evidence, GATES[family], note])
    print(f"wrote {OUT.relative_to(ROOT).as_posix()} ({len(controls)} controls)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
