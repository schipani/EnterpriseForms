# E3-M2f-3G capability-refusal evidence — disposable PostgreSQL verification gate

Development/Testing only. **Test-only.** Not part of `EnterpriseForms.All.slnx`; never built in place. Owner authorization of
E3-M2f-3G (2026-11-17). Record: `docs/security/E3_M2F3G_VERIFICATION_RECORD.md`. Evidence of record: `docs/evidence/e3-m2f3g/`.

```
powershell -File scripts\dev\e3-m2f3g-evidence-pg-gate.ps1 -Evidence <folder> -RunLabel run-1 [-Groups all|H,M,T,F,Q,L,W,G,B] [-BigRows 2000000]
                                                             [-BoundaryMaxWaitMinutes 61] [-LegacyBuildsRoot <earlier work dir>] [-KeepWorkDir]
powershell -File scripts\dev\e3-m2f3g-mutations.ps1 -Out <folder> [-LegacyBuildsRoot <dir>] [-Only PM-01,...] [-DryRun]
```

## What runs

- **Containers:** one fresh `postgres:18.0` (tmpfs, loopback port, random name and credentials, autovacuum off, every statement logged
  with database and application name, lock waits logged) and one fresh `hashicorp/vault:1.20.4` dev container (the host's Vault Transit
  signer; the repository's `authority-transit` policy; a token holding only it). Both are removed; the script verifies the container
  list and the volume list are unchanged.
- **Isolation:** every database is created by the run and admitted by its per-run nonce comment and the cluster system identifier
  (`Linked/Isolation.cs`, the unchanged E3-M2e-2 guard); every connection string passes `Target.Require`; inherited connection,
  configuration, Azure and Vault environment variables are removed before anything starts.
- **The real Authority host** (`dotnet AuthorizationAuthority.Api.dll` from a copy without `appsettings.Development.json`; every
  setting explicit): deployment floor `D = 2`, Release B floor 3, `Content:EnvelopeV2AcceptanceEnabled = false`, in-memory archive,
  SpiceDB unreachable. Its endpoint filter, recorder, retry service and logs run unchanged. Requests come through real business
  clients: this tree's clients in-process (capability header removed: a level-1 client) and the real historical builds 11e15ee and
  0be5f52 through the E3-M2f-2G `BusinessAgent` (their own clients and headers).
- **The real recorder over the real `AuthorityStore`** in-process (approved runtime identity, this build's versioned application name),
  for scenarios that need an exact fault point. Test-only: a shifted in-process clock (caches, windows, backoff only; the T2 budget and
  all database times are real), an outcome observer, EF interceptors that pause, signal, terminate or fail at one exact statement or
  commit (they never alter SQL text, parameters or results), a deterministic test MAC for in-process level-2 writers.
- **Commands:** `--migrate`, `--apply-content-acceptance-floor` and the accepted E3-M2f-3i `--provision-capability-refusal-evidence-index`
  run as separate processes of the built Authority with the migration identity.
- **Oracle:** after every applicable scenario, the harness's own read-only SQL (index and bitmap scans disabled) checks the whole audit
  stream of the organization: ≤ 1 evidence entry per (key, bucket), ≤ 7 per bucket, closed approved bodies, non-decreasing evidence
  times along revisions (I-1), digest = SHA-256 of the stored bytes, chain links, contiguous revisions and the organization's audit head.

## Controlled test setup (disposable copies only, labelled in the record)

Organizations and census snapshots seeded as the services would write them; a large synthetic audit history; earlier-hour, future-dated,
duplicate-key and malformed evidence appended through the real `AuditJournal`; one stored body altered in place; server-side faults (a
`NOT VALID` CHECK constraint, a deferred constraint trigger) on one copy each; competing sessions holding a row, a table or IX-B;
`auto_explain` enabled on the liveness copy after seeding; `track_functions = all` on T2 copies.

## Never

An Azure call, a persistent database, Envelope v2 acceptance, a data-key binding or bound organization (no v2 content is written),
a runtime privilege change, a migration, or a G14 recovery mechanism. Disposable-container planner and timing results do not establish
Azure PostgreSQL behaviour or performance.

## Groups

| Group | Content |
|---|---|
| S | templates (migrate, floors, IX-B by the 3i command), runtime role, copies without floors / without IX-B |
| H | the real host on all seven routes (M2F3-07..14/17, -26 with the real 11e15ee and 0be5f52 builds), header grammar, level 2, archive delivery, restart, crash before T2 (M2F3-34), rolling deployment (M2F3-83) |
| M | 2 and 3 real hosts on one database (M2F3-27), ten organizations at once |
| T | the real T2 on PostgreSQL: statement shape and footprint (M2F3-54/-71), revisions and CatchUp (M2F3-56), triggers, deduplication, eviction flood (M2F3-52), coalescing, restart loop (M2F3-51), row-lock wait (M2F3-36), table and IX-B statement bounds (M2F3-63), archive outage (M2F3-31) |
| F | server-side insert, update and commit failures, committed and rolled-back unknown outcomes (M2F3-35/-48/-49/-50), crash before T2, clock regression and resume (M2F3-65), invalid and altered bodies, eight-row duplicate (MZ-22 re-review), unique violation (M2F3-64) |
| Q | attempt limit, retry capacity, retry exhaustion, restart (M2F3-37) |
| L | mature organization: plans inside the real T2 (M2F3-30/-70), generic prepared plan, earlier-hour history, lookup and catalogue timeouts, missing / mismatched / invalid IX-B (M2F3-62) and recovery |
| W | floor activation (A-1) and Release B level-2 writers next to T2 (M2F3-28 without binding inserts) |
| G | restore-triggered G14 detection (M2F3-55/-57) and restore without IX-B (M2F3-84) |
| B | a real hour boundary: reverse lock order (M2F3-69) with the transaction_timestamp counterfactual, later-hour liveness, fleet flood across two buckets with level-2 latency (M2F3-29) |
| Z | sentinels: runtime privileges unchanged; no acceptance requested; no data-key binding anywhere |
