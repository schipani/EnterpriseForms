# Tier 2 verifier: SpiceDB `LookupSubjects` client interface and cost model (C-138 IX5-D6; review annex E)

- **Status:** interface proposal and cost model. **No client code exists; nothing was implemented or measured.** Prepared by Claude (Opus) under C-138 §7; not an independent assessment.
- **Purpose:** M1 (the verified-revision fence) and M2 (the full rolling sweep) of design §17.1 compare a record's access rows with the subjects SpiceDB says can access it. `ISpiceDbProjection` today offers Check, CheckBulk, CheckBulkStrict and LookupResources only.

## 1. SpiceDB facts (vendored API, SpiceDB v1.56.2 era: `authzed/api` commit `3270b082`)

From `permission_service.proto` (`LookupSubjectsRequest`, `LookupSubjectsResponse`):

| Fact | Consequence |
|---|---|
| One `resource` per request; the call is a server stream | **No server-side batching.** One stream per (resource, permission, subject type, subject relation) |
| "Results are streamed and **not guaranteed to be unique**" | The client must de-duplicate. Two messages for one subject with different `permissionship` values must fail the call |
| `optional_concrete_limit` "is currently unimplemented … and will return an error" | **No server-side limit.** The client must enforce its own maximum and abort the stream |
| `optional_cursor` "is not currently supported … will be ignored" | **No pagination and no resumption.** A failed or cancelled stream must be restarted from the beginning, at the same snapshot |
| `wildcard_option` (default *include* wildcards) | The client must send `EXCLUDE_WILDCARDS`. The schema has no wildcard relations, so any `*` subject is a defect and fails the call |
| `looked_up_at` per message; `consistency` in the request | Use `at_exact_snapshot(pin)`; every message's `looked_up_at` must equal the pin, as `LookupResourcesAsync` already requires |
| `subject.permissionship` may be `CONDITIONAL_PERMISSION` | The schema has no caveats, so conditional is a defect and fails the call |

These facts must be **re-verified against the SpiceDB version actually deployed** in staging. The proto comment refers to 1.40.1; later versions may add limits or cursors.

## 2. Which lookups verification needs

The access rows that Tier 2 derives (design §9.2 mapping; annex A option C) are compared with the zed permissions they mirror:

| Access row kind | SpiceDB permission | Subject queries |
|---|---|---|
| Record-level rows (owner, parent owner, share-derived discover) | `form_record#discover` | `user` (transitive: includes users reached through team membership) and `team#participant` (team usersets granted directly) |
| Field-scoped rows (share-covered field items) and the Evaluable-parity rule | `field_item#read` | `user` and `team#participant` |

- Design §17.1 step 3 lists both subject types for `discover`, but **counts only one lookup per field item**. Both subject types are needed for field items too.
- The correct count per verified record is **2 × (1 + F)**, where F is the number of the record's field items the verification must cover (approved-field items; all share-covered items under option C).
- Team rows are compared at team level. User-level results are compared after expanding team rows through the canonical `member` edges (§17.1 step 3). A user who appears only through a team must not require a direct user row.

**Possible reduction, for the reviewer only.** `field_item#read = parent_record->effective_owner + shared_via->read`. A verifier could instead query:
- `form_record#effective_owner` once per record;
- `record_share#read` once per share.

Per-item calls would then be needed only for items covered by a share. That reduces the calls a great deal, but it reproduces the schema's structure inside the verifier, so the verifier would no longer be an independent oracle for the parts it decomposes. **Not recommended without the reviewer's view** (TQ-8).

## 3. Proposed interface (sketch; not implemented)

