# SEC-BL-01 WP-7A mutations (Development/Testing)

Driver: `scripts/dev/wp7-mutations.py` (WP-7 driver extended with MV-7A-01..10 and the PostgreSQL baselines BASELINE-PG-F1/-J2/-OIDR7), on a
scratch clone of the working tree (robocopy, `bin`/`obj`/`.git` excluded), every edit anchored exactly once, original bytes restored and
SHA-256 verified after each mutation (`restored: true` for all 51 executions).

## Attempt 1 (40 executions)

- BASELINE (in-process, 417 tests) PASS; BASELINE-ARCH PASS; RESTORED PASS.
- MV-01..MV-18 and MV-X1 (the WP-7 set, re-run on the WP-7A product code; MV-16's anchor shortened because a VF-15 comment now precedes the
  full-sweep request, the edit is unchanged): all DETECTED.
- MV-7A-01, -02, -05, -07, -08, -09, -10, -10-PG, -05-PG, -01-PG: DETECTED.
- **BASELINE-PG FAIL** (`*ReadOnly*` filter): `G08TheDedicatedVerifierLoginIsReadOnlyAndRunsTheVerifier` hit `schema "authority" does not
  exist`. A pre-existing test-order dependency: under a method filter no earlier test had created the schema (the WP-7A tests changed the
  order). The test now ensures the schema itself. Attempt-1's MV-15b verdict (same filter) is therefore **invalid** and was re-run.
- **SURVIVED:** MV-7A-03 (no test changed an established key's evidence to evidence that still fully verifies), MV-7A-04 (equivalent mutant:
  refused attestations are also removed from the cache, so the single edit changed no behaviour), MV-7A-06 (no in-process gate test with
  successor evidence; only the PostgreSQL J2 test covered it). Tests added: the `generation-substituted-with-stop` F-1 variant (×3 streams)
  and `AuthorizationStreamGateDPlusTests.TheGateRefusesAHeadWhoseSuccessorIsArchived`; MV-7A-04 redefined to disable both guards.

## Attempt 2 (11 executions)

BASELINE (424) PASS; BASELINE-PG, BASELINE-PG-F1 (21), BASELINE-PG-J2 (1), BASELINE-PG-OIDR7 (18) PASS; MV-15b DETECTED; MV-7A-01 (re-run with
the added tests), MV-7A-03, MV-7A-04, MV-7A-06 DETECTED; RESTORED PASS.

**Result: every mutation DETECTED against passing baselines; all files restored.**
