# Level 2 — Operator Disaster Recovery Runbook

**Runbook classification:** Security-sensitive operational instructions; do not store secrets, keys, security-domain private shares, PINs or raw authentication tokens in this repository. Execute in an isolated recovery environment under approved incident command. See normative `COORDINATED_DISASTER_RECOVERY.md` and test plan.

## Before any incident (quarterly checklist)

- [ ] Confirm independent security-event archive contains replayable event bodies + verified external receipt/high-water mark for every effective tenant command.
- [ ] Confirm encrypted immutable candidate record/document bytes are retained at least as long as trusted committed heads and compliance/legal hold require.
- [ ] Verify independent backups of Authority PostgreSQL, Business PostgreSQL, SpiceDB projection (optional, rebuild preferred), immutable object catalogue and auditable release evidence.
- [ ] Confirm Azure Managed HSM Security Domain is securely downloaded; multiperson quorum shares are independently held and not kept inside business environment.
- [ ] Schedule Managed HSM full backups; confirm historical key versions and disabled/retired verification keys remain recoverable.
- [ ] Confirm geo backup/restore features are deployed where required and tested on current Azure PostgreSQL and Storage configurations.
- [ ] Document separately administered recovery identities, approvers, emergency network isolation and independent recovery epoch controller.
- [ ] Test ledger service identity and receipt verification after certificate rotation; validate archive payloads, not just digests.
- [ ] Record which content acceptance rule (CARV) floor migration is in force (`ck_content_operation_rule_floor_v{F}`; E3-M2b-1, `docs/security/E3_M2B1_CARV_OPERATIONS.md` §6), so a restore can re-apply it.

## During an incident

**1 — Isolate and fence.** Declare incident; disable protected access and background exporters, revoke prior workload/deployment credentials, open incident recovery operation and external monotonic recovery epoch. Snapshot forensic evidence. Recheck that old deployments cannot issue accepted writes or serve plaintext.

**2 — Establish trusted point.** Select highest independently verifiable accepted authorization/content stream heads from ledger/archive, not a convenient wall-clock timestamp. Inventory accepted event body objects and immutable content bytes. If a missing body is detected: STOP for affected scope; do not roll back revisions.

**3 — Restore offline.** Provision *new quarantined* Business and Authority PostgreSQL targets, restore backups into isolated network, inspect roles/RLS and schema versions. Do not route user traffic. Treat every restored local ownership/share field as untrusted projection.

**4 — Replay independent security.** Verify immutable event digest chain, actor/approval/organization context, external receipts; replay authoritative revisions after backup; recalculate relationship state digest. Never allow privilege escalation through modified business DB rows.

**5 — Recover cryptographic keys.** Prefer existing HSM. If reconstruction is necessary, use new HSM, recover same Security Domain with approved quorum, then full backup restore and verify historical versions; **full restore is destructive and must not run on live production HSM**. Revalidate operational IAM. Verify non-sensitive known-answer tests.

**6 — Recover content and files.** Use accepted manifest heads and immutable version catalogue. Verify exact signed values and attachments, complete Merkle member set, file digest, AEAD authentication and key version. Map storage destination version identifiers after verifying bytes. Quarantine anything missing, never silently substitute old content.

**7 — Rebuild SpiceDB/search.** Rebuild SpiceDB from verified authority desired state; validate relationship graph, fencing marker and new ZedToken. Rebuild search from verified content and apply field-scoped authorization. Invalidate every prior session, unlock, cache, export and lease.

**8 — Release approval.** Produce machine-readable report with differences, accepted revision numbers, missing items, key availability, privilege audit, FIDO floor, data counts, measured RTO/RPO, security tests. Require two separate qualified human approvers. Anchor attestation externally, then activate new independent recovery epoch and route approved tenant traffic.

**9 — Follow-up.** Document scope, recovery lag, data loss/quarantine inventory, audit continuity, any override/rejection, key rotation, postmortem and follow-on drill improvements.

## Mandatory STOP conditions

- Latest independent security event body cannot be recovered or verified.
- Ledger / checkpoint cannot be authenticated, or signer/key inventory is incomplete.
- Authority/SpiceDB relationship digest mismatch or stale worker can still mutate the old projection.
- Any owner/member who was revoked can regain access through restored data.
- Latest accepted content references absent/unverifiable bytes; affected record is not quarantined.
- Two distinct approval identities cannot be verified.
- Recovered database privileges or identity-provider policy are weaker than the currently accepted security baseline.
- Business app/API can directly edit authority relationships, alter trusted heads, or obtain unrestricted HSM signing access.
- A restored AuthorityDb lacks a CARV floor migration that was in force before the incident (R-CARV-4): re-apply every such floor (`AuthorizationAuthority.Api.dll --apply-content-acceptance-floor <F>` with the migration identity) and confirm the Authority readiness guard reports it on every instance (with `ContentAcceptance:RequiredFloor` set) before reopening any organization gate. **Mandatory Release B gate RB-CARV-1 (open; `docs/security/E3_M2B1_CARV_OPERATIONS.md` §2a, contract §21.10):** after floor activation, `ContentAcceptance:RequiredFloor` must be enforced through protected deployment configuration; an Authority instance must refuse writes when its required floor is absent, lowered or cannot be verified; and after a restore to a pre-floor state, Authority writes must not resume until the floor is safely reinstated. RB-CARV-1 (contract §22, Development/Testing): a Release B instance (`ContentAcceptance__EnforcementPhase=ReleaseB`, `ContentAcceptance__RequiredFloor=1`) verifies the floor in every content write transaction and refuses without it (`503 CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED`, alert reason `FloorNotInstalled` after a pre-floor restore), and an instance with a missing or invalid phase does not start; the re-apply command is not affected by that refusal. Legacy binaries and instances configured as Release A are stopped only by the floor itself, so keep them out of the restored fleet and keep every organization gate closed until the floor is confirmed. **STOP** if the Release B phase or `RequiredFloor` is absent from a Release B deployment's protected configuration, or if any instance of a Release B deployment runs as Release A.

**No emergency button that sets a trusted head to an older revision or blindly re-signs old SQL values is permitted.** Missing accepted content can require an exceptional, separately approved business data-loss remediation; it never justifies an authorization downgrade.
