# PB-06 — Binding Business API Contract Standard

**Accepted:** 2026-10-17. Read with `docs/decisions/ADR-PB-006-VERSIONED-MINIMAL-API-CONTRACTS.md`, `CLAUDE.md`, and `CLAUDE-DESIGN.MD`.

## 1. Ownership and source of truth

- ASP.NET Core 10 Minimal APIs with `MapGroup("/api/v1/...")` and feature-local registration (e.g. `Features/FormRecords/Endpoints/`).
- Versioned wire DTOs/validators, authorized projections, handlers and endpoint tests belong to one owning feature.
- Generate OpenAPI from compiled endpoint metadata with reproducible tooling; snapshot, diff and publish the artifact as CI output. Avoid manually edited parallel OpenAPI definitions.
- Produce a TypeScript client from the published OpenAPI artifact, pin the generator and pass all transport via the global coordinator. Client files are regenerated, not hand-edited.
- Trust Authority has a separate private service contract, not part of the browser-visible API document.

## 2. Required request and response conventions

| Concern | Binding convention |
|---|---|
| Prefix | `/api/v1`, feature-specific route groups |
| Transport | Same-origin HTTPS (TLS outside localhost); BFF cookie and anti-CSRF controls for cookie-authenticated mutations |
| Errors | RFC 9457 Problem Details, stable `type` and `error_code`, safe `trace_id` |
| Time | RFC 3339/UTC API instants; IANA zone for user display/preferences; no implied server local time |
| IDs | UUIDv7 for new entities, encoded as canonical UUID strings |
| Pagination | Opaque, expiring server cursor; limit caps; scope-bound to principal, tenant, filter, sort, authorization revision/epoch and snapshot semantics |
| Concurrency | ETag responses; `If-Match` mandatory where concurrent edits matter, `412` on stale revision, `428` if missing |
| Retry safety | `Idempotency-Key` on retryable commands; scope by actor, tenant, operation and payload hash with durable replay result |
| Long jobs | `202 Accepted` plus operation ID/status URL while queued; completion verified separately |
| File intake | Initiate, accepted chunks, finalize, status and authorized download; range/chunk checksums and declared upload/session limits |
| Search | Only ADR-PB-004 supported predicates; bounded completeness proof or explicit non-partial failure |

Explicitly document which POST routes permit a retry and which operations are non-repeatable. Never treat `202` as a committed business-record success. `GET` does not require an idempotency key.

## 3. Problem Details classification

- `400`: malformed wire contract; `422`: valid syntax but rejected domain/search rule.
- `401`: session missing/expired; `403`: denied when returning denial does not disclose resource existence; `404`: not found or intentionally concealed.
- `409`: conflicting business operation, such as idempotency-key reused with a different payload; `412`: stale `If-Match`; `428`: required revision precondition absent.
- `413`: disallowed upload size; `429`: resource/tenant rate limit.
- `503`: mandatory Authority/signing/integrity/checkpoint dependency unavailable, quarantined or unable to establish correctness. Do not return success or partial search results in fail-closed cases.
- Search (ADR-PB-004, Phase C7): `SEARCH_REQUEST_INVALID` 422, `SEARCH_OPERATOR_NOT_SUPPORTED` 422, `SEARCH_FIELD_NOT_ALLOWED` 422 (one indistinguishable response for unknown, hidden, encrypted or unapproved fields), `SEARCH_VERIFICATION_LIMIT` 422 (`limit_kind`), `SEARCH_SNAPSHOT_CHANGED` 409 (restart), `SEARCH_VERIFICATION_UNAVAILABLE` 503 (`unavailable_reason`, `retryable`). Predicate values travel only in request bodies, never in URLs, and are never echoed. See `docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md` §6.
- Use stable domain-specific error codes for client messages and accessibility; never echo secrets, internal SQL, object keys, sensitive field labels, or raw exception data.

These are defaults to refine per endpoint without changing protocol semantics.

## 4. Authorization and tenant safety

- BFF resolves and validates identity, org membership, active tenant, sessions and CSRF protections server-side; ignore client-asserted access scopes.
- Authority is the source of truth for owner/sharing and protected-record authorization, including batch checks and revision barriers.
- Authorized data projection excludes all fields/documents not explicitly permitted; never filter a broad unauthorized EF query only in the browser.
- Access failures do not leak the existence, title or sort position of protected records.
- No arbitrary signing, SpiceDB administration, or AuthorityDb operations from browser or business feature endpoints.

## 5. Operations required in first secured vertical slice

1. Identity bootstrap/session and permitted organization context.
2. Publish a minimal Accused form and reusable AccusedAddress form definition.
3. Create, read, edit and verify Accused record and child relationships.
4. Begin upload, put chunks, finalize, poll status and authorized view/download of the associated document.
5. Explicit share/revoke and verified authorized record projection.
6. Record audit timeline and safe error/consistency states.
7. Revoked/forged-owner/adversarial failure test coverage.

Do not design all 69 endpoints as untested stubs. Require OpenAPI and generated-client compatibility for each slice.

## 6. CI acceptance

- `dotnet build`, .NET API/contract tests, deterministic OpenAPI generation and breaking-change diff.
- TypeScript client regeneration and compile/lint/typecheck with no diffs or unexplained manual overrides.
- Negative authorization, CSRF, scope, cross-tenant, concurrency, cursor and idempotency tests.
- Upload restart/navigation, job status, authority lag, fail-closed and evidence tests, preserving PB-03 security gate.
- Both the service API and its generated schema must exclude operational admin surfaces except behind separately authorized internal contracts.
