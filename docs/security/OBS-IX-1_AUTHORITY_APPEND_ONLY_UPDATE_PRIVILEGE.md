# OBS-IX-1: Authority runtime role holds UPDATE on tables described as append-only

- **Status:** **OPEN finding, recorded separately by owner instruction (C-121, 2026-11-06).**
  * Raised during the indexed-search design (C-120). It is not caused by that design and does not depend on it.
  * **No remediation is implemented or authorized.**
  * It is included in the indexed-search independent review package as question 12.
  * **C-122 (FD-7, deferred):** the independent reviewer is to establish the severity and recommend remediation. The proposed Medium is **not** accepted and must not be treated as an accepted rating.
  * **C-124:** **provisional High, pending verification** (owner decision). Remediation proposal: `AUTHORITY_PRIVILEGE_REMEDIATION_PROPOSAL.md`, which also covers the broader exposure of the runtime database credential (INSERT-based paths). No remediation authorized.
  * **C-123:** kept **open**. §6 adds the owner-review analysis (actual exposure, exploitation paths, detection, remediation alternatives). **No final severity is assigned.**
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
| `security_journal_entries`, `platform_journal_entries` | Archive lifecycle only: `archive_delivery_state` (Pending → Anchored / VerificationFailed), archive attempt count, receipt reference and digest, archived time, last archive error code | `ArchiveProcessor.cs` 26–57; `PlatformJournal.cs` 195/439 |
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

**Proposed severity: Medium** (defense-in-depth gap on integrity-critical tables; trusted actor required). **Not accepted (C-122 FD-7).** **C-124: provisional High, pending verification.**

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

## 6. Owner-review analysis (C-123)

Prepared for the owner's owner-led review (`docs/governance/REVIEW_AND_AUTHORIZATION_WORKFLOW.md`, kind 1). Source inspection at `e830d08` only; **nothing was executed**. Not an independent assessment.

### 6.1 Actual privilege exposure

The runtime role holds `SELECT, INSERT, UPDATE` on all 14 tables (`AuthorityRuntimeGrants.cs` 49–52). What `UPDATE` permits beyond the legitimate lifecycle columns of §2:

| Tables | Content that can be rewritten in place |
|---|---|
| `security_journal_entries`, `platform_journal_entries` | Event body, event digest, previous-entry digest, stream name and revision, event kind, operation id |
| `security_commands` | Payload, actor, reason, command kind, expected revision |
| `authority_relationship_edges` | Resource, relation and subject of a tuple; **clearing `removed_by_security_command_id` reactivates a removed edge** |
| `projection_outbox_events` | The payload to be projected to SpiceDB, before delivery |
| `record_manifest_commits`, `record_manifest_leaves`, `content_integrity_checkpoints`, `content_candidate_leaves` | Roots, leaf digests, row signatures, revisions, checkpoint digests |
| `published_form_versions` | The published field manifest that AC-6 field-search eligibility reads (`FieldSearchRules.ReadManifest`; `FieldSearchCapabilityService.cs` 56, 110) |
| `protection_census_results`, `protection_reconciliation_attestations` | Census outcomes and attested revisions |
| `search_catalogue_snapshot_entries`, `platform_operator_evidence_uses` | Snapshot entries; evidence-use records |

### 6.2 Who can exercise it

| Path | Assessment |
|---|---|
| Code execution in the Authority process | The `authority` container is the only holder of the runtime credential (`infrastructure/docker/compose.yaml` 203–216). It runs the API, the work loop (projector, archive) and the S1 monitor (`Program.cs` 94, 97) |
| SQL injection in the Authority | No injection surface found. The only raw SQL is the grants code, with regex-validated identifiers from configuration (`AuthorityRuntimeGrants.cs` 37–62), and `SET TRANSACTION READ ONLY`. Row locks use `FromSqlInterpolated`, which parameterizes (`AuthorityStore.cs` 54, 164, 301, 404, 500) |
| Stolen runtime credential | Locally a password in the environment. In Azure, a managed-identity token for the runtime role. Network access to AuthorityDb is required (authority network only) |
| Business DBA, business API, business worker | **No path** (separate cluster and network, G08) |
| AuthorityDb administrator | Has full rights regardless of this grant; this finding does not change that exposure |

### 6.3 Context: what `INSERT` already allows

`INSERT` is legitimately needed on these tables, and it is not addressed by any option below.
- **Security-command authorization is decided from the canonical edges in AuthorityDb, not from SpiceDB.** `SecurityCommandService.cs` 68 builds `DesiredStateGraph` from `GetActiveEdgesAsync`. `CommandPlanner` authorizes from that graph (for example lines 152–153, 189, 241, 286, 367, 620).
- So a holder of the runtime credential can already insert, for example, an organization-owner edge for an identity it controls, and then issue legitimate security commands that are journaled and projected to SpiceDB.
- **Conclusion:** the runtime credential can forge Authority state whatever the UPDATE grant. This is consistent with the Level 2 model, where the Authority runtime is trusted.
- **What `UPDATE` adds** is the ability to rewrite **existing history in place**: journal bodies and digests, manifest leaves and roots, checkpoints. It also allows reactivating edges without creating any new row or journal entry.

