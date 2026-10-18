# G14 — Coordinated Disaster Recovery Test Plan

**Status:** Test specification only, unexecuted. Must run in disposable Docker/staging with isolated organization datasets, followed by independently reviewed Azure staging drills. No automatic destructive test against production.

## Harness prerequisites

- Two independently administered PostgreSQL instances, SpiceDB, Authorization Authority, signed content/manifests and replayable immutable security journal/archive.
- Vault Transit Docker in local integration; Azure Managed HSM (dedicated nonproduction keys) in staging; independent Confidential Ledger test ledger or deterministic fault stub locally.
- Per-organization security/content revisions, idempotent command IDs, committed record+attachment version hashes, signatures, immutable current policy floors, audit trail, and a reliable recovery-controller/epoch simulation.
- Fault injection for dropped acknowledgement, network partition, expired token, checksum mismatch, deleted archive body, missing key, missing blob, reordered journal event and simulated stale old API instance.
- Collect evidence: before/after independent revisions, accepted digest chain, checkpoint receipts, reconciled records count, quarantined resources, denied-permission tests, and measured recovery time/data-loss exposure. Never write plaintext secrets to evidence.

## Tests DR-01 through DR-36

| Test | Injected failure / adversarial action | Must demonstrate |
|---|---|---|
| DR-01 | Restore Business PostgreSQL to before a recorded revocation | Revoked user cannot discover, read, search, edit or download |
| DR-02 | Restore Authority PostgreSQL before same revocation | Independent event replay applies revocation and restores correct current revision |
| DR-03 | Restore both PostgreSQL databases before team membership removal | Removed team member cannot access; stale DB membership is ignored |
| DR-04 | Restore old SQL ownership GUID and valid old HMAC | External security/content heads detect replay and deny |
| DR-05 | Restore old SpiceDB snapshot with old grants | Block protected access until graph rebuilt/verified with fresh token |
| DR-06 | Delete a committed security event body from external archive | Keep organization quarantined, even with intact ledger hash |
| DR-07 | Modify replayed command body while retaining old digest | Digest/receipt verification rejects archive payload |
| DR-08 | Reorder independent accepted journal events | Hash chain/revision validation rejects replay |
| DR-09 | Recover archive through last receipt, but pending command was not anchored | Pending is not effective; no stale permission opens during reconciliation |
| DR-10 | Drop archive acknowledgement then retry command | Exactly one authorized accepted command; idempotent replay |
| DR-11 | Restore accepted root referencing deleted committed field row | Quarantine rather than silently return a prior row |
| DR-12 | Restore record with forged extra field row | Extra value never leaks through read/search/export; mismatch discovered |
| DR-13 | Lose attachment ciphertext after manifest commit | Attachment inaccessible/quarantined; old blob cannot substitute |
| DR-14 | Restore wrong attachment version with same file name | Hash, logical ID or AEAD binding mismatch denies |
| DR-15 | Remap geo-replicated destination blob version IDs | Verified ciphertext maps to logical version without root changes |
| DR-16 | Restore ciphertext with corrupted GCM tag | Authorized decrypt fails closed; original plaintext not emitted |
| DR-17 | HSM backup predates an accepted new key version | Recovery quarantines all dependent records, does not re-sign |
| DR-18 | Restore HSM to separate instance from same Security Domain | Required historical key versions sign/verify/decrypt probe; IAM rechecked |
| DR-19 | Attempt destructive full HSM restore on live instance | Operator policy and approval gate prevent action |
| DR-20 | Remove an older HMAC verification key required by old revisions | Key-inventory preflight fails; no blind re-signing |
| DR-21 | Lost/insufficient Security Domain quorum shares | HSM reconstruction fails safely; documents remain quarantined |
| DR-22 | Confidential Ledger unavailable during recovery | No release until verified trusted receipts/identity are available |
| DR-23 | Ledger service certificate renewed during recovery | Verification handles authenticated endorsements/chain; rejects forgery |
| DR-24 | Restore organization passwordless policy from before FIDO2 enforcement | Latest immutable FIDO2 floor remains in force |
| DR-25 | Revive revoked session/PIN unlock/stream/download token after restore | Old sessions and leases fail new epoch/freshness checks |
| DR-26 | Bring stale business API instance online after epoch advance | Instance cannot authorize protected calls or write accepted content |
| DR-27 | Two recovery leaders attempt simultaneous activate | Independent epoch fence admits at most one leader/cutover |
| DR-28 | Recovery controller lacks second distinct human approval | Release denied; no approval can be self-duplicated |
| DR-29 | Recovery operator requests HSM key material/plaintext file access | Denied; operator only receives safe cryptographic probe result |
| DR-30 | SQL restore grants BYPASSRLS or unexpected SECURITY DEFINER access | Privilege audit blocks release; authority still cannot be modified |
| DR-31 | Restore cross-tenant field or share association | Tenant binding/manifest/access validation denies both tenants |
| DR-32 | Poison/restored old search projection | Search remains closed until verified rebuild and ACL tests |
| DR-33 | Restore old authorized form schema with weaker field encryption requirement | Independent current encryption floor wins; no downgrade |
| DR-34 | Restore incomplete audit events and archive with intact receipts | Reconcile continuity; reject apparent clean history if missing evidence |
| DR-35 | Regional outage: Business DB and blob restore at different timestamps | Select independently trusted heads; restore exact bytes or quarantine gaps |
| DR-36 | Complete coordinated game day with approvals and measured metrics | Successful evidence bundle includes RTO/RPO, all security tests, accepted current revisions and sign-offs |

## Release criteria

1. Every test has reproducible setup, failure injection, assertions, logs, cross-service revision evidence, and recorded reviewer. No mock-only result proves Azure HSM/ACL/PostgreSQL or region failover safety.
2. DR-01..DR-10 and DR-17..DR-28 are **critical mandatory**. Any failure blocks Level 2 production. Every other test is also required for the declared protected-data workloads; risk acceptance must be explicit and cannot authorize bypassing tenant isolation or resurrection of revoked access.
3. Proof of recovery must include actual content bytes: do not mark restored because root and signatures match when referenced objects are absent.
4. Evidence includes accepted authority security revision and content revision **independently of restored DB and SpiceDB**, ledger transaction receipt and current identity, archive payload availability, historical key inventory, object catalogue, current policy floor, fresh projection status and distinct recovery approvers.
5. Measure end-to-end RTO and business-content RPO in local fault tests and Azure staging. Log observed replication lag for PostgreSQL, object archive and independent event journal; reject any claim of universal zero data loss not supported by measured durability.
6. Repeat full drill when changing KMS or journal implementation, authority event schema/canonicalization, region/data-residency profile, storage replication/immutability settings, SpiceDB datastore/schema, and recovery gateway/fencing implementation.

## Suggested first runnable vertical slice

Create two organizations and three records with signed values/attachments. Owner A shares one field with B, then revokes the share at independent revision N. Restore Business PostgreSQL to N-1 and Authority PostgreSQL to N-1 while preserving the independent archived accepted event N. Rebuild authority and SpiceDB from archive; validate B is denied in direct read, list, search, download and export. Then delete one accepted blob version: recovery must quarantine only affected record(s) without rolling back revision N. This demonstrates the central Level 2 safety claims before tackling regional DR.
