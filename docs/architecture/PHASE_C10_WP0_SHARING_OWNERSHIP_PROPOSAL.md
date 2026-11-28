# Phase C10 WP-0 — Sharing, ownership and access completion

**Status update (2026-11-28, C-202):** C10 functional implementation complete in Development/Test; code integrated into `main` at `5cc4efe3` (PR #88, squash of the authorized head `5c40a489`). Formal Development/Test acceptance is pending; production readiness is not approved. Outstanding obligations: §10.9 and §10.12. The status below is the original design-approval record and is kept unchanged.

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

### 3.6.1 Replacement through a share (owner decision 2026-11-27, C-196)

The extension "bounded by the parent share's effective scope" above cannot hold for a delegated share: its parent covers only the
earlier version, and extending the parent would show the new version to the upstream manager. Resolution: `canReplaceDocument` may be
set only on a share the record owner grants directly (depth 1, no parent, resolved lineage) with the Contributor or Manager role; a
delegated share never carries it, and a request for it is rejected (`DOCUMENT_REPLACE_REQUIRES_OWNER_GRANT`), never silently
reduced. Viewing and downloading still delegate. A replacement is exactly the next verified version of the one shared attachment, with
no other content change, behind the contributor-write switch, and only while the accepted version lies within the authorizing share's
boundary (a later version by the owner can neither be seen nor superseded through the share; independent review D-2, §10.4); only the replacing share's
boundary advances, in the content commit that accepts the version; no other share is extended; if more than one of the actor's shares
could authorize it, it is refused (`DOCUMENT_REPLACE_SHARE_AMBIGUOUS`).
- **Version and content rule on every content path (owner decisions 1 and 2, 2026-11-27, §10.5).** An attachment that stays in the
  record is either the same version with unchanged verified content or exactly the next version; a lower number, a skipped number or
  new content under the accepted number is refused, at Prepare and at Commit, owner path included (protection migration exempt).
  The business numbers a new version from the attachment's accepted version. A boundary extended by a replacement advances the share
  revision (its ETag) and is journalled as `ShareDocumentBoundaryExtended`.
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
| M1-WP9 Journal and timeline | `ShareExpired`, `ShareInvalidated`, `ShareScopeUpdated` journal events (and `ShareDocumentBoundaryExtended`, §10.5) and allowlisted timeline kinds; record-scoped history query (Authority side) | Envelopes allowlisted; pending commands visible as pending; archive receipts |
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

## 10. C10 implementation record (Development/Test, 2026-11-27; not formal acceptance)

Signed candidate `coord/c10-m1-candidate-r2` at `2b0887c1`: CI-equivalent .NET gate 3,635 passed, 0 failed, 223 authorized skips, 0
rejected; strict specification validation; frontend lint, typecheck, build and 659 unit tests. Per-package heads and evidence are in
`docs/governance/WORK_PACKAGE_REGISTER.md` (checkpoints 1–4). Nothing has been independently reviewed.

### 10.1 Status against Section 9

| WP | Status | Disposition |
| --- | --- | --- |
| WP1 schema | Done | Disposable databases only |
| WP2 access-time expiry | Done | Checks, projection, discover, catalogue and indexed search (owner decision D5: expired shares excluded from share-only candidates; snapshot ends at the earliest live expiry) |
| WP3 lineage and depth | Done | |
| WP4 update and share revision | Done | |
| WP5 cascades | Done | Revoke, expiry, narrowing (fields and documents), team removal, deactivation (D2, terminal), transfer (§3.4.1: invalidates nothing) |
| WP6 ExpireShare | Done | §4 tests 8, 9, 13, 16 need real infrastructure |
| WP7 step-up | Done | Staging and production refuse the development substitute |
| WP8-A documents | Done | Version boundary from Authority-verified attachment rows (Option A); attachments accepted before it are refused until their record's next accepted commit (production-readiness obligation; backfill deferred) |
| WP8-B replacement through a share | Done (Development/Test) | §3.6.1, §10.2; under the existing CARV; independent review must confirm no increment is needed |
| WP9 journal and history | Done | Every committed expiry change listed (D6) |
| WP10 lineage dry run | Done | Execution remains unauthorized |
| WP11 business adapters | Done | M2: PATCH, details, effective scope, histories, `/me/access`, member reassignment |
| WP12 SRC-1 | Done | |

M3 pages: PG-022 (edit, history), PG-023, PG-053 (transfer, member reassignment), PG-066.

### 10.2 WP8-B (R4) assessment

- **How owners replace a document.** `UploadSessionHandlers.CommitAsync` (business) stages a complete new candidate: every current row
  cloned unchanged, the replaced attachment's row omitted, and the processed version row added — same `file_attachment_id` (the
  manifest item id), next `version_number`, fresh storage object and envelope from the scanning/encryption worker. Each content change
  operation stages its own immutable rows; earlier versions stay as earlier operations' rows. The Authority accepts it in
  `ContentService.PrepareAsync` through `ClassifyMutations` because the caller is the record owner.
- **Why a share cannot reuse it today.** A non-owner goes through `ContentDeltaRules.ClassifyShareScoped` (Prepare only; the attachment
  rule is `ShareUpdatable: false` and `version_number` is a key column) and `ContentService.AuthorizeMutations` (Prepare and Commit;
  only field value and selection updates). The business commit also requires a complete (owner) read.
- **Is CARV 4 mandatory?** Not by the CARV contract itself (`docs/security/E3_M2B_IMPLEMENTATION_CONTRACT.md` §3/§19: the version is
  incremented only when "Prepare starts enforcing a rule that Commit does not fully re-derive"; "every check Commit can re-derive from
  staged bytes is re-run there"). It is mandatory only if the replacement rule includes a Prepare-only check.
- **Smallest secure approach without CARV 4** (every new check re-derived at Commit, so the existing fence is unchanged):
  1. A share editor's candidate may contain exactly one Attachment Update, every other item Unchanged (Commit derives mutation kinds
     from the candidate and accepted leaves).
  2. Authority state at Commit: the actor holds a live share whose binding allows replace for that attachment (same rule as
     `ShareDocumentReadability`, plus `CanReplaceDocument`), contributor writes enabled (G20 switch, default off).
  3. Version: candidate `version_number` = the accepted leaf's verified `AttachmentVersionNumber` + 1 (Option A evidence).
  4. All other attachment checks are the owner path's own (identity binding, registered schema, envelope rules, data-key ledger);
     the version row comes from the same scanning/encryption worker.
  5. In the same Commit transaction as the content head: extend that share's binding `SharedThroughVersionNumber` to the new version.
  An older build that commits an operation prepared this way refuses it (its `AuthorizeMutations` rejects non-owner attachment
  mutations): fail closed across a rolling deployment.
- **Contract conflict to resolve first.** §3.6 says the extension "is bounded by the parent share's effective scope (§3.3)". For a
  delegated share the parent's boundary is the earlier version, so the child's extension would exceed it, and the WP5 narrowing
  rule (`ShareDocuments.Covers`) would later invalidate the child. Extending the parent too would show the new version to the
  upstream manager, contradicting "versions added by anyone else stay invisible". Recommended reading: replacement through a share
  only on owner-granted (depth 1) shares; a delegated share is refused `canReplaceDocument`.
- **Implemented (owner decision 2026-11-27, §3.6.1):** the share-scoped classification (`ContentDeltaRules.ClassifyShareScoped`) admits
  the replaceable attachment through one narrow branch (rule table unchanged); `AuthorizeMutations` (Prepare and Commit) allows it
  as the only change of the operation; `ShareDocumentReplacement.RequireAtCommit` re-derives the next verified version, record,
  organization and clean scan from the staged bytes and accepted leaves; `ShareDocumentReadability.ReplaceableAsync` derives the
  authorizing share from committed state at Prepare and again at Commit; the boundary advances in the commit transaction.
