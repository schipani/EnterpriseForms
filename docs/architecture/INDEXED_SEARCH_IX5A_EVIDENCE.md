# IX-5a evidence: dark Tier 2 access projection (C-147)

- **Status (2026-11-10):** IX-5a implemented for Development/Testing under GA-IX5-1 and the approved design acceptance record (baseline `7ec5a79`). Owner-led; **not independently reviewed.** Tier 2 serves no search; IX-5b and IX-5c are not started.
- **Scope:** C-147 §2 (TQ-6 gate), §3 (journal completeness gate), §4 (access projection), §5 (acceptance tests 1–12).
- **Labelling (workflow rule 5):** "code fact" means checked by inspection, with file and line; "tested" means executed test evidence with a run id; everything else is a design statement.

## 1. TQ-6: item identity (first gate)

**Result: PASS. The assumptions hold; IX-5a proceeded.**

Tests: `SearchAccessItemIdentityTests`, 2/2, run `20261110-134314-2c1b730e`.

| Assumption | Evidence |
|---|---|
| A scalar and a single selection keep their item id across accepted revisions | Tested (revisions 1→2; the projection's item ids equal the accepted manifest's field leaves at each head) |
| Multi-select: a retained option keeps its id; a dropped option's id disappears; a new option gets a new id | Tested (t01,t02 → t02,t03) |
| Repeating-group entries and their values keep their ids through edits; a new entry gets new ids | Tested (two entries edited, one added) |
| A newly added item inherits no share | Tested: the share's (item, share) edges are unchanged by edits; the Reader sees the edited retained items, never t03 or a group item |
| A value entered after a clear is a new item, not covered by the old share | Tested (count cleared, then re-entered) |
| Direct and team shares of the same fields cover the same items | Tested |
| An ownership transfer changes no share edge and grants nothing | Tested: share edges identical before and after; previous owner denied; recipients' item sets unchanged |
| Repeating-group fields cannot be shared on their own | Tested (`FIELD_IN_REPEATING_GROUP`) |

**Finding R-53 (code fact, not tested; pre-existing; for owner decision).** The Authority enforces identity-column immutability (`field_definition_id`, group, record) only for share editors. For record owners, `ContentService.ClassifyMutations` (`src/authority/AuthorizationAuthority.Application/Features/Content/ContentService.cs:1221`) compares payload digests only. An owner-authorized write could therefore re-key an existing item id to a different field, and every share of that item would then disclose the new field. The product's write path never does this: `CandidateBuilder` keeps each field's item id. Only an owner-authorized caller could. SpiceDB, Tier 1 and Tier 2 all key by item id, so the three agree; this is not a Tier 2 divergence. Proposed hardening, not implemented: bind retained items to their identity columns on the owner path too.

## 2. Journal completeness gate

**Result: PASS.**

- **Code fact.** Canonical relationship edges are written only by `SecurityCommandService.ApplyPlan`. It journals the identical delta (`relationship_delta`) in the same transaction, and nothing else appends the authorization stream. This is enforced by `SearchAccessBoundaryTests.TheAuthorizationJournalAndTheCanonicalEdgesHaveOneWriter`.
- **Payload.** Every `SecurityCommandCommitted` body carries the full tuple delta and `committed_authorization_revision`; the envelope carries `stream_revision` and `previous_digest_hex`. Access derivation needs nothing else: no business data is read.
- **Durability and ordering.** Each event is in the same transaction as the edges it describes. Revisions are contiguous per organization. The verifier accepts only checkpoint + 1 and checks:
  * the SHA-256 of the stored bytes;
  * the envelope's format, organization, stream and revision;
  * the chain link, in both the column and the envelope;
  * the event kind;
  * the committed revision.
- **Replay.** `VerifyJournalReplayAsync` replays revision 1 to the head in memory and compares the result with the canonical edges. Tested: COMPLETE after every command kind exercised in the stack tests (§4).
- **Failure.** Missing, duplicated or moved, reordered and corrupted events fail closed: unit theory `JournalTamperingQuarantinesTier2AndOnlyARebuildLeavesIt`, and the stack theory (§4).

## 3. Implementation

