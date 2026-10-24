# Record creation idempotency (F-C7-01)

- **Decision:** C-65. Finding F-C7-01 was recorded on 2026-10-24 (Phase C7 M1 benchmark). A record creation that failed part-way could not be completed by retrying it with the same idempotency key: the retry got `IDEMPOTENCY_CONFLICT`, and the record could stay registered with no content.
- **Branch:** `hotfix/f-c7-01-record-creation-idempotency`, from `main` ce6c365. It contains only this correction.
- **Authority protocol:** unchanged. No Authority code, contract, signed format or journal changes.

## 1. The creation sequence and where it broke

| Step | Store | Retry behaviour before the fix |
|---|---|---|
| 1. Validate values, derive the record id from the idempotency key | — | deterministic |
| 2. Authority `RegisterRecord` (key `record:<key>`) | AuthorityDb | idempotent: the Authority's digest excludes the authorization-revision precondition |
| 3. Local record projection + audit | BusinessDb | idempotent (insert if absent) |
| 4. Build the first candidate | — | **not reproducible**: new item and entry ids, a new timestamp, fresh AEAD nonces for encrypted values |
| 5. Authority prepare (key `content:<key>`, deterministic operation id) | AuthorityDb | **conflict**: same key, different candidate digest |
| 6. Stage the signed candidate | BusinessDb | — |
| 7. Authority commit (head compare-and-swap) | AuthorityDb | — |
| 8. Project the accepted head; answer | BusinessDb | an idempotent replay answered with the retry's own (uncommitted) value ids |

A record registered at step 2 whose content never committed has no accepted head. It is unreadable (`NO_COMMITTED_CONTENT`) and not listed, so it was never usable. It simply could not be completed.

## 2. Correction

1. **One fingerprint per creation.** Before any Authority command, the handler records `form_record_creation_attempts`: one row per tenant and derived record id, insert-only for the runtime role.
   - The row holds an HMAC-SHA-256 of the canonical request: actor, form, owner team, parent and relationship, every value (keys sorted), and every repeating entry in order.
   - The HMAC key is random per attempt. It is stored only as a field-encryption envelope: KEK-wrapped and AAD-bound to tenant, record and attempt.
   - The business database therefore holds neither values nor a digest that could be tested against guessed values. A retry recomputes the HMAC and compares in fixed time. A different request, or an envelope that does not open (tampered, moved, foreign key), is `409 IDEMPOTENCY_CONFLICT`.
2. **Completion by what exists, never by repair.**
   - **Accepted head at revision 1:** this creation already succeeded. Only the creation writes revision 1, and the fingerprint proved this is the same request. The original outcome is replayed (the request's values with the Authority's head), exactly like the first answer, with no read and no new version. Replaying without a read matters: the Authority refuses reads until the new checkpoint is archived, so a read-based answer briefly turned a completed duplicate into a 503 (found by SEC-P08).
   - **Accepted head at revision ≥ 2:** the record was edited since. The answer is the current verified record; no new version is created.
   - **Staged candidate of the creation's operation:** that exact candidate is committed. The Authority verifies the staged rows against what it signed. If they don't match (`CANDIDATE_MISMATCH`), the request fails closed and nothing is replaced. An expired or aborted operation moves on to a fresh one.
   - **Prepared but never staged:** a fresh operation (a retry-specific key) prepares the retry's candidate. The abandoned operation stays Prepared until its existing 10-minute expiry. It can never be accepted afterwards, because the head compare-and-swap admits only one first revision.
   - **A concurrent attempt committed first** (`CONTENT_REVISION_CONFLICT`): answered from a verified read.
3. **Testing-only fault seam.** Five checkpoints: after register, after projection, after prepare, after stage, after commit. They are armed per request with `X-Testing-Fault`. The seam is registered only when `Testing:WriteFaultInjection` is true and the environment is Testing; every other environment refuses to start with it set. All other hosts get a no-op.

## 3. Properties

| Requirement | Result |
|---|---|
| One accepted record | The record id is derived from the key, and only one first content revision can commit (Authority head CAS). |
| Same request ⇒ same outcome | Same record id. The accepted content is the request's values. Repeated retries answer with the same record. |
| Transient failure is retryable | Every checkpoint failure completes on retry (§4). |
| Genuine conflict is rejected | Fingerprint mismatch ⇒ 409, before and after completion, including requests by another user with the same key. |
| No new Authority version for the same operation | Exactly one accepted revision after any number of retries (asserted against AuthorityDb). |
| No usable incomplete record | Unchanged: content-less records stay unreadable and unlisted until completed. |
| Existing content intact | A retry after later edits returns the current record and commits nothing. |
| Orphans | Abandoned prepared operations expire at the Authority (existing mechanism). A registered record without content is completed by a retry with the same key; otherwise it stays unusable (no automatic deletion). |
| No deletion or repair from BusinessDb data | Staged rows are committed only if the Authority verifies them. Tampered fingerprint ⇒ conflict. Tampered staged rows ⇒ refused. Nothing is deleted. |

