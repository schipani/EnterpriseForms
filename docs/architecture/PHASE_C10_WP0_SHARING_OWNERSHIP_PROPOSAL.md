# Phase C10 WP-0 — Sharing, ownership and access completion

**Status (2026-11-25): WP-0 DESIGN APPROVED — C-193 (direction) and C-194 (C10-A…C10-D, final). Implementation NOT
authorized.** Every design decision of this document is approved as a contract (§8). No C10 code, migration, endpoint or
page may be written until the owner separately authorizes a C10 implementation package (C10 M1 first, §9). The legacy
lineage migration may be designed and its read-only dry-run report prepared (§5.1); executing it, or any invalidation or
repair, in a persistent environment needs separate authorization. (The file name keeps "PROPOSAL" for link stability.) Baseline: `main` `fbf18bc`. Normative sources:
CLAUDE.md §4B, §4C.5, §4H, §4J, §4M, §4N, §4T; `trust_authority/TRUST_AUTHORITY_ARCHITECTURE.md`;
`trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md`; `CLAUDE-DESIGN.MD` §17 (PG-022, PG-023, PG-024, PG-053, PG-065,
PG-066).

## 1. Baseline (verified on `fbf18bc`)

| Capability | State | Where |
|---|---|---|
| Create share (explicit field allow-list, 1…500 manifest items, no wildcard) | Exists | Authority `CommandPlanner.CreateShareAsync`; business `Sharing/CreateShare`; `POST /api/v1/form-records/{id}/shares` |
| Revoke share | Exists; owner or the share's grantor (identity binding); removes only that share's edges; **no downstream cascade** | `CommandPlanner.RevokeShareAsync` |
| Update share scope in place | **Not implemented** (`UpdateShareScope` exists in the contract and timeline catalogue; the planner answers `COMMAND_KIND_NOT_IMPLEMENTED`) | `AuthorizationContracts.cs`, `SecurityCommandTimelineCatalog.cs` |
| Share expiry | **None** in the Authority or contracts; the business projection CHECK already admits `Expired` | `SharingEntityConfigurations.cs` |
| Share revision / CAS | Organization-wide `ExpectedAuthorizationRevision` only (409); business `ShareRevision` fixed at 1 | `SecurityCommandService` |
| Delegation lineage | **None** (reference only: `trust_authority/authority-store.reference.sql` `share_delegation_edges`) | — |
| Manager delegation bound | Union of all the Manager's delegable items at creation; Managers cannot create Managers | `CreateShareAsync`; `CommandPlannerTests.ManagerCannotDelegateBeyondOwnScopeOrCreateManagers` |
| Team-derived access | Exists; membership changes are security commands | `authority.schema.zed` `team#participant` |
| Ownership transfer | Owner, or SystemAdministrator override (not to self or own team, G25); reason required; **no step-up**; no UI; no `RECORD_OWNERSHIP_TRANSFERRED` emitter | `TransferOwnershipAsync`; `TransferOwnershipHandler` |
| Step-up gate | Exists for membership, security-administrator, field-search-approval and owner-recovery commands (`FieldSearchApprovalGate.RequireStepUp`; unavailable in staging/production until PB-08, `PB08_STEP_UP_NOT_AVAILABLE`); **not applied** to share or ownership commands | `CommandPlanner.Membership.cs` etc. |
| Workload-authorized command precedent | Reprotection: no delegated user; authorized by an effective `PublishFormSecurityRevision` command; recorded under that designer's binding with workload name `reprotection-worker`; user + command shapes mutually exclusive | `ContentService.ResolveActorAsync` |
| Share / ownership history | Owner card reads business audit rows written after Effective; Security Auditor timeline is org-wide | `GET …/activity`; `GET /api/v1/security/audit/security-commands` |
| Document / entry items in shares | **None**; repeating-group fields refused (`FIELD_IN_REPEATING_GROUP`) | — |
| Pages PG-022, PG-023, PG-024, PG-053, PG-065, PG-066 | No routes; Access tab inside the record page only | `features/sharing` |

## 2. Invariants C10 must preserve

1. The Authority is the sole authority; business share/owner rows are untrusted projections (SEC-P01/P02).
2. Each security change is one Authority command under the organization lock: revision CAS, journal entry, outbox, archive
   receipt; compensating revisions only, never deletion.
3. A11: Pending/CommittedAwaitingProjection is never success; no page claims a revocation complete before Effective.
4. Checks pin a marker, use an exact SpiceDB snapshot and re-read the gate; caveated SpiceDB results are denied.
5. Share scope is an explicit snapshot of accepted-manifest items; later fields, entries, children and documents excluded.
6. Child access derives from the parent owner chain only.
7. Cursors bind the authorization revision; movement invalidates them.
8. §4T: histories are projections of canonical Authority events; no new independent event writer.
9. Contributor writes stay behind the default-off switch (G20).
10. Tenant isolation on every new resource reference; naming, UUIDv7, Merkle v1 and RowHmac v1 unchanged.
11. **No weakening of an existing policy:** §4J requires short-lived re-authentication for ownership transfer and sharing
    changes; §4B requires the administrator override to be audited and to confer no content access; G25 separation of
    duties; §4C.5 transfer never disables a challenge; D8-4 reassignment before a member who owns records is disabled.