| Part | Where |
|---|---|
| Entities | `AuthorizationAuthority.Domain/Features/SearchAccess/SearchAccessEntities.cs`: checkpoint, edge mirror, record grants, field-item grants |
| Derivation | `Application/Features/SearchAccess/SearchAccessDerivation.cs`, mirroring `authority.schema.zed`. Teams are principals and never expanded. Owners read all items with no per-item rows; shares produce one row per (principal, item, share). The distinct-item union is taken at query time |
| Journal verifier | `Application/Features/SearchAccess/SearchAccessJournalVerifier.cs` |
| Maintainer | `Application/Features/SearchAccess/SearchAccessProjectionService.cs`: step, catch-up, rebuild, verification, replay check, quarantine and status |
| Store | `Application/Common/Ports/ISearchAccessStore.cs`; `Infrastructure/Persistence/SearchAccessStore.cs` |
| Schema | `authority_search_access`, migration `AuthoritySearchAccessIx5a`. Forced RLS on `authority_search_access.organization_id`. Grants: runtime only (`AuthorityRuntimeGrants.SearchAccessStatements`); **no grant to the search reader** |
| Composition | `Api/Composition/SearchAccessComposition.cs`: `SearchIndex:Access:Mode` is Off by default; Dark is allowed only in Development/Testing; the maintenance loop. No serving switch exists |
| Testing endpoints | `Api/Features/SearchIndex/SearchAccessTestingEndpoints.cs` (Testing environment only, workload-authenticated) |
| SpiceDB check | `SpiceDbCheck` gained an optional `SubjectRelation` (default empty, so existing callers are unchanged) for team-level verification |

**Maintenance (FD-5-ALT).** The maintainer runs in its own transaction for each event:
1. lock the checkpoint (`FOR UPDATE`);
2. accept only event n = checkpoint + 1;
3. verify it;
4. apply its access edges to the mirror (a delete of an absent edge fails closed);
5. re-derive the rows of the affected records: the records named, the records of the named shares, and all their descendants;
6. compare-and-set the checkpoint from n − 1 to n;
7. commit.

It never takes the organization lock, so it adds no work to security commands. A checkpoint ahead of the organization, or at the head with a different digest, quarantines Tier 2. A checkpoint written by a newer derivation protocol fences an older binary out (design §12.4).

**Readiness (A-3)** requires all of:
* state `Maintaining`;
* the current protocol;
* the consumed revision equal to the organization's authorization revision;
* the consumed digest equal to its journal head digest.

**Quarantine** is recorded on the checkpoint with a reason code, journaled on the audit stream (`SearchAccessProjectionQuarantined`) and alerted. It is never repaired automatically. **Rebuild** (`SearchAccessProjectionRebuilt`, journaled) reads the canonical edges under the organization lock at its current revision and is the only way out of quarantine. The initial build of an organization with no checkpoint is automatic.

**Verification** has two parts:
1. M1, in one read-only snapshot: the mirror equals the canonical access edges, and the stored rows equal their derivation;
2. a sampled SpiceDB comparison in a read-committed scope at a pinned S1 snapshot, so a concurrent command is reported as "moved", never as a mismatch. It runs team-level checks (`team#participant`) and user-level checks for team members, covering `effective_owner`, `discover` and `field_item#read`.

A difference quarantines Tier 2.

## 4. Focused test results

All local Docker security-test stack runs, synthetic data, focused classes only (C-131). **Not Stage B.**

| Suite | Result | Run |
|---|---|---|
| Authority unit tests (17 new in `SearchAccessProjectionTests`) | 487/487 | `c147-unit-20261110-142307` |
| Architecture tests (5 new in `SearchAccessBoundaryTests`) | 102/102 | local, 2026-11-10 |
| `SearchAccessItemIdentityTests` (TQ-6 gate, before IX-5a code) | 2/2 | `20261110-134314-2c1b730e` |
| `SearchAccessProjectionStackTests` (IX-5a) | **10/10** | `20261110-142446-2b6aa53d` (fresh stack, rebuilt IX-5a images) |
| `SearchAccessItemIdentityTests` on the IX-5a images | 2/2 | `20261110-142644-ec193db1` |
| `SpiceDbBehaviourContractTests` (CT-1 to CT-6, IX2-CT-07; the bulk-check echo now matches the subject relation) | 7/7 | `20261110-142705-8f99e16d` |
| Tier 1: `IndexedSearchShareOnlyItemChannelTests` | 6/6 | `20261110-142718-7a8cf03d` |
| Tier 1: `SearchIndexFoundationTests` | 9/9 | `20261110-143041-a7cf0cf6` (its commit-latency measurement ran on battery; its numbers are invalid and not reported) |

