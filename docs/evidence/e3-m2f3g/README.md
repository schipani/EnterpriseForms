# E3-M2f-3G evidence (capability-refusal evidence, disposable PostgreSQL 18 verification)

Record: `docs/security/E3_M2F3G_VERIFICATION_RECORD.md`. Development/Testing only. Scrubbed by the gate script: no credential, nonce
or local path remains (the script fails the run otherwise).

| Folder | Content |
|---|---|
| `run-1/`, `run-2/`, `run-3/` | The runs of record (run-2 failed one check, M-03, analysed in the record §2; run-1 and run-3 passed) (`scripts/dev/e3-m2f3g-evidence-pg-gate.ps1 -Groups all -BigRows 2000000`), each on its own fresh `postgres:18.0` and `hashicorp/vault:1.20.4` containers. `m2f3g-evidence.json` (every check: expected, observed, pass; demonstrations outside the totals; not-executed items), `m2f3g-log.txt` and `console.txt` (chronological log), `summary.json` (container/volume cleanup, deadlocks, leak scan), `traces/` (T2 statement traces from `auto_explain` and the EF observer), `plans/` (the eight-row lookup's plans captured inside the real T2 and the generic prepared plan), `host-*.log` (real Authority host output), `agent-calls/` (outputs of the real 11e15ee and 0be5f52 business builds), `postgres-errors-and-lock-waits.log` (the server's error, cancellation and lock-wait lines), build hashes (`authority-sha256.txt`, `business-*.txt`, `harness-sources-sha256.txt`), `merge.txt`, `source.txt` (HEAD plus the uncommitted working tree) |
| `mutations/` | `scripts/dev/e3-m2f3g-mutations.ps1`: `mutations.json` (classification, failing checks, restoration by SHA-256 and the restored-baseline run) and, per mutation, the gate's `summary.json` and `m2f3g-evidence.json` (each mutation ran on its own fresh containers with `-BigRows 200000`) |

Exploratory runs used while building the harness are not kept and are not runs of record.
