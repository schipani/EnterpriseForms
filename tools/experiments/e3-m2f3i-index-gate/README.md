# E3-M2f-3i capability-refusal evidence index — disposable-PostgreSQL gate

Development/Testing only. Not part of `EnterpriseForms.All.slnx`; never built in place. Run it with:

```
powershell -File scripts\dev\e3-m2f3i-index-pg-gate.ps1 -Evidence <folder> [-BigRows 2000000] [-KeepWorkDir]
```

The script builds this working tree's Authority (Release) and `HarnessI` (a copy in a temporary work directory), starts ONE fresh
`postgres:18.0` container (tmpfs, loopback port, random name and credentials, autovacuum off, every statement logged with database and
application name), creates an ordinary runtime role and an unrelated login, and runs 40 checks I-01..I-30 (plus one demonstration
outside the pass totals). The container is removed afterwards; no other stack, volume or database is touched.

- The command under test, `--provision-capability-refusal-evidence-index`, always runs as a separate process of the built Authority with
  the identity in `ConnectionStrings__AuthorityDbMigration`, exactly as an operator would run it.
- Each scenario uses its own throwaway database copied from one migrated template. Synthetic organizations and journal rows are inserted
  with the container superuser as test setup; competing sessions (snapshot holders, lock holders, other builders, live writers) are
  harness connections. Verification uses the harness's own catalogue queries, independent of the command's.
- Planner results on this container do not establish the plan on Azure PostgreSQL.

Record: `docs/security/E3_M2F3I_IMPLEMENTATION_RECORD.md`. Evidence of record: `docs/evidence/e3-m2f3i/`.
