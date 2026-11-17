# E3-M2d evidence (Development/Testing)

Record: `docs/security/E3_M2D_IMPLEMENTATION_RECORD.md` §8. Owner-led work; not an independent review, not a certification.

| Path | What |
|---|---|
| `pg-gate/run-of-record-1/`, `pg-gate/run-of-record-2/` | Two runs of `scripts/dev/e3-m2d-carv2-pg-gate.ps1` on the final tree: one fresh disposable `postgres:18.0` container each (tmpfs, loopback port, removed afterwards). `m2d-evidence.json` (47 checks with expected and observed values), `m2d-log.txt`, `console.txt` (re-encoded UTF-8), `floor2-definition.txt` (the live PostgreSQL 18.0 rendering of floor 2), binary hashes of the build under test and the version-1 build (`git archive` of `8915f77`; release builds are not byte-reproducible, so the hashes identify the run, not provenance), PostgreSQL version and image, `source.txt` (HEAD plus the uncommitted working tree). The per-call files of the version-1 agent (staged canonical test bytes) are not copied; `v1-calls.txt` records their number |
| `mutations/m2d_mutations.py` | The enforcement-removal driver: one mutation at a time, backups outside the repository, sha256-verified restore |
| `mutations/unit-removal.json` | 27 mutations against the Authority and architecture test classes |
| `mutations/pg-removal.json` | 6 floor and command mutations against the PostgreSQL gate |

Local paths and user names are scrubbed (`<temp>`, `<user-path>`). Floor 2 was installed only in the disposable containers (owner
decision D-3). No persistent database, Azure service or deployment was touched; Stage B did not run.
