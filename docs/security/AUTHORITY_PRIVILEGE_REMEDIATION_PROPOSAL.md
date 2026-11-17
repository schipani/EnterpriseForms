# Authority privilege remediation proposal (OBS-IX-1 and the runtime database credential)

- **Status:** **PROPOSAL for owner review (C-124). Nothing is implemented or authorized.** Remediation needs a separate authorization.
- **Severity of OBS-IX-1:** **provisional High (C-124), pending verification.**
- **Kind:** evidence for an owner-led review (`docs/governance/REVIEW_AND_AUTHORIZATION_WORKFLOW.md`). Not an independent assessment.
- **Evidence:** source inspection at `e830d08`. **No attack path in this document was executed.** Each path is a reading of the code. A reviewer or an authorized adversarial test must confirm it.
- **Related:**
  * `OBS-IX-1_AUTHORITY_APPEND_ONLY_UPDATE_PRIVILEGE.md` (§6 exposure and alternatives);
  * `PROJECTION_INTEGRITY_S1_OPERATIONS.md` (RE-2: writers inside the Authority domain are outside the Level 2 trust assumption);
  * indexed-search design §18.1 (adversaries AD-1 to AD-3).

## 1. Summary

1. OBS-IX-1, `UPDATE` on "append-only" tables, lets the runtime role **rewrite existing history in place** and **reactivate removed permissions**.
2. A broader exposure exists independently of `UPDATE`.
   - The runtime role legitimately holds `INSERT` on commands, journal entries, outbox events and edges, and `UPDATE` on the organization state and identity bindings.
   - The security journal chain is **unkeyed** SHA-256 (`JournalChain.cs` 30–57).
   - The projector applies the outbox delta to SpiceDB once an anchored journal entry exists at that revision (`ProjectionProcessor.cs` 28–70).

   Read together, a holder of the runtime **database** credential alone (AD-1, no HSM) appears able to create authorization history that is archived and projected as if legitimate (AP-3).
3. **Options C and E together are not sufficient** against AD-1. C closes in-place rewrites only. E compares local state with the archive, but the archive would hold the forged entries too. Preventing AD-1 forgery needs **keyed authentication** of authorization-relevant state (component R6).
4. **Recommendation on blocking:**
   - it does **not** block IX-1 (Development/Testing, dark derivation, synthetic data; IX-1 changes none of these paths);
   - it **blocks** any environment with real customer data and the production Level 2 gate, until the owner authorizes remediation or explicitly accepts the risk under the Level 2 trust assumption (RE-2).

## 2. Attackers

| Id | Attacker | Has |
|---|---|---|
| AD-1 | Holder of the Authority runtime **database** credential (stolen credential or SQL execution as that role) | AuthorityDb `SELECT, INSERT, UPDATE`; no MAC provider; no SpiceDB key |
| AD-2 | Compromise of the Authority **process** | AD-1 plus MAC provider and SpiceDB |
| AD-3 | AuthorityDb administrator | Everything in the database, including DDL |
| AD-B | Business DBA | No path (G08) |

## 3. Attack-path analysis (read, not executed)

