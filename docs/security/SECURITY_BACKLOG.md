# Security Backlog

Explicitly tracked security work that is open and not scheduled. An entry here is **not** an approval, a design decision or an implementation; it keeps an open security obligation visible after the workstream that found it was closed. Each entry names its source, its current status under the original contract, its risks and what closing it requires.

| ID | Title | Status | Source |
|---|---|---|---|
| SEC-BL-01 | Historical audit-integrity verification of delivered journal entries (M2F3-43) | **Open, not scheduled** | E3-M2f-3 PD-15 (closure test M2F3-43) |

---

## SEC-BL-01 — Historical audit-integrity verification of delivered journal entries (M2F3-43)

| Item | Content |
|---|---|
| Opened | 2026-11-17, owner decision closing the E3-M2f-3 refusal-evidence implementation workstream for Development/Testing (Option A; `E3_M2F3_CONTRACT_CLOSURE.md` §14.1) |
| Requirement (unchanged) | M2F3-43 (`E3_M2F3_CONTRACT_CLOSURE.md` §10.5): "Evidence-removal: delete or alter a delivered evidence row ⇒ chain / archive verification detects it". The requirement is **not amended or weakened** |
| Status under the original contract | **FAILED** (PD-15 gate, which remains **OPEN**). Dedicated test executed in FU-M2f3-01 (`CapabilityRefusalEvidenceRemovalTests`; `E3_M2F3_FU_VERIFICATION_RECORD.md` §2.3): detected only for alterations that are sent to the archive again and for in-window alterations with an unchanged digest; **not detected:** deletion of a delivered row, a re-digested alteration never resent, an alteration outside the recorder's newest-eight window. Three expected-gap tests characterize the non-detection |
| Current coverage | `E3_M2F3_DELIVERED_ENTRY_VERIFICATION_DESIGN.md` §1–§2 (seven cases, source basis `b50264e`) |
| Risk | **R-FU-1** (retained): a privileged AuthorityDb writer can delete or consistently forge a delivered evidence row without product detection; the archive preserves the delivered original, but nothing reports the divergence; a forged row can suppress recording of the forged key for the rest of its hourly bucket. Within privileged-actor class A4 (G08 residual). Related: R-M2f3-6 (undelivered tail, accepted) |
| Candidate architecture | **Design D** (archive-anchored chain verification), preserved as a **candidate only**, not approved and not implemented: `E3_M2F3_DELIVERED_ENTRY_VERIFICATION_DESIGN.md` §4–§7. Its owner decisions OD-DV-1..OD-DV-7 are open |
| Dependencies | G14 recovery (classification of restore divergence, resolution of findings); an independently administered archive (G14, PB-10, G08); no hard dependency on BD-1 or BD-2 (design §6) |
| Closing requires | Owner decisions OD-DV-1..7; a separately authorized contract closure and implementation increment; executed tests showing M2F3-43 detection as written (the expected-gap tests inverted), a disposable PostgreSQL gate and mutations; owner acceptance. Closing SEC-BL-01 is necessary but not sufficient for PD-15 (other PD-15 gaps: `E3_M2F3_CONTRACT_CLOSURE.md` §14.1) |
| Not claimed | No verifier, no detection of the three undetected cases, no change to M2F3-43, no PD-15 pass |
