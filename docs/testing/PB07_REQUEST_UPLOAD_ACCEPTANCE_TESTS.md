# PB-07 — Required Request Scheduling and Upload Acceptance Tests

**Status:** Test requirements, not executed. References ADR-PB-007 and the mandatory PB-03 security proof.

| ID | Test | Minimum pass condition |
|---|---|---|
| PB07-01 | Navigate Accused → Addresses → Dashboard while a large file is uploading | Chunk transfer continues and Upload Center updates; no page coupling |
| PB07-02 | Upload saturates its own transfer lane | Active record read and save still complete without file-lane starvation |
| PB07-03 | Independently refresh one dashboard card | Only its query is refetched; sibling cards remain functional |
| PB07-04 | Abort or unmount a card query | Card request cancelled if appropriate; unrelated upload continues |
| PB07-05 | File upload loses connectivity between chunks | Retry only unacknowledged chunks with backoff and no duplicates |
| PB07-06 | Simulated Web Worker crash and recreation | Queue resumes safe work only; no false success or unsafe write replay |
| PB07-07 | Reload tab during transfer | Upload reports interrupted; safely resumes after server receipt check and verified file re-selection |
| PB07-08 | Close all browser tabs during transfer | UI makes no guarantee that upload continues; server status accurately reflects accepted chunks |
| PB07-09 | Close browser after finalized receipt | Durable .NET scanner/encrypt/manifest workflow completes or safely retries |
| PB07-10 | Revocation between upload initiation and chunk | Subsequent chunks rejected; no unauthorized file release |
| PB07-11 | Revocation between final byte and finalization | Finalize denied or quarantined under documented policy; no reveal |
| PB07-12 | Change active organization while upload queued | Stale work cancelled/quarantined, scoped query caches scrubbed |
| PB07-13 | Logout/session expiry during pending commands | No stale cookie/anti-CSRF replay; session revalidation required |
| PB07-14 | Duplicate finalize / duplicate chunk | Exactly-once business outcome via idempotent receipts despite at-least-once delivery |
| PB07-15 | Replay idempotency key with altered payload | Stable 409-style rejection; original operation unchanged |
| PB07-16 | Stale `If-Match` save retried automatically | No silent write; explicit 412/concurrency UX |
| PB07-17 | Server returns `202` then scan failure | Upload not shown successful; failure surfaced with remediation |
| PB07-18 | Scanner/HSM/Authority outage | Fail closed, quarantine job, no download success |
| PB07-19 | Malicious worker message changes endpoint URL/headers | Allowlist rejects unregistered target and privileged headers |
| PB07-20 | Heavy upload with memory constraints | Only bounded Blob slices buffered, fair queues, no excessive allocations |
| PB07-21 | Worker/main-thread fallback in Chrome/Edge/Firefox/Safari | Same authorization, CSRF, retry and security semantics across supported versions |
| PB07-22 | Keyboard-only / screen reader Upload Center and card refresh | Named controls, `aria-busy`, progress announcements, focus preserved |
| PB07-23 | Metadata persistence inspection | No OAuth tokens, protected document bytes or confidential record plaintext in IndexedDB/logs |
| PB07-24 | Two-tab same-session upload race | Server receipts/finalization dedupe; no cross-tenant leakage |

**Gate:** Document platform/browser versions and actual evidence. All tests required for supported features prior to claiming deployment readiness; run the critical tests for Stage B with real Docker stack, not mocked allow-all authorization. PB-03 Stage A gating remains independent and prior.