### 6.4 Exploitation paths and detection today

| Alteration (as the runtime role) | Effect | Detected today by | When |
|---|---|---|---|
| Rewrite a journal body **after** it is anchored | Local evidence falsified | Not by the archive processor: it recomputes a body digest only before delivery (`ArchiveProcessor.cs` 26–36). Not by S1: its markers bind `organization_authorization_states.latest_security_journal_digest`, a stored column, and do not recompute bodies (`ProjectionIntegrityVerifier.cs` 218, 372). The external archive holds the original | Only when the local journal is compared with the archive (G14 recovery; **not implemented**) |
| Rewrite a **pending** journal body, keeping its digest | Delivery refused | Archive processor: `JOURNAL_BODY_DIGEST_MISMATCH` → organization quarantine (`ArchiveProcessor.cs` 29–36) | Next archive pass |
| Rewrite a pending body **and** its event digest consistently | A forged body could be archived | Chain-link verification (previous-digest of the next entry) on the archive path was **not confirmed** by this inspection; the external archive refuses a different body for an already-archived revision (`ARCHIVE_BODY_MISMATCH`, 50–55) | Unconfirmed |
| Clear `removed_by_security_command_id` on an edge | The edge is active again in `DesiredStateGraph`: command authorization and commit-time content authorization (`ContentService.cs` 284) treat it as present. SpiceDB is unchanged until something re-projects | Not per request: S1 compares markers, not tuples. A full canonical-edge to SpiceDB reconciliation (S3/G14) would detect it | Not implemented |
| Rewrite a manifest leaf digest or root | Open path: BusinessDb bytes no longer match → **record quarantine** (false quarantine, a denial of service). Forged content cannot pass the open path, because the MAC of the bytes is verified (`ContentService.cs` 993–1016) | Open path | On next open |
| Rewrite `published_form_versions` manifest | Alters the Authority's view of field classification used by AC-6 eligibility. Impact is bounded by the encryption floor (Confidential values are ciphertext), but not fully analysed | None identified | — |
| Rewrite checkpoints, census results, attestations | Evidence falsified locally | No request-path reader identified in this inspection | G14 / archive comparison |
| Indexed-search design (not implemented): coordinated rewrite of posting, leaf and head root | A false match among records the caller may already discover | Entry seals (S-B) and the open path; see owner review OR-1 | S-B deadline |

### 6.5 Factors relevant to severity (no final severity assigned)

**Toward higher severity:**
- `CLAUDE.md` §4D requires audit storage to be "append-only to ordinary application identities; deny ordinary UPDATE/DELETE". The Authority runtime is an application identity, and its journal is security-event evidence.
- The code's own grant comment states the tables are append-only to the runtime.
- In-place history rewrite is undetected until G14, which is not implemented.

**Toward lower severity:**
- A trusted credential is required (Level 2 model, `CLAUDE.md` §4M).
- The incremental risk over `INSERT` is limited for authorization: forging is already possible.
- The external archive and ledger hold the original anchored evidence.

**Severity:** the earlier proposed Medium is not accepted (C-122). The owner or an independent assessor assigns it.

### 6.6 Remediation alternatives

None is implemented or authorized. Each needs owner approval, and none is part of the indexed-search work.

| Option | What it closes | What it leaves | Cost and risk |
|---|---|---|---|
| A. Column-level `UPDATE` grants on the lifecycle columns of §2; `SELECT, INSERT` only on write-once tables | In-place rewrite of content columns by the runtime role | `INSERT` forgery (§6.3); rewrite by the table owner or administrator | Migration and `AuthorityRuntimeGrants` change. Needs a test that EF Core updates only modified columns, and a check of every update path (an update touching an unlisted column would fail at runtime) |
| B. `BEFORE UPDATE` immutability triggers (refuse changes to non-lifecycle columns; refuse all updates on write-once tables) | Same as A, and also protects against code defects | Same as A; a table owner can disable triggers | Trigger migration; tests per table |
| C. A + B | Both of the above | `INSERT` forgery; administrator | Sum of A and B |
| D. Accept as residual risk | Nothing | Everything in §6.4 | Explicit owner risk acceptance; conflicts with `CLAUDE.md` §4D |
| E. Detection: a periodic job that recomputes anchored journal bodies, digests and chain links and compares them with archive receipts, and compares canonical edges with SpiceDB | In-place rewrite and edge reactivation, **detected** within the job interval | Prevention | New Authority job; part of the G14/S3 backlog |
| F. A separate append-only journal-writer role used only by the journal code path | Narrows the identity that can touch journals | Complexity; other tables | Larger change; second credential in the same process (compare UF-5) |

**Suggested for the owner's consideration:** C to restore the stated append-only property, plus E as the detection layer that also covers `INSERT` forgery. This is a recommendation for review, not a decision.
