# E3-M2e-2 controlled Envelope v2 acceptance verification — disposable-environment gate

Development/Testing only. **Test-only.** Not part of `EnterpriseForms.All.slnx`; never built in place. Owner authorization of 2026-11-16
(readiness decisions D-1..D-12). Record: `docs/security/E3_M2E2_VERIFICATION_RECORD.md`.

```
powershell -File scripts\dev\e3-m2e2-acceptance-pg-gate.ps1 -Evidence <folder> [-WorkDir <dir>] [-LegacyBuildsRoot <earlier work dir>] [-Groups all|F,P,N,C,X,D,R,H]
powershell -File scripts\dev\e3-m2e2-mutations.ps1 -BaseWorkDir <work dir of a gate run> -Out <folder> [-Only MX-01,...]
```

## What runs

- `HarnessE2` runs this tree's **real Authority** (Release, CARV 3) in-process against **one fresh `postgres:18.0` container** and uses
  the **real E3-M1 core** (`FieldSealBatchV2`, `DocumentEnvelopeV2Cipher`) with the real `LocalDevelopmentKeyProvider` (wrap code 1) and
  `VaultTransitKeyProvider` against **one fresh `hashicorp/vault:1.20.4` dev container** (wrap code 2). Rows are encoded by the business
  signed-column registry (`BusinessSignedSchemas.Encode`).
- The M1 core is `internal` by design. The harness reaches it **by reflection** (`M1Core.cs`) instead of changing production visibility or
  impersonating the contract-test assembly; span members are read from their private `byte[]` backing fields with length checks.
- The frozen CARV-1 (`8915f77`) and CARV-2 (`a1127a1`) builds run in separate processes through the E3-M2e-1 `LegacyAgent`.
- The Authority host is started (group H) from a copy of the Release output **without `appsettings.Development.json`**, with every
  setting explicit, against dedicated disposable databases.

## Isolation (owner authorization §3)

- Containers: random names, random free loopback ports, random passwords and tokens, PostgreSQL on tmpfs, both `--rm`, both removed in
  `finally`; the script verifies the container list after the run equals the list before it.
- Every database the harness touches is admitted only after its **per-run nonce** (database comment), the cluster **system identifier**
  (read by the script from inside the container) and server port 5432 are verified; every connection string passes `Target.Require`
  (127.0.0.1, the run's port, an admitted database). A mismatch is an immediate stop.
- The harness process removes inherited `ConnectionStrings__*`, `Content__*`, `ContentAcceptance__*`, Azure, ASP.NET Core environment and
  related settings before anything runs. No Azure SDK credential, no Blob archive (in-memory archive only, D-4).
- Envelope v2 acceptance is enabled **only** on switch objects constructed in the harness with the literal Testing configuration after
  floor 3 and Release B `RequiredFloor = 3` are verified, and becomes effective only after the policy is recorded on the disposable
  platform journal. No settings file or deployment file is written; the script fails if any repository configuration file mentions
  `EnvelopeV2AcceptanceEnabled`, before or after the run.
- F-E2: the ordinary `--migrate` runs before any runtime connection (and once more in group F with no runtime writer); runtime grants are
  compared with the setup baseline between groups; any `permission denied` in the server log that is not from the labelled expected
  checks (`app=m2e2-expected-42501`) fails the run.

## Groups

| Group | Content |
|---|---|
| setup, S | identity admission, environment scrub, migration, grants; Release B floor 3 readiness; switch refusals; missing policy observation; policy record |
| F | floors 1 → 2 → 3, C-14 re-run, floor-3 preconditions, raise under writers, monotonicity |
| P | positive acceptance matrix (wrap codes 1 and 2, field batches, documents, DK-4..DK-8, P-4, M2-ENV-16, carries, partial batches, removals, switch-off carries, v1, replay, archive and digest chain) |
| N | negative matrix (reuse, reservations, M2-ENV-18, DK-3, forged operations, commitment substitution, P-1/P-2, stale revision, policy observation, S-4, S-9, downgrade, share editor) |
| C | M2-CON-01..04 and a v2 writer stress |
| X | cross-build checks |
| D | database-level enforcement with acceptance on (ledger objects, floor corruption, floor-2 restore copy) |
| R | M2-RST-06 and the recovery-gap demonstrations (`EXPECTED_LIMIT`, `EXPECTED_GAP_BD1`; never counted as passes) |
| H | Authority host starts |
