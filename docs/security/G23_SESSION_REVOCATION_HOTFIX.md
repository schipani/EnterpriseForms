# G23 — Server-side session state and revocation (security hotfix)

- **Decision:** C-63. Finding G23 was recorded on 2026-10-24 while testing G22 (C-59): sign-out only deleted the browser cookie, so a cookie copied before sign-out kept working until its ticket expired.
- **Branch:** `hotfix/g23-session-revocation`, from `main` 65d25fa. It contains only this correction; no Phase C7 functionality.
- **Status:** corrected and tested (§5). The Authority changes still need the independent review that every Authority change needs before production (PB-08/PB-12). This document is not that review and claims no Level 2 certification.

## 1. Requirement → design

| Requirement | How it is met |
|---|---|
| Server-verifiable active/revoked state | The Authority keeps one row per browser session (`authority.authenticated_sessions`). On **every** cookie-authenticated request the BFF asks the Authority (`POST /internal/v1/sessions/validate`); the cookie alone authenticates nothing. |
| Unpredictable session identity | 256-bit random secret (`RandomNumberGenerator`), new at every sign-in, carried only inside the encrypted, HttpOnly session ticket (claim `ef:sid`). |
| Protected against BusinessDb manipulation (Level 2) | State lives in AuthorityDb, outside the business database administrator's reach, written only by the Authority runtime behind the workload credential **and** the user's own delegated assertion. BusinessDb has no session table. |
| No raw secrets in logs or databases | Only `SHA-256(secret)` leaves the BFF process; the Authority stores the 32-byte digest (unique, `octet_length = 32` check). Logs carry reason codes only (tested). |
| Immediate revocation at logout | `POST /api/v1/session/logout` revokes at the Authority **before** the cookie is deleted. If revocation cannot be confirmed: `503 SESSION_STATE_UNAVAILABLE`, cookie kept, the UI says "You are still signed in" and offers a retry. |
| Absolute and inactivity expiry | Absolute expiry = the identity assertion's expiry, at most 12 h (bounded on both sides); never extended. Idle timeout 30 min (`Sessions:IdleTimeoutMinutes`); last-seen written at most every 60 s. |
| Safe login, renewal, rotation | No renewal endpoint exists (sliding expiration and refresh stay off). Every sign-in mints a new secret and CSRF token; the session the browser presented is revoked in the same Authority transaction that registers the new one (rotation, and fixation protection: a planted session never survives a sign-in). The cookie is issued only after the Authority has registered the session. |
| Multiple API instances | State is central (Authority), so every instance sees a revocation at once. Instances that serve one front end share the Data Protection key ring (`DataProtection:KeyRingDirectory`) so they can read the same tickets; the key ring makes a ticket readable, never valid. |
| Fail closed when state is unavailable | Any Authority/transport failure during validation refuses the request: `503 SESSION_STATE_UNAVAILABLE` (not 401, so clients do not mistake an outage for a sign-out) and the endpoint never runs. |
| Fixation and replay | Fixation: see rotation. Replay: a copied cookie (with or without its CSRF token) is refused once the session is revoked, expired or idle; a revoked digest can never be registered again. |
| Preserve CSRF, cookie settings, service-to-service auth | G22's middleware is unchanged and still runs before the endpoint (it now sees only Authority-valid sessions). Cookie name/flags unchanged. Bearer and workload/operator/verifier authentication untouched. |
| Tenant switching and authorization changes | Switching identity or organization is a new sign-in, which revokes the previous session. Sessions carry no permissions: every action is still authorized by the Authority at that moment. A disabled identity binding invalidates its sessions (no disable path exists yet; the check is in place). |

## 2. Components

* **Authority:** `AuthenticatedSession` entity and `authenticated_sessions` table (migration `20261024111904_AuthenticatedSessions`, additive: one table, three indexes, three check constraints; runtime role gets SELECT/INSERT/UPDATE, no DELETE); `SessionService` (create with replacement, validate, idempotent revoke); internal endpoints `POST /internal/v1/sessions`, `/sessions/validate`, `/sessions/revoke` (workload policy + delegated assertion of the session's own user; another user can neither validate nor revoke someone else's session); options `Sessions:IdleTimeoutMinutes|MaximumAbsoluteHours|LastSeenWriteIntervalSeconds`, validated at startup.
* **Business BFF:** narrow port `IBrowserSessionStateAuthority` (separate from the main Authority client); `BusinessAuthentication.ValidateSessionAsync` in the cookie handler's `OnValidatePrincipal`; `StartSessionAsync` registers first, then issues the cookie; `EndSessionAsync` revokes, then deletes; `BusinessDataProtection` (shared key ring option).
* **Web app:** a sign-out the server cannot confirm keeps the user signed in with an alert and a retry button.
* **Docker security-test profile:** second API instance `api-secondary` (127.0.0.1:5181) sharing an ephemeral key ring with `api`.

## 3. Decision taken (C-63)

Session state is held by the independent Authority rather than BusinessDb or a cache, because the requirement is resistance to a hostile business database administrator and the Authority is the only independently administered store. This adds a table and three internal endpoints to the Authority and one Authority call per cookie-authenticated request. It does not change any existing Authority protocol, signed format or journal. It is the direct consequence of the stated requirements, not a weaker alternative; it is listed for the independent Authority review.

## 4. Residual and open items

