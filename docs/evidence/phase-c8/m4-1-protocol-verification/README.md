# C8 M4-1 Design B and Design A: focused protocol verification evidence (C-115, C-116)

- **What this is:** verbatim output of throwaway probes run by Claude (Opus) on 2026-11-04 against the disposable security-test stack (`authzed/spicedb:v1.56.2`, PostgreSQL datastore), to answer owner questions on the S1 protocol and the M1 cryptographic contract.
- **What this is not:**
  * product code, tests that run in CI, or an implementation of S1 or M1;
  * an independent review.
- The probe sources are kept here as `.txt` files so reviewers can rerun them; nothing compiles them.

## Environment

- Branch `phase-c8/m4-governance-audit` at `e117399`; the security-test stack (compose project `enterpriseforms-sectest`) had been up 9 hours.
- Probes ran in containers attached to `enterpriseforms-sectest_authority-net`:
  * `mcr.microsoft.com/dotnet/aspnet:10.0` (probe built with `mcr.microsoft.com/dotnet/sdk:10.0`, the Authority's own vendored authzed protos, Grpc.Net.Client 2.84.0);
  * `fullstorydev/grpcurl:v1.9.1`.
- **Probe organizations** were random UUIDs that the Authority does not know. The probe wrote only those organizations' relationships, emulating the projector's marker CAS.
- **The datastore restores and replacement roll back the whole `spicedb` database**, so the stack must be recreated (`down -v`) before any shard run.
- The dump files were deleted afterwards.

## Files

| File | Content |
|---|---|
| `grpcurl-characterisation-results.txt` | P1–P6: token and consistency behaviour (C-115) |
| `zedtoken-probe-tok.py.txt` | constructs the altered ZedTokens used in P4–P6 |
| `s1probe-Program.cs.txt`, `s1probe-S1Probe.csproj.txt` | the S1 probe: projector-equivalent CAS writes, the pin/decide/verify protocol, race and bench modes |
| `bench-results.txt` | latency, baseline versus protocol, p50/p95/p99 |
| `e1-race-restore-without-restart.txt` | 8 workers × 45 s; a `pg_restore --clean` into the live datastore at 12 s |
| `e3-race-restore-with-restart.txt` | 8 workers × 60 s; SpiceDB stopped, restored and restarted at about 10 s |
| `pinned-and-replacement-results.txt` | E2/E3 (snapshot pinned before the restore, decision after), E4 (datastore replaced), E5 (old dump restored into the replacement) |
| `m1kat-Program.cs.txt`, `m1kat-output-dotnet.txt` | M1 known-answer vector, .NET `AesGcm` |
| `m1kat-crosscheck-kat.py.txt`, `m1kat-output-python.txt` | independent reproduction with Python `cryptography` 44.0.0: AAD, plaintext, ciphertext, tag and wrapped DEK all match byte for byte |

## Results in one line each

- **Today's check** (`at_least_as_fresh` with the Authority token) allowed the revoked user **44,024** times (E1) and **62,034** times (E3) after the restore. That is T16S-1, with and without a SpiceDB restart.
- **The proposed protocol allowed the revoked user 0 times** in E1 and E3. Every request after the restore ended in a marker mismatch (or `MISSING` during the restore) or in a fail-closed error.
- **E2/E3:** a check at a snapshot pinned **before** the restore and evaluated **after** it returns `ALLOW` for the revoked user with `checked_at = S`. **A pin alone is unsafe.** The post-decision `fully_consistent` marker read detected the restore in both cases.
- **E5:** after an older datastore is restored under a running SpiceDB, old exact-snapshot tokens fail with "different datastore instance", while `at_least_as_fresh` silently serves restored data.
- **Latency (50–150 requests/s, 1 or 25 checks):** the protocol adds 8.8–9.8 ms at p95 and 11.1–14.4 ms at p99 over today's check, measured as protocol total minus baseline at the same percentile. Small dataset, single-node Docker, no TLS: this is not a substitute for the staging benchmark.
