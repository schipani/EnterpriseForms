# ADR-PB-007 — Prioritized Request Coordinator and Resumable Document Uploads

- **Status:** Accepted
- **Date approved:** 2026-10-17
- **Finding:** PB-07, Global background request coordinator and resumable upload feasibility
- **Approval:** Product owner approved the recommended prioritized coordinator, dedicated upload Web Worker, TanStack Query integration and durable .NET server-side processing.
- **Normative companions:** `docs/architecture/PB07_BROWSER_REQUEST_COORDINATOR_STANDARD.md`, `docs/testing/PB07_REQUEST_UPLOAD_ACCEPTANCE_TESTS.md`, and ADR-PB-006.
- **Implementation status:** Architectural requirement only. No browser interoperability, security, throughput or actual uploads tested.
- **Amendment A1 (2026-10-31, SD-6, owner-approved):** dashboard sections load automatically without per-section Refresh buttons; see "Amendment A1" below. Decision C-94.

## Context

The user must be able to navigate any SPA route while large documents upload. Independently refreshed cards, secure form saves, relationship edits and search must continue without being blocked by large transfers. Network tasks also must respect the Level 2 authority boundary, idempotent command semantics, and browser lifecycle limitations.

## Decision

1. **Single logical coordinator per open application tab.** All application-owned API reads and writes enter a strongly typed `IRequestCoordinator` interface. Do not implement a universal single-file serial queue or duplicate independent fetch layers in feature modules. The authenticated SPA shell owns it so route transitions do not destroy queued uploads.
2. **Priority lanes with bounded independent concurrency:** security/bootstrap, interactive reads, interactive writes, background card reads, file transfer and polling. Initial concurrency guidance is in `CLAUDE-DESIGN.MD` §5; benchmark and revise. Security operations are not a means to bypass server checks. Implement fairness, backpressure, retry budgets, cancellation and per-resource write ordering.
3. **Dedicated Web Worker for transfers and suitable API transport.** Implement a typed structured-clone message protocol with validated endpoint identifiers, limits and operation IDs. Main-thread transport is allowed **only as a reviewed capability fallback or browser-managed exception**, using the same logical coordinator and security rules. OIDC redirects, native download interactions and static assets are not forced into worker fetches.
4. **TanStack Query owns server-state caching, query keys, cancellation and widget refresh; coordinator owns scheduling/transport.** A card's refresh affects only that card's scoped query key. Background card reads cannot starve active writes or transfers; stale results must not cross tenant/session/security epochs.
5. **Transfer session lifecycle:** obtain authorized server session; upload bounded independently acknowledged chunks with checksums; resume only confirmed missing chunks; idempotently finalize; poll or receive authorized processing state; report success **only once the server confirms durable safe/committed availability**. Virus scanning, encryption, metadata/manifest commitment and notification are server-side work.
6. **Navigation vs. browser shutdown:** SPA navigation must not interrupt transfers. A full reload, lost file reference, OS suspension, tab closure and browser shutdown can interrupt the worker. The app may persist minimal non-sensitive session metadata; resumption after reload may require the person to reselect and verify the original file. Do not claim background upload continues after last tab closes.
7. **Security:** Secure same-origin HTTP-only BFF cookie authentication plus CSRF defenses; no OAuth access tokens or protected document bytes in persistent browser storage. On logout, tenant change, revocation or stale authorization epoch cancel/quarantine pending jobs and invalidate caches, and reauthorize subsequent chunks/finalization/download on the server. A client queue is never the source of permission or commit truth.
8. **Durable backend:** ASP.NET Core API handles authorized intake and resumable status; `.NET BackgroundService` consumers use a durable job store/outbox, leases, idempotent processing, retries, observability and dead-letter/quarantine. A volatile `Channel<T>` by itself is not a durable processing guarantee. Retain independent authority/security journal checkpoints.
9. **Accessibility and observability:** Each upload is visible in a global Upload Center with queued/transferring/paused/interrupted/scanning/encrypting/committed/failed states, progress/status announcements, cancel/resume, and final outcome. Every separately loaded server-data card includes a scoped accessible refresh action (except PG-006 dashboard sections, Amendment A1). Record and correlation IDs must not leak sensitive values in telemetry.
10. **Browser feasibility gate:** Test on supported versions of Chrome, Edge, Firefox and Safari, including worker fetch+cookies+CSRF, chunk upload, route navigation, tab reload/reselection, network disruptions, memory budgets, unauthorized retries and failure states. No unsupported fallback may silently reduce security.

## Relationship to previous decisions

- ADR-PB-002: The complete Stage B Accused/AccusedAddress/documents vertical slice demonstrates real navigation during upload.
- ADR-PB-003: Security proof precedes protected Stage B use; negative revocation, tampering and service failure tests remain mandatory.
- ADR-PB-004: Search/index content classification unchanged; client queue cannot weaken search integrity.
- ADR-PB-005: Local Docker stack and deployed Azure .NET worker images implement the durable backend.
- ADR-PB-006: Generated versioned OpenAPI client flows through the coordinator. Use `Idempotency-Key`, `If-Match`, Problem Details and upload session/operation-status endpoints.

## Rejected alternatives

- **One serial queue:** Prevents responsiveness under long-running transfers.
- **Uploads owned by record page components:** Route unmounts interrupt the transfer.
- **Service Worker / Background Sync as guaranteed upload process:** Browser lifecycle and support do not provide a reliable guarantee after browser/tab closure.
- **Worker as security or durable job authority:** Cannot survive arbitrary client termination, and cannot authorize sensitive operations.
- **Blind automatic retry of mutations after reauthentication/revocation:** May replay operations under changed permissions.

## Release gates

- Passing automated worker/coordinator unit tests, generated-client contract tests and real-browser integration tests in `docs/testing/PB07_REQUEST_UPLOAD_ACCEPTANCE_TESTS.md`.
- Verified `.NET` durable processing across browser disconnect and worker crash; no false client 'success' status.
- Tests demonstrate protected access is denied following revocation or tenant switch, including after chunk receipt and before finalize.
- Performance, accessibility and memory thresholds are set and met under the separate PB-09 scalability decision when approved.
- Approval here does not mean implementation or production validation has occurred.

## Amendment A1 — automatic dashboard section loading (SD-6, approved 2026-10-31)

The project owner approved SD-6 (decision C-94) after visually accepting the redesigned dashboard (C-91).

- **Scope:** the PG-006 dashboard sections only (My records, Upload activity, Recent personal activity, Permitted forms,
  and the Security monitoring section). Every other separately loaded server-data card keeps its scoped accessible refresh
  action.
- **Rule:** a dashboard section loads when the dashboard opens, when its page changes and when one of its filters changes
  (free text debounced). It has no individual Refresh button and does not poll.
- **Unchanged guarantees:** each section keeps its own query key, cache scope, loading/empty/error state and screen-reader
  status; one section's failure or reload never disturbs the others; a failed section keeps its own accessible "Try again"
  action; reads still go through the request coordinator and never starve writes or transfers; stale results never cross
  tenant, session or security epochs.
- **Not changed:** this amendment adds no permission, changes no security control and is not production evidence.
