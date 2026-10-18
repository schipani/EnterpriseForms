# ADR-PB-008 — Entra Workforce Identity, Verified Step-Up and Controlled Recovery

- **Status:** Accepted
- **Date:** 2026-10-17
- **Finding:** PB-08, authentication, step-up and recovery
- **Approved by:** Product owner, accepting the recommended Microsoft Entra ID workforce-first approach
- **Normative standards:** `docs/identity/PB08_IDENTITY_ASSURANCE_STANDARD.md` and `docs/testing/PB08_IDENTITY_SECURITY_TESTS.md`
- **Implementation status:** Design requirement, not an existing operational Entra configuration or validated integration.

## Context

The enterprise forms platform processes sensitive data under a Level 2 hostile-BusinessDb threat model. Identity verification, organizational membership, record authorization, authentication-strength step-up, and recovery must have distinct trusted owners. An apparently valid application session, email address, or MFA claim must not implicitly confer data access or prove phishing-resistant authentication.

## Decision

1. **Initial production provider:** Microsoft Entra ID **workforce** identities, integrated over OpenID Connect Authorization Code flow through the .NET 10 server-side BFF. Keep an `IIdentityProvider`/OIDC adapter boundary; avoid dependence on React or record modules. Other provider onboarding, including Entra External ID, is out of scope for V1 pending a separate provider assurance review.
2. **Identity boundary:** Validate signature and token audience, issuer, lifetime, nonce/state and OIDC protocol requirements. Bind account identity to immutable `(issuer, subject)`; for Entra directory-spanning correlation use verified `(tenant ID, object ID)` when available. Never use an email, username, UPN or display name as a security key; never merge identities merely because an email matches.
3. **App organization is not an Entra tenant:** Explicitly map permitted issuer/tenant/account identities into application Organizations. One person may have multiple permitted organizations, with different roles. Only the independent Authorization Authority may approve effective organization membership, ownership, teams and record permission. A BFF cookie or organizational choice in the browser is never an authorization grant.
4. **Session model:** Use same-origin secure, HttpOnly, SameSite-appropriate session cookies and CSRF defenses for state-changing APIs, with rotation, sliding/absolute lifetime and server-side session invalidation. No access/refresh tokens in browser local/session storage or a worker. OIDC redirects remain outside the generic request queue.
5. **FIDO2/passkeys:** Organization-level policy may mandate FIDO2/passkey methods if supported and verified by the configured workforce tenant. Use Entra authentication methods policies and Conditional Access authentication strengths, not a generic `mfa` claim. A built-in **phishing-resistant** strength is broader than **FIDO2-only**; when FIDO2-only is mandated, configure and verify an appropriately restrictive policy, not a broad built-in strength that admits other methods.
6. **Verified step-up:** For sensitive actions use Entra Conditional Access authentication contexts and supported claims challenges; validate issuer, audience, `acrs` required context against an **administrator-verified policy binding**, and a separate accepted freshness assertion or explicit recent interaction. Presence of `acrs`, `amr` or `mfa` alone is not independent proof of fresh FIDO2; force a new interactive challenge when evidence is missing/stale/ambiguous and fail closed if the provider cannot supply verifiable evidence. Restrict how long the server remembers step-up and invalidate on tenant switch, revocation and policy changes.
7. **User and organization lifecycle:** First owner bootstrap needs verified issuer binding and one-time privileged setup with audit. Before enforcing organization FIDO2-only policy require two separately controlled eligible Organization Owners or an approved independently controlled emergency recovery route; provider readiness and actual passkey enrollment must be checked. Once policy enters `Enforced`, no silent downgrade or self-service bypass: recovery must meet equivalent authentication assurances, require independent approval, create auditable security commands and be time-bounded. If no compliant recovery is available, protected operations remain blocked.
8. **Organization switching:** Recheck currently authorized membership with Authority, rotate/change server-side context, invalidate browser query caches and document/download capability grants, cancel and discard queued requests and uncommitted uploads from the previous tenant, expire step-up proofs, and enforce tenant- and epoch-bound requests. Backend rejects late responses/commands carrying old context.
9. **Deprovisioning:** Support disabled account/removed group or organization grant, revoked sessions, security-epoch invalidation and current Authority checks for protected operations. Do not rely on token expiry alone to revoke record access. Rate-limit, investigate and audit login, step-up, session creation/end, failed recovery, provider-binding and owner-bootstrap events without storing secrets.
10. **Environment distinction:** Local Docker tests use an explicitly designated OIDC test provider or deterministic adapter, with no path into production; Azure staging exercises actual workforce Conditional Access, FIDO2 and recovery behavior using identities/accounts configured for the test. Do not claim compliant FIDO2 assurance from mocks.

## Out of scope and follow-up

- A first-release guarantee for Entra External ID or arbitrary OIDC providers, provisioning from SCIM/Graph, or cross-tenant guest authentication-strength claims without a separate compatibility/security test.
- Exact session and step-up timeouts are **configurable policy values** pending PB-09 operational/security targets; do not hardcode permanent browser unlocks.
- Identity authentication does not grant implicit Designer access to record data, OrganizationOwner access to all records, or SystemAdministrator decryption capability.

## Release gates

- Tests demonstrate correct issuer and tenant/object binding; CSRF/session fixation protections; forced reauthentication for stale step-up; blocked weaker MFA; FIDO2-only restrictions; two-person recovery; revocation while browsing/uploading; multi-org isolation and fail-closed provider/Authority outage.
- Azure staging security evidence verifies the actual Conditional Access policy, authentication strengths, claims challenge and issued claims against intended sensitive operations. See `docs/testing/PB08_IDENTITY_SECURITY_TESTS.md`.
- No production signoff until the independent PB-03 Level 2 adversarial security gate passes.
