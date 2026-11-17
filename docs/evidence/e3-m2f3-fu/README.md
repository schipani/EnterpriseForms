# E3-M2f-3 FU-M2f3-01 / FU-M2f3-02 evidence (2026-11-17)

Record: `docs/security/E3_M2F3_FU_VERIFICATION_RECORD.md`. Driver: `scripts/dev/e3-m2f3-fu-mutations.py`. Development/Testing only.

| Folder | Content |
|---|---|
| `mutations/V22-*`, `V25-*`, `V43-*` | Violation demonstrations for the three dedicated tests (M2F3-22, -25, -43): one product edit each, run against the new and the existing test classes |
| `mutations/MZ-*` | FU-M2f3-02: the closure mutations MZ-02a/b, MZ-03a (closest expressible variant of MZ-03), MZ-09a/b (and `MZ-09a-full`: the full Authority and contract suites), MZ-13, MZ-24, MZ-34a/b, MZ-35, MZ-36a/b |
| `baselines/BASE-*` | Unmutated checks before the mutations (declared classes; E3-M2f-3i gate) |
| `baselines/RESTORED-*` | The restored tree after all mutations (declared classes; E3-M2f-3i gate; E3-M2f-3G gate groups T and L) |
| `superseded/MZ-35-first-attempt` | The first MZ-35 run: unit and 3i layers ran; the 3G gate refused to start (preflight defect fixed as HC-12); superseded by `mutations/MZ-35` |
| `mutations-summary.json` | One line per record: verdict, restoration, failing tests or checks |
| `fu03/` | Follow-up FU-M2f3-03 (2026-11-17): unmutated baseline and MZ-09a / MZ-09b against the new success-path test `CapabilityRefusalEvidenceAdmittedSuccessHostTests` (own `mutations-summary.json`) |
| `hc12/preflight-check.txt` | HC-12 safety check (`scripts/dev/e3-m2f3-hc12-preflight-check.ps1`): HEAD and corrected preflight functions of both gates against the repository and staged probe files, 36/36 |

Each `result.json` names the edited file(s), the SHA-256 of the original bytes (equal to the repository file at `b50264e`), the restoration check, the builds, every declared test class or gate with its totals and failing tests or checks, and the verdict. `tests-*-summary.txt` keeps only the failing tests (with their first assertion line) and the run totals. Gate folders keep `summary.json` and the gate's own JSON report. Every mutation ran in a separate scratch clone, never in the repository working tree. Local paths are replaced by `<clone>`, `<scratch>`, `<workdir>`, `<repo>`; the gates scrub their own credentials and nonces.
