# PB-08 — Workforce OIDC Identity and Session Assurance Standard

**Binding:** ADR-PB-008, `CLAUDE.md` and `CLAUDE-DESIGN.MD`; Stage A security foundation and Stage B Accused user journey.

## Architecture and feature ownership

- `Features/Identity/` owns BFF sign-in, OIDC callback, provider identity mapping, sessions, logout and verified step-up challenges.
- `Features/Organizations/` owns policies and safe organization-selection UI. Authority—not a client-side role list or BusinessDb projection—owns organization permissions, membership and approved security commands.
- `Features/Audit/` records canonical activity events; Trust Authority stores its independently recoverable security-command journal and enforces the current permission/epoch barriers.
- Keep `IIdentityProvider` provider-neutral at the application boundary. Entra workforce OIDC implementation lives in Infrastructure/Identity; test-identity adapter may be configured **only** under developer and integration-test profiles. Reject test-only issuer/audience in staging and production.
- Use an explicit allowlist of accepted workforce Entra tenant IDs/issuers and defined organization-provider binding. Identity normalization requires immutable issuer+subject (or Entra tenant+object) identifiers; never match accounts by email.

## Safe login / bootstrap

1. Unauthenticated browser performs server-managed OIDC authorization-code flow with server-validated state, nonce, PKCE as appropriate, redirect URI, audience/issuer and token signature. Only BFF handles tokens; browser receives a session cookie.
2. Resolve trusted provider identity, map to one or more explicitly bound Organizations, and obtain current allowed membership from Authorization Authority.
3. If first owner account, require separately authorized onboarding/setup, auditable creation and a verified recovery route before high-assurance enforcement. No automatic owner assignment based solely on first successful sign-in.
4. Issue server-session ID with rotation and expiration; use Secure+HttpOnly cookies, SameSite policy appropriate to redirect flow, session fixation protection, anti-CSRF token for unsafe HTTP methods, CSP and HSTS in non-local environments. Restrict BFF session stores by service identity and protect them during restore.
5. After sign-in, present permitted organization choices if more than one; on selection/switch reauthorize, rotate active context, cancel queued work and invalidate tenant-scoped TanStack Query caches, capabilities and step-up grants.

## High-assurance actions

- Organization requirements distinguish **passwordless**, **phishing-resistant**, and **FIDO2/passkey-only**; these are not equivalent labels.
- Entra authentication methods policy enables supported user methods; Conditional Access authentication strength specifies permissible combinations. Store only verified binding identifiers and validation status in the Authority, never a client-asserted authentication-method flag.
- For changing encryption floors, sensitive record unmask/download, owner transfer, organization security policy, recovery or high-impact sharing, require fresh step-up when the applicable policy requires it. Use Entra authentication context and claims challenge (`acrs` value) mapped to an independently provisioned policy. Verify trusted token claims in the proper token context for the BFF, strong provider configuration, and session freshness; never accept `amr`, `mfa`, generic `acrs` or a UI passkey icon alone as proof of a *fresh* phishing-resistant/FIDO2 sign-in.
- If freshness cannot be obtained or proven, deny action; require a provider-supported interactive reauth flow. Avoid treating merely cached access to a past Conditional Access context as a newly performed authentication.
- Step-up capabilities are scoped to subject, organization, operation class, session and short validity window, expire on logout, tenant switch, revocation, policy change or recovery, and are rechecked server-side before commit.

## Policy lifecycle, owners and recovery

`Preparing` → `Ready` → `Enforced`. Organization Owners can prepare policy settings only if permitted by Authority, but `Enforced` requires independent second approval and a verified provider readiness/enrollment check. Do not enable an enforcement rule that locks out every owner. `Enforced` is not self-service reversible. An independently approved emergency workflow must have equal or stronger assurance and create recorded Authority-approved commands with user/principal, reason, scoped action, expiration and audit/correlation ID. If compliant recovery cannot be established, quarantine affected actions rather than weakening sign-in requirements.

Recovery must include tenant administrator account restoration/IdP-side proof when relevant, NOT an untrusted application-only password reset. Limit delegated approvals; no single BusinessDb administrator may promote, recover, or assign owners. Track verification results and alert on unusual changes.

## Organization and security epoch boundaries

- Explicit app `OrganizationId` may be different from Entra `tid`; never infer its value from an email domain or take it from a UI-supplied header as authority.
- Enforce currently allowed organization at start *and commit* of protected operations. Long uploads must check at chunk/finalize and server-side release; revoked sessions terminate remaining upload capability, while already committed object bytes follow retention policy.
- Cancel pending queue work; clear tenant-specific caches (query keys include organization/principal/auth epoch); invalidate step-up grants, stale signed download links and protected UI projections on switch or revocation. Do not let late responses from previous tenant repopulate UI.
- Do not use long-lived read permissions from a prior session/organization and do not auto-replay writes after refresh/reauthentication. Offline privilege escalation is forbidden.

## Required quality gates

- Real provider tests for invalid issuer/audience/signing key, cross-tenant collision and email rename, provider outage, revoked user, cookie CSRF/session fixation, denied weaker strength, expired challenge, wrong `acrs`, revoked `Enforced` bypass attempts, recovery/two-approver decisions and multi-org switching during uploads.
- In Azure staging, inspect Microsoft Entra tenant Conditional Access policies, authentication strengths, license/environment availability, actual issued claims and interactive challenge behavior. Test fail-closed when configuration drifts from approved binding.
- Use synthetic test identities/records; do not log identity secrets, assertion content or plaintext protected values.

## References for implementers

- Microsoft Learn: https://learn.microsoft.com/en-us/entra/identity/authentication/concept-authentication-strength-how-it-works
- Microsoft Learn: https://learn.microsoft.com/en-us/entra/identity/authentication/concept-authentication-strengths
- Microsoft Learn: https://learn.microsoft.com/en-us/entra/identity-platform/claims-challenge
- Microsoft Learn: https://learn.microsoft.com/en-us/entra/identity-platform/access-tokens