* Bytes already delivered to a browser cannot be recalled.
* Authority availability now also gates browser sessions (fail-closed by design). Latency: one additional Authority round trip per cookie request (measured in §5 only functionally; PB-09 benchmarks pending).
* Session events are not yet in an audit stream (`main` has none; the C7 branch adds one). Follow-up on C7.
* Production key-ring storage (Azure Blob + Key Vault key encryption) is a deployment item (PB-11); the file-system ring is for Docker and development.
* Entra ID (PB-08) will reuse the same session registry; its own revocation signals (CAE) are not integrated.
* Real-clock idle expiry is proven at unit level; absolute expiry is proven against the real Authority (§5).

## 5. Tests

| # | Required property | Executed proof |
|---|---|---|
| 1 | A copied cookie works before logout and fails after | Integration `ACopiedCookieWorksBeforeSignOutAndNeverAfterEvenWithItsCsrfToken` (real logins, separate client replaying the cookie); browser `signing out ends the session everywhere…` (second browser context with the copied cookies); contract `ACopiedCookieStopsWorkingOnceTheSessionIsRevoked`, `SignOutRevokesAtTheAuthorityBeforeDeletingTheCookie`. |
| 2 | Copied cookie + old CSRF token fails | Same integration test: PUT with the copied cookie and its own CSRF token after sign-out → 401, preference unchanged (checked from a fresh session); browser replay → 401. |
| 3 | Fresh login works | Integration `AFreshSignInWorksAfterSignOutWhileTheOldCookieStaysDead` (new cookie and token, mutation succeeds, revision +1, old cookie still dead); browser test signs in again and opens My Records. |
| 4 | Expired and revoked sessions cannot be renewed | Integration `TheAuthorityEnforcesAbsoluteExpiryAndRevocationIsPermanent` against the real Authority (absolute expiry passes in real time → `SESSION_EXPIRED`; a revoked digest stays revoked and cannot be registered again; another user gets `SESSION_IDENTITY_MISMATCH`); `SigningInAgainOrAsSomeoneElseEndsThePresentedSession` (presenting a dead cookie at sign-in revives nothing); unit `AbsoluteExpiryIsNeverExtendedByActivity`, `IdleSessionStopsAuthenticatingAndDoesNotRecover`, `CreatedSessionValidatesAndRevocationIsImmediateAndPermanent`; contract `AnExpiredOrForgedSessionCookie…` (G22 suite). No renewal endpoint exists. |
| 5 | Parallel API instances observe revocation | Integration `ParallelApiInstancesObserveTheSameRevocation`: sign in through instance A (5180), sign out through B (5181) → refused on both; and the reverse. |
| 6 | Storage failure fails closed | Integration `WhenTheSessionStoreIsUnavailableCookieRequestsFailClosedAndTheSessionSurvives` (AuthorityDb paused: session probe, mutation and logout all `503 SESSION_STATE_UNAVAILABLE`, no cookie deleted, nothing written; after recovery the session works and signs out); contract `WhenTheSessionStateCannotBeReadEveryCookieRequestFailsClosedWithoutEndingTheSession`, `NoCookieIsIssuedWhenTheSessionCannotBeRegistered`; browser `a sign-out the server cannot confirm keeps the user signed in and says so`. |
| 7 | Browser flows work | `src/frontend/e2e/session-revocation.spec.ts` (2 tests) plus the full browser suite (sign-in, sign-out and every workflow through real cookie sessions). |
| 8 | G22 still works | G22 contract suite (22) with the session registry in place; G22 integration suite (6); `session-csrf.spec.ts`. One G22 contract test was adjusted: it reused a session after signing it out, which G23 now (correctly) refuses; it now uses a second session for that step. |
| 9 | Existing workflows still function | Full integration suite and full browser suite on a fresh disposable stack (results below). |
| — | Fixation / rotation | Contract `SignInRegistersAFreshSessionAndRevokesTheOneTheBrowserPresented`; unit `ANewSignInRevokesTheSessionTheBrowserPresented`; integration `SigningInAgainOrAsSomeoneElseEndsThePresentedSession`. |
| — | Secrets never in logs or databases | Contract `TheSessionSecretAndItsDigestNeverReachTheLogsOrTheBrowserBody`; integration `SessionStateLivesOnlyInTheAuthorityAsDigests` (column inventory of `authenticated_sessions`, 32-byte digests, no session table in BusinessDb as seen by the hostile business DBA container). |
| — | Legacy and unregistered tickets | Contract `ATicketWithoutASessionSecretIsNotASession` (pre-G23 cookies are refused without an Authority call), `AnUnregisteredSessionSecretIsNotASession`. |

**Local results (branch head before the PR, 2026-10-24):** Authority unit 119/119 (11 new); business contract 211/211 (9 new G23, 22 G22); architecture 66/66; integrity 34/34; frontend unit 184/184; Stage B integration on a fresh disposable stack 112/112 (7 new G23, 6 G22); browser 85 passed, 4 skipped (existing conditional skips), including both new specs. Locally the disposable stack ran with a scratchpad override that moves its two pinned subnets (172.30.x, still held by the retired project's preserved networks on this machine) to 172.33.x with matching `pg_hba` copies. Because the harness recreates `api` with the tracked compose files only, the one test that does so (`RecordLifecycleTests.WithTheBusinessLockBypassed…`) was excluded locally. Hosted CI runs every test on the tracked files unchanged; hosted results are recorded in the pull request and in C-63.
