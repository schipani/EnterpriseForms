# Phase C8 — Administration and Governance: status

- **Branch:** `phase-c8/administration-governance`, from `main` `b1e1b30a78ebe36b8055ec52343390419dff9ba9` (Phase C7 squash merge, PR #29). Not merged; no PR.
- **Architecture and milestone plan:** `docs/architecture/PHASE_C8_ADMINISTRATION_AND_GOVERNANCE.md`.
- **Decisions:** C-77 (M0 plan; stopped at the architecture approval checkpoint).
- **State (2026-10-29):**
  * M0 (planning) delivered as documentation only.
  * **No code, schema, API, UI or Authority change has been made.**
  * M1–M4 are not started. They wait for the owner's decisions D8-1…D8-10 (architecture §9).
- **Findings:** G19, G20, G21, G24, G25, G26, PB-08, PB-09, DR-C7-1…6, F-C7-03, AUD-C7-01, the C-74 development incident remediation and production Level 2 are all **unchanged and open**. The AC-5/AC-6 independent review is still required. The proposed AC-7/AC-8 would join that review.

## 1. Baseline verified for M0

- `origin/main` = `b1e1b30` ("Phase C7: Advanced Field Search (M0–M4) (#29)"). Its tree is identical to the reviewed C7 head `9bda786`.
- Administration capabilities on `main` were inventoried from the code: Authority SpiceDB schema, security command kinds, operator plane, business endpoints, entities and frontend features. The results are in architecture §1.
- The decisive finding: after bootstrap the Authority can change only the Security Administrator role. Adding, disabling or re-roling members needs new Authority commands (proposed AC-7).

## 2. Next step

The owner reviews the architecture document and decides D8-1…D8-10. Each approval is recorded as a decision-log entry before the milestone it unblocks starts. Nothing beyond M0 is begun until then.