```csharp
public sealed record SubjectLookup(string ResourceType, string ResourceId, string Permission, string SubjectType, string? SubjectRelation);

/// <summary>Distinct subject ids, ordinal-sorted. Never partial.</summary>
public sealed record SubjectLookupResult(SubjectLookup Lookup, IReadOnlyList<string> SubjectIds);

public interface ISpiceDbProjection
{
    // ... existing members unchanged ...

    /// <summary>
    /// Every subject of the given type (and relation) holding the permission on the resource, unconditionally, at exactly the snapshot's pin.
    /// Fails closed: a looked_up_at different from the pin, a conditional or wildcard subject, a duplicate with a different permissionship,
    /// or an invalid subject id fails the call; more than maximumSubjects distinct subjects throws SpiceDbLookupLimitExceededException.
    /// Nothing partial is returned. Default implementation: NotSupportedException.
    /// </summary>
    Task<SubjectLookupResult> LookupSubjectsAsync(SubjectLookup lookup, VerifiedProjectionSnapshot snapshot, int maximumSubjects, CancellationToken cancellationToken);
}

/// <summary>Application-level batching over the single-resource RPC (not a SpiceDB feature).</summary>
public interface ISubjectLookupBatcher
{
    /// <summary>
    /// Runs every lookup at the same snapshot, with at most the configured number of concurrent streams. All or nothing: any failure
    /// cancels the remaining streams and throws; results are returned in request order, mapped by the lookup each answers.
    /// </summary>
    Task<IReadOnlyList<SubjectLookupResult>> LookupAllAsync(IReadOnlyList<SubjectLookup> lookups, VerifiedProjectionSnapshot snapshot, int maximumSubjectsPerLookup, CancellationToken cancellationToken);
}
```

The client applies the IX-2 O-1 hardening:
- results are mapped by the request they answer, never by position;
- any per-item error fails the whole call;
- nothing is truncated.

## 4. Behaviour

### 4.1 Snapshot consistency

1. The verifier processes a pending record only after the projection has reached the pending row's revision (`projection_applied_revision ≥ revision`).
2. It takes an S1 pin and verifies it (the existing `VerifiedProjectionSnapshot`), and reads the access rows and the canonical edges in one AuthorityDb `REPEATABLE READ` transaction. That transaction must observe `authorization_revision` equal to the pinned marker's revision; otherwise the attempt is Inconclusive and is retried.
3. Every lookup for one record uses the same pin.
4. A verification proves equality **as of that revision**. It may delete pending rows only at or below that revision. A newer command on the same record has written its own pending row.
5. If the SpiceDB datastore's garbage-collection window ends during a long verification, the pin becomes unavailable (`SpiceDbSnapshotFailure.Unavailable`). The attempt is then Inconclusive, and the record is re-verified under a new pin. Results are never mixed across pins.

### 4.2 Outcome classes (the §17.10 classes, applied to S-D)

| Result | Class | Effect |
|---|---|---|
| Rows equal the subjects (after team expansion) for every lookup | Pass | Delete the record's pending rows up to the revision; set `last_access_verified_on`; advance `access_verified_revision` per §17.1 step 4 |
| Any difference | Fail | Run the S1 decisive verification first (Q4). If S1 is consistent: Tier 2 disabled for the organization, rebuild from canonical edges, verify again; a persisting difference is a derivation defect (Critical alert) |
| `SpiceDbUnavailableException`, timeout, cancellation, lease loss, snapshot unavailable or datastore changed, AuthorityDb error | Inconclusive | Keep the pending rows; record `last_attempted_on` and the outcome code; the fence stays closed, and M2's deadline keeps running |
| `SpiceDbLookupLimitExceededException` | Inconclusive, with a distinct alert | As above. A record with more subjects than the limit can **never** pass, so Tier 2 stays unavailable for its organization until the limit is changed by the owner. The limit is a safety bound, not a silent truncation |
| Schema missing (`SpiceDbSchemaMissingException`), snapshot token mismatch | Inconclusive, escalated to S1 | S1's existing handling (E4 and E5) applies first |

### 4.3 Timeouts, retries, rate limits, cancellation

- **Timeout per stream:** a separate option (proposed `SpiceDb:LookupSubjectsTimeoutSeconds`, default 5, matching `TimeoutSeconds`). It is not raised to absorb lock contention (C-137 rule).
- **Retry:** restart the entire stream at the same pin. No per-message resumption exists (§1). The verifier's retry backoff is separate from search traffic.
- **Concurrency:**
  * a per-Authority-instance limit on concurrent streams (proposed default 8);
  * a per-organization share, so that one organization's verification backlog cannot exhaust it;
  * the verifier's SpiceDB budget is separate from the search path's, so verification load cannot starve searches (R-33 lesson).
