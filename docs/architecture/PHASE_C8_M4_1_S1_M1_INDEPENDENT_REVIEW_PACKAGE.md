# Phase C8 M4-1 (AC-8.1): independent security review package for S1 and M1

- **Status:** **Ready for submission to an independent security reviewer (C-119). NOT REVIEWED.**
- **Independence notice:** this package was written by the implementation agent (Claude). Neither Claude nor the project owner is an independent reviewer. Nothing here is a finding, an approval or a certification.
- **Code baseline:** branch `phase-c8/m4-governance-audit`, commits `5f5fb07`, `9b47a8b`, `051a65d` and `f117995` (section 4). All commits are local. Nothing is pushed, merged or deployed.
- **Evidence level:** Development/Testing only, on a disposable local Docker stack. There is no Azure staging evidence, no hosted CI run and no PB-09 measurement.

## 1. Purpose and status

### 1.1 What is under review

| Item | What it is |
|---|---|
| **S1** | Projection integrity for the Authorization Authority. Every protected SpiceDB decision is pinned to a snapshot, decided at that snapshot, and then verified against a history-bound marker and the Authority's own database. A confirmed mismatch puts the organization in a permanent quarantine. It remediates finding T16S-1 (risk RR-11): restoring SpiceDB's datastore from an older backup made revoked permissions effective again. |
| **M1** | Sealed Security Auditor reason notes. The business API encrypts the optional note before sending it. The Authority stores and archives ciphertext only. No decrypt operation exists. It remediates finding RN-1 (risk RR-12): the note was plaintext in AuthorityDb and the archive. |
| **AC-8.1** | The M4-1 deliverable that contains S1 and M1 (design: [PHASE_C8_M4_1_DESIGNS_M1_S1.md](PHASE_C8_M4_1_DESIGNS_M1_S1.md), revision 2). |

### 1.2 Status of acceptance

- The owner approved the designs (decisions C-116, C-117) and gave interim acceptance of the reported results plus the V3 amendment (C-118).
- C-119 (2026-11-05): the owner accepted S1 and M1 **as implemented, for the purpose of independent review**, and authorized submission of this package and the P-S1-1 investigation.
- **Owner acceptance is not independent approval.** It does not close M4-1.
- **M4-1 remains open.** M4-2 is not authorized. Push, pull request, merge and deployment are not authorized.
- G14 recovery, the RCF-1 fix and the P-S1-1 optimization are not authorized.
- No claim of certification, production readiness or Level 2 compliance is made or implied.

## 2. Reviewer independence requirement

The reviewer must:
- not have contributed to the design, implementation, testing or documentation of this project;
- not rely on the owner's acceptance (C-118, C-119) or on any "proposed answer" or "claimed" statement in this package;
- not be an AI agent acting for the owner or for Claude (such a review is not independent and must not be recorded as such);
- be independent of the business-deployment chain, in the sense of the PB-12 independent Security Owner role;
- record any conflict of interest in the attestation (section 10), including any relationship with the owner, with Anthropic, or with SpiceDB, Authzed or Microsoft Azure.

Findings are recorded with a severity (Critical, High, Medium, Low, Info) in the table in section 9. The owner then makes a decision-log entry from the reviewer's signed statement.

## 3. Scope: the eight assessment questions

Each subsection gives the question, where to look, what the implementation claims, and what evidence exists. "Claims" are statements to test, not conclusions.

Reading order for the whole review:
1. [PHASE_C8_M4_1_DESIGNS_M1_S1.md](PHASE_C8_M4_1_DESIGNS_M1_S1.md): sections 1 (M1), 2 (S1), 3 (decisions), 4 (answers to the C-116 questions).
2. [PROJECTION_INTEGRITY_S1_OPERATIONS.md](../security/PROJECTION_INTEGRITY_S1_OPERATIONS.md).
3. [SECURITY_ADMINISTRATOR_MANAGEMENT.md](../security/SECURITY_ADMINISTRATOR_MANAGEMENT.md) (the note rules).
4. [PHASE_C8_M4_1_REVIEW_AND_REMEDIATION.md](PHASE_C8_M4_1_REVIEW_AND_REMEDIATION.md): the original findings RN-1, T16S-1, RCF-1 and T16-A.
5. [PHASE_C8_M4_AC8_SECURITY_CONTRACT.md](PHASE_C8_M4_AC8_SECURITY_CONTRACT.md): the risk register (RR-1 and onward).
6. [PHASE_C8_STATUS.md](../PHASE_C8_STATUS.md) section 39: implementation evidence.
7. Decision log [PHASE_C_DECISION_LOG.md](../decisions/PHASE_C_DECISION_LOG.md): rows C-116, C-117, C-118.

### 3.1 Question 1: S1 authorization-integrity guarantees and residual risk RE-1

**Question.** Does S1 deliver the guarantee G-S1 as stated, and is the accepted residual risk RE-1 correctly bounded and acceptable?

**What to look at.**