## 4. Tests

| Suite | What it proves |
|---|---|
| `tests/integration/EnterpriseForms.StageBTests/Tests/RecordCreationIdempotencyTests.cs` (16, real stack: Authority, SpiceDB, both databases, Vault Transit, real encryption) | **Fault injection at every checkpoint** (after register, after projection, after prepare, after stage, after commit with the response lost): the interrupted request fails with 503, and a retry with the same key and request creates the record. A further retry returns the same record and revision. AuthorityDb holds exactly one accepted revision. The record holds the submitted values, including the encrypted name. The list shows exactly one record. After the prepare fault, the abandoned operation is still Prepared and exactly one operation is Committed. **Conflicts:** a changed value or a removed value with the same key → 409, both before and after completion, and the record is unchanged. **Later edits:** a retry after an edit returns revision 2 and commits nothing. **Concurrency:** four simultaneous identical requests → one record and one accepted revision; any request that did not succeed failed transiently and succeeds on retry. **Hostile business DBA:** a rewritten fingerprint → 409; a staged candidate row altered after the stage fault → the Authority refuses it (`CANDIDATE_MISMATCH`) and nothing is accepted. **Real outages and restarts:** a paused BusinessDb and a paused Authority during creation fail with a recoverable 5xx (the Authority outage is marked retryable), never a conflict, and the retry completes. An API restart between stage and commit loses nothing the retry needs. **Concurrent conflicting requests** (two different requests, same key, raced) leave one record with the winner's values, and the loser gets 409. **Missing business data:** with the staged candidate and the local record row deleted after a partial creation, the retry stages a fresh candidate from its own request (nothing deleted is trusted) and exactly one revision is accepted. **Another user with the same key** gets a conflict or a denial that reveals neither the record id nor any value, and cannot read the record. The retries resend on the Authority's transient `AUTHORITY_NOT_READY` (archive receipt pending), as the web coordinator does. |
| `tests/features/EnterpriseForms.Api.ContractTests/RecordCreationIdempotencyUnitTests.cs` (4) | Same request in any key order matches. Every kind of difference is a conflict: value, missing or extra key, repeating entries and their order, actor, owner team, form. The stored row holds no value and is not a plain digest. A flipped fingerprint, a row moved to another record, a corrupted or unparsable envelope, or another KEK → no match. The fault seam is the no-op unless Testing **and** configured; any other environment with the setting refuses to start. |
| User-facing errors | Recoverable failures are 503 (with `retryable` for Authority outages) and the web coordinator retries them with the same key. A real conflict is `409 IDEMPOTENCY_CONFLICT`, which the web app titles "This request was already submitted with different details" and does not retry. |
| Full Stage B integration and browser suites | Every creation path (records, child records, the designer's sample flows, sharing, lifecycle, documents) still works through the new sequence. |

The negative baseline was observed directly in the C7 M1 benchmark run (C-62): a retry after a partial failure returned `IDEMPOTENCY_CONFLICT`. The fault checkpoints did not exist before this change, so that baseline was not re-run with them.

## 5. Residual

* A different request that reuses a key after the first attempt failed before step 1 recorded the fingerprint is simply the first request. Nothing of the earlier attempt exists.
* The orphaned registered-but-empty record (when the client never retries) remains as before. A reconciliation listing for operators is not part of this fix.
* The fingerprint table grows with every creation (one small row each). Retention is a later data-lifecycle item (PB-13).

## 6. Related fix in this change (G23 follow-up)

The full browser run found an intermittent failure in the G23 sign-out flow, caused by an existing race in the session loader. Two concurrent session probes start in development (React's double effect). When the first finds no session it clears the security context, which cancels the second, and the cancelled probe then overwrote the settled "signed out" state with "The application cannot start". The loader now ignores a cancelled probe (`packages/app-runtime/src/session.tsx`). The G23 browser specs passed three consecutive runs afterwards. The G23 spec also no longer navigates back and forward immediately after sign-out.