- **If CARV 4 were nevertheless chosen** (e.g. a Prepare-only envelope-freshness check is required): `CurrentVersion = 4`, a fourth
  floor migration with its exact definition added to the recognized set, and the existing guard tests; owner authorization required.

### 10.3 Remaining obligations

- **Independent review:** performed 2026-11-27 (§10.4); re-review of the corrections recorded there. Not an owner acceptance.
- **PR consolidation (owner authorization):** merge #79 then #80; merge #77 (with #82 folded in) and #78; move the register from #81
  into the C10 PR and close #81 (retire the Codex policy documents); close #83–#85 as superseded (heads are ancestors of the
  candidate); one signed merge of `main` into the candidate taking main's final versions of the copied #77–#80 files; one C10 PR.
- **Focused Stage B (release authorization, C-131 §1, fresh isolated stack):** a new share-lifecycle class (expiry, cascades,
  deactivation/reactivation, transfer, documents and version boundaries, D5 search), plus `SharingTests`, `ContributorWriteTests`
  and the functional search subset; §4 tests 8, 9, 13, 16; download and stream re-checks.
- **Accessibility:** Playwright axe for PG-022/023/053/066 and the reassignment page (isolated stack); manual NVDA and VoiceOver.
- **Production readiness:** attachment version evidence for attachments accepted before WP8 (backfill deferred, fail closed until
  then); `ExpireShare` convergence outside Development/Testing; real step-up (PRD-1); Azure staging evidence; G06 closure.

### 10.4 Independent review (2026-11-27) and corrections

Three reviewer agents with fresh context and read-only access (Claude Code subagents, not a human or external review) reviewed
`fbf18bc7..695ee7b3` against this document, C-193, C-194 and C-196: (1) the content path for documents (WP8-A, WP8-B, version
evidence, CARV); (2) the Authority share and ownership commands and journal; (3) D5 search, the M2 reads, the M3 pages and upload
admission. **CARV:** reviewers (1) and (2) independently concluded that WP8-B needs no content acceptance rule version increment
(every new Prepare check is re-derived at Commit; an older build refuses such an operation at Commit).

**Corrected (Development/Test; tests added):**

| Finding | Correction |
| --- | --- |
| Expired share still authorizes field writes (high) | `ShareDocumentReadability.ExpiredSharesAsync`: shares expired at the instant (own or ancestor expiry) confer no edit scope at Prepare and again at Commit (`DesiredStateGraph.EditableFieldItemsViaShares`). Lineage status Invalid is not excluded (legacy edge-only shares); normal commands remove such edges |
| Replacement past the share's boundary (D-2) | `ReplaceableAsync` requires the accepted version to lie within the binding's boundary, at Prepare, Commit and in the advisory list |
| Attachment version number not checked on the owner path (D-1, partial) | `AttachmentVersionRules`: a candidate attachment version number is positive and never below the accepted one, on every non-migration path, at Prepare and re-derived at Commit. See residual below |
| Snapshot and cursor expiry bound ignored the lineage minimum (F-1) | `ShareExpiryOverlay` propagates every expiry to descendants (lineage minimum), one set-based read per level (`FindShareMetadataByParentsAsync`, re-review N-1); `NextExpiryFor` and the expired set use it |
| Record-list continuation decided by a client field (F-8) | A page with `Offset > 0` is a continuation |
| Share create and revoke from the Access tab sent no step-up (F-2) | The create dialog and the revoke confirmation ask for the development step-up and send it |
| Organization Owner could list another member's owned records (F-3) | `OwnedRecordsService` admits only Security Administrators (the transfer override's role) |
| Ownership history readable by Managers; Security Auditor had none (F-4) | `view=ownership`: owner or Security Auditor, owner changes only; share history: owner or ManageAccess, a non-owner sees no owner changes |
| History served under integrity quarantine (F-5) | Refused while the organization is quarantined. Pending commands are still listed with their own state (§3.5), but the reader's own authority comes only from grants whose commands are Effective (re-review residual) |
| One failing share stopped the expiry pass (F5) | Per-share failure is logged and counted as unconverged; the alert fires after two passes |
| Partial readers saw the full version history (D-3) | A share recipient's document detail lists only the version released to them |
| Unreachable historic-download branch for recipients (D-4) | Removed: recipients download the current released version only (historic verification needs full read at the Authority) |
| Share-replacement upload declared another field's policy (F-7) | The declared field must be the replaced attachment's own field (same answer as an unknown attachment otherwise) |
| `RequireAtCommit` dead field check (O-1/F7) | Removed; the field and other identity columns are re-derived at Commit by `ContentItemIdentityRules.BindAtCommitAsync` (documented) |

**Re-review of the corrections (same three reviewers, 2026-11-27).** Confirmed resolved: F1, F2/D-2, D-3, D-4, O-1/F7, F-1, F-2, F-3,
F-4, F-7, F-8, F5, §4 test 16 (static). CARV re-confirmed: no increment. Corrected after the re-review: the replacement version rule had
been relaxed to "above the accepted version", which contradicted C-196 and let a compromised business push a boundary to the maximum
number; it is restored to exactly the next version. History readers are authorized only through Effective grants. The overlay's
descendant read is set-based (N-1). F1 comment wording corrected.

**Not corrected — owner decision required** (decided 2026-11-27; dispositions in §10.5):

- **Replacement after an unaccepted upload (functional, fail closed; found during correction):** the business assigns
  `max(every version row) + 1`, so after any upload that was never accepted the next number skips one and replacement through a share
  is refused permanently for that attachment. Options: the business assigns the accepted version + 1, or a bounded skip is allowed
  (the owner rule says "exactly the next verified version").
- **N-2 (low):** history truncation is applied before the view filter, so an ownership view can miss an older change behind many
  newer share commands (with `truncated = true`).
- **Read audit (F-3 residual, N-3; low–medium):** owned-record listing and Security Auditor ownership-history reads are not journaled
  (CLAUDE.md §4D); `AuditAccessService` is the precedent.

- **D-1 residual (high, Level 2 hostile-business writes):** new attachment content that keeps the accepted version number. Every carried
  row is re-staged with a new operation binding, so Commit cannot tell an unchanged attachment from new bytes under the same number
  without accepted content evidence, which the Authority does not hold; a Prepare-only comparison is exactly what the E3 contract
  says requires a CARV increment (not authorized). Options: (a) extend Option A with Authority-verified content evidence (e.g. the
  attachment's ciphertext digest) on manifest and candidate leaves, captured at Prepare and re-derived at Commit (a Development/Test
  migration; no CARV increment); (b) resend the current items with Commit and re-prove them against the accepted leaves
  (`E3_M2B_IMPLEMENTATION_CONTRACT.md` §3.2, OD-M2-24; no migration, no CARV increment, both API contracts change; re-review);
  (c) CARV 4 with a Prepare-only rule (not authorized). Until decided, a compromised business can
  give a recipient the owner's new bytes under a number the share already covers.
- **D-5:** the boundary advance in the content commit is recorded in no security journal entry, and the share ETag does not change.
  A restore or replay would drop the extension (fail closed: the recipient loses the new version). Decide whether to add it to the
  content checkpoint body or a journal entry, and the replay rule.
- **F-6 (low):** share update and delegated share creation resolve field keys against all committed values before the Authority checks
  scope, so different errors reveal whether an out-of-scope field has a value or is in a repeating section. A complete correction
  resolves keys against the caller's own verified readable scope first (restructures both handlers).
- **F4 (low–medium):** `RECORD_OWNERSHIP_TRANSFERRED` (business projection) is written only when the transfer returns Effective; there
  is no reconciliation for a transfer that becomes Effective later. The Authority journal and ownership history are complete.
