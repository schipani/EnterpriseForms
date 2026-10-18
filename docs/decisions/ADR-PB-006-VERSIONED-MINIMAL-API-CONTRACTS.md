# ADR-PB-006 — Versioned ASP.NET Core Minimal APIs and Generated Client Contracts

- **Status:** Accepted
- **Decision date:** 2026-10-17
- **Approval:** Product owner explicitly approved the recommended pre-build Finding PB-06 architecture
- **Finding:** PB-06 — unified versioned API contract
- **Normative companion:** `docs/api/PB06_API_CONTRACT_STANDARD.md`
- **Implementation status:** Specification only; not a claim that endpoints, tests or generated clients already exist.

## Decision

1. Implement business-facing HTTP endpoints with **ASP.NET Core 10 Minimal APIs**, organized by the owning feature and use case. Route groups, versioning, authorization, validators, handlers, contracts and tests reside within their feature. Domain and application logic do not depend on HTTP or EF Core.
2. Publish business endpoints under **`/api/v1/...`**. Maintain a separately authenticated **internal Authorization Authority API** with its own OpenAPI contract, credentials, compatibility rules and ingress; never expose it to the browser.
3. The **running .NET endpoint definitions and DTO contracts are the source** for generated, version-controlled OpenAPI artifacts. CI validates a deterministic schema, breaking-change policy and contract tests. Do not independently hand-maintain a contradictory OpenAPI document.
4. React accesses backend features exclusively through **generated, typed TypeScript API clients**, wrapped by the shared request coordinator and TanStack Query integration. Do not duplicate wire models, use direct ad hoc `fetch` in components, or couple generated models to database entities.
5. Standardize failures with **RFC 9457 Problem Details** using stable `type`/application error codes and correlation identifiers; avoid confidential record/tenant existence leaks, stack traces, SQL, signer internals or plaintext secrets.
6. Use **opaque server-issued pagination cursors** bound to tenant, permitted principal/security epoch, sort/filter and query snapshot or declared consistency semantics. Do not suggest that mere pagination proves malicious-DBA search completeness; follow ADR-PB-004.
7. Enforce optimistic concurrency through **ETag/`If-Match`** for revision-protected updates. Return `412 Precondition Failed` for mismatch and `428 Precondition Required` when conditional revision is mandatory but absent; preserve authority revision barriers.
8. Require **`Idempotency-Key` for retryable commands** whose duplicate execution would matter. Persist scoped outcomes durably, reject same-key/different-payload reuse, and define request expiry and replay behavior. Mutations may not be retried past a changed authorization epoch or failed precondition.
9. Model large uploads as **initiate → bounded verified chunks → finalize → inspect processing status**. The browser worker/queue is not durable business processing; the server's job/outbox and authority checks are.
10. Long-running commands return an accepted job/operation reference and status endpoint; never show success before a durable final state is independently verified.
11. Keep the independently deployed Trust Authority's API, typed commands and forensic logs separate. Every protected business endpoint must obtain authority decisions and verified record projections before returning data.
12. Apply consistent serialization of UUIDv7 as canonical UUID strings, UTC timestamps in RFC 3339 (offset `Z` for UTC), and documented user-local formatting at the UI boundary.

## Scope and boundaries

- This decision applies to all new feature-owned business APIs, including FormDefinitions, FormRecords, RelatedForms, Documents, Sharing, Search, Audit, Organizations and dashboards.
- No full 69-page endpoint scaffold is required at the outset. Extend the contract per complete vertical slice, prioritizing Stage A security proof then Stage B Accused workflow under ADR-PB-002.
- API format choices do not authorize weakening Level 2 security, encryption classification, journal recoverability or verified search.
- API contract versioning does **not** version published form definitions or their record histories; those have independent semantic versions.

## Release checks

- Minimal API routes, endpoint schemas and generated TypeScript clients compile and pass contract conformance tests.
- Static checks catch unversioned or unowned endpoints, forbidden direct frontend transport, accidental exposed internal Authority endpoints, and breaking schema drift.
- Negative tests cover rejected unauthorized and cross-tenant requests, field masking, stale `If-Match`, reused idempotency key with different body, search-epoch changes, pagination replay, worker upload interruption and failed authority dependencies.
- OpenAPI documentation excludes secret internal-only paths from public distribution; generated schemas are reviewed for sensitive fields.
- Tests against real services are mandatory before marking the architecture implemented.