- **Rate limits:** a configurable ceiling on lookups per second per instance (proposed: unset until PERF-01 measures SpiceDB capacity).
- **Cancellation:** propagated to every open stream; cancelled streams leave no state; the pending rows remain.
- **Logging:** counts, durations and outcome codes only. No subject ids, record ids or field ids are logged at information level (consistent with event 9305 to 9307).

### 4.4 Result completeness

- Completeness depends on SpiceDB returning every subject on an uncapped stream (§1).
- The client treats an end of stream without a transport-level success status as a failure, never as completion.
- De-duplication is by subject id. Ids must be canonical lowercase UUIDs (the Authority issues every id); anything else fails the call.

## 5. Cost model (formulas; inputs unmeasured)

Notation:
- `F` = field items to verify per record;
- `c` = lookups per second available to the verifier (all instances);
- `n` = records verified.

| Workload | Lookups | Example |
|---|---|---|
| One record (M1 or M2) | 2 (1 + F) | F = 10: 22. F = 250 (PB-09 maximum fields per form): 502 |
| `CreateShare` or `RevokeShare` on one record | 2 (1 + F) | 22 to 502 |
| `TransferOwnership` of a record with d descendants | (1 + d) × 2 (1 + F) | d = 50, F = 10: 1,122 |
| `RegisterRecord` (ingestion) | 2 (1 + F) per record | At R-37's ≈ 0.5 to 2.6 records/s and F = 10: 11 to 57 lookups/s |
| Field approval or withdrawal on a form of R records (option B only) | R × 2 (1 + F) | R = 100K, F = 10: 2.2M |
| M2 full sweep in T days over the organization's N records | N × 2 (1 + F) / (T × 86,400) per second | N = 250K, F = 10, T = 7: ≈ 9 lookups/s. F = 250: ≈ 207 lookups/s |
| M2 user differential (`LookupResources` per user) | One enumeration of \|D_u\| results per user per cycle | 500 users × up to 250K results each per 7 days: streamed volume, not call count, dominates |

**Fence lag.** After a command affecting n records, the fence stays closed for at least n × 2(1 + F) / c, plus the projection and archive delay (R-37: tens to hundreds of milliseconds locally). Examples:
- **c = 200 lookups/s (assumed; unmeasured):**
  * a share change on a 10-field record: about 0.1 s;
  * a 250-field record: about 2.5 s;
  * a transfer with 50 descendants: about 6 s;
  * a B-option approval change on 100K records: about 3 hours.
- **Continuous ingestion at 2 records/s with F = 10:** 44 lookups/s just to keep up, with the fence open only in the gaps between commands (fence starvation; UF-18).

**SpiceDB cost per lookup** depends on fan-out: owners, shares and the members of teams expanded transitively for `user` lookups. A record shared with a team of 500 members returns at least 500 users. It is unmeasured locally and in staging. **PERF-01 must measure:**
- p50 and p95 per lookup as a function of the subject count;
- maximum sustainable `c` per SpiceDB node at the PB-09 envelope;
- the impact on concurrent search Check and LookupResources latency.

## 6. Open questions for the reviewer

| # | Question |
|---|---|
| TQ-8 | Is the per-item `LookupSubjects` oracle required, or is the decomposed form (§2) acceptable, given what it costs in independence? |
| TQ-10 | Is "a record whose subject count exceeds the limit can never pass" the right fail-closed behaviour, or should such records be verified by another method (for example a full `ReadRelationships` comparison of the record's edges)? |
| TQ-11 | Is verification at the projection's revision, with deletion of pending rows only at or below it, sufficient under concurrent commands on the same record? |
| — | Do newer SpiceDB versions change any fact in §1 (limit, cursor, uniqueness), and should the client then adopt them? |