| Path | Attacker | Steps (high level) | Effect | Detected today | C (column grants + triggers) | E (archive reconciliation) | R6 (keyed journal + bound outbox) |
|---|---|---|---|---|---|---|---|
| **AP-1 Identity substitution** | AD-1 | `UPDATE identity_bindings` (a mutable table) so that the attacker's identity-provider subject maps to an existing privileged Authority user | Sign in as that user: all of their authority | Not identified | No (mutable table; needs a trigger on the subject columns specifically) | Only if bindings are compared with journaled history | Only if bindings are keyed or journal-derived |
| **AP-2 Forged permission edge** | AD-1 | `INSERT` an organization-role or ownership edge into `authority_relationship_edges` | Security commands are authorized from the canonical edges (`SecurityCommandService.cs` 68; `CommandPlanner`), so the attacker can issue legitimate commands (for example sharing a record to themselves); those are journaled, archived and projected | Not per request: S1 compares markers, not tuples | **No** (INSERT) | Yes, if E replays the journal into expected edges and compares (an edge without a creating journal entry) | Yes, if decisions use only journal-verified edges |
| **AP-3 Forged command history** | AD-1 | `INSERT` a command, a journal entry whose unkeyed chain digest the attacker computes, and an outbox event; `UPDATE` the organization's revision and latest digest | The archive processor archives the forged entry (body and digest consistent). The projector applies the forged delta to SpiceDB with a valid new marker. S1 then sees a consistent state | **Not identified.** No check of the outbox delta against the journal body was found in `ProjectionProcessor.cs` 28–70; to be confirmed | **No** (INSERT and mutable state) | **No**: the archive holds the forged entry too | **Yes**: AD-1 cannot produce journal MACs, and the projector would refuse a delta not bound to a MAC-verified body |
| **AP-4 Permission reactivation** | AD-1 | `UPDATE` an edge to clear `removed_by_security_command_id` | The edge is active again for command and commit-time authorization; SpiceDB unchanged until a later projection | Not per request | **Yes** (trigger: removal is monotonic) | Yes | Yes |
| **AP-5 In-place history rewrite** | AD-1 | `UPDATE` an anchored journal body or digest, a manifest leaf, a root or a checkpoint | Local evidence falsified; a leaf rewrite produces a false record quarantine on open | Pending entries only (`ArchiveProcessor.cs` 26–36); not after anchoring | **Yes** | Yes (local ≠ archive) | Yes |
| **AP-6 Search-index tampering** (design, not implemented) | AD-1 | Rewrite posting, leaf and head root together | False match among discoverable records (OR-1) | Seals (design) | Partly | Yes | n/a (seals are already keyed) |
| AP-7 Any of the above | AD-2 | Same, plus valid MACs | Full compromise inside the Authority | Only external evidence | No | Partly (rewrites only) | No |
| AP-8 Any of the above | AD-3 | Same, plus DDL (disable triggers) | As AD-1, and can remove C | pgAudit (human access) | No | Yes (rewrites, if E runs outside AD-3's control) | Yes (no MAC provider) |

**Consequence of compromising the runtime database credential:** on this reading, **AD-1 is equivalent to full authorization authority over every organization**. It can impersonate users (AP-1), grant itself access (AP-2, AP-3) and rewrite history (AP-5), with detection only for some paths and only after the fact. This is consistent with the stated Level 2 assumption that the Authority domain is trusted (S1 RE-2), but it means that protection rests entirely on custody of that credential.

## 4. Remediation components

| # | Component | Closes | Leaves | Migration and compatibility risks |
|---|---|---|---|---|
| R1 | **Column-limited privileges**: `UPDATE` only on the lifecycle columns of OBS-IX-1 §2; `SELECT, INSERT` only on write-once tables; column grants on `identity_bindings` for its legitimately updated columns | AP-5 for content columns; part of AP-1 | AP-2, AP-3, the administrator | EF Core issues column-specific updates for tracked entities, and no `Update()` call exists in the Authority (checked). Still needs a test per entity. Any future code touching an ungranted column fails at runtime. A rolling deploy must apply grants and code together |
| R2 | **Database-enforced immutability**: `BEFORE UPDATE` triggers that refuse changes to non-lifecycle columns, and refuse every update on write-once tables | AP-5, also against code defects | The table owner and administrator can disable triggers | Triggers on hot tables (journal, leaves) add a small cost per write; the projector and archive processor updates must be listed exactly |
| R3 | **Reactivation protection**: `removed_by_security_command_id` and `relationship_removed_on` may go from NULL to a value only, never back or to another value; edge tuple columns are immutable | AP-4 | AP-2 (new edges) | None beyond R2 |
| R4 | **Journal, digest and checkpoint immutability**: body, digests, stream revision and kind are immutable; `archive_delivery_state` moves only Pending → Anchored or VerificationFailed; receipt fields are write-once | AP-5 for journals and checkpoints | AP-3 (new entries) | As R2 |
| R5 | **Independent archive reconciliation (option E)**: a job, running under a **separate read-only identity** (not the runtime role), that: (a) recomputes anchored body digests and chain links and compares them with archive receipts and bodies; (b) replays the authorization journal into the expected edge set and identity bindings and compares them with AuthorityDb and SpiceDB; (c) compares content heads with the anchored content checkpoints | Detects AP-1 (if bindings are journal-derived), AP-2, AP-4, AP-5 after the fact | **AP-3** (forged entries are archived too); AD-2 | False positives quarantine organizations (permanent until G14): needs staged rollout and dry-run mode. Load on AuthorityDb and archive. Part of the G14/S3 backlog |
| R6 | **Keyed journal authentication and bound outbox**: each journal entry carries a MAC (separate key purpose) over its existing canonical envelope digest, made through the MAC provider when the command commits. The archive processor and projector verify it. The projector requires the outbox delta to be bound by digest to the MAC-verified journal body. Authorization decisions use only edges whose creating entry is MAC-verified | AP-3; with the decision rule, AP-2 | AD-2 | **Largest change.** One MAC sign per command and verification on archive and projection. The rule "decisions use only verified edges" needs a design for verifying the graph without one HSM call per edge (for example an in-process graph rebuilt incrementally from verified entries). A new signed message schema needs explicit cryptographic review. Existing history needs a migration (sign-forward from a reviewed checkpoint) |
| R7 | **Credential custody**: managed identity only (no password); private network; PIM/JIT for any human use; rotation; alerts on runtime-role sessions from unexpected hosts | Reduces the likelihood of AD-1 | Everything once AD-1 exists | Azure configuration; PB-11/PB-05 |

**Options C (R1 + R2, with R3 and R4) and E (R5) evaluated together:**
- they close AP-4 and AP-5;
- E detects AP-2 after the fact;
- they leave **AP-3 undetected and unprevented**, and AP-1 unless bindings are covered.

They are therefore **not sufficient** against AD-1. Adding R6 (and binding coverage for AP-1) is required to make AD-1 detectable or preventable. R7 reduces the likelihood in all cases.

## 5. Blocking recommendation

| Gate | Blocked? | Reasoning from the attack paths |
|---|---|---|
| **IX-1** (dark derivation, Development/Testing, synthetic data) | **No** | IX-1 grants no new privilege on any `authority` table: `DELETE` only on derived search tables, and the reader role is SELECT-only. It changes none of AP-1 to AP-5. Seals add keyed protection for the index against AD-1 (AP-6). With synthetic data, the plaintext that R-D adds to AuthorityDb has no customer value |
| IX-2 to IX-4 (Development/Testing) | No | Same reasoning; no real data |
| Any environment holding **real customer data** (staging with real data, production, PB-13 onboarding) | **Yes** | AP-1 to AP-3 give AD-1 full authorization authority, and indexed search adds approved plaintext to the AuthorityDb that AD-1 can read |
| **Production Level 2** | **Yes** | Owner decision required: authorize R1 to R5 plus R6 (or an equivalent) and R7, **or** explicitly accept AD-1 as outside the Level 2 trust assumption (RE-2), with R7 as the control |

## 6. What needs owner decisions

1. Confirm or revise the provisional High (AP-3, if confirmed by execution, may justify a higher rating).
2. Authorize an **adversarial confirmation test** of AP-1 to AP-5 in the isolated security-test stack (not authorized now).
3. Choose a remediation scope (R1 to R7), or a recorded risk acceptance for each attack path.
4. Decide whether R6 becomes a separate Authority design work item, with its own cryptographic review.