## 3. Approved direction (C-193) and its refinement

### 3.1 Expiry — enforced at access time (approved)

- The Authority stores `share_expires_on` (UTC, optional) on the share resource, set by `CreateShare` or `UpdateShareScope`;
  it is part of the committed authorization graph (G03). The business copy is a display hint only.
- Every path that can confer share-derived access treats a share with `share_expires_on <= now` as absent before Allow,
  using the Authority's `TimeProvider`: `PermissionCheckService` checks, `VerifyRecordProjection` (field and document
  readability), discover/lookup enumeration, the verified-search catalogue snapshot, the indexed-search permission postings,
  document download and stream re-checks, export re-checks (Phase C12), and content-release paths. A share's **effective
  expiry** is the minimum along its lineage (§3.3).
- No SpiceDB caveats: invariant 4 stays.
- Background expiry processing exists only for projection convergence (§4, C10-A); access control never depends on it.
- Bounds (proposed defaults, tenant policy may narrow): minimum lifetime 1 hour, maximum 365 days, or no expiry.

### 3.2 In-place update with share revision (approved)

- `UpdateShareScope` implemented: replaces item set, role, document flags or expiry by an edge diff in one command.
- The Authority holds `share_revision` (+1 per change). Business: `PATCH /api/v1/form-records/{formRecordId}/shares/{recordShareId}`
  with `ETag` (`"{recordShareId}:{shareRevision}"`) and `If-Match`: `412` stale, `428` missing (PB-06). This resolves D9-3
  for the new share endpoints: PB-06 `If-Match`/412/428 is the platform convention for new endpoints; existing endpoints
  keep the body revision with 409 until separately migrated (recorded as a platform note, not a silent change).
- The organization revision CAS still applies; its conflict is returned as `409 STALE_AUTHORIZATION_REVISION` with no
  automatic retry under the same idempotency key (the Authority stores refusals under the key).
- Widening is bounded by §3.3; narrowing triggers §3.4.

### 3.3 Lineage and bounded delegation (approved; depth approved in C-194, §5)

- A share created or updated by a non-owner names exactly one `parentRecordShareId` held by the actor (directly or through
  an active team membership). Items, actions, document flags and expiry must be a subset of the parent's **current
  effective** scope (`effective_expiry(child) <= effective_expiry(parent)`). Owner-created shares have no parent.
- Lineage is stored in AuthorityDb on the share resource (`parent_record_share_id`, `delegation_depth`) and covered by the
  authorization-graph commitment; cross-tenant and cross-record parents are refused.
- Scope is capped by the named parent only, never the union of the actor's shares.

### 3.4 Descendant invalidation (approved)

In the same Authority command that revokes, expires, or narrows a share — or that removes the delegator's path to it
(team-membership removal, ownership transfer ending the grantor's owner rights, loss of the parent share) — the planner
walks descendants in `DesiredStateGraph` (already loaded under the organization lock) and moves every descendant whose
scope is no longer a subset of its parent's effective scope to `Invalidated` (reason recorded), removing its edges.
`Invalidated` and `Expired` are terminal; a database transition trigger forbids returning to `Active` (as
`trg_field_search_approval_transition` does for C-188). Independent grants are untouched: every share owns its edges.

### 3.4.1 Ownership transfer and path loss (owner decision, 2026-11-26)

The owner decided that a transfer must not revoke the former owner's grants wholesale: grants that do not depend on the former
owner's continuing owner rights survive; the former owner loses at once every authority derived solely from ownership;
delegated grants whose only valid path is lost are invalidated; independent paths and explicit revocations are honoured;
unknown or ambiguous lineage never yields an invented parent, an unsupported invalidation claim or access.

**Two kinds of grant in the Authority model.** A *durable grant* is a share registered by the record's owner (or an
owning-team member) at registration: depth 1, no parent, authorized once by its committed `CreateShare`. Its authority is the
committed decision, not the grantor's continuing ownership. A *delegated grant* (depth 2 or 3) names one committed parent
Manager share (§3.3) and depends on its grantor continuing to hold that parent: its validity is re-evaluated through lineage
at every access and by the §3.4 cascades.

**Reconciliation.** §3.4's trigger "ownership transfer ending the grantor's owner rights" has no share in scope: no share's
authorization path is its grantor's continuing ownership (durable grants have none; delegated grants depend on a Manager share,
which a transfer does not touch). §4B "separate ACL grants remain unless explicitly revoked" therefore holds without
exception, and a transfer invalidates no share. What ends is the former owner's *authority*: owner edges move in the transfer
command, and every share command authorizes a non-owner only through held delegated authority —

