# E3-M2b-2 business signed schema 2 groundwork — evidence (Development/Testing)

Normative record: `docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md` §23. Owner-led work; not an independent review and not a
certification. Nothing here was produced against a persistent database, a security-test stack, Azure, or Stage B.

| Folder | Content |
|---|---|
| `pre-check/` | D-8 read-only pre-check: the aggregate-only SQL and its results on disposable copies of the two local business databases (1,038 field rows, 184 document rows; zero violations of any new CHECK) |
| `run-of-record-1/`, `run-of-record-2/` | Two runs of `scripts/dev/e3-m2b2-business-pg-gate.ps1` on the final tree, each on its own fresh disposable `postgres:18.0` container: 24 checks each (pre 7, up 7, legacy 1, post 5, restore 4), all passed, container removed. `gate-<phase>.json` holds each check; `constraint-definitions.txt` the CHECK definitions as PostgreSQL 18.0 renders them; `legacy-writer.json` the pre-E3-M2b-2 build's canonical bytes; `round-trip-encodings.json` the canonical bytes compared after restore |
| `mutations/enforcement-removal.json` | 22 enforcement-removal checks (MU-01..MU-22): each applied to one source file, the affected suite rebuilt and run (or the PostgreSQL gate re-run for MU-14 and MU-15), the file restored and its sha256 verified. All 22 detected by at least one failing test or gate check; all restored |
| `mutations/s4-enforcement-removal.json`, `mutations/s4_mutations.py` | R-M2b2-11 targeted hardening (contract §23.14): nine S-4 enforcement-removal checks (MS4-01..MS4-09) on the Authority source, each rebuilt and run on the four focused Authority test classes, every file restored and its sha256 verified. Records, per check, the failing tests, so it shows both directions: with the E3-M2a schema refusal (and the version allow-list) removed the S-4 tests still pass (S-4 alone refuses the mismatched pairs), and with S-4 or its Prepare or Commit call site removed tests fail (the census call site alone is behaviour-neutral while the E3-M2a rule is intact: MS4-06 undetected, MS4-07 detected). Re-run: `python docs/evidence/e3-m2b2-business/mutations/s4_mutations.py <repository root> <output.json>`. No PostgreSQL run in this set (no SQL, migration or persistence change); the owner-required focused PostgreSQL verification followed in `s4-pg-gate/` (§23.15) |
| `s4-pg-gate/` | Owner-required focused real-PostgreSQL verification of S-4 (contract §23.15): two runs of `scripts/dev/e3-m2b2-s4-pg-gate.ps1` on the final tree (`run-of-record-1`, `run-of-record-2`), each on a fresh disposable `postgres:18.0`, the real Authority build in-process under Release B as the runtime role: 38 / 38 checks each, container removed. `s4-evidence.json` holds each check, `s4-log.txt` the log, `new-sha256.txt` the build under test, `source.txt` the base commit and working-tree status. `pg-enforcement-removal.json`: PGM-1 (S-4 Commit call removed: detected, 12 failed) and PGM-2 (E3-M2a schema refusal removed: every S-4 check still passed), both restored with sha256 verified |

Line endings were normalized to LF after capture; the content is otherwise verbatim. Local paths and credentials are not
recorded (the gate uses throwaway passwords generated per run).

Re-run: `powershell -File scripts\dev\e3-m2b2-business-pg-gate.ps1 -Evidence <folder>` (requires the local `postgres:18.0`
image; builds this tree and a `git archive` of the legacy commit in a temporary work directory).
