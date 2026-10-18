# PB-07 — Browser Request Coordinator and Durable Upload Standard

**Status:** Binding implementation requirements under `ADR-PB-007-PRIORITIZED-REQUESTS-RESUMABLE-UPLOADS.md`.

## Ownership and transport

- `frontend/packages/work-queue`: versioned `IRequestCoordinator` contract, scheduler, lane policy, typed `WorkerRequest`/`WorkerResponse` envelope, cancellation, bounded queues, security-context reset and telemetry metadata. The app shell creates one coordinator per active document before bootstrap.
- `frontend/packages/upload-manager`: upload session receipts, filename/display sanitization, file selection, limited IndexedDB metadata (not file bytes), resume verification, global tray and notification UX. Does not determine document authorization.
- `frontend/packages/api-client`: generated OpenAPI TypeScript client; adapter binds generated operations to registered endpoint IDs and coordinator methods. No direct feature-specific fetch or duplicate DTOs.
- `frontend/features/*`: TanStack Query keyed by tenant, principal/session/security epoch and feature/record scope. QueryFn calls generated coordinator-backed client. Mutations use declared idempotency/precondition semantics; avoid optimistic displays of committed security state.
- `backend/Features/Documents`: authenticated and authorized `initiate`, `uploadChunk`, `getStatus`, `finalize`, `cancel`, `download` API operations under `/api/v1`; the file storage adapter uses scoped staging objects and server-verifiable chunk receipts.
- `backend/EnterpriseForms.Worker`: durable stage reconciliation, scan, encrypt, manifest and finalization; uploads fail closed if scanner/authority/keys unavailable, with bounded retry and dead-letter/quarantine.

## Six bounded scheduling lanes

| Lane | Suggested initial slots | Intended workloads |
|---|---:|---|
| security | 2 | current session, sign-in bootstrap, access challenge/epoch refresh |
| interactive-read | 4 | active form/record reads, explicit card refresh |
| interactive-write | 2 overall, 1 per resource | form save, share, revoke, publish; strict concurrency headers |
| background-read | 3 | authorized low-priority dashboard card load and prefetch |
| file-transfer | 2 simultaneous files; 2–4 chunks/file subject to total connection budget | upload chunk transfer/retry |
| polling | 2 | upload processing and durable job status |

These are initial ceilings, **not a mandate to open 17 simultaneous connections**. Enforce a separate global per-origin budget, memory bounds and fairness/aging. Allow caps per principal, tenant and network quality; exact limits are subject to PB-09 benchmarks.

## Operation safety

1. Represent each request with immutable `operation_id`, allowlisted `endpoint_key`, lane, resource/tenant binding, security context generation, timeout, abort handle and correlation ID; use an approved validated payload and method. Browser values are hints only; BFF and Authority validate real scope.
2. Send only cloneable payloads, including bounded `Blob` slices for chunk upload. Do not read/hash a complete multigigabyte file into memory; calculate chunk digests and full object identity with streaming/incremental algorithms when required.
3. Worker transport uses `fetch` with `credentials: 'same-origin'` and a per-request anti-CSRF mechanism compatible with same-origin BFF cookies. Web Worker cannot read DOM cookies; define a safe, scoped request token/header exchange through the app shell as required. Never persist or log token values.
4. Any non-idempotent command without a server-durable idempotency contract is **not automatically retried**. On `401`, `403`, `409`, `412`, `428`, security context change or revoked grant, stop and re-evaluate; never silently replay a protected write.
5. `AbortSignal` propagation cancels requests where safe; server commands already committed may continue. Cancellation and timeout never imply a server rollback; query server authoritative status before displaying outcome.
6. Worker crash/restart: restore only safe sessions from server status, regenerate runtime credentials on rebootstrap, require file re-selection when original reference is absent, and discard stale context tasks.
7. Separately loaded cards must expose their own refresh and local error states. Use independent queries; avoid a single `Promise.all` failure blanking the dashboard.
8. Use origin/CSP and cross-site defenses; no arbitrary URL fetch proxy, caller-supplied privileged headers, worker-held OAuth tokens, or browser direct Trust Authority/HSM/SpiceDB access.

## Server upload protocol and state machine

Typical protected endpoint operations (exact generated names derived from ADR-PB-006):

```text
POST   /api/v1/document-upload-sessions             → upload_session_id, chunk limits, accepted expires_at
GET    /api/v1/document-upload-sessions/{id}        → trusted status, accepted byte ranges
PUT    /api/v1/document-upload-sessions/{id}/chunks/{index} → bounded content and digest, idempotent receipt
POST   /api/v1/document-upload-sessions/{id}/finalize → 202 processing operation, never immediate 'Ready'
POST   /api/v1/document-upload-sessions/{id}/cancel
GET    /api/v1/document-processing-operations/{id}  → scanning/encrypting/committed/failed
```

Each request rechecks organization/resource permissions against the independent Authority, active session, expiry, quota, allowed MIME/file type policy, destination form/version and upload safety. Validate chunk offsets/sizes/checksums, final file digest, version ownership and reassembly integrity. Quarantine unscanned bytes; never expose staged upload URLs/content to unauthenticated browsers. Object keys and file metadata are classified as sensitive.

**Client-visible states:** `Queued`, `Uploading`, `Interrupted`, `AwaitingFileReselection`, `ServerReceived`, `Scanning`, `Encrypting`, `Committing`, `Ready`, `Failed`, `Cancelled`. Only `Ready` is a successful accessible document. A `202 Accepted` response indicates pending processing. If authorization changes during upload, next protected operations fail closed and server expires/quarantines the session per policy.

Server job transitions are idempotent and recover from power loss. A staged file is not trusted until scanning succeeds, required encryption completes, authority manifest/revision is committed, audit evidence is recorded and secure download rules hold. Reporting alone must not expose document availability.

## Privacy, persistence, and lifecycle

Persist only `upload_session_id`, non-sensitive fingerprint/size, safe transfer state, acknowledgment metadata and permitted display descriptors in IndexedDB, with expiry and cleanup. Avoid file bytes, bearer tokens, plaintext protected values and complete record DTOs in browser storage. Reselect after reload when file handle unavailable; compare strong identity and size before resuming. At logout/organization switch or known revocation, clear/suspend queued work and scrub scoped caches, then rely on backend status and reauthorization to determine future action. Two tabs may cooperate via `BroadcastChannel` as an optimization; each has its own coordinator and the backend is responsible for deduplicating chunks/commits.

## Observability and validation

Expose bounded queue depth, item age, lane occupancy, chunk retransmits, worker restarts, authority failures, scanner times, and end-to-end state transitions as sanitized telemetry; do not log filenames, raw headers, tokens or confidential content. Provide accessible, manually refreshable card sections and a global live-region Upload Center. CI includes Vitest/Playwright browser tests, independent worker/message fuzzing, server API integration, Docker crash/recovery simulation and PB-03 adversarial tests. Supported browser versions and budgets must be approved at PB-09 before production.