| Former owner after a transfer | Outcome |
|---|---|
| Create a share without a parent, update any share, transfer | Refused (no owner edge) |
| Revoke a durable grant they made as owner | Refused: a non-owner revokes only a delegated share they registered while still holding an Active Manager role on its committed parent, whose lineage is Active (tightened from "the grantor may revoke", which let a former owner act under former owner rights) |
| Shares they hold as a recipient | Kept (independent grant) |

The new owner manages every share, including the former owner's durable grants; revoking a Manager share cascades (§3.4).

**Path loss that does invalidate.** Team-membership removal (the delegator's only path to the parent was that team) and member
deactivation (a disabled member holds no usable authority) invalidate, in the same command, the shares the member registered
under a parent Manager share and their subtrees (`GrantorPathLost`). Deactivation keeps the member's own held shares and
durable grants; re-enabling restores the member's access but never an invalidated share (terminal).

**Legacy lineage.** A share with unresolved legacy lineage (`legacy_lineage_unresolved`) is neither reparented nor invalidated
by a transfer, a team removal or a deactivation, and only the current owner may revoke it: no committed parent exists that its
grantor could be shown to hold. Its classification stays with the §5.1 dry run; execution remains unauthorized.

Evidence: `OwnershipTransferShareTests` (11), `ShareMemberDeactivationTests` (4), `ShareGrantorPathLossTests` (2).

### 3.5 Read surfaces

| Endpoint | Purpose | Authorization |
|---|---|---|
| `GET …/shares/{recordShareId}` | Grantor, recipient, items, documents, role, expiry, revision, lineage, state | Owner; the grantor; a Manager whose effective scope covers the share |
| `GET …/shares/{recordShareId}/effective-scope` | What the recipient can do now and why (direct / team / owner / lineage source), naming no other principal | Same |
| `GET …/shares/history` | Record-scoped share history from the Authority journal, including pending commands | Owner or ManageAccess on the record |
| `GET …/ownership-history` | Record-scoped ownership history from the Authority journal | Owner; Security Auditor |
| `GET /api/v1/me/access` | PG-066 panels: owned, direct, team-derived, expiring soon | Current user; enumeration through the Authority discover path |

### 3.6 Documents in shares (approved: included in C10)

- Share items gain document entries `{fileAttachmentId, canViewDocument, canDownloadDocument, canReplaceDocument}`
  (`canReplace ⇒ canView`, `canDownload ⇒ canView`), registered as `document_item` Authority resources bound to the record
  and the File field, and verified against the accepted manifest (attachment leaves) at grant time.
- **Version visibility (approved, C-194 C10-D):** a document share covers the attachment's versions up to
  `shared_through_version_number` at grant time. A replacement made through this share extends it to the new version in
  the same command; versions added by anyone else stay invisible until an `UpdateShareScope`. A replacement never exposes
  a distinct attachment.
- **Replacement never widens access (approved, C-194):** `canReplaceDocument` authorizes exactly one operation — adding a new
  version to the one shared `fileAttachmentId`, subject to upload validation, scanning, encryption and the field's upload
  policy. It does not authorize reading other attachments, other versions of the same attachment (earlier unshared
  versions or later versions by others), new attachments, other fields or child records, record content outside the share's
  items, or deletion, restore or unlink. The share's extension to the version it created is part of the same Authority
  command and is bounded by the parent share's effective scope (§3.3); a delegated share can grant `canReplaceDocument`
  only if its parent holds it. Negative tests: replace-then-read another attachment, replace-then-read a later version by
  the owner, replace on an unshared attachment id, replace after parent narrowing, cross-record and cross-tenant ids.
- Downloads stay backend-mediated with per-document authorization, encryption and audit (§4C.4).
- Entry (repeating-group instance) shares stay refused in C10 (`FIELD_IN_REPEATING_GROUP`); their design is deferred.

### 3.7 Ownership transfer (approved, C-194: single step with mandatory step-up)

- Existing policies that apply, and how C10 meets them without weakening any:

| Policy | Source | C10 treatment |
|---|---|---|
| Owner may transfer; SystemAdministrator audited override with reason; no implicit content access; creator preserved; former owner loses owner-derived rights; independent grants survive | §4B | Unchanged Authority rules; PG-053 shows authoritative owner only |
| Administrator cannot transfer to self or a team they belong to | G25 (C-70) | Unchanged |
| Ownership transfer and sharing changes require short-lived re-authentication | §4J | **Not enforced today (finding G27); C10 adds `RequireStepUp` to human-initiated `TransferOwnership`, `CreateShare`, `UpdateShareScope` and `RevokeShare` (C-194 C10-C), revocation included** |
| Transfer never disables or bypasses a record challenge | §4C.5 | No challenge exists yet (Phase C15); C10 adds a contract test placeholder only when C15 lands |
| Owner change re-evaluates delegation chains | §4H | §3.4 |
| A member who owns records cannot be disabled until reassigned | D8-4 | PG-053 offers administrator reassignment of a member's records (one transfer command per record, each with reason and step-up) |
| Two-person approval | Not required by §4B for transfer (dual control is required for break-glass, §4C.5) | Not added; PG-053 shows the command states (Pending → Effective) as its Pending → Authorized → Applied → Completed states |

- Consequence of §4J step-up (C-194 C10-C): like retained-field approvals (D-H5), human-initiated ownership transfer and
  share creation, update and revocation fail closed in staging and production until PRD-1 provides validated provider
  step-up; Development/Test uses the existing development step-up substitute under its existing environment restrictions.
- **Human-initiated changes versus deterministic enforcement (C-194 C10-C).** Step-up binds a human decision. The following
  are not new human decisions and never wait for an interactive step-up session, because each only removes access as a
  consequence of a decision that was already authorized (and stepped up, where required) or of committed Authority state:
  `ExpireShare` (§4); descendant invalidation inside any command (§3.4); enforcement of an upstream scope reduction; removal
  of access consequent on a team-membership removal, a member deactivation or an ownership transfer that were themselves
  authorized. None of these can grant, widen, restore or transfer access (plan-shape guard, §4).
- **Emergency revocation:** no share-level emergency or incident-containment revocation exists today. The existing
  containment contracts — platform-operator emergency suspension, journal-recovery containment attestation, member
  deactivation and session revocation — keep their approved contracts unchanged; C10 adds no emergency bypass.
- The business emits `RECORD_OWNERSHIP_TRANSFERRED` as a projection keyed by `security_command_id` (no new writer).

### 3.8 Page placement (PG-024 reconciled)

`CLAUDE-DESIGN.MD` §17 defines PG-024 "Record security and additional access" with challenge and step-up content only
("additional access" refers to the owner-managed challenge). There is no conflict to split: **PG-024 belongs wholly to
Phase C15**, unchanged. The C10 access explanation already has catalogued homes and needs no new page id or contract
change:

| Page | C10 scope |
|---|---|
| PG-022 Record sharing manager | Route `/app/records/:formRecordId/sharing`; grants with expiry, lineage and documents; edit/revoke; the "effective permissions preview" section is the access explanation for the record |
| PG-023 Share details and effective permissions | "Derived vs direct permissions, upstream chain, expiration, latest revision" — the per-share explanation |
| PG-053 Ownership transfer workflow | Transfer, administrator override, reassignment of a member's records |
| PG-066 My access and shared-with-me | Own effective permission sources, expiry warnings |
| PG-024 | Phase C15 (record challenges); not built in C10 |
| PG-065 | Phase C11 (access requests) |

## 4. C10-A — security contract for the share-expiry convergence command (D10-2) — APPROVED (C-194)

Purpose: converge the projection, the business copy, cursors and history after a share's expiry. It is **not** the access
control: §3.1 denies an expired share at access time whether or not this command ever runs. The owner approved this
contract as a design (C-194), with the mandatory constraints: no public or internal endpoint for external callers; no
generic system actor or authorization bypass; authority derived solely from the authorized human command that set the
expiry; eligibility verified against committed Authority state and trusted server UTC time and revalidated under the
organization lock; it cannot expire an unexpired share, cannot grant, widen, restore or transfer access, and cannot reverse
`Expired`/`Invalidated`; deterministic idempotency, concurrency and replay protection; descendant invalidation preserves
independent grants; journal integrity, archive receipt, evidence-based authorization and recovery contracts unchanged; a
failure of background processing never leaves an expired share effective.

| Element | Contract |
|---|---|
| Command kind | `ExpireShare`, a new Authority-internal kind. The external security-command endpoint refuses it from any caller (`COMMAND_KIND_INTERNAL_ONLY`, 422); there is no HTTP, gRPC or business route that submits it |
| Workload identity | The Authority process itself: a hosted `ShareExpiryLoop` inside the Authority calls the planner in-process. No new network identity, credential, endpoint or business-worker involvement. The loop's only input is a `recordShareId` it read from AuthorityDb |
| Recorded actor (no generic system actor) | Derived authority, as for reprotection: the command is recorded under the identity binding of the human command that last set the share's expiry (`CreateShare` or `UpdateShareScope`, referenced by `expiry_source_security_command_id`), with workload name `share-expiry-worker`. A request carrying a delegated user is refused; a share without an expiry-setting human command is never eligible |
| Narrow capability | The plan may contain only: removal of the share's own edges and of its descendants' edges, and state transitions `Active → Expired` (the share) and `Active → Invalidated` (descendants, reason `UpstreamExpired`). A plan-shape guard rejects any plan with added edges, added resources, role or item changes, or other resource kinds, before `ApplyPlan` |
| Trusted time | The Authority's `TimeProvider` (UTC host clock, NTP-synchronized). No caller-supplied time is accepted. The same clock drives the access-time check, so convergence and enforcement agree |
| Eligibility (established independently) | Inside the organization lock, the planner re-reads the share from AuthorityDb: it must be `Active`, its committed `share_expires_on` must be `<= now`, and its `share_revision` must equal the revision the loop read. Anything else ⇒ no command is recorded (not eligible; nothing journalled) |
| Why it cannot expire an unexpired share | Expiry time and revision come only from committed Authority state under the lock; an extension by a human `UpdateShareScope` increments the revision, so a stale loop read becomes ineligible. A backwards clock step can only delay expiry; a forward clock step on the Authority host is outside the Level 2 threat model (the Authority host is trusted) and is detected (below) |
| Why it cannot grant, expand or restore access | Plan-shape guard; `Expired`/`Invalidated` are terminal (database transition trigger); re-granting needs a new human `CreateShare` with its own step-up |
| Idempotency | Key `share-expiry:{recordShareId}:{shareRevision}` (deterministic); a repeat replays the stored command; the internal path reads the current authorization revision under the lock, so it cannot meet a stale-revision refusal that the Authority would store under the key |
| Concurrency | Same organization lock and revision increment as every command; races with human commands serialize; the loser re-reads eligibility on the next pass |
| Journal attribution and evidence | Journal event `ShareExpired` {security_command_id, record_share_id, form_record_id, share_expires_on, evaluated_on, share_revision, expiry_source_security_command_id, workload `share-expiry-worker`, invalidated descendant ids}; normal outbox, archive receipt and ledger checkpoint path; its own allowlisted timeline kind; record-scoped histories show it as "Expired (scheduled at grant)" attributed to the grantor's decision |
| Replay protection | Deterministic key; terminal states; journal chain and archive receipts; after a restore from an older backup the access-time check still denies (the committed `share_expires_on` is in the past) and G14 replay re-applies archived `ShareExpired` events; a restored `Active` row never becomes effective again |
| Failure and recovery | Loop or command failure leaves enforcement intact (§3.1). The loop skips organizations whose gate is closed or quarantined and retries on the next pass (no tight retry). Metric and alert: shares expired but not converged for more than 2 passes; clock sanity: if `now` is earlier than the organization's latest journal timestamp, the loop stops and alerts (no command) |
| Descendant interaction | Descendants are invalidated in the same command (§3.4); each descendant's access-time effective expiry is already the lineage minimum, so a descendant is denied from the parent's expiry instant even before convergence |
| Generic-bypass prevention | Architecture test: only `ShareExpiryLoop` constructs `ExpireShare`; the planner's `ExpireShare` handler accepts no payload beyond `recordShareId`; no other command kind is reachable from the loop; `ExpireShare` absent from the published business OpenAPI and the external Authority contract's accepted kinds |

**Required negative and adversarial tests (C10 M1):**

1. An unexpired share is never expired: loop invoked with a share whose expiry is in the future ⇒ no command, no journal entry.
2. An extended share (human update between loop read and plan) ⇒ ineligible, no command.
3. The external command endpoint refuses `ExpireShare` from the business workload, an operator and a user assertion.
4. A request with a delegated user and `ExpireShare` ⇒ refused; `ExpireShare` without an expiry-source command ⇒ ineligible.
5. Plan-shape guard: an injected plan with an added edge, a role change or another resource kind ⇒ refused before apply.
6. `Expired → Active` and `Invalidated → Active` refused by the database trigger, including as the migration identity.
7. Idempotency: the same key twice ⇒ one command; a different revision ⇒ a different key.
8. Concurrency: expiry racing a human revoke, an update and a transfer ⇒ exactly one outcome, no double journal entry.
9. Access-time denial with the loop disabled: checks, projection, discover, verified search, indexed postings, download and
   open cursors all deny from the expiry instant.
10. Descendants: parent expiry denies descendants at once and invalidates them on convergence; an independent grant to the
    same recipient keeps its access.
11. Clock: a backwards step stops the loop with an alert; enforcement unchanged.
12. Gate closed / quarantined organization ⇒ skipped, retried later; no partial convergence.
13. Restore an AuthorityDb backup taken before convergence ⇒ the share stays denied; replay converges it.
14. Cross-tenant: a share id of another organization is never eligible; organization binding in the journal event.
15. Attribution: the recorded binding is the expiry-source command's binding; the timeline envelope contains only allowlisted
    fields.
16. Route inventory: no route of the Authority (public or internal contract) or of the business OpenAPI maps to
    `ExpireShare`; the published contracts list it in no accepted external kinds.
17. Derived authority: an expiry-source command that is Pending, CommittedAwaitingProjection, rejected or compensated confers
    no eligibility; only an Effective human `CreateShare`/`UpdateShareScope` whose committed payload set the expiry does.
18. No transfer or ownership effect: a plan touching owner edges, team membership, approvals or any resource other than the
    share and its descendants is refused by the plan-shape guard.
19. Evidence path: an `ExpireShare` command is reported Expired to readers only after it is Effective (A11), with its archive
    receipt; while it is pending, access-time enforcement still denies.
20. Step-up independence: `ExpireShare` and descendant invalidation succeed with no step-up session present, while every
    human share and ownership command without step-up is refused (`STEP_UP_REQUIRED`) — both on the same stack.

## 5. C10-B — maximum delegation depth — APPROVED (C-194)

- **Approved: maximum depth 3** (platform maximum; organizations may configure 1 or 2, never more). The database CHECK is
  defense in depth; the Authority planner is the enforcement. Every delegated share identifies exactly one covering parent
  share and stays bounded by that parent's current effective scope; invalidating one lineage never affects an independent
  grant. Depth 1 = a share granted by the owner (or owning-team member). A Manager holding a depth-1
  share may create depth-2 shares, including Manager shares. A Manager holding a depth-2 share may create depth-3 shares as
  Reader or Contributor only. A depth-3 share can never be Manager. Tenant policy may lower the maximum to 1 or 2, never
  raise it above 3 in C10.
- **Rationale:** it supports owner → lead → team lead → worker, the deepest pattern in the requirements (§4H "bounded
  delegation"), while keeping each cascade walk small (descendants ≤ fan-out², not unbounded) and the lineage explainable on
  PG-023. Depth 2 (today's effective behaviour) prevents any Manager from delegating management; deeper chains add review
  and explanation cost without a stated need.
- **Enforcement:** the planner computes `delegation_depth = parent.depth + 1` under the organization lock, refuses
  `DELEGATION_DEPTH_EXCEEDED` (422) above the maximum and `MANAGER_AT_MAXIMUM_DEPTH` for a Manager share at depth 3; the
  depth is stored, committed and covered by the graph commitment; a database CHECK bounds it to 1…3.
- **Consequences for existing shares:** owner-created shares become depth 1 with no parent. Shares created by a Manager
  have no recorded lineage today. A one-time Authority lineage migration attaches each to the grantor's **single** Manager
  share whose effective scope covers it (depth 2). If none or more than one covers it, the share is moved to `Invalidated`
  with reason `LINEAGE_UNRESOLVABLE` (fail closed). A dry-run report lists every such share before execution. Disposable
  stacks hold synthetic data only; running the migration on the persistent Development Authority is a separate ACT
  authorization.

### 5.1 Legacy lineage migration: design and read-only dry-run report (preparation approved, C-194; execution not)

**Inputs (read-only):** AuthorityDb share resources and their edges, the registering commands
(`RegisteredBySecurityCommandId`, actor binding), current ownership and team membership, all evaluated in one
`REPEATABLE READ, READ ONLY` transaction at a recorded authorization revision. Nothing is written; no command is submitted.

**Classification of every Active share:**

| Class | Rule | Proposed outcome (only on a later, separately authorized execution) |
|---|---|---|
| `OwnerGranted` | Registered by the record's owner (or owning-team member) at registration time | Depth 1, no parent |
| `UnambiguousParent` | Registered by a non-owner holding exactly one Active Manager share on the record whose current effective scope covers the share's items, role and documents | Parent = that share, depth 2 (or 3 if the parent is depth 2 and the share is not Manager) |
| `MultipleCandidateParents` | More than one covering Manager share | **Never resolved automatically**; listed for owner decision; invalidated only if the owner so decides |
| `NoCoveringParent` | Grantor holds no covering Manager share (lost, narrowed, never had) | `Invalidated`, reason `LINEAGE_UNRESOLVABLE` |
| `ExceedsDepth` | Assignment would place it beyond depth 3, or a Manager share at depth 3 | `Invalidated`, reason `DELEGATION_DEPTH_EXCEEDED` |
| `IndependentlyCovered` | (annotation) the recipient also holds the same access through another grant | Reported so that the effect of an invalidation is visible; the other grant is untouched |

**Report content:** counts per class and organization; per share an opaque share id, record id, recipient kind (user or
team) and pseudonymous recipient reference (keyed digest, never names or email addresses), grantor pseudonymous reference,
item and document counts (no field names, values or file names), candidate parent ids, the would-be outcome and whether
the recipient keeps equivalent access through an independent grant; the authorization revision read and the tool version.
Visible only to the Security Administrator and the owner of the run; stored as an evidence artifact, not in the business
database.

**Recovery and rollback considerations:** the execution (later) is one Authority command per organization under the lock,
journalled and archived; invalidation is terminal, so "rollback" means new human `CreateShare` commands (with step-up) for
shares the owner decides to restore — the report therefore records each invalidated share's exact scope so it can be
re-granted deliberately; the run refuses to execute if the authorization revision moved since the reviewed dry run; G14
replay applies the archived migration events after a restore. **Not authorized:** execution, invalidation or repair in any
persistent environment; an automatic choice among multiple candidate parents.

## 6. Technical defaults (C10-D) — APPROVED (C-194)

| Item | Proposed default |
|---|---|
| Expiry bounds | 1 hour minimum, 365 days maximum, or none; tenant policy may narrow |
| ETag format | `"{recordShareId}:{shareRevision}"`; `If-Match` required on PATCH (428 when missing, 412 when stale) |
| Cursor and snapshot expiry binding | Verified-search catalogue snapshots and discover continuations record the earliest effective expiry among the grants they used; a page fetched after that instant returns `409 CONTINUATION_INVALIDATED`; the convergence command's revision bump covers the rest |
| Document version visibility | §3.6 (`shared_through_version_number`; extension only through the sharing recipient's own replacement or an update) |
| Entry-instance shares | Deferred (refused, as today) |
| D9-3 | PB-06 `If-Match` for new endpoints from C10; existing endpoints unchanged until separately migrated |
| Search interaction | Expiry and lineage applied during enumeration in both engines; G06-reviewed code changes get their own test set |

## 7. Owner decisions (closed)

| Id | Decision | Outcome |
|---|---|---|
| C10-A | `ExpireShare` convergence-command contract (§4) | **Approved** (C-194), with the mandatory constraints of §4; design only |
| C10-B | Maximum delegation depth 3, enforcement, legacy lineage migration (§5) | **Approved** (C-194); migration design and read-only dry-run report only (§5.1) |
| C10-C | Literal §4J step-up on human-initiated `TransferOwnership`, `CreateShare`, `UpdateShareScope`, `RevokeShare`; deterministic enforcement exempt (§3.7) | **Approved** (C-194); finding G27 records the current gap |
| C10-D | Technical defaults (§6), document replacement bounds (§3.6), PG-024 reconciliation (§3.8), D9-3 through the C10 concurrency contract | **Approved** (C-194) |

## 8. D10 decision table (final)

| Id | Subject | Decision | Contract |
|---|---|---|---|
| D10-1 | Expiry enforcement point | Approved (C-193) | Authority access-time check on every decision, enumeration, search, download, cursor and content-release path; no SpiceDB caveats (§3.1) |
| D10-2 | Expiry convergence command | Approved (C-194, C10-A) | §4 |
| D10-3 | Share concurrency | Approved (C-194, C10-D) | Authority `share_revision`; ETag `"{recordShareId}:{shareRevision}"`; `If-Match` 412/428; resolves D9-3 for new endpoints (§3.2) |
| D10-4 | Organization-revision conflicts | Approved (C-194, C10-D) | 409, no automatic retry under the same idempotency key |
| D10-5 | Scope cap | Approved (C-193, C-194) | One named parent share's current effective scope (§3.3) |
| D10-6 | Delegation depth | Approved (C-194, C10-B) | Platform maximum 3; no Manager at depth 3; organizations may lower it (§5) |
| D10-7 | Descendant invalidation | Approved (C-193, C-194) | Terminal `Invalidated` for non-subset descendants on revoke, expiry, narrowing, path loss; independent grants kept (§3.4) |
| D10-8 | Ownership transfer | Approved (C-194) | Single step where existing policies permit, with mandatory step-up (C10-C) and G25 separation of duties (§3.7) |
| D10-9 | PG-024 | Approved (C-194) | Wholly Phase C15; access explanation in PG-022/PG-023/PG-066 (§3.8) |
| D10-10 | Cursor and snapshot expiry binding | Approved (C-194) | Earliest effective expiry bound into snapshots and continuations; `409 CONTINUATION_INVALIDATED` after it (§6) |
| D10-11 | History source | Approved (C-193, C-194) | Authority journal, record-scoped (§3.5) |
| D10-12 | Document-level shares | Approved (C-193, C-194) | §3.6 (version binding, flags, replacement never widens access); entry-instance shares deferred |

## 9. C10 M1 — implementation scope (awaiting implementation authorization)

**Scope:** the Authority side of the approved contract, with the minimum business-side change needed to keep existing
flows working, plus the SRC-1 page-id comment corrections (C-193: with the first C10 implementation package). No new pages
(M3), no new business read endpoints beyond adapters (M2), no persistent-environment migration, no activation.

| WP | Content | Acceptance criteria |
|---|---|---|
| M1-WP1 Schema | AuthorityDb migration: share resource `share_expires_on`, `share_revision`, `parent_record_share_id`, `delegation_depth` (CHECK 1…3), `expiry_source_security_command_id`; document items with `shared_through_version_number`; lifecycle states `Expired`/`Invalidated` with a terminal-transition trigger; organization policy `maximum_delegation_depth` (CHECK 1…3); descriptive multiword snake_case names; disposable databases only | Applies on empty and populated disposable databases; the trigger refuses `Expired`/`Invalidated` → `Active` for every role including the migration identity; naming tests pass |
| M1-WP2 Access-time expiry | Effective-expiry evaluation (lineage minimum, Authority `TimeProvider`) in `PermissionCheckService`, `VerifyRecordProjection`, discover/lookup, verified-search catalogue snapshots, indexed-search permission postings, download and stream re-checks; cursor/snapshot expiry binding | Every path denies from the expiry instant with convergence disabled (§4 test 9); open cursors refused after the earliest expiry; no SpiceDB caveat introduced |
| M1-WP3 Lineage and depth | `CreateShare`/`UpdateShareScope` take `parentRecordShareId` for non-owners; single-parent cap; depth computation; organization maximum | `DELEGATION_DEPTH_EXCEEDED`, `MANAGER_AT_MAXIMUM_DEPTH`; union-of-parents shape refused (SEC-08 regression); cross-tenant and cross-record parents refused |
| M1-WP4 UpdateShareScope and share revision | Planner with edge diff; `share_revision` CAS; organization-revision CAS unchanged | Stale share revision refused; organization conflict 409; idempotent replay; no partial edge diff |
| M1-WP5 Descendant invalidation | Cascade inside revoke, expiry, narrowing, team removal, member deactivation and ownership transfer | §3.4 cases; independent grants preserved; terminal states; journal lists invalidated ids |
| M1-WP6 ExpireShare | Internal kind, `ShareExpiryLoop`, plan-shape guard, derived attribution, deterministic key, clock sanity stop, metrics | §4 tests 1–20 |
| M1-WP7 Step-up (G27) | `RequireStepUp` on human-initiated `TransferOwnership`, `CreateShare`, `UpdateShareScope`, `RevokeShare`; development substitute only in Development/Testing; staging/production fail closed (`PB08_STEP_UP_NOT_AVAILABLE`) | Each command refused without step-up; deterministic enforcement unaffected (§4 test 20); a production configuration refuses the substitute at startup |
| M1-WP8 Document items | `document_item` resources; view/download/replace flags; version binding; replacement-through-share extension | §3.6 negative tests |
| M1-WP9 Journal and timeline | `ShareExpired`, `ShareInvalidated`, `ShareScopeUpdated` journal events and allowlisted timeline kinds; record-scoped history query (Authority side) | Envelopes allowlisted; pending commands visible as pending; archive receipts |
| M1-WP10 Legacy lineage dry-run tool | Read-only report of §5.1 (operator CLI inside the Authority deployment, like the existing recovery commands) | Read-only on disposable stacks with seeded legacy shares; covers every class; redaction verified; no write path exists |
| M1-WP11 Business adapters | Minimal: pass `parentRecordShareId`, expiry and step-up evidence through the existing share and transfer handlers; map the new refusals; `RECORD_OWNERSHIP_TRANSFERRED` projection keyed by `security_command_id`; OpenAPI and clients regenerated | Existing flows pass with step-up in Development/Testing; contract tests for the new refusals |
| M1-WP12 SRC-1 | Page-id comment corrections: `UploadCenterPage.tsx` (PG-010), `FormsPage.tsx` (PG-012), `NewRecordPage.tsx` (PG-014), `DesignerPage.tsx` and `DesignerCanvas.tsx` (PG-029), `ChildRecordList.tsx` (PG-018), `RecordAccessPanel.tsx` (PG-022, the sharing manager embedded in PG-016) | Comment-only diff |

**Verification plan:** Authority unit tests (planner, plan-shape guard, cascade, lineage, depth, expiry, step-up); contract
tests (business adapters, refusals, OpenAPI drift); architecture tests (only `ShareExpiryLoop` constructs `ExpireShare`; no
route maps to it); integrity vectors unchanged; full .NET CI-equivalent set; then focused Stage B classes on a fresh isolated
stack under C-131 §1 (a new `ShareLifecycleTests` class plus `SharingTests`, `ContributorWriteTests` and the functional
subset of the search classes); no hosted Stage B without a release authorization. Register rows §11 #23–#27 and #56–#62
updated with exact evidence. **Not in M1:** the PG-022/PG-023/PG-053/PG-066 pages (M3), the read endpoints of §3.5 (M2),
persistent migration, staging.

| Later milestone | Scope |
|---|---|
| C10 M2 — Business API | PATCH with `If-Match`, share details, effective scope, histories, `/me/access`, reassignment |
| C10 M3 — UI | PG-022, PG-023, PG-053, PG-066; axe; manual accessibility checklist |
| C10 M4 — Acceptance | Focused Stage B, Playwright, register rows |

Test obligations across C10: CLAUDE.md §11 #23–#27, #56–#62; SEC-08, SEC-11; REL-11, REL-12, REL-33; §4 tests 1–20; §3.6
replacement negatives; cascade on revoke, expiry, narrowing, team removal and transfer; 412/428/409; cursor invalidation at
expiry; cross-tenant parents, recipients, documents and transfer targets; administrator transfer without content access;
A11; journal-derived history including pending commands.
