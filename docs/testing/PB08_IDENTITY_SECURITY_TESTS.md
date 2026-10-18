# PB-08 — Identity and Account Recovery Security Acceptance Tests

**Status:** Test definitions, **not executed results**. Executable test implementations and signed CI/Azure evidence are required under ADR-PB-003.

| ID | Adversarial scenario | Expected result |
|---|---|---|
| PB08-I01 | Signed ID token has wrong issuer or audience | Reject session and alert; no account/organization exposure |
| PB08-I02 | Same email appears in two issuers, or email/UPN changes | Never merge principals or grant old user permissions |
| PB08-I03 | Entra tenant ID and signing key issuer mismatch | Reject identity/token |
| PB08-I04 | First user attempts to self-assign OrganizationOwner | Deny unless approved bootstrap authority command |
| PB08-I05 | Cookie session is fixed/replayed after login | Rotate/revoke IDs; stale cookies denied |
| PB08-I06 | Cross-site unsafe POST without required CSRF proof | Reject without side effects |
| PB08-I07 | Browser attempts to read session tokens | OAuth/refresh tokens unavailable to frontend/worker storage |
| PB08-I08 | User presents ordinary MFA for FIDO2-only organization | Deny as insufficient assurance |
| PB08-I09 | User presents phishing-resistant CBA where policy requires specifically FIDO2 | Deny unless independently approved as an allowed policy alternative |
| PB08-I10 | Correct `acrs` but auth too old for sensitive action | Require fresh approved reauthentication; no silent bypass |
| PB08-I11 | Authentication-context claim lacks verified policy binding | Deny sensitive operation; alert on configuration drift |
| PB08-I12 | Fresh challenge interrupted or issuer unavailable | Fail closed; no success action |
| PB08-I13 | Enforced FIDO2 change requested by sole Owner | Block activation until readiness and independent second approver |
| PB08-I14 | Emergency recovery without independent verification | Deny and audit; do not downgrade enforced policy |
| PB08-I15 | Revoked user continues using open record page | Subsequent protected read/write denied; cache purged |
| PB08-I16 | User switches organizations with upload in flight | Stop old-org capabilities and prevent cross-org content commits |
| PB08-I17 | Old-org dashboard request finishes after switch | Discard response; no protected data displayed |
| PB08-I18 | Authentication provider unavailable during required step-up | Deny sensitive action with safe, accessible error |
| PB08-I19 | Session revoked while a document is being processed | Only authorized finalization/release; fail closed and quarantine otherwise |
| PB08-I20 | Entra identity disabled but token remains valid | Current independent membership/epoch prevents protected access |
| PB08-I21 | Test identity adapter enabled in production | Fail deployment/startup and alert |
| PB08-I22 | Organization policy changes authentication strength | Invalidate step-up assertions, require revalidation |
| PB08-I23 | Provider guest/external identity lacks proved strength semantics | Fail closed for sensitive actions pending explicit provider approval |
| PB08-I24 | Audit write/authority evidence unavailable for recovery/admin change | No unrecorded privileged operation; follow fail-closed policy |

Tests PB08-I01–PB08-I24 (formatted above as `PB08-Ixx`) require feature-owner, build SHA, deployment environment, input identity, observed outcome, trace reference and independent acceptance in the security evidence register. Unit tests alone cannot prove Entra Conditional Access strength or freshness; conduct real interactive Azure staging checks.
