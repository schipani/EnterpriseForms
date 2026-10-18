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
        writer = csv.writer(handle)
        writer.writerow(["ControlId", "Title", "Status", "Owner", "Evidence", "Gate", "Note"])
        for cid, title in controls:
            status, evidence, note = PHASE0_EVIDENCE.get(cid, ("Designed", "", "Normative text only; no executable evidence in Phase 0."))
            family = "ADR" if cid.startswith("ADR") else cid.split("-")[0] if "-" in cid else "G"
            owner = "security-owners" if family in {"SEC", "G", "SIM", "ADR"} else "maintainers"
            writer.writerow([cid, title, status, owner, evidence, GATES[family], note])
    print(f"wrote {OUT.relative_to(ROOT).as_posix()} ({len(controls)} controls)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
