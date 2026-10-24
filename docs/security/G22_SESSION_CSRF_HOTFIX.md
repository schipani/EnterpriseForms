# G22 — Session CSRF double-submit enforcement (security hotfix)

- **Decision:** C-59. The finding was recorded as C-58 on the Phase C7 branch, where the M0 browser tests found it on 2026-10-23.
- **Branch:** `hotfix/g22-session-csrf`, from `main` 4c00ce7. It contains only this correction.
- **Status:** corrected and regression-tested (§4). Production security assurance still requires independent review of the authentication pipeline (PB-08/PB-12). This document is not that review.

## 1. Defect

Browser sessions are BFF cookies (scheme `Session`) whose ticket carries a random per-session claim `ef:csrf`. The SPA's request coordinator sends that value as `X-CSRF-Token` on every non-GET request. `UseSessionCsrfProtection` was meant to reject unsafe cookie-session requests that lack the matching header, but:

* the default authentication scheme is the JWT bearer (`AddAuthentication(Scheme)`), so `UseAuthentication` authenticates only the bearer;
* the session cookie is authenticated later, by the `AuthenticatedUser` authorization policy (`AddAuthenticationSchemes(Scheme, SessionScheme)`), at the endpoint;
* the CSRF middleware runs between the two and read `HttpContext.User`. For cookie requests that was always the anonymous principal, so the check never ran.

Observed on the development stack: `POST /api/v1/session/logout` and `POST /api/v1/form-records/field-search` were accepted with a session cookie and no token. Every request was still fully authorized (no authorization or tenant impact). `SameSite=Lax` limited cross-site exploitation, but same-site and legacy-browser paths remained.

## 2. Correction

One change, inside the existing middleware (`BusinessAuthentication.UseSessionCsrfProtection`):

* For every non-safe method (anything but GET, HEAD and OPTIONS), the middleware authenticates the session scheme itself. The cookie handler caches the result, so the policy does not authenticate twice.
* When a **valid session cookie** is present, the request must carry that session's own token, compared in fixed time. This applies **whether or not a bearer is also present**, so adding an `Authorization` header cannot switch the check off.
* A request without a valid session cookie is not a cookie session. Bearer clients keep their contract, and an expired or forged cookie is rejected by authentication (401).
* The rejection is unchanged: `403` with only `type`, `title` (`CSRF_TOKEN_INVALID`), `status` and `detail`. No token, claim or session detail is returned or logged.
* Nothing else changes: no route exemptions; session issue, logout, cookie flags, scheme order, Authority and worker authentication are untouched. Signing in again over a live session (development login) now requires that session's token, which the SPA already sends.

## 3. Residual risk found while testing (not part of this fix): G23

Sessions are stateless cookie tickets. Logout clears the browser's cookie but keeps no server-side record, so a cookie captured before logout stays valid until its ticket expires (at most `SessionHours`, 8 h in development). CSRF is not involved: the token of a session that ended cannot be used with a new session, which the tests below cover. Server-side revocation needs a session store or revocation list, which is an authentication-architecture change. It is recorded as **G23** for an owner decision.

## 4. Tests

| Suite | What it proves |
|---|---|
| `tests/features/EnterpriseForms.Api.ContractTests/SessionCsrfTests.cs` (22 cases, in-process real pipeline; cookies in the host's own ticket format; store and Authority on closed ports, so nothing can be mutated) | Every unsafe method (POST, PUT, PATCH, DELETE; session, preferences, records, upload sessions, designer) is rejected without a token, or with a wrong, empty, truncated or other session's token, **before** the endpoint. The correct token passes and reaches the endpoint. A rejected logout leaves the session working. An earlier session's token fails for a new session. Safe methods need no token. Bearer-only requests keep their contract. Bearer + cookie without a token is rejected. Expired and forged cookies get 401. Origin and `Sec-Fetch-Site` (cross-site, same-site, same-origin) cannot replace the token. Error bodies hold only the fixed fields; the token appears in no log line. **Against the unfixed middleware, 18 of 22 fail** (every negative case); the four positive cases pass in both. |
| `tests/integration/EnterpriseForms.StageBTests/Tests/SessionCsrfTests.cs` (6 cases, real stack: development logins, Authority, both databases) | The same rules with real sessions. The rejected preference update leaves its revision unchanged, and the correct token increments it. Logout, then sign-in, makes the earlier token useless. Re-login over a live session needs its token. A bearer works without a token, while bearer + cookie without one is rejected. |
| `src/frontend/e2e/session-csrf.spec.ts` (browser, real stack) | A UI sign-in session: direct requests without, or with a wrong or cross-site, token are rejected and change nothing. The token works. App navigation and UI sign-out work. After a new sign-in the old token is refused. |
| Existing hosted integration (100) and browser (93 on C7; 86 + this spec on `main`) suites | Login, logout, record editing, lifecycle, sharing, documents, uploads and designer publishing still work with the token the coordinator sends. Every integration test authenticates with bearer tokens, so the bearer contract is exercised throughout. |
| SEC-P01..SEC-P12 (`security-gate-a`) | Authority and workload authentication unaffected. |

Hosted results are recorded in `docs/decisions/PHASE_C_DECISION_LOG.md` C-59 and in the pull request.