Acceptance tests (C-147 §5) and where each is shown:

| # | Test | Evidence |
|---|---|---|
| 1 | TQ-6 identity stability | `SearchAccessItemIdentityTests` (§1) |
| 2 | Direct, team and overlapping sharing | Stack `SharingPathsOwnershipChildrenRevocationAndMembershipProjectExactly`; unit derivation test |
| 3 | Ownership transfer and child unlinking | The same stack test (transfer to a team moves ownership of the record and its child; unlink re-verified); unit catch-up test |
| 4 | Field-item permissions | Item rows per share equal the shared items exactly, by field |
| 5 | Revocation and team membership changes | The same stack test plus `ReadinessIsExactAndARevocationIsNeverReadyWhileUnconsumed`; membership changes write no row |
| 6 | Organization isolation | `OrganizationsAreIsolatedAndTheSearchReaderCannotReadTier2`: forced RLS (bound organization only; nothing unbound; cross-organization insert refused); runtime cannot update derived rows or delete checkpoints; **the search reader is denied on the schema** |
| 7 | Exact checkpoint readiness | `ReadinessIsExactAndARevocationIsNeverReadyWhileUnconsumed` |
| 8 | Crash recovery and idempotent replay | `ACrashBeforeCommitRollsBackAndRecoveryReplaysIdempotently` |
| 9 | Concurrent maintainer fencing | `ConcurrentMaintainersAreFencedByTheCheckpoint` |
| 10 | Journal tamper detection | Theory `JournalTamperingQuarantinesTier2OnlyAndIsNeverRepairedSilently`: body, chain, gap and reorder; unit theory (five kinds) |
| 11 | Quarantine and controlled rebuild | `DivergedRowsAreQuarantinedWithEvidenceKeptAndOnlyAControlledRebuildRestoresThem` |
| 12 | Tier 1 equivalence and unchanged command transactions | Architecture tests (no search, command or content path references Tier 2); Tier 1 stack classes rerun on the IX-5a images, all passing (above); a share commits while Tier 2 is quarantined (test 11). The commit-latency half (P-5) was not measured (§6) |

Every stack step ends with verification CLEAN, comprising: M1 (mirror = canonical edges, rows = their derivation); SpiceDB comparison with checks > 0; journal replay COMPLETE.

Two defects found and corrected during the runs, both in IX-5a code, before the passing run:
1. **The SpiceDB comparison's expected answer for a user counted only the user's own rows.** SpiceDB answers through the user's teams as well, so verification quarantined correct rows (fail-closed, but wrong). Corrected: the expected answer is the distinct union over P = {user} ∪ the user's teams, from canonical membership. That is INV-T2-13, the same union a Tier 2 query must take.
2. **Verification could raise an error when S1 was not yet pinnable** (SpiceDB projection behind). It now returns `SPICEDB_NOT_READY`, without quarantine.

A test-setup defect (the workspace forms were not built) was also corrected.

## 5. Checkpoint, fencing and quarantine evidence

- **Exact readiness:**
  * the maintenance loop paused;
  * one share leaves the status `Building` (consumed < current) and verification `NOT_AT_HEAD`;
  * a revocation leaves the stale item row physically present but the status not `Ready`, so no Tier 2 query could use it (A-3);
  * after consumption it is `Ready` and the row is gone.
- **Crash:**
  * the `CrashBeforeAccessProjectionCommit` fault is armed and an explicit catch-up runs;
  * the Authority process terminated mid-transaction (marker in the log) and restarted;
  * after recovery the checkpoint revision advanced by **exactly one** for the one event, the consumed revision equals the organization's, and verification is CLEAN with replay COMPLETE. Nothing of the crashed transaction committed, and the redo was applied once.