| Kind | Path |
|---|---|
| Design | [PHASE_C8_M4_1_DESIGNS_M1_S1.md](PHASE_C8_M4_1_DESIGNS_M1_S1.md) section 2.9 (G-S1, invariants I1 to I6, RE-1 to RE-6), section 2.2 (SpiceDB behaviour relied on), section 2.12 (rejected alternatives) |
| Operations | [PROJECTION_INTEGRITY_S1_OPERATIONS.md](../security/PROJECTION_INTEGRITY_S1_OPERATIONS.md) section 1 |
| Source | `src/authority/AuthorizationAuthority.Application/Common/ProjectionIntegrityVerifier.cs`; `.../Common/VerifiedProjectionSnapshot.cs`; `.../Common/ProjectionMarker.cs`; `.../Features/Projection/ProjectionIntegrityMonitor.cs` (marker transition and periodic check); `.../Features/Projection/ProjectionProcessor.cs` (projector) |
| Unit tests | `tests/authority/AuthorizationAuthority.Tests/ProjectionIntegrityTests.cs` |
| Architecture tests | `tests/architecture/EnterpriseForms.ArchitectureTests/ProjectionIntegrityBoundaryTests.cs` (ARCH-01 to ARCH-03, U-09) |
| Stage B tests | `tests/integration/EnterpriseForms.StageBTests/Tests/AuthorizationRollbackTests.cs` (T16-B, T16-S = RB-01); `.../Tests/ProjectionIntegrityRollbackTests.cs` (RB-02 to RB-15) |

**Claims.**
- A decision is released only if SpiceDB evaluated it at a pinned snapshot, a `fully_consistent` marker read after the decision names the Authority's committed revision and journal digest, and the Authority's own `(revision, projection revision, digest)` is unchanged and not quarantined before the pin and after the verify.
- A rollback that is in place when the verify read runs, or arrives before the decision completes, is never released.
- RE-1 is **not** eliminated: a datastore that reverts to an older state and back within one request's decide-to-verify interval (about 8 to 10 ms at p95 in the local measurement), or two live datastore backends behind one SpiceDB at once. The design argues that no Authority-side protocol can close it, because SpiceDB offers no way to evaluate a check and read the marker in one datastore transaction. The mitigation is deployment rule I3 (section 3.5) plus quarantine on any mismatch seen by any read.
- RE-2 (a writer inside the Authority domain), RE-3 (AuthorityDb and SpiceDB restored together), RE-4 (a revoke committed after a decision was released; pre-existing), RE-5 (false quarantine) and RE-6 (undocumented SpiceDB behaviour) are listed as not claimed.

**Evidence.** RB-01 to RB-15 pass on a fresh stack at `5f5fb07` and `051a65d` (section 7). Unit tests and architecture tests pass in the non-stack sweep (990/990 at the documentation commit). **No evidence exists for RE-1 itself**: no test can produce a revert-and-return inside one request, and the owner accepted it as a risk (C-117).

**Reviewer should assess.** Whether the proof of G-S1 holds against the SpiceDB behaviours listed in design section 2.2 (the design says they were verified against `authzed/spicedb:v1.56.2`); whether RE-1 is correctly bounded; whether any protected SpiceDB path bypasses the verifier.

### 3.2 Question 2: exact-snapshot permission checks and marker validation

**Question.** Is the pin, decide, verify sequence correct, and are the marker and snapshot checks sufficient?

**What to look at.**

| Kind | Path |
|---|---|
| Design | design section 2.3 (history-bound marker), section 2.4 (protocol, single-use snapshot), section 2.5 (lock-confirm) |
| Source | `.../Common/ProjectionIntegrityVerifier.cs` (pin, decide, verify, lock-confirmed quarantine); `.../Common/VerifiedProjectionSnapshot.cs` (single-use); `.../Common/ProjectionMarker.cs` (marker format and parsing); `.../Common/Ports/ISpiceDbProjection.cs` (the ports); `src/authority/AuthorizationAuthority.Infrastructure/SpiceDb/SpiceDbProjectionClient.cs`; `.../SpiceDb/authority.schema.zed` |
| Callers | `.../Features/Authorization/PermissionCheckService.cs`; `.../Features/Catalogue/SearchCatalogueService.cs`; `.../Features/Content/ContentService.cs` |
| Contract tests | `tests/integration/EnterpriseForms.StageBTests/Tests/SpiceDbBehaviourContractTests.cs` (CT-1 to CT-6); probe tool `tests/integration/EnterpriseForms.SpiceDbContractProbe/Program.cs` |