- **F6 (low, display):** the business share list shows descendants invalidated by a cascade as Active; access is denied by the Authority.
- **Pre-existing (outside the range):** `TransferOwnership` on a child record lets the parent owner add a direct owner edge, giving two
  effective owners (§4B, §4S, §3.4.1 conflict).

**Accepted limitations recorded by the review:** partial readers see only the current head (historic verification needs full read);
`scan_verdict` is business-supplied and signed on every path; mixed-build rollout loses attachment version evidence for that commit
(fail closed until the record's next commit, D-6); step-up is the synthetic development substitute (PRD-1).

**Missing verification evidence (Stage B or staging):** real PostgreSQL proof of the terminal-state and D6 triggers and of commit
rollback after the boundary change; business upload admission and document handlers (no business harness); the record-list
continuation rule (F-8); §4 tests 8, 9 and 13. §4 test 16 and the generic-bypass rule are now asserted statically
(`ExpireShareInventoryTests`: no published contract or route names `ExpireShare`; only `ShareExpiryConvergenceService` records it
and only `ShareExpiryLoop` drives it).

### 10.5 Final bounded corrections (owner decisions of 2026-11-27; Development/Test, not acceptance)

Owner decisions 1–5 on §10.4. Implemented in `3d1406da` and `1de30596` on `coord/c10-m1-candidate-r2`; re-examined by the same three
reviewer agents (fresh context, read-only; Claude Code subagents, not a human or external review), limited to the corrected areas.
No CARV increment, content-acceptance floor migration or deployment-fencing change.

| Finding | Disposition |
| --- | --- |
| D-1 (decision 1) | **Corrected.** Content identity = SHA-256 over the attachment row's own Content-class canonical column frames (name length, name, framed value: the bytes the row signature covers; no key, signature or exchanged format). Prepare compares each attachment with the verified current snapshot; Commit recomputes the candidate values from the verified staged rows and takes the accepted ones from the accepted leaf (`record_manifest_leaves.attachment_content_fingerprint`) or, for a head accepted before the evidence existed, from what this operation's Prepare verified (`content_candidate_leaves.accepted_attachment_*`). Same version: content must be equal; lower or skipped number: refused; no evidence or disagreeing evidence: refused. Evidence is written with the accepted commit. Migration `AuthorityC10AttachmentContentEvidence` (three nullable columns, two checks). Reviewers: resolved for every C10 build on owner and share paths; no CARV increment (Commit re-derives or refuses) |
| Version gaps (decision 2) | **Corrected.** A replacement is numbered from the attachment's accepted version (`file_attachments.current_version_number`); commit refuses a stale number before anything is staged (`DOCUMENT_VERSION_SUPERSEDED`) and corrects the local projection from the verified record. Processed copies may share a number (partial unique index, `BusinessC10ProcessedVersionNumbers`); object keys name the upload session, so uploads never share an object. Exactly one version is accepted per number (exactly-next, content rule, item identity rules, head compare-and-set). Version history and historic download use the copy whose operation the Authority's accepted history names (newest revision first, at most 8 revisions; otherwise no copy, fail closed). No historical version renumbered |
| D-5 (decision 3) | **Corrected.** In the content commit transaction: the binding boundary, the share revision (checked increment; the share ETag) and one `ShareDocumentBoundaryExtended` entry on the hash-chained, archived audit stream (identifiers, revisions and version numbers; no document id or scope; catalogued for the Security Auditor timeline). Aborted commits and replays change nothing; a stale `If-Match` is refused by the existing `STALE_SHARE_REVISION` rule. **Replay rule:** a recovery that re-applies accepted content heads must re-apply each archived `ShareDocumentBoundaryExtended` (binding id, shared-through version, share revision) after its `ContentHeadCommitted`; until then the recipient lacks the new version (fail closed). Recorded for G14; not implemented as a recovery step |
| F-6 (decision 4) | **Corrected.** A caller without full read resolves field keys only against the value items it can read itself (one Authority-verified read, the same whatever keys are asked), before any field is inspected: unknown, empty, repeating and unreadable fields all give `FIELD_NOT_IN_SHAREABLE_SCOPE` (403); rows outside that set are not sent. The Authority still caps a delegated scope by its named parent share. (A first version used the parent share's effective scope, which the Authority does not serve to that share's own recipient; found by the re-review and replaced.) |
| F4 | **Corrected.** The transfer page replays a pending transfer under the same idempotency key (bounded); one still pending keeps its key, and "Check again" replays the same command. The handler records `RECORD_OWNERSHIP_TRANSFERRED` once, when Effective. A transfer abandoned while pending (page closed) still has no business projection: the Authority journal and ownership history remain complete (accepted limitation) |
| F6 | **Corrected (display).** The share list shows the Authority's lifecycle state of each locally Active share it can read there; otherwise, or on any Authority error, the local status. One Authority read per locally Active share (cost noted) |
| N-2 | **Corrected.** The ownership view reads the commands of the record's owner edges; both views filter before truncating, and a capped share view stops at the oldest command read |
| Read audit (N-3) | **Corrected as required by CLAUDE.md §4D** (read operations captured; access to audit audited): `RECORD_OWNERSHIP_HISTORY_VIEWED` and `MEMBER_OWNED_RECORDS_VIEWED` (identifiers and counts), written after the Authority allowed the read; a failed audit write fails the read. The AC-8 audit-read contract defers PG-053 and is unchanged |
| Child-record transfer (pre-existing) | **Corrected afterwards (owner approval 2026-11-27, §10.6).** Reported here first:  It conflicts with mandatory requirements: §2 invariant 6, §3.4.1 and §3.7 ("former owner loses owner-derived rights"), CLAUDE.md §4B (one owner at every committed state) and §4S (child ownership derives from the parent). It is exploitable for insider retention: the parent owner transfers the child to themself (or to their team; the self-transfer refusal applies to administrators only), then transfers the parent, and keeps full owner rights on the child. An administrator's transfer of a child does not remove the parent owner. **Minimum correction:** `TransferOwnership` refuses a record that has a parent (owner and administrator paths) with a clear code (transfer the parent, or unlink first); the business maps it and the UI hides transfer for child records; tests for both paths and for a parent transfer moving the child. Optional hardening: ignore direct owner edges on records with a parent edge, and check Development/Test data for existing ones |

**Existing-record compatibility limitations.**
- A head accepted before the content evidence works through the Prepare-observed fallback; the first such commit writes evidence onto every attachment leaf.
- An operation prepared by a pre-C10 Authority build that carries a legacy-head attachment is refused at Commit (422).
- A pre-C10 Authority build serving writes at the same CARV can still accept new bytes under an accepted number and erase evidence (no fencing, by decision). Before D-1 is claimed in a shared environment, the deployment inventory must show that no pre-C10 Authority build serves content writes (deferred obligation).
- A stale local `current_version_number` (crash between the Authority commit and the projection update) makes the next upload superseded; the user uploads again. The version is bound into the encryption AAD, so it cannot be renumbered.
- Production backfill of evidence remains deferred.

**Missing evidence (Stage B or staging):**
- real PostgreSQL proof of the new checks, the partial index, NULL behaviour of older EF models and commit rollback after the boundary change;
- business handler tests of concurrent uploads and of the stale-number refusal against real stores;
- an end-to-end test of delegated share creation and update against the real Authority (the F-6 regression would have been caught there);
- an end-to-end test that an ETag read before an extension is refused after it.

### 10.6 Child-record ownership correction (owner approval 2026-11-27; Development/Test, not acceptance)

**Correction (`68ef7fb6`, `2c93c8d8`).**
- `TransferOwnership` refuses a record that has a parent, on the owner and the administrator paths, before any edge changes (`CHILD_RECORD_OWNERSHIP_FOLLOWS_PARENT`, 422); a caller who is neither owner nor administrator still gets 403 first.
- The reported sequence (transfer the child to oneself or one's team, then the parent) fails at its first step; transferring the parent still moves the child (`parent->effective_owner`); shares on the child are untouched.
- The business passes the refusal through as for every Authority refusal (status and code); reassignment reports it per record.
- The record projection reports no transfer permission while the record is linked to a parent (the current business link, so an unlinked record regains it; reviewer finding D-1 on `68ef7fb6`, corrected in `2c93c8d8`), so the UI hides the action.
- No other path gives a child a direct owner edge: child registration adds only a `parent` edge, `UnlinkChildRecord` removes the parent edge first, and no link or reparent command exists.
- Tests: `ChildRecordOwnershipTransferTests` (3); Stage B `C10ShareLifecycleTests.AChildRecordIsNeverTransferredOnItsOwnAndFollowsItsParent` (prepared, not run).

**Limitation: direct owner edges created on children before the correction.** A child with both a `parent` edge and a pre-correction direct `owner` edge still gives that owner full effective ownership (`effective_owner = owner + parent->effective_owner`), so the reported retention survives for such records. The correction also removes the earlier, accidental remedy (a further transfer of the child), so the only remaining route is the parent owner unlinking the child (which leaves two direct owners until the now top-level record is transferred). A member who holds such an edge cannot be disabled (`OWNS_RECORDS`) until it is removed. No ownership data was changed. **Minimum remediation (owner decision required):**
1. A read-only inventory of records with an active `parent` edge and an active `owner` edge (record id, owner subject, creating command; identifiers only), reusing the legacy-lineage dry-run pattern, run on Development/Test and any shared environment and recorded.
2. If it finds none and no production data exists: record the limitation as closed by evidence.
3. If any exist: an explicit, journalled, audited command with reason and step-up, run by the parent's effective owner or the administrator override, that removes a child's direct owner edges to restore inheritance. Not an implicit rule that ignores such edges (that silently changes effective rights and cannot be expressed cleanly in the SpiceDB schema).

### 10.7 Focused Stage B readiness (prepared, not executed)

**New tests (compile and are discovered; group coverage `stage_b_groups.py plan`: 456 tests in 12 groups, each class once):**

| Class / spec | Group | Covers |
| --- | --- | --- |
| `C10ShareLifecycleTests` (9) | records-lifecycle | update under revision and stale 412; narrowing; history; revocation; access-time expiry on projection, record list, My access, effective scope and download before convergence (§4 test 9); inherited expiry with an independent grant and convergence of descendants (test 10; the set-based descendant read on real PostgreSQL); expiry racing a revoke, an update and a transfer (test 8); member deactivation and reactivation; child-record ownership; delegated shares against the real Authority (F-6 regression); document boundary, replacement, D-5 share revision and journal entry, stale ETag, owner version outside the boundary; live download re-check after revocation; business upload admission; numbering from the accepted version and the stale-commit refusal (decision 2) |
| `C10DatabaseVerificationTests` (2) | records-lifecycle | C10 evidence columns nullable, both checks installed and enforced; business partial index definition; terminal-state and D6 triggers refusing even the AuthorityDb administrator; a commit that crashes inside the transaction after the boundary changed rolls back the boundary, share revision, head and journal together, and the retry succeeds |
| `C10ShareExpiryRestoreTests` (1) | projection-integrity-restore (own stack) | §4 test 13: a restore of the share's pre-convergence state leaves it denied and convergence runs again |
| `e2e/c10-sharing-pages.spec.ts` (6) | browser | axe (WCAG 2.0/2.1/2.2 A and AA) and no horizontal scroll on PG-022, PG-023, PG-053 transfer, PG-053 reassignment and PG-066 at 320, 768 and 1440 px and at 640 and 320 px (1280 px at 200% and 400%) |

**Existing tests prepared:** G27 made step-up mandatory for share creation, revocation and ownership transfer, so 82 existing Stage B and security-test calls (51 share creations, 7 transfers and 21 revocations through the API, plus 3 request bodies and direct Authority commands) now send the development step-up (no assertion changed; the commit message of `2c93c8d8` says 81). Without it they would fail at the Authority (`STEP_UP_REQUIRED`).

**Focused run (needs your release-testing authorization; fresh security-test stack):**
1. Groups `records-lifecycle` (new classes, `SharingTests`, `ContributorWriteTests`, `Ix6RecordListStackTests`, lifecycle and restore classes) and `projection-integrity-restore` (its own stack).
2. Classes `DocumentTests`, `DocumentEncryptionAuditTests`, `ParentChildTests`, `FieldSearchExecutionTests` and `IndexedSearchQueryStackTests` with `-ExcludeBenchmarks` (functional search coverage of expiry).
3. Playwright `c10-sharing-pages.spec.ts` and `contributor-writes.spec.ts` against the isolated browser stack.

Estimated run time: 2–3 h (worst case about 4 h with one fix cycle).

**Not covered by this focused run:**
- the verified-search and indexed-posting branches of §4 test 9 (covered only by the existing search classes, not with an expiring share);
- server-side re-checks during a long download stream (none is implemented; revocation is re-checked on the next request);
- real passkey step-up (PRD-1);
- Azure staging (HSM, ledger, real database privileges);
- production expiry processing;
- manual NVDA and VoiceOver checks.

### 10.8 Repository governance: proposed Development/Test amendment (prepared, not applied)

**Discrepancy (read-only check, 2026-11-27).**

| Setting | Checked in (`.github/rulesets/main-branch-protection.json`, ADR-PB-011 A-21) | Live ruleset 25341169 |
| --- | --- | --- |
| Required approving reviews | 1 | 0 |
| Last-push approval | required | not required |
| Code-owner review | required (A-21: not enforceable, deferred) | not required |
| Stale-review dismissal, thread resolution, signatures, linear history, no deletion or force-push, 4 strict status checks, no bypass actors | required | same |
| Allowed merge methods | not stated | merge, squash, rebase (merge commits are blocked by linear history) |

`scripts/governance/verify_repository_protection.py` therefore reports "main ruleset: PR + 1 approval…" as FAIL against the live settings. The repository has one collaborator, so no GitHub approval by another person is possible.

**Proposed owner decision (A-24, Development/Test integration on the sole-owner repository).**
1. On the public development repository, a pull request into `main` is integrated on the owner's explicit, recorded authorization (decision log), which is the Development/Test integration decision.
2. Review by AI reviewer agents (fresh context, read-only, recorded with their findings and dispositions) is technical review evidence. It is never described as independent human review or as a GitHub approval, and it does not satisfy any production review requirement.
3. Every other control stays enforced: required status checks (strict), signed commits, linear history, no deletion or force-push, no bypass actors, stale-review dismissal, thread resolution, secret scanning and push protection, Dependabot, CodeQL, the release-tag ruleset, and the Stage B release-only policy.
4. Production review requirements are unchanged: independent human approval of the last push and enforceable CODEOWNERS security-owner review remain production prerequisites, delivered by the private Azure DevOps migration (A-21, A-23).
5. The amendment ends at that migration or when a second eligible reviewer exists, whichever is first; then the checked-in ruleset returns to one approval with last-push approval.

**Exact changes for approval (none made yet).**
- `.github/rulesets/main-branch-protection.json`: `required_approving_review_count` 1 → 0, `require_last_push_approval` true → false, `require_code_owner_review` true → false; every other rule unchanged. This matches the live ruleset, so no live setting changes.
- `scripts/governance/verify_repository_protection.py`: the main-ruleset row accepts 0 approvals only while A-24 applies, and adds a DEFERRED row "independent human approval of the last push (A-24 until the Azure DevOps migration)".
- `docs/decisions/ADR-PB-011-GITHUB-ACTIONS-SECURE-DELIVERY.md`: append Amendment A-24 (points 1–5).
- `docs/delivery/BUILD00_REPOSITORY_PROTECTION.md`: control 3.3 row updated to match.
- `docs/decisions/PHASE_C_DECISION_LOG.md`: the decision entry.
- Optional, not part of the minimum: restrict live allowed merge methods to squash and rebase (a live change; needs its own approval).

### 10.9 Obligations retained (not complete)

- Prove that no incompatible older Authority build can serve content writes before D-1 is relied upon in a shared environment.
- G14: implement re-application of `ShareDocumentBoundaryExtended` in recovery.
- Production backfill of attachment version and content evidence.
- Real passkey step-up (PRD-1).
- Azure staging validation.
- G06 closure.
- Production expiry processing (the convergence loop runs only in Development/Testing).
- Manual NVDA and VoiceOver verification of the five C10 pages (PB14-44…48).
- ~~The pre-correction child owner-edge inventory and, if needed, remediation (§10.6).~~ Closed by evidence (§10.10): 0 affected records in every Development/Test store.
- ~~CONS-23 long-download revocation re-check (mandatory, not implemented; minimum correction in §10.10).~~ Implemented and tested in Development/Test (§10.11). Still open: sign-out during a download end to end, a download through the web nginx, Azure ingress buffering, and PB-09 measurement of re-check load and the gate-closed interruption rate.
- ~~Real-loop expiry convergence evidence (§4 tests 8, 10 and 13; §10.10).~~ Recorded with real elapsed time (§10.11). Still open: §4 test 13 "replay converges it", blocked on G14 (journal replay, SpiceDB rebuild, reviewed quarantine lift).
- Product reviewer sign-off.
- The post-revocation buffered-data exposure observed in Development/Test (§10.11: 9.53 MiB after an active revocation at 512 KiB/s, held in the transport buffers) remains a documented property, to be measured through the web nginx and Azure ingress (staging, PB-09).
- §4 test 8: the "loop wins" race ordering is asserted but was not exercised by real timing (§10.11).
- The CLAUDE.md §11 requirements register rows for Phase C10 (#6, #11, #12, #23–#26, #56–#62, #65) still carry their 2026-11-25 evidence and obligations. C-194 requires them to be re-mapped to the C10 evidence, with statuses decided only on reviewed evidence, before C10 development acceptance.

### 10.10 Governance, consolidation and focused Stage B (owner authorization 2026-11-27, C-200; Development/Test, not acceptance)

**Child-record ownership inventory (§10.6, read-only).** Each Authority store was copied read-only into a throwaway volume. Each copy was queried with no network, in a read-only transaction, for records with both an active `parent` edge and an active direct `owner` edge, current or historic.

| Store | Records with a parent | Affected | Historic |
| --- | --- | --- | --- |
| `enterpriseforms-sectest` | 2 | 0 | 0 |
| `enterpriseforms-dev` | 14 | 0 | 0 |
| `enterpriseforms` (older dev) | 22 | 0 | 0 |

No production data exists. The §10.6 limitation is closed by this evidence: no ownership data was changed and no remediation is needed.

**Governance (A-24, C-199).** Integrated as PR #86 (squash `e5a9f0fc`) under the owner's authorization, with the required checks passing. The checked-in ruleset now matches the live ruleset 25341169: 0 approvals, no last-push review, no code-owner review. The verifier reports 17 PASS, 0 FAIL, 7 DEFERRED, including the new deferred row "3.3 independent human approval of the last push".

**PR consolidation.**
- Merge order:
  - #79 (`a05e70fa`);
  - #80 (`820b4735`);
  - #77, with #82 folded in and the manifests regenerated (`12ae941f`);
  - #78 (`3c463a24`, the `main` that the candidate merged).
- #81–#85 were closed only after their content was verified in signed archive tags:
  - `archive/pr77-c9-closeout-5865eb89`;
  - `pr78-bh1-baseline-be2e4f0b`;
  - `pr79-deps-seroval-146dff37`;
  - `pr80-deps-zod-977e7ee5`;
  - `pr81-coordinator-95ee28d5`;
  - `pr82-c195-baseline-cdfc566b`;
  - `pr83-c10-authority-1488f775`;
  - `pr84-c10-step-up-b70de6ad`;
  - `pr85-c10-lineage-48dcc4b8`;
  - `c10-candidate-r2-f04cdb5b`;
  - `c10-register-checkpoint8-5e951a7a`;
  - `c10-consolidated-4785eab5`.
- The #81 register is carried at `docs/governance/WORK_PACKAGE_REGISTER.md`. The Codex policy documents are retired.
- The signed merge of `main` into the candidate is `4785eab5`. The full gate passed: .NET 3,677 passed, 0 failed, 223 authorized skips; frontend 665; strict validation.
- Consolidated PR: #88 (`c10/consolidated`), **not merged**.

**Focused Stage B.** Runs used the local `enterpriseforms-sectest` stack, rebuilt from the candidate after each product fix. Only the approved groups, classes and specs ran. Each rerun is a new run id recorded with `-RetryOf`. There was no automatic full repeat.

| Run id | Head | Selection | Result |
| --- | --- | --- | --- |
| 20261127-072808-ca7ce123 | 4785eab5 | group records-lifecycle (C-200) | 50/79; 29 `PB08_STEP_UP_NOT_AVAILABLE` (the test stack lacked `HumanCommand__StepUp`) |
| 20261127-074430-0e07f7e9 | e6779ca2 | group records-lifecycle, retry | 72/79; all 7 failures in the two new C10 classes; every existing class passed |
| 20261127-081151-6405743e, -081639-883187f7 | d5c75a0c | the two C10 classes, retries | 4/9, 1/2 |
| 20261127-082438-9fb48983, -083005-21ffb978 | 2019facc | the two C10 classes, retries | 7/9, 1/2 |
| 20261127-083516-7cd21eae, -084047-80b40007 | ca1efd72 | C10ShareLifecycleTests, C10DatabaseVerificationTests | 9/9, 2/2 |
| 20261127-084215-2d4bf70d | ca1efd72 | SharingTests | 2/2 |
| 20261127-084236-fe0dcfdf | ca1efd72 | ContributorWriteTests | 7/7 |
| 20261127-084337-6b906839 | ca1efd72 | DocumentTests | 3/3 |
| 20261127-084535-a25a6fe9 | ca1efd72 | ParentChildTests | 2/2 |
| 20261127-084611-d6ae83e4 | ca1efd72 | FieldSearchExecutionTests (functional) | 14/14 |
| 20261127-084838-bd7f2447 | ca1efd72 | IndexedSearchQueryStackTests (functional) | 21/21 |
| 20261127-084412-68982ba3 | ca1efd72 | DocumentEncryptionAuditTests | 3/5 (removed-document regression; promote expectation) |
| 20261127-091201-2083cd55 | 402fb4f1 | DocumentEncryptionAuditTests | 5/5 |
| 20261127-091350-675d7684 | 402fb4f1 | DocumentTests | 3/3 |
| 20261127-091427-f845455c | 402fb4f1 | C10ShareLifecycleTests | 9/9 |
| 20261127-092614-932f3e8e | 402fb4f1 + spec fix | Playwright c10-sharing-pages | 6/6 |
| 20261127-092735-97e33f03 | 402fb4f1 + spec fix | Playwright contributor-writes | 4/4 |
| 20261127-092932-c0582345 | 9403ace6 | group projection-integrity-restore, fresh stack (C-200) | 14/14 |
| 20261127-093319-6063a121, -093531-be13d63f | 9403ace6 | RecordLifecycleTests, RecordRestoreTests (the upload path changed after their passing run) | 12/12, 11/11 |

No test was skipped in any run. `-ExcludeBenchmarks` left out only the benchmark-tagged tests. Stack and test time came to about 2.5 hours.

**Verified-search expiry** is covered by `FieldSearchExecutionTests.AnExpiredShareStopsFieldSearchAtTheExpiryInstantBeforeConvergence`, using the existing harness: field search stops at the expiry instant, before convergence.

**Product defects found by Stage B (minimal corrections, retested):**
1. **Test-stack step-up (`e6779ca2`).** The disposable test stack did not enable the synthetic human-command step-up that G27 requires. Fixed with `HumanCommand__StepUp: SyntheticDevelopment` in `compose.security-test.yaml`. This applies in Development/Testing only; elsewhere the Authority refuses it.
2. **Share-replacement admission (`d5c75a0c`).** At begin, the admission looked up the attachment's field among the caller's authorized attachments. A replacement-only recipient does not see those, so every such replacement was refused. It now checks the verified record rows.
3. **Upload re-checks and share-scope update (`2019facc`).**
   - Chunk upload and completion re-checked only record Edit permission, so every admitted share replacement was refused. They now also accept the Authority still listing the attachment as replaceable.
   - Share-scope update deleted narrowed projection rows, which the business runtime role may not do (G08). The error was `permission denied for table record_share_fields`, returned as 503. Narrowed rows are now marked unshared, and list and detail show shared rows only.
4. **Removed-document regression from C10 WP8-A (`402fb4f1`).**
   - After a document was removed, a full reader could still download its earlier versions with `?versionNumber=1`, contrary to B-05. A document absent from the accepted current content now releases no version.
   - In the same commit, the chunk re-check now runs before the caller's chunk index is examined (CodeQL `cs/user-controlled-bypass`, alert 8 on PR #88).

**Test corrections** (`d5c75a0c`, `ca1efd72`, `402fb4f1`, `9403ace6`):
- Share expiry must be at least one hour ahead. Tests now create a valid expiry, then move it a few seconds ahead with AuthorityDb administrator SQL on the disposable stack.
- A delegated expiry stays within its parent's expiry.
- The trigger test uses an Invalidated share, because Revoked is not a terminal state (§4 test 6).
- A stale ETag expects `PRECONDITION_FAILED` (412).
- An outside-boundary replacement is expected to be refused at begin.
- Contributor writes are re-enabled after the crash-induced Authority restart.
- Under C-197 decision 2, a forged historic copy is not found (404) instead of refused with 423. It is never released, and detecting the unexpected row is now the reconciliation sweep's job (CLAUDE.md 4O).
- The browser specs tick the G27 step-up and wait for the Access tab's shares heading.

**Limitation: real-loop expiry convergence (§4 tests 8, 10 and 13).**
- Moving the expiry makes the committed expiry disagree with the source command. The convergence loop therefore refuses it (`PAYLOAD_EXPIRY_MISMATCH`), and after two passes raises the alert `SHARE_EXPIRY_NOT_CONVERGED`. Access stays denied.
- The focused run proves access-time enforcement, and that a tampered expiry never converges.
- It does not prove real convergence, descendant convergence, or test 13 (`C10ShareExpiryRestoreTests` was removed). Real convergence is covered only by the Authority unit tests with a test clock.
- Evidence needs either a run of at least 65 minutes with a genuine expiry, or a Testing-only Authority clock (owner decision).

**CONS-23 (mandatory, not implemented, pre-existing).**
- **Requirement:** `trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md`, "Long streams and strict revocation", normative through CLAUDE.md 4L.5, 4M and 4N. Downloads use short bounded chunks, with an independent Authority re-check at a documented maximum interval (for example ≤5 s) before more plaintext is sent. The stream terminates when the revision changes or the Authority is unavailable. The CONS-23 test: "Stream terminates within documented baseline recheck bound".
- **Current state:** `DocumentHandlers.DownloadAsync` checks only at admission, then streams the whole decrypted document. A revocation takes effect at the next request.
- **Minimum correction:**
  - Decrypt the verified envelope segment by segment in the download writer.
  - Once the configured interval has passed, re-check the Authority before writing the next segment. The interval is `Documents:StreamRecheckIntervalSeconds`, default 5, with an enforced upper bound. The check is Read and, for share recipients, the document's download binding, at the admitted authorization revision.
  - Abort the response on denial, revision change or Authority unavailability, with an audited `DOCUMENT_DOWNLOAD_FAILED`.
  - Add a contract test with a fake Authority, and a Stage B CONS-23 test: revoke during a throttled large download, and check the stream stops within the bound.
- No acceptance claim covers long-download revocation until this exists. It is a mandatory requirement and is not silently deferred: it needs the owner's authorization as a separate correction.

**Automated accessibility.** 6/6 passed: axe checks (WCAG 2.0, 2.1 and 2.2, levels A and AA) with no horizontal scrolling.
- Pages: PG-022, PG-023, PG-053 transfer, PG-053 reassignment and PG-066.
- Widths: 320, 768 and 1440 px, plus 640 and 320 px (a 1280 px window at 200% and 400% zoom).

Manual NVDA and VoiceOver review is outstanding (PB14-44…48).

### 10.11 CONS-23 correction and real-elapsed-time expiry verification (owner decisions 1 and 2, 2026-11-27, C-201; Development/Test, not acceptance)

**CONS-23 (owner decision 1).** Requirement: `trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md`, "Long streams and strict revocation".

**Implementation** (`e41bf4f6`, review corrections `6a67eb7c` and `a1e8ee7f`):
- The only document plaintext egress is `DocumentHandlers.DownloadAsync`. There are no exports and no other long streams. Its admission check is unchanged.
- Plaintext is written through `RecheckedDownloadStream`. Every write is cut into slices of at most 64 KiB.
- Before a slice is written, once `Documents:StreamRecheckIntervalSeconds` has passed since the last check began, the stream re-checks. The interval defaults to 5; only 1–5 is accepted, and anything else refuses to start. The interval is measured with a monotonic clock, starting before the admission read.
- The re-check does two things:
  - It re-validates the browser session at the Authority (G23). Bearer requests have no session.
  - It repeats the verified read the admission used: Authority permission, accepted head, integrity, share scope and access-time expiry.
- `DocumentStreamAdmission.Evaluate` releases only the admitted version of the admitted document. The stream stops when any of these happens:
  - the Authority denies, or is unavailable;
  - the session is no longer valid;
  - the authorization revision goes backwards;
  - the document is no longer in the accepted current content (B-05);
  - the streamed version is no longer the accepted current version, because of a replacement (the relevant revision; see the decision below);
  - for a share recipient, the document is no longer downloadable;
  - for an earlier version, the caller no longer has full read.
- A stop is audited as `DOCUMENT_DOWNLOAD_FAILED` with a reason code only. The outcome is `Failed` when the Authority could not decide and `Denied` when it refused. The response is aborted with an incomplete body; nothing is written after a failed check.
- Downloads send `X-Accel-Buffering: no`, and the web nginx never spools API responses to disk (`proxy_max_temp_file_size 0`). A buffering proxy therefore cannot take the plaintext ahead of the re-checks, and no decrypted bytes land on its disk (4C.4).
- Encryption, decryption-failure recording, `DOCUMENT_DOWNLOADED`, the headers and `Content-Length` are unchanged.

**Documented exposure window (protocol baseline).**
- Plaintext emitted after access ends is bounded by the throughput over the interval plus one check's duration, measured from the start of the last successful check.
- In-transit bytes come on top of that: one 64 KiB slice, Kestrel's pipe (about 64 KiB), the TCP buffers, and any ingress buffer.
- On the local stack the TCP buffers are at most 6 MiB receive and 4 MiB send. Stage B measured 9.53 MiB after a revocation for a client reading at 512 KiB/s, almost all of it the socket buffers.
- A stalled Authority call holds the stream without emitting bytes until the Authority client's timeout ends it (`AUTHORITY_UNAVAILABLE`).
- Bytes already delivered cannot be recalled. No strict-profile (gateway lease) guarantee is claimed.
- Azure ingress (Container Apps / Envoy) buffering must be checked in staging.

**Decisions recorded with the correction:**
- *Relevant revision.* The protocol says "terminate when a revision changes". The implementation re-evaluates at the current revision: it continues when an authorization revision increase still releases the admitted version, and stops on regression, removal, replacement, a download binding that no longer allows download, or loss of full read for historic versions. This matches the owner brief's "relevant revision" and is no weaker than the wording for the admitted document.
- *Gate-closed interruptions.* While an organization's gate is closed (§4N projection lag after any security command), a re-check fails closed and stops in-flight downloads of that organization. This is accepted as fail-closed. The interruption rate under realistic command rates needs measuring (PB-09).
- *Cost.* Each re-check is a full verified read (head, staged rows, `VerifyRecordProjection`). This is correct; the Authority/HSM load under PB-09 concurrency needs measuring.
- *Durability.* The stop audit is written to BusinessDb (the existing §4D gap for denial evidence).

**Tests:**
- `Cons23DownloadRecheckTests`, 16 contract tests: interval, slicing, slow-client backpressure, permanent stop, synchronous I/O refused, configuration bounds, the release rule, the session check, and failure classification.
- Stage B `C10DownloadRecheckTests`, 5 tests: an undisturbed slow download completes byte for byte through its re-checks with the no-buffering header; revocation (paused and while reading); replacement; Authority unavailable.
  - The stopping cases read from inside the Docker network, because Docker Desktop's Windows port forwarder reads ahead by many megabytes.
  - Each asserts the server-side stop instant (the audit event time) within the re-check bound.
- The natural-expiry download case is in `C10ShareNaturalExpiryTests`.
- Not exercised end to end: sign-out during a download (contract-tested only), and a download through the web container (the security-test stack has none; the header is asserted).

**Independent review.**
- A Claude Code subagent with fresh context, read-only; not a human or external review. It found three blockers or important gaps: proxy read-ahead, no session re-check, and the window not documented. It also raised four minor or note findings.
- Corrections: `6a67eb7c`. Re-review follow-ups: `a1e8ee7f`. The re-review confirmed findings 1, 2, 7 and 8 resolved and agreed that 3–6 are documentation or decisions (recorded above).

**Real-elapsed-time expiry convergence (owner decision 2).**
- No Testing clock. No expiry moved by SQL. No trigger suspended.
- Shares are granted or updated through the supported commands with a genuine expiry one hour ahead. They expire naturally and converge through the real 30 s loop.

**Product defect found (`41d28452`).**
- Run `20261127-181441-3e96b665` (§4 test 13 on its own stack) showed a share with a genuine expiry denied at the instant but never converged: `PAYLOAD_EXPIRY_MISMATCH`, alert `SHARE_EXPIRY_NOT_CONVERGED`.
- The journalled payload keeps the request's 100 ns ticks (`…05.6805932`), while AuthorityDb stores microseconds (`…05.680593`). The exact comparison in `ExpireShareAdmission` therefore refused every genuine expiry that was not microsecond-aligned.
- The comparison is now at the stored precision. A change of one microsecond or more, such as an expiry moved by SQL, is still refused. Two unit tests cover this.
- The earlier test-clock unit tests used whole-second values, so they could not see it.

**One timeline.**
- §4 tests 8, 10 and 13 and the CONS-23 natural-expiry download share one timeline in `C10ShareNaturalExpiryTests` (group `share-expiry-convergence`, its own stack).
- Every outcome is captured before the last step. That step is test 13: AuthorityDb backed up with `pg_dump` before the instant, then restored over the whole database with `pg_restore --clean`, with the Authority stopped around it (the T16 technique).

**Results.** All runs used the local `enterpriseforms-sectest` stack. CONS-23 and regression classes ran on a stack built at `41d28452` product code (later commits change tests only); each expiry run used a fresh stack. Each rerun is a new run id.

| Run id | Head | Selection | Result |
| --- | --- | --- | --- |
| 20261127-181441-3e96b665 | bd669b16 | §4 test 13, own stack | failed: genuine expiry never converged (`PAYLOAD_EXPIRY_MISMATCH`); found the precision defect |
| 20261127-192729 … -192827 (5 runs) | 41d28452 | regression classes | not executed: Release compile error in the test project (`ef0a2782`) |
| 20261127-193000-af17b7ec | ef0a2782 | DocumentTests | 3/3 |
| 20261127-193032-6ddfcaee | ef0a2782 | DocumentEncryptionAuditTests | 5/5 |
| 20261127-193149-8a0cfea0 | ef0a2782 | C10ShareLifecycleTests | 9/9 |
| 20261127-193640-c4ac8111 | ef0a2782 | SharingTests | 2/2 |
| 20261127-192915-2512f41f | ef0a2782 | C10DownloadRecheckTests | 0/5: a 40 MiB document exceeds the 20 MiB field limit (resized, `eac7c179`) |
| 20261127-193803-9a0fcaef | d51b1d0f | C10DownloadRecheckTests | 1/5: the Windows port forwarder read the whole document ahead (in-network client, `03431003`) |
| 20261127-194256-ceeb8b79 | 03431003 | C10DownloadRecheckTests | 4/5: the paused revocation fell inside the first interval (pause past it, `5fd75357`) |
| 20261127-194606-f9ba23a7 | 5fd75357 | C10DownloadRecheckTests | 4/5: 9.53 MiB after an active revocation, above a provisional 8 MiB bound; bound derived from the stack's TCP buffers (`1f480ba4`) |
| 20261127-194855-229efb47 | 1f480ba4 | C10DownloadRecheckTests | 5/5 |
| 20261127-195331-c635362e | 1f480ba4 | group share-expiry-convergence, fresh stack (73 min) | 2/4: test 8 and the CONS-23 natural-expiry download passed; test 10 (test count defect) and test 13 (see below) failed |
| 20261128-003140-f1417291 | 529b53bb | group share-expiry-convergence, fresh stack (74 min) | 1/4: every share state converged as asserted and the CONS-23 natural-expiry download passed; the corrected journal counts read the wrong JSON path (top level instead of the envelope's `body`) and returned 0 for tests 8, 10 and 13 (path corrected, `6a824791`) |
| 20261128-014838-1c42c802 | 6a824791 | group share-expiry-convergence, fresh stack (73 min) | **4/4** |

**Expiry convergence evidence.**
- §4 test 8 (race with the real loop): passed (run `20261128-014838-1c42c802`). The revoke reached the share first: it was still Active (expired by time, not yet converged), the revoke returned 200 and the share ended Revoked with no `ShareExpired`. The update was refused; the updated and transferred shares each converged to Expired with exactly one `ShareExpired`; two more loop passes recorded nothing further; the designer and administrator stayed denied. Only the "revoke wins" branch occurred in this run; the "loop wins" branch is asserted but was not exercised by real timing.
- §4 test 10 (a parent's natural expiry): passed (run `20261128-014838-1c42c802`). At the instant the delegated share row was still Active, yet the designer saw only the independent grant's field (`family_name`): denied through the lineage before any convergence. On convergence the manager share was Expired with one `ShareExpired`, which lists the delegated share; the delegated share was Invalidated with one `ShareInvalidated` and no `ShareExpired` of its own; the independent grant stayed Active and its field visible; the parent recipient was denied.
  - Run `20261127-195331-c635362e` failed one assertion only: `DelegatedExpired` was 1. The parent's `ShareExpired` event lists the delegated share in `invalidated_descendant_share_ids`, and the test counted any event containing the id. Every product assertion before it had passed (parent Expired, delegated Invalidated, independent grant Active). Counts now use the event's own `record_share_id`, and test 10 also asserts one `ShareInvalidated` for the delegated share, listed by the parent's expiry (`529b53bb`). Those counts first read the share id from the top of the journal row; it is inside the envelope's `body`. The next run (`20261128-003140-f1417291`) therefore counted 0 for every share, while all its state assertions passed. Corrected in `6a824791`; the path was checked against a surviving event on that run's stack.
- §4 test 13 (restore of a pre-convergence AuthorityDb): passed as decided (run `20261128-014838-1c42c802`): converged to Expired with one `ShareExpired` before the restore; after the restore the share row was Active again and access returned 423 `INTEGRITY_QUARANTINE`; after a 180 s wait still Active, still 423, and no new `ShareExpired`; quarantine reason `PROJECTION_MARKER_AHEAD`; gate `Quarantined` at authorization revision 13 (projection applied 13).
  - After the whole-database restore, SpiceDB (not restored) still held markers for revisions the restored AuthorityDb no longer had. The Authority detected this and quarantined every organization in the stack with `PROJECTION_MARKER_AHEAD` (alert `PROJECTION_INTEGRITY_QUARANTINE`). This is the §4R anti-rollback behaviour: an earlier snapshot never resets accepted security state.
  - Quarantine is permanent under normal operation and lifted only by the separately reviewed G14 recovery process. **"Replay converges it" is therefore UNMET and blocked on G14** (journal replay from the independent archive, SpiceDB rebuild, reviewed quarantine lift).
  - Owner decision (C-201): test 13 asserts the denial, the detected rollback (quarantine reason and gate state) and that access is not revived after a 180 s wait. It does not assert convergence.
- CONS-23 on natural expiry: passed (run `20261128-014838-1c42c802`; also passed in `20261127-195331-c635362e`). The recipient received 8,454,144 of 19,922,944 bytes, 1,081,344 before the pause and 7,372,800 after it. The bytes after the pause were already in the transport buffers (TCP receive and send buffers and the Kestrel pipe) while the server was blocked; this is within the stack's socket-buffer bound (6 MiB receive + 4 MiB send). The server's first re-check after the instant stopped the stream, and the download ended 3.1 s after the instant, with one `DOCUMENT_DOWNLOAD_FAILED` (reason `NOT_FOUND`: the expired share no longer reveals the record).

**Superseded evidence.**
- C-200 runs of C10ShareLifecycleTests, DocumentTests and DocumentEncryptionAuditTests (download path changed): superseded by the runs at `ef0a2782` above.
- C-200 SharingTests: superseded by `20261127-193640-c4ac8111`.
- §10.10 "Limitation: real-loop expiry convergence": superseded by the expiry evidence above, except test 13's replay, which remains UNMET (G14).
- §10.10 "CONS-23 (mandatory, not implemented)": superseded by this section.
- Every other C-200 result stands unchanged; no other product path changed.

**CI-equivalent gate (`1f480ba4`).** .NET 3,918 total: 3,695 passed, 0 failed, 223 authorized skips. Frontend lint, typecheck, build and 81 files / 665 tests. Strict specification validation passes; OpenAPI unchanged. Repeated at the final test head `6a824791` (only Stage B test files changed since `1f480ba4`): .NET 3,918 total, 3,695 passed, 0 failed, 223 authorized skips; Stage B project Release build 0 errors; strict specification validation passes. No frontend file changed, so the frontend result stands.

**Not proven or not done.**
- Sign-out during a download end to end (contract-tested only).
- A download through the web container's nginx (header and configuration asserted; the security-test stack has no web service).
- Azure ingress buffering, re-check load and the gate-closed interruption rate (staging, PB-09).
- §4 test 13 replay convergence (G14).
- Not done: merge, formal acceptance, production change, capability activation outside the disposable stack, C11.

### 10.12 Code integration and development closeout (owner authorization 2026-11-28, C-202; Development/Test, not acceptance)

**Authorization.** The owner authorized code integration of PR #88 into `main` for Development/Test at the exact head `5c40a4894d7821aa3114b7186f379d66c3ec36f4`. This is code integration only. It is not formal C10 acceptance, not production approval and not authorization to activate restricted capabilities.

**Pre-merge verification (2026-11-28 06:10 EST).**
- PR #88 was open at the authorized head, mergeable, merge state `CLEAN`, base `main` `3c463a24` (an ancestor of the head).
- All ten checks on the head passed, including the four required status checks and CodeQL (actions, C#, JavaScript/TypeScript, Python).
- All 102 PR commits carry a GitHub-verified signature. No unresolved review threads; 0 open code-scanning, Dependabot or secret-scanning alerts.
- A-24 in effect: `scripts/governance/verify_repository_protection.py` reports 17 PASS, 0 FAIL, 7 DEFERRED; the live `main` ruleset matches `.github/rulesets/main-branch-protection.json` and has no bypass actors.
- All 17 signed `archive/*` tags are on the remote and verify. (The C-201 report said 34; that counted each tag's peeled `^{}` line twice.)

**Merge.**
- Squash-merged through the normal protected path with `--match-head-commit`: `main` is now `5cc4efe3436288e6baca17ac475afa62eaa9c60f` (parent `3c463a24`), GitHub-verified signature.
- The integrated tree `a28a65f4` is identical to the tree of the authorized head; the diff between them is empty.
- The repository deletes merged branches, so `c10/consolidated` was removed. The final head and its history are preserved in the signed tag `archive/c10-final-head-5c40a489`, added beside the 17 existing tags (none changed).
- Post-merge: the `ci` push workflow (specifications, .NET build with architecture and contract tests, frontend, Docker Compose) and CodeQL (actions, C#, JavaScript/TypeScript, Python) passed on `5cc4efe3`; 0 open code-scanning alerts on `main`.

**C10 status (roadmap §2 levels, reported separately).**
- Code-complete (Development/Test): **yes**, functional implementation complete.
- Merged to `main`: **yes**, `5cc4efe3`.
- AI technical review: completed by Claude Code subagents with fresh, read-only context (§10.4–§10.6, §10.11). They are independent of the writer's context but not a human or external review, and they do not reach the roadmap's "independently validated" level.
- Focused Stage B evidence: recorded within its tested scope (§10.10, §10.11).
- Owner development acceptance: **pending** (formal Development/Test acceptance not given).
- Independently validated: **no**. Production-ready: **not approved**.

**Obligations retained.** Every item in §10.9, unchanged in meaning, including the three added by this section (buffered-data exposure, the §4 test 8 alternative ordering, and the §11 register re-mapping), and the §10.11 "not proven" items: sign-out during a download end to end, a download through the web nginx, Azure ingress buffering, PB-09 re-check load and gate-closed interruption rate, and §4 test 13 replay convergence (G14). None is waived or marked complete.

**C11 readiness (assessment only; no C11 work done).**
- Approved scope (roadmap, C-193 RM-1): notifications and the task inbox (PG-009), access requests and invitations (PG-065), welcome and first use (PG-004). Exit evidence: a request flow without existence disclosure (PG-065 rule).
- Stated dependencies: Phase C10, because requests act on shares (satisfied at code level by this integration); in-app invitations need PRD-1 (D8-3(b), deferred in C-78).
- No C11 design exists. C10 followed design approval in WP-0 (C-193, C-194) before a separate implementation authorization, so C11 needs a WP-0 design and owner approval before any implementation authorization.
- No contract makes C11 Development/Test work wait for C10 formal acceptance or for C10's production obligations. The roadmap's status levels are separate, and its dependency column names Phase C10 work, not its acceptance.
- Genuine dependencies: Entra in-app invitations cannot be built until PRD-1 (Development/Test onboarding stays on the C8 operator-plane path); access requests must use the C10 share commands and the Authority unchanged, with contributor writes still off outside disposable stacks (ACT-5, G20); any step-up beyond development step-up (G27) needs PRD-1.

**Not done.** Formal acceptance, production or Azure change, capability activation, Stage B, C10 implementation, C11 work.
