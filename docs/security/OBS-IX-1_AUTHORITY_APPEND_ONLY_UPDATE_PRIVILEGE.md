# OBS-IX-1: Authority runtime role holds UPDATE on tables described as append-only

- **Status:** **OPEN finding, recorded separately by owner instruction (C-121, 2026-11-06).**
  * Raised during the indexed-search design (C-120). It is not caused by that design and does not depend on it.
  * **No remediation is implemented or authorized.**
  * It is included in the indexed-search independent review package as question 12.
  * **C-122 (FD-7, deferred):** the independent reviewer is to establish the severity and recommend remediation. The proposed Medium is **not** accepted and must not be treated as an accepted rating.
- **Raised by:** Claude (Opus), from source inspection at `e830d08`. Not an independent finding. Its severity is **proposed**, not assessed.
- **Area:** G08 (privileged PostgreSQL access), AuthorityDb; related to G02, G03 and G14 (journal and manifest immutability).

## 1. Observation

**Grants.** `src/authority/AuthorizationAuthority.Infrastructure/Persistence/AuthorityRuntimeGrants.cs`:
- Its comment says the runtime role "cannot DELETE from the journal/command tables (append-only to the runtime)".
- It grants `SELECT, INSERT, UPDATE` on both its `AppendOnlyTables` list and its `MutableTables` list; the same statement appears in both loops.

The `AppendOnlyTables` list is:

`security_commands`, `security_journal_entries`, `authority_relationship_edges`, `projection_outbox_events`, `record_manifest_commits`, `record_manifest_leaves`, `content_integrity_checkpoints`, `content_candidate_leaves`, `search_catalogue_snapshot_entries`, `published_form_versions`, `protection_census_results`, `protection_reconciliation_attestations`, `platform_journal_entries`, `platform_operator_evidence_uses`.

**Role.** The runtime role is created by `infrastructure/docker/postgres/init-authority-roles.sh`: `NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS`. It has no `DELETE` and no DDL. AuthorityDb has no row-level security and no immutability trigger on these tables. The S1 permanence trigger covers only quarantine clearing on `organization_authorization_states`.

**Consequence.** For these tables, "append-only" is enforced by the absence of `DELETE` and by application code, not by privilege. Any SQL executed as the runtime role can rewrite existing rows, including content columns.

## 2. Which updates are legitimate (source inspection, not exhaustive)

| Table | Columns legitimately updated by the code | Evidence |
|---|---|---|
| `security_journal_entries`, `platform_journal_entries` | `archive_delivery_state` (Pending → Anchored / VerificationFailed) | `ArchiveProcessor.cs` 33/42/51; `PlatformJournal.cs` 195/439 |
| `security_commands` | `command_state_name` (CommittedAwaitingProjection → Effective / FailedReconciliation / Rejected) | `SecurityCommandService.cs` 82/248; `ProjectionProcessor.cs` 85/112 |
| `authority_relationship_edges` | `removed_by_security_command_id`, `relationship_removed_on` (logical removal) | `SecurityCommandService.cs` 150–151 |
| `projection_outbox_events` | delivery state, attempt count and time, confirmed token, applied time, fencing epoch, error code | `ProjectionProcessor.cs` |
| `record_manifest_commits`, `record_manifest_leaves`, `content_integrity_checkpoints`, `content_candidate_leaves`, `published_form_versions`, others | **None found**: these appear to be written once | grep of property assignments; to be confirmed |

So the role needs `UPDATE` only on specific lifecycle columns of four tables. It needs none on the content columns of any listed table, such as journal bodies and digests, edge tuples, manifest leaves and checkpoints.

## 3. Impact (proposed analysis, for the reviewer)

**Who can exploit it:**
- SQL running as the Authority runtime role: a compromised Authority runtime, an SQL-injection defect in the Authority, or a stolen runtime credential.
- The Authority runtime is **trusted** in the Level 2 model (`CLAUDE.md` §4M), so this is defense in depth, not a model violation.
- A business database administrator cannot exploit it (separate cluster, G08).

**What it would allow:**
- rewriting a journal entry's body or digests after anchoring;
- reactivating a logically removed edge by clearing `removed_by_security_command_id`;
- altering manifest leaves or checkpoints of accepted content.

**Existing detection (partial; not verified in this finding):**
- the archive and ledger hold the anchored digests, and G14 recovery compares against them;
- the projector refuses a missing digest (`JOURNAL_CHAIN_GAP`) but does not recompute body digests on its path;
- an altered manifest leaf fails the next full verified read of that record (root mismatch);
- an edge reactivated in AuthorityDb would not by itself reach SpiceDB without a projection step.

The reviewer should determine which alterations are detected, by what, and when.

**Proposed severity: Medium** (defense-in-depth gap on integrity-critical tables; trusted actor required). **Not accepted (C-122 FD-7).** The reviewer establishes the severity.

## 4. Remediation options (not implemented; each needs owner approval)

| Option | Description | Notes |
|---|---|---|
| A | **Column-level grants:** `GRANT UPDATE (archive_delivery_state) ON security_journal_entries …`, similarly for the four tables; `SELECT, INSERT` only on the write-once tables | Smallest change; EF Core issues column-specific UPDATEs only for modified properties, which needs a test. A migration plus a change to `AuthorityRuntimeGrants` and its unit test |
| B | **Immutability triggers:** `BEFORE UPDATE` triggers that refuse changes to any column except the allowed lifecycle columns, and refuse every UPDATE on write-once tables | Also protects against code defects; does not stop the table owner (as RC24 shows for S1) |
| C | A + B | Recommended for review |
| D | Accept as residual risk | Requires an explicit owner risk acceptance |

**Tests a remediation would need:**
- for each listed table, an UPDATE of a non-lifecycle column as the runtime role fails;
- the legitimate lifecycle updates still succeed;
- Stage B shards, S1 RB/CT suites and G14-related tests pass.

## 5. Relation to the indexed-search design

- Revision 3 does not depend on this finding.
- The new `authority_search` tables are **derived** and are granted `DELETE` deliberately (design §5.1). Their integrity relies on I-1, I-2, entry seals and sweeps (design §6, §17.5), not on append-only privileges.
