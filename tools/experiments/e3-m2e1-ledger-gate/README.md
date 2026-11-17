# E3-M2e-1 dark data-key ledger — disposable-PostgreSQL gate

Development/Testing only. Not part of `EnterpriseForms.All.slnx`; never built in place. Run it with:

```
powershell -File scripts\dev\e3-m2e1-ledger-pg-gate.ps1 -Evidence <folder> [-LegacyBuildsRoot <work dir of an earlier run>]
```

The script builds this working tree's Authority (Release), `git archive` Release builds of `a1127a1` (E3-M2d, CARV 2) and `8915f77`
(E3-M2c, CARV 1), the harness (`HarnessE1`, against this tree) and the legacy agent (`LegacyAgent`, once per frozen build), all in a
temporary work directory. It starts ONE fresh `postgres:18.0` container (tmpfs, loopback port, removed afterwards), creates an
ordinary runtime role and runs the 44 checks E-00..E-42 (plus E-33b). Envelope v2 acceptance is never enabled; floors are installed
only in that container.

- `HarnessE1` is derived from the E3-M2d `HarnessD`: model, drivers, evidence helpers and fixtures are reused; the scenarios are new.
- `LegacyAgent` is the E3-M2d `V1Agent` (one Prepare or Commit of a frozen build per process).
- Seeded Prepared operations and the two seeded Envelope v2 heads use the controlled test-only mechanism of the earlier gates (an admin
  session; `session_replication_role = replica` only for the seeded heads, so every CHECK still applies). No production code,
  configuration, verification or validation is bypassed.
- `-LegacyBuildsRoot` reuses the frozen builds of an earlier run's work directory (`carv1-8915f77`, `carv2-a1127a1`).

Record: `docs/security/E3_M2E1_IMPLEMENTATION_RECORD.md`. Evidence of record: `docs/evidence/e3-m2e1/`.