**Claims.**
- **Pin.** A marker read, at least as fresh as the Authority-held token, fixes the snapshot.
- **Decide.** Every check, bulk check and lookup runs at exactly that snapshot (`at_exact_snapshot`). The returned `checked_at` or `looked_up_at` must equal it, otherwise the decision is refused (`PROJECTION_SNAPSHOT_MISMATCH`).
- **Verify.** A `fully_consistent`, cache-bypassing marker read, then a re-read of AuthorityDb state.
- **Marker.** `step-{org}-{n}-{digest}`: the committed revision plus the hash-chained authorization-journal digest. A foreign or restored history carries a different digest (`PROJECTION_FOREIGN_HISTORY`). The marker is the only evidence; the ZedToken is never integrity evidence (CT-4: SpiceDB's `at_least_as_fresh` silently accepts foreign and future tokens).
- **Single use.** `VerifiedProjectionSnapshot` is the only route to the SpiceDB decision ports. ARCH-01 to ARCH-03 scan the source for direct reads, for consumers that do not pin and complete, and for use of the Authority-held ZedToken outside allowed places.
- **Legitimate concurrent change** never quarantines: if the Authority state changed during the decision, the decision is retried and never results in an allow (RB-11).
- **Outage** fails closed without quarantine (RB-15).

**Evidence.** RB-01 to RB-15, CT-1 to CT-6, ARCH-01 to ARCH-03 and U-09, 21 Authority unit tests. Section 7 gives the shard results.

**Reviewer should assess.** Race windows between the pin and the first check; behaviour on error classes (CT-5); whether the periodic check (S2, `ProjectionIntegrity:PeriodicCheckSeconds`, 10 to 600 s, default 60) and the one-time marker transition at startup have gaps; whether the architecture tests (source scans) are strong enough to be relied on.

### 3.3 Question 3: persistent quarantine and its recovery limitations

**Question.** Is the quarantine correctly persisted, enforced everywhere, and are its recovery limits honestly stated?

**What to look at.**

| Kind | Path |
|---|---|
| Design | design section 2.5 (lock-confirm and permanent quarantine), section 2.6 (failure classes) |
| Operations | [PROJECTION_INTEGRITY_S1_OPERATIONS.md](../security/PROJECTION_INTEGRITY_S1_OPERATIONS.md) section 4 (quarantine), section 5 (alerts), section 7 (G14 backlog) |
| Migration | `src/authority/AuthorizationAuthority.Infrastructure/Persistence/Migrations/20261104231152_ProjectionIntegrityS1.cs` (trigger `trg_organization_integrity_quarantine_is_permanent`) |
| Source | `.../Common/OrganizationGate.cs`; `.../Common/ProjectionIntegrityVerifier.cs`; `.../Features/Projection/ProjectionProcessor.cs`; `.../Features/Projection/ProjectionIntegrityMonitor.cs`; `src/authority/AuthorizationAuthority.Domain/Features/Organizations/OrganizationAuthorizationState.cs` |
| Tests | RB-07, RB-08, RB-09 and RB-10 in `ProjectionIntegrityRollbackTests.cs`; `AuthorizationRollbackTests.cs` (RB-01); `tests/integration/EnterpriseForms.StageBTests/Tests/TeamMembershipReconciliationTests.cs` (RC24) |

**Claims.**
- The quarantine is stored in `organization_authorization_states.integrity_quarantine_enabled` and `integrity_quarantine_reason_code`, with an audit-stream journal event `ProjectionIntegrityQuarantined` in the same transaction. A Critical alert `PROJECTION_INTEGRITY_QUARANTINE` (log event 9100) follows the commit.
- It is enforced at every protected decision, catalogue snapshot validation, every security command, the projector (which does not write SpiceDB for a quarantined organization), onboarding, owner recovery and provisioning.
- It is permanent under normal operation: no application code clears it (ARCH-03); the first reason is kept; the trigger refuses any UPDATE that clears it or changes its reason, for every role (RB-10).
- **Recovery is not implemented.** Clearing is reserved for a future, independently authorized G14 recovery (rebuild SpiceDB from AuthorityDb desired state, verify, two human approvals with step-up, archive evidence, recovery epoch). S1 implements none of it (decision B-Q1). Until then a quarantined organization stays unavailable for protected access.
- **A false quarantine persists** (RE-5). This is an availability cost the owner accepted.
- **The trigger does not stop DDL by the table owner.** The Stage B test RC24 lifts a quarantine with `ALTER TABLE ... DISABLE TRIGGER`, one UPDATE and `ENABLE TRIGGER` in one transaction, as the database owner, on the disposable stack. The same privilege lets an AuthorityDb administrator do so. That administrator is inside the Authority trust boundary (risks RR-7 and G08).

**Evidence.** RB-10 (permanence across restarts, commands, projection and a direct UPDATE); RB-07 (only the rolled-back organization is quarantined); RC24 (the DDL path, test fix `051a65d`).

**Reviewer should assess.** Whether a false-quarantine denial of service is acceptable; whether any path can read or write SpiceDB for a quarantined organization; what the minimum acceptable G14 recovery procedure is.

### 3.4 Question 4: M1 encryption, authenticated context and confidentiality

**Question.** Is the note sealing sound, and does the Authority keep the note confidential?

**What to look at.**

| Kind | Path |
|---|---|
| Design | design section 1 (1.1 reuse, 1.2 AAD, 1.3 padding, 1.4 envelope, 1.5 Authority validation V1 to V11, 1.6 business contract, 1.7 keys, 1.8 vectors) |
| Spec | [SECURITY_ADMINISTRATOR_MANAGEMENT.md](../security/SECURITY_ADMINISTRATOR_MANAGEMENT.md) (the "Note" rule) |
| Business source | `src/backend/EnterpriseForms.Application/Features/Organizations/ReasonNotes/AuditorReasonNotes.cs` (port, context, normalization, padding); `src/backend/EnterpriseForms.Infrastructure/Features/Organizations/AesGcmAuditorNoteSealer.cs`; `src/backend/EnterpriseForms.Application/Features/Organizations/SecurityAuditorsHandlers.cs` (call site, line 96) |
| Authority source | `src/authority/AuthorizationAuthority.Application/Features/SecurityCommands/AuditorReasonNoteEnvelopeValidation.cs`; `.../CommandPlanner.SecurityAdministrators.cs`; contracts `src/authority/AuthorizationAuthority.Contracts/V1/FieldSearchContracts.cs` |
| Tests | `tests/features/EnterpriseForms.Api.ContractTests/AuditorReasonNoteSealerTests.cs` (known-answer vector KAT-M1-01, context binding, tampering, cross-domain, padding, key provider unavailable, no decrypt operation); `tests/authority/AuthorizationAuthority.Tests/SecurityAuditorTests.cs` (V2 to V10, V3 equivalence test `AnOmittedAndANullEnvelopeAreEquivalentAndEveryOtherNonEnvelopeValueIsRefused`); `tests/integration/EnterpriseForms.StageBTests/Tests/SecurityAuditorTests.cs` (`ASealedReasonNoteReachesTheAuthorityAsCiphertextOnlyAndThePlaintextIsNeverKept`) |

**Claims.**
- AES-256-GCM (128-bit tag), a random 256-bit single-use DEK per note, a random 96-bit nonce, DEK wrapped by the existing `IEncryptionKeyProvider`. The existing field and file encryption services are not modified.
- AAD is `ASCII("DynamicForms/AuditorReasonNote/v1")` followed by big-endian envelope version, organization id, target user id, note id, role name and direction (116 bytes for appoint, 115 for remove). The Authority takes the context from the command, never from the envelope.
- The plaintext is normalized and padded to a fixed 2,048 bytes (deterministic, unambiguous, authenticated). Ciphertext length is therefore constant.
- The Authority validates structure before persisting anything (rules V1 to V11): strict binding, plaintext `reason_note` permanently refused (V2), exact envelope format, key reference, UUID v7 note id, strict base64, decoded sizes (nonce 12, tag 16, ciphertext 2,048, wrapped DEK 29 to 512), size caps, note id unused in the organization, role `security_auditor` only.
- **V3 amendment (C-118):** an omitted `reason_note_envelope` and an explicit JSON `null` are equivalent and mean "no note". Any other non-object value is refused. A plaintext `reason_note` is refused whatever the envelope is.
- **No decrypt operation exists** anywhere in the product. Decryption exists only as a test helper. The Authority cannot check that the tag verifies or that the plaintext is a real note; it is non-authoritative data and is never opened.
- With no key provider configured, a note is refused (503) and nothing is sent.

**Evidence.** The Stage B test checks that the Authority receives only the envelope, and that a plaintext sentinel appears nowhere: not in `pg_dump` of AuthorityDb or BusinessDb, the rejected rows, the audit stream, or the api, authority and worker logs. Contract and Authority tests pass in the non-stack sweep.

**Reviewer should assess.** The AAD construction and its domain separation; nonce and DEK handling; the padding scheme; whether an unauthenticated well-formed envelope from a compromised business workload is acceptable (design section 1.5 says it is within the trust model); whether the V3 amendment weakens anything; key provider limits (section 6).

### 3.5 Question 5: the single-SpiceDB-database deployment rule (I3)

**Question.** Is invariant I3 an acceptable binding deployment constraint, given that code cannot enforce it?

**What to look at.** Design section 2.9 (I3, RE-1) and section 2.12; [PROJECTION_INTEGRITY_S1_OPERATIONS.md](../security/PROJECTION_INTEGRITY_S1_OPERATIONS.md) section 3; RB-12 (live restore under a running SpiceDB is detected) and RB-13 (replaced or recreated datastore fails closed) in `ProjectionIntegrityRollbackTests.cs`; `infrastructure/docker/compose.security-test.yaml`.

**Claims.**
- I3: exactly one database backend behind SpiceDB at a time; one connection URI to one primary; **no SpiceDB read replicas** until S1 is independently re-verified with them; failover only through synchronous same-endpoint HA with no data loss; any datastore replacement, restore or asynchronous-replica promotion must stop SpiceDB first and is a recovery event.
- No second independent SpiceDB in front of the same organizations; no SpiceDB pointed at a database restored from another environment (a cloned environment's markers carry another history digest and are quarantined).
- Only the Authority projector writes SpiceDB (I1).

**Evidence.** RB-12 and RB-13 test detection of a restore and a replacement. **Nothing tests or enforces I3 itself.** There is no deployment template (Bicep) or pipeline check that does so; Azure PostgreSQL behaviour for SpiceDB is untested.

**Reviewer should assess.** Whether I3 is realistic for the intended Azure deployment (for example geo-redundant or replica-based high availability) and what independent control should verify it.

### 3.6 Question 6: deployment rollback compatibility and minimum safe Authority version

**Question.** Does the rollback fence prevent a silent downgrade, and is the missing pipeline enforcement an acceptable gap?

**What to look at.** [PROJECTION_INTEGRITY_S1_OPERATIONS.md](../security/PROJECTION_INTEGRITY_S1_OPERATIONS.md) section 2; the migration `20261104231152_ProjectionIntegrityS1.cs`; `src/authority/AuthorizationAuthority.Infrastructure/Persistence/Configurations/AuthorityEntityConfigurations.cs`; RB-10 (the legacy column stays NULL); the startup transition in `ProjectionIntegrityMonitor.cs`.

**Claims.**
- **Minimum safe version:** the Authority build that contains migration `20261104231152_ProjectionIntegrityS1` and the S1 code. Never deploy, or roll back to, an older image against a database that has applied the migration.
- **Fence:** the migration moves the Authority-held ZedToken to `projection_snapshot_zed_token` and constrains the old column `projection_zed_token` to NULL (`ck_legacy_projection_token_retired`). A pre-S1 build finds no token, keeps every gate closed, and cannot store a token (its commit fails). It fails closed for every organization.
- **Upgrade path:** at startup every pre-S1 marker (`step-{org}-{n}`) is upgraded by compare-and-swap under the organization lock. Readiness is false until the transition completes. A marker that does not match the Authority's revision is quarantined, not upgraded.
- **Not enforced by tooling.** The rule that a pipeline must record the minimum safe version and refuse an older image digest is a PB-11 requirement. **No PB-11 pipeline enforces it today.** The fence is a database-level protection only. Rolling the migration back (`Down`) is a Development/Testing operation.

**Evidence.** RB-10 asserts the legacy column stays NULL. No test runs an actual pre-S1 binary against a post-S1 database (reviewer to consider whether one is needed).

### 3.7 Question 7: open RCF-1 and G14 recovery dependencies

**Question.** Are the open items RCF-1 and T16-A / G14 correctly classified, and do they block anything claimed here?

**What to look at.** [PHASE_C8_M4_1_REVIEW_AND_REMEDIATION.md](PHASE_C8_M4_1_REVIEW_AND_REMEDIATION.md) (RCF-1, T16-A); risks RR-13 and RR-14 in [PHASE_C8_M4_AC8_SECURITY_CONTRACT.md](PHASE_C8_M4_AC8_SECURITY_CONTRACT.md); `trust_authority/COORDINATED_DISASTER_RECOVERY.md`; [PROJECTION_INTEGRITY_S1_OPERATIONS.md](../security/PROJECTION_INTEGRITY_S1_OPERATIONS.md) section 7.

**Claims.**
- **RCF-1 (RR-13):** rejected commands of kinds other than the auditor commands keep the raw request, step-up evidence included. Open. "Low now and latent High"; to be fixed before PB-08 (D8-75). S1 and M1 do not change it. The RCF-1 fix is not authorized.
- **T16-A (RR-14):** full Authority recovery (AuthorityDb and SpiceDB restored together to the same older revision) is untested. S1 cannot detect it (RE-3). It needs the G14 recovery plane. T16-B (hostile BusinessDb restore) passes. A production-readiness blocker.
- G14 items not implemented by S1: coordinated database restoration, journal replay, recovery epoch, SpiceDB rebuild (S3), reconciliation, quarantine clearing, T16-A, recovery drills.

**Evidence.** No test exists for T16-A. T16-B passes at both commits.

### 3.8 Question 8: incomplete Stage B reruns and outstanding performance findings

**Question.** Is the evidence set adequate, and are the performance findings acceptably classified?

**What to look at.** Section 7 of this package; [PHASE_C8_STATUS.md](../PHASE_C8_STATUS.md) sections 39.2 and 39.4; [the evidence README](../evidence/phase-c8/m4-1-s1-implementation/README.md); source tests `tests/integration/EnterpriseForms.StageBTests/Tests/AuthorizationLatencyTests.cs` and `.../Tests/SearchCatalogueTests.cs` (`BenchmarkTheVerifiedScopeAt50And150And480Records`).

**Claims.**
- **Not every shard passed in full at one commit** (section 7).
- **P-S1-1 / RR-15:** with S1, a 480-record verified search scope is refused (`SEARCH_VERIFICATION_LIMIT`, 15 s budget) on the developer laptop; pre-S1 completed it at 12.5 s p50. A later timing probe puts S1's direct cost at about 0.5% of the budget; the implementer now concludes the refusal is not attributable to S1 (section 8). The refusal is the typed fail-closed answer, with no partial result. Not a security defect.
- **B-Q9** (added p95 of at most 15 ms per authorization check): met at 50 rps; **not reliably met at 150 rps** (mean added 14.6 ms; one of two runs added 22 ms). Measured on one laptop that also ran the owner's dev stack, without TLS or staging data volume.
- **PERF-01 on Azure staging under PB-09 conditions is mandatory** and has not been run. Optimizations O-B1 and O-B2 are not implemented.

**Evidence.** Latency and benchmark files in the evidence folder (section 5).

## 4. Commits and how to reproduce

### 4.1 Commits

| Commit | Content |
|---|---|
| `5f5fb07` | S1 (57 files): verifier, snapshot, marker v2, monitor, projector changes, migration, metrics, tests CT-1 to CT-6, RB-01 to RB-15, latency test |
| `9b47a8b` | M1 (24 files): sealer, validation V1 to V11, handlers, contracts, OpenAPI and TypeScript client, People dialog, tests |
| `051a65d` | Test fix only: RC24 simulates the privileged G14 clearance because the trigger now refuses ordinary UPDATEs. No product change |
| `f117995` | Documentation, evidence, V3 amendment (C-118) with its equivalence test and a one-line validation change in `AuditorReasonNoteEnvelopeValidation.cs`, risk register, manifest |

Review the diff with `git diff 70894d2..f117995` (`70894d2` is the pre-S1 design commit) or per commit with `git show --stat <commit>`.

### 4.2 Tests that need no Docker stack

Run from the repository root with the .NET SDK (the project uses `dotnet test --project <csproj>`):

| Project | Covers |
|---|---|
| `tests/authority/AuthorizationAuthority.Tests` | S1 unit tests, V1 to V11 and the V3 test, search catalogue |
| `tests/features/EnterpriseForms.Api.ContractTests` | M1 sealer vectors and tests, API contract |
| `tests/architecture/EnterpriseForms.ArchitectureTests` | ARCH-01 to ARCH-03, U-09, naming and ownership |
| `tests/integrity/EnterpriseForms.Integrity.Tests` | integrity |
| `tests/platform-console/EnterpriseForms.PlatformConsole.Tests` | console |

Recorded result at the documentation commit: 990/990 (Authority 346, contract 507, architecture 80, integrity 34, platform console 23). The package author did not re-run these.

### 4.3 Stage B integration tests (disposable stack)

- **Never run these against a developer's dev stack.** The tests use their own compose project, `enterpriseforms-sectest` (files `infrastructure/docker/compose.yaml` plus `compose.security-test.yaml`, SpiceDB `authzed/spicedb:v1.56.2`). The dev stack and the sectest stack pin the same subnets, so the dev stack must be stopped first. Do not run Playwright or Vite against any dev instance either.
- The runner is `scripts/dev/stage-b.ps1` (PowerShell). Its header documents the options:
  - `-List`: group names and test counts;
  - `-Group <name> -Fresh`: run one group after recreating the sectest stack (`down -v`);
  - `-Class <name>`: one test class (must belong to a group);
  - `-All`: every group on one stack.
- **Recreate the stack with `down -v` for every shard** (`-Fresh`). Reason recorded in the project notes: a stale stack can carry a quarantine or an archive body mismatch (`ARCHIVE_BODY_MISMATCH`) from an earlier test, which fails later tests. Because S1 quarantine is permanent, a reused stack can also fail tests for that reason.
- The seven groups are defined in `tests/integration/EnterpriseForms.StageBTests/stage-b-groups.json`: `forms-documents`, `authorization-security-onboarding`, `records-lifecycle`, `administration-membership`, `search-catalogue`, `search-execution`, `protection-reconciliation`. A full shard takes roughly 3 to 15 minutes; the search shards are the longest.
- The runner expects Docker, Python and the .NET SDK. A general description of the test levels is in [FAST_FEEDBACK_TESTING.md](../testing/FAST_FEEDBACK_TESTING.md).
- The SpiceDB behaviour probe (`tests/integration/EnterpriseForms.SpiceDbContractProbe`) is a tool that was used to establish the behaviours CT-1 to CT-6 pin; CT-1 to CT-6 themselves run inside the `authorization-security-onboarding` group.

## 5. Evidence index

All files are in [docs/evidence/phase-c8/m4-1-s1-implementation/](../evidence/phase-c8/m4-1-s1-implementation/README.md). The evidence was produced by Claude on one Windows laptop on 2026-11-04 and 2026-11-05. It is not independent evidence.

| File | Shows | Limits |
|---|---|---|
| `README.md` | Index, latency table, search benchmark table, verdict | Written by the implementer |
| `shards-s1-5f5fb07-<group>.txt` (seven groups) | Per-shard totals at `5f5fb07`, each on a fresh stack; failures by name for `administration-membership` (3) and `search-catalogue` (1) | Test summaries only, not full logs |
| `shards-m1-051a65d-administration-membership.txt` and `...-authorization-security-onboarding.txt` | The two M1-affected shards at `051a65d` (55/55 and 93/93) | Other five shards not rerun at this commit |
| `shard-authorization-security-onboarding-run1.txt` | First S1 run, 92/93 (RB-10 test setup error), kept for transparency | Superseded by the 93/93 runs |
| `s1-authorization-latency-pre-s1*.txt`, `s1-authorization-latency-s1-*.txt` | `AuthorizationLatencyTests` before and after S1; fair A/B pairs `ab-1` and `ab-2` | Two runs each; one laptop shared with another stack; no TLS; `s1-in-shard` and `s1-warm-1` are noisier; `s1-ab-*` headers name `70894d2` because they ran from the working tree before the commit (image digests identify the build) |
| `search-benchmark-pre-s1-70894d2*.json/.txt` | Verified-scope timings for 50, 150, 480 records before S1; 480 passed at 12.5 s p50 | One run |
| `search-benchmark-s1-5f5fb07-in-shard.json` and `search-benchmark-s1-051a65d*.json/.txt` | The same benchmark with S1; 480 refused | Three runs in total; the comparison is not statistical |
| `SHA256SUMS.txt` (repository root) | Checksums of the evidence files | Integrity of files only, not of the claims in them |
| `docs/PHASE_C8_STATUS.md` section 39 | Narrative and tables | Written by the implementer |

## 6. Known limitations and open items

| ID | Item | State |
|---|---|---|
| RE-1 | Revert-and-return within one decision interval, or two live SpiceDB backends | **Accepted residual risk** (C-117), subject to I3 and this review |
| RE-2 | Writer inside the Authority domain copying the marker over stale tuples | Outside the Level 2 trust assumption; G14/S3 |
| RE-3 | AuthorityDb and SpiceDB restored together | Open: T16-A / G14 |
| RE-4 | Revoke committed after a decision was released | Pre-existing, unchanged |
| RE-5 | False quarantine persists until a G14 recovery | Accepted availability cost (B-Q1) |
| RE-6 | Undocumented SpiceDB behaviour | Pinned by CT-1 to CT-6 for one image version only |
| RR-6 | Local archive is in-memory or Azurite, not immutable or a Confidential Ledger | Open; no production immutability claim |
| RR-7 | An AuthorityDb administrator could alter the journal or lift the quarantine by DDL; separate production administration unproven | Open (G08) |
| RR-11 | T16S-1 | Remediated in Development/Testing only; pending this review and Azure staging |
| RR-12 | RN-1 | Resolved in Development/Testing only |
| RR-13 | RCF-1 | Open; fix not authorized |
| RR-14 | T16-A | Open; blocker for production readiness |
| RR-15 | P-S1-1 | Open (section 8) |
| M1 keys | Only `LocalDevelopmentKeyProvider` (one KEK, refused in staging and production by the startup guard) and `UnavailableKeyProvider` exist. **No production key provider and no key rotation** | Open; no production claim |
| Rollback enforcement | No PB-11 pipeline checks the minimum safe Authority version | Open |
| Flaky test | `StaleAndConcurrentChangesLoseCleanlyAndValidationHolds` once returned a retryable 503 `AUTHORITY_NOT_READY` (`AwaitingArchive`) instead of a 409. The test still expects only 409. It passed on the rerun. The cause was not investigated; the status document calls it a timing issue | Open, unexplained |
| PERF-01 | Azure staging performance gate for S1 (checks and search) | Not run; mandatory |
| Hosted CI | GitHub Actions has not run on these commits | Not run |
| Azure | No Azure staging evidence of any kind | Not run |

Not covered by this review: M4-2, G14 design, RCF-1 remediation, the performance remedies.

## 7. Stage B results

Source: [PHASE_C8_STATUS.md](../PHASE_C8_STATUS.md) section 39.2. Each run used a freshly recreated `enterpriseforms-sectest` stack.

**Not all seven shards have passed at one commit.** Five passed in full at `5f5fb07`. `administration-membership` passed in full only at `051a65d`. `search-catalogue` has never passed in full. Four shards were not rerun at `051a65d`.

| Shard | At `5f5fb07` (S1) | At `051a65d` (S1 + M1 + RC24 fix) |
|---|---|---|
| authorization-security-onboarding | 93/93 (an earlier uncommitted run was 92/93: RB-10 test setup error) | 93/93 |
| forms-documents | 38/38 | not rerun |
| records-lifecycle | 62/62 | not rerun |
| administration-membership | 52/55 | 55/55 |
| search-catalogue | 16/17 (480-record benchmark refused) | shard not rerun; class `SearchCatalogueTests` alone 11/12 (same failure) |
| search-execution | 14/14 | not rerun |
| protection-reconciliation | 22/22 | not rerun |

The three `administration-membership` failures at `5f5fb07`:
- **RC24**, caused by S1: the new trigger refused the test's own SQL clearance. Fixed in `051a65d` (test only).
- **RC19**, environment: NETSDK1004, the worktree had not been restored with NuGet.
- **`StaleAndConcurrentChangesLoseCleanlyAndValidationHolds`**, intermittent timing: see section 6.

### 7.1 Final Stage B matrix at f117995

Every shard was run again at `f117995` on a freshly recreated stack, 2026-11-05/06. A failed shard was rerun once, except for the known P-S1-1 benchmark. Evidence: `shards-final-f117995-<group>.txt`, `shards-final-rerun-f117995-<group>.txt` and `shards-final-rerun-f117995-quarantine-alerts.txt`.

| Shard | Final pass at `f117995` | Single rerun | Full pass at `f117995`? |
|---|---|---|---|
| forms-documents | 38/38 | not needed | yes |
| authorization-security-onboarding | 92/93: `SecurityAdministratorTests.BrowserSessionsNeedCsrfAndKeepNoPrivilegeAfterRemovalOrSignOut` got 423 | 92/93, the same test and the same 423 | **no** |
| records-lifecycle | 62/62 | not needed | yes |
| administration-membership | 55/55 | not needed | yes |
| search-catalogue | 16/17: the 480-record benchmark was refused (`time`, P-S1-1) | not rerun (known finding) | **no** |
| search-execution | 14/14 | not needed | yes |
| protection-reconciliation | **Incomplete.** One failure, then the host slept during the next test (the test ran 16 h and its docker call failed) | 22/22 | yes (rerun) |

**Five of seven shards have a full pass at `f117995`; two do not. The full Stage B suite is not green.**

**The authorization failure is reproducible, and it is a test-isolation defect, not a product defect.** Evidence from the rerun's Authority log, in the quarantine-alerts file:
- RB-13 (`Rb13AReplacedOrRecreatedDatastoreFailsClosed`) swaps in an empty SpiceDB datastore.
- S1 then quarantines **every** organization on the stack with `PROJECTION_SCHEMA_MISSING`: 18 organizations in one burst. That is the designed fail-closed behaviour, and it matches deployment rule I3: a replaced datastore is a recovery event.
- The burst includes the earliest organization on the stack, `01a1aee5-0415…`, created by dev-seed at stack start. That is the shared seeded development organization the browser-session tests sign in to.
- When `BrowserSessionsNeedCsrf…` runs after RB-13, its first roster read gets 423.
- The test passed at `5f5fb07` and `051a65d`. Test order evidently varies between runs; not investigated further.
- A read of the quarantine table failed (no database password was supplied), so the identification rests on the logs.

**Remedy, not implemented, test change for owner decision:** isolate the tests that destroy the whole datastore. Run RB-13, T16-S and the other whole-datastore restores in their own shard or after every seeded-organization test, or recreate the stack after them. Change no product behaviour.

The protection-reconciliation failure in the final pass, `TheCatalogueCeilingEndsIncompleteAndExpiredSnapshotsAreRenewedSafely`, did not recur in the rerun:
- expected `Blocked|ENCRYPTION_UNAVAILABLE|1`, actual `…|2`;
- the reconciliation was correctly blocked on the missing key, but it was already in round 2 when the test's 500 ms poll first saw it;
- recorded as an intermittent timing assertion; the cause has not been investigated.

## 8. P-S1-1 investigation

See [PHASE_C8_M4_1_P_S1_1_SEARCH_OPTIMIZATION_PROPOSAL.md](PHASE_C8_M4_1_P_S1_1_SEARCH_OPTIMIZATION_PROPOSAL.md). **No optimization has been implemented.**

- **Benchmark history:**
  * pre-S1 (class alone, fresh stack): 1,990 ms at 50 records, 5,738 ms at 150, 12,481 ms at 480 (passed);
  * S1 at `5f5fb07` (in the shard): 1,327; 4,276; refused;
  * S1 + M1 at `051a65d` (class alone): 1,561; 4,451; refused.
- **Timing probe at `f117995`:** throwaway instrumentation, not committed. The diff and raw lines are in the evidence folder, `p-s1-1-probe-*`. Per 50-record Authority batch, the S1 pin and verify took 2.8 ms and 4.6 ms (median), 0.7% of the batch. The rest:
  * per-leaf MAC verification (400 Vault Transit calls): 54%;
  * single-record SpiceDB checks: 19%;
  * per-record AuthorityDb reads and processing: 27%.

  On the business side, sequential BusinessDb content reads were about a third of the whole search. In the refused 480-record scope, S1's pin and verify took 68 ms over the 8 batches that finished before the deadline.
- **Implementer's conclusion, for the reviewer to test:** the refusal is a capacity limit that predates S1, not an S1 regression. The pre-S1 480-record figure is lower than a linear extrapolation of its own 150-record figure, and this is unexplained (proposal section 3).
- **Proposed remedies, for owner decision:**
  * O-1: batch-level bulk SpiceDB checks. These touch the S1 decision path, so the reviewer is asked to comment on proposal section 4, O-1.
  * O-2: batched BusinessDb reads.
  * O-3 and O-4 deferred to PERF-01.
- **Observation for PERF-01:** 3,840 HMAC verifications per 480-record search against Managed HSM in production.

## 9. Findings template

Severity scale: Critical, High, Medium, Low, Info. Add rows as needed.

| ID | Question # | Severity | Finding | Evidence | Recommendation | Implementation response |
|---|---|---|---|---|---|---|
| F-1 | | | | | | |
| F-2 | | | | | | |
| F-3 | | | | | | |

Also record for each of the eight questions: accept the claim, accept with conditions, or reject with a required change.

| Question # | Decision | Conditions or required change |
|---|---|---|
| 1 (S1 guarantees, RE-1) | | |
| 2 (exact snapshot, marker) | | |
| 3 (quarantine, recovery limits) | | |
| 4 (M1 cryptography) | | |
| 5 (I3 single backend) | | |
| 6 (rollback, minimum version) | | |
| 7 (RCF-1, G14) | | |
| 8 (evidence, performance) | | |

Overall verdict (reviewer's choice): S1 and M1 acceptable for continued development / acceptable with conditions / not acceptable.

## 10. Attestation

| Field | Entry |
|---|---|
| Reviewer name | |
| Organization | |
| Independence statement | I did not contribute to the design, implementation, testing or documentation of this project, and I am not an AI agent acting for the owner or for Claude. I have not relied on the owner's acceptance (C-118, C-119). |
| Conflicts of interest (or "none") | |
| Commit reviewed | |
| Scope reviewed (questions 1 to 8; any excluded) | |
| Date | |
| Signature | |

The owner's acceptance of S1 and M1 (C-118, C-119) is **not** a substitute for this attestation. This review does not by itself make S1 or M1 production ready; Azure staging evidence, PERF-01, G14 and the other items in section 6 remain.