- **Concurrent maintainers:**
  * five or more events, then eight parallel catch-up calls;
  * the advances summed to exactly the number of events, and the checkpoint revision increased by exactly that number;
  * verification is CLEAN.

  Compare-and-set loss is unit-tested (`Contended`, checkpoint unchanged).
- **Tampering (stack):**

  | Tamper | Reason code |
  |---|---|
  | Body edited (`#reader@` → `#manager@`, a role escalation) | `ACCESS_JOURNAL_DIGEST_MISMATCH` |
  | Previous-digest column zeroed | `ACCESS_JOURNAL_CHAIN_BREAK` |
  | Event moved away (gap) | `ACCESS_JOURNAL_GAP` |
  | Two events swapped | `ACCESS_JOURNAL_CHAIN_BREAK` |

  In each case:
  * Tier 2 is quarantined;
  * journal replay is not COMPLETE;
  * after the loop resumed, the status stayed Quarantined;
  * a `SearchAccessProjectionQuarantined` audit event was journaled.

  Unit tests add: wrong event kind; a delete of an edge absent from the mirror; a checkpoint ahead of the organization (restore); and a newer protocol fenced (no writes).
- **Divergence:**
  * a forged grant row is inserted directly (AuthorityDb administrator);
  * verification returns `ACCESS_ROWS_DIVERGED` and quarantines;
  * the forged row is kept as evidence, not repaired;
  * the loop does not touch it;
  * a share still commits (command transactions are unaffected);
  * a controlled rebuild (journaled `SearchAccessProjectionRebuilt`) restores `Ready`, verification is CLEAN, and the forged row is gone. A mirror divergence is unit-tested (`ACCESS_MIRROR_DIVERGED`).

## 6. P-5

Not run: the host was on battery for the whole session (`BatteryStatus` 1, `PowerOnline` false), and the standing rule requires AC power for timing. No latency number is reported. P-2, P-4 and P-6 were also not run (timing).

## 7. Remaining defects and limitations

- **R-53 (open, owner decision):** the owner content path does not bind a retained item id to its field (§1). Pre-existing, Tier-independent.
- **R-54 (open):** a 1-in-256 flaky unit test, unrelated to IX-5a (register).
- **P-5 not measured (§6).** "Unchanged command transactions" is shown structurally (architecture tests: no command or content path references Tier 2; the maintainer never takes the organization lock), not by timing. **R-02 stays FAIL.**
- **Content stream not consumed.** A-3 readiness for serving needs both the authorization and the content checkpoint. IX-5a maintains access rows only, which the content stream does not change, so only the authorization checkpoint exists. The content checkpoint belongs to IX-5b, with the value rows.
- **Archive gate.** The maintainer consumes committed journal events without waiting for their archive receipt (the SpiceDB projector waits). Tier 2 is not ready until it equals the head, and a restore is detected (checkpoint ahead or digest mismatch). Whether serving must also require the anchored receipt is an IX-5c decision.
- **Scale and cost (unmeasured):**
  * a step loads only the affected records' subgraph, but verification and rebuild read the whole organization: rebuild under the organization lock, verification from a snapshot. Neither is measured at 100K/250K (R-43);
  * the SpiceDB comparison is a sample (default 20 records), not complete; complete coverage is M1 against the canonical edges, not against SpiceDB;
  * the maintenance loop visits every organization every 250 ms (sectest) or 500 ms (default).
- **Recovery and operations:**
  * rebuild is reachable only through the Testing endpoint; an operator-plane rebuild with approvals is not implemented;
  * there is no scheduled verification sweep, so M1 and SpiceDB verification run on demand only. The §17.10 deadlines do not yet apply to Tier 2;
  * G14 coordinated recovery is not integrated: a restore is detected and fails closed, but there is no recovery procedure.
- **Production-only gaps:**
  * Dark mode is refused outside Development/Testing;
  * no Azure staging, Managed HSM or signed external checkpoint evidence;
  * independent review outstanding (GA-IX5-1 §2.6).
- **Tier 2 serves no search.** There is no serving switch and no query reads these rows (architecture test). IX-5b and IX-5c are not started.
