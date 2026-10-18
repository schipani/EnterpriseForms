# CLAUDE-DESIGN.MD — Enterprise Forms Web Experience & UI Architecture

> **Final pre-build UI decision ADR-PB-014 (accepted):** Before scaling the complete 69-screen catalog, implement and review protected internal gallery PG-069 and three working representative journeys: PG-006 dashboard, PG-010/016–021 Accused records/addresses/documents, and PG-029/033/037 designer. Default near-black primary buttons and visible checkboxes, safe top-left organization logo, accessible token-pair themes/text scaling, WCAG 2.2 AA engineering target and actual 320/768/1440 px + 200%/400% zoom/keyboard/NVDA/VoiceOver verification. Use safe validated tenant CSS tokens, not arbitrary tenant CSS. The UI remains replaceable without .NET/Authority changes. Require manual design/accessibility signoff plus executed PB-14 tests, not static mockups. Reference `docs/decisions/ADR-PB-014-ENTERPRISE-UI-DESIGN-VALIDATION.md`, `docs/ui/PB14_ENTERPRISE_DESIGN_VALIDATION_STANDARD.md` and `docs/testing/PB14_UI_DESIGN_ACCEPTANCE_TESTS.csv`.

> **Data lifecycle decision ADR-PB-013 (accepted):** First production release includes feature-owned Import Center, Export Center, and Organization Offboarding after Stage A/B, with independently refreshing progress/error cards, keyboard-accessible mapping and review flows, durable background operations, independently authorized encrypted export, and retention/hold-aware closure. Never grant tenant-wide decryption solely because a user is an Organization Owner. All exports/uploads remain in approved Canadian storage. Follow `docs/data-lifecycle/PB13_IMPORT_EXPORT_OFFBOARDING_STANDARD.md`; reconcile any new subpages with the normative 69-screen inventory and ownership matrix. These features remain unimplemented.

> **Release assurance decision ADR-PB-012 (accepted):** Implement an authorized Release Security Dashboard (Administration/Operations feature) backed by protected release-policy and evidence APIs. Each section asynchronously loads, has independent refresh/error/accessibility behavior and shows requirement/test coverage, source and image digests, staging/production checks, independent approvals, expiring noncritical exceptions and blocking findings. No UI action may bypass mandatory Level 2 release controls. Follow `docs/governance/PB12_SECURITY_EVIDENCE_RELEASE_STANDARD.md` and approved Canadian evidence handling. These are specification requirements, not live validated release evidence.

> **Delivery decision ADR-PB-011 (accepted):** All frontend and web/BFF Docker images are compiled/tested in the **private GitHub + GitHub Actions** pipeline, stored in **ACR by immutable digest**, with SBOM and signed verifiable provenance; only that checked digest is promoted to Azure Container Apps via reviewed Bicep. Entra OIDC federation and human deployment approvals are required. Trust Authority deployment is an independently permissioned/reviewed security release, not a web-app workflow step. See `docs/delivery/PB11_CICD_SUPPLY_CHAIN_RELEASE_STANDARD.md`. Do not publish sensitive fixtures, auth cookies or customer logs in hosted CI.


> **Residency decision ADR-PB-010 (accepted):** For first production, customer records, attachments, audit/security evidence, key material/recovery artifacts and customer-bearing telemetry must remain in approved Canadian locations (Canada Central primary preferred, Canada East recovery preferred subject to verified service capability). UI upload workflows, support dashboards, telemetry, exception reporting, frontend CDN/third-party tools and generated document previews must not transmit protected data to unapproved locations. Do not claim any provider handles all identity/support processing only in Canada without evidence. Refer to `docs/decisions/ADR-PB-010-CANADA-FIRST-DATA-RESIDENCY.md`, `docs/privacy/PB10_CANADIAN_RESIDENCY_AND_PRIVACY_STANDARD.md` and data-flow register.

> **Performance decision ADR-PB-009 (accepted):** Build the UI and request coordinator for a 25-organization, 500-active-user initial benchmark envelope, 100K records per organization, 250-field forms, 250 related items per collection, up to 100 documents/record, and 500 MB default / 2 GB controlled uploads (two simultaneous per user). Target p95 loaded/refreshed dashboard card <2s and visible application shell <2s under a defined measured browser profile. Back-end authenticated record read <1s, write <2s and provably complete bounded search <3s are product targets, not authorization bypass permissions. Read `docs/decisions/ADR-PB-009-CAPACITY-PERFORMANCE-RECOVERY-TARGETS.md` and `docs/performance/PB09_CAPACITY_SLO_STANDARD.md`; targets and 99.9% availability, 4h RTO, ≤15m business RPO and zero authorization rollback have not been validated.

> **Pre-build decision ADR-PB-001 (accepted):** One canonical Git repository contains the application, separately deployed Trust Authority source, infrastructure, tests, and all normative specifications. Review `docs/decisions/ADR-PB-001-CANONICAL-REPOSITORY.md` and `docs/decisions/PREBUILD_DECISION_REGISTER.md`. A complete manifest and CI reference/completeness checks are mandatory before build Gate 0. Historical ZIP bundles are not authoritative.

> **Pre-build decision ADR-PB-002 (accepted):** First implementation milestone is **Stage A: prove the independent Level 2 security foundation**, then **Stage B: a complete, working Accused + AccusedAddressList + document-upload vertical slice**. All 69 screens remain in the longer-term feature inventory, but do not scaffold them as the first deliverable. Read `docs/decisions/ADR-PB-002-SECURITY-FIRST-ACCUSED-VERTICAL-SLICE.md` and the pre-build decision register. Stage A must pass before Stage B protected workflows are exposed. Neither stage alone constitutes production security certification.


> **Pre-build decision ADR-PB-003 (accepted):** The protected Accused workflow cannot be enabled until all twelve Stage A `SEC-P01`–`SEC-P12` Docker adversarial checks pass with real service integration and inspectable evidence. UI error/loading/revoked-access states must reflect fail-closed security decisions; no mocked allow authorization. Azure staging with real HSM, Confidential Ledger, identity and network isolation is required for production. See `docs/decisions/ADR-PB-003-MANDATORY-ADVERSARIAL-SECURITY-VALIDATION.md` and `security_validation/SECURITY_VALIDATION_RUNBOOK.md`.

> **Pre-build decision ADR-PB-008 (accepted):** First production sign-in uses Microsoft Entra ID workforce via server-managed OIDC BFF, with provider-neutral identity interfaces. Require validated immutable issuer/subject binding, authoritative organization membership, policy-verified FIDO2/passkey assurance, fresh authentication-context step-up, and independently approved recovery. Tenant switches must invalidate app-shell work, authorization epochs, uploads, query caches and recent challenges. See `docs/decisions/ADR-PB-008-ENTRA-WORKFORCE-IDENTITY-FIDO2-RECOVERY.md` and `docs/identity/PB08_IDENTITY_ASSURANCE_STANDARD.md`.

> **Pre-build decision ADR-PB-004 (accepted):** New unclassified free text and documents are **Confidential encrypted by default**. Only explicitly approved Public/Internal typed fields may be plaintext indexed and searched; V1 does **not** support field-value queries against Confidential/Restricted data. Trusted V1 searches must completely enumerate and verify their authorized candidate set through the independent Authority within a tested bound, or fail with a typed non-partial error. See `docs/decisions/ADR-PB-004-ENCRYPTION-SEARCH-CAPABILITY.md`, `security_hardening/SEARCH_CAPABILITY_MATRIX.csv`, `security_hardening/SEARCH_SECURITY_ACCEPTANCE_TESTS.md`. This tightens but does not replace the Level 2 encryption/search standards.

> **Status:** Implementation specification, approved design direction, not deployed code.  
> **Audience:** Claude Code, frontend and backend engineers, accessibility auditors, security reviewers, DevOps.  
> **Companion:** Read `CLAUDE.md`, `security_hardening/SECURITY_SIMPLIFICATION_STANDARD.md`, `form_model/RELATED_FORM_COLLECTIONS.md`, and `trust_authority/` before implementation.  
> **Precedence:** The Level 2 trust boundaries, data protection, authorization, recovery, and naming requirements in those documents remain binding. This document owns **web experience, design system, application-shell, asynchronous orchestration, and UI-related API contracts**. Do not weaken core security for UX convenience.  
> **Output:** Build a production-grade responsive web client and ASP.NET Core API integration; do not generate a static mockup as the finished deliverable.  
> **Infrastructure decision ADR-PB-005 (accepted):** Frontend and .NET API/worker run in reproducible Docker Compose profiles locally and independently deployable Azure Container Apps images in staging/production. Azure infrastructure is authored in **Bicep**. The Trust Authority requires an independent administrative, network, credential and database boundary; Vault Transit remains development-only and Azure Managed HSM/Confidential Ledger are required in production. See `docs/decisions/ADR-PB-005-DOCKER-AZURE-BICEP-DEPLOYMENT.md` and `docs/infrastructure/PB05_DEPLOYMENT_BLUEPRINT.md`. Do not make frontend styling or worker abstractions dependent on Container Apps infrastructure SDKs.
> **Background work decision ADR-PB-007 (accepted):** Implement one app-shell request coordinator per open tab, bounded priority lanes, a dedicated Web Worker for uploads/supported API calls, TanStack Query per-card refresh, and durable .NET post-ingress processing. Safe main-thread fallback must preserve semantics; SPA navigation continues uploads, but browser close can interrupt them. Never auto-replay protected writes after auth changes; read `docs/decisions/ADR-PB-007-PRIORITIZED-REQUESTS-RESUMABLE-UPLOADS.md`, `docs/architecture/PB07_BROWSER_REQUEST_COORDINATOR_STANDARD.md` and `docs/testing/PB07_REQUEST_UPLOAD_ACCEPTANCE_TESTS.md`.
> **API architecture:** ADR-PB-006 in `CLAUDE.md` and `docs/api/PB06_API_CONTRACT_STANDARD.md` mandate feature-local ASP.NET Core 10 Minimal APIs under `/api/v1`, generated OpenAPI and generated TypeScript clients through the shared request coordinator, standardized Problem Details, cursors, ETags, idempotency, and durable upload/job statuses. Do not expose the internal Authority API.
> **Feature architecture:** ADR-ARCH-001 in `CLAUDE.md` and Sections 8/17 here require a feature-first modular monolith with vertical-slice Clean Architecture. These rules supersede the older layer-first sample folders while preserving the independent Trust Authority and replaceable React client.
> **Screen coverage:** Section 16 is the normative, screen-by-screen specification for **69 routes/workflow surfaces** (including protected operator-only and developer-only screens). It supersedes the initial abbreviated navigation map.

---

## 0. Claude instructions: how to use this document

1. Inspect the existing repository, architecture, packages, API contracts, identity integration, Docker setup, and tests. Reuse compatible established conventions. If the repository has no frontend, implement the architecture below.
2. Read and obey `CLAUDE.md` and all normative referenced security and form-model documents. Do not create separate definitions of record permissions, layouts, or persistence semantics in the web project.
3. Implement in **small, testable vertical slices**. Start with the design tokens, application shell, BFF/session, authenticated widget host, and worker queue; proceed to dashboard, renderer, designer, upload manager, organization branding, and hardened flows.
4. Treat code snippets here as **reference contracts** to adapt to the actual repository, not blindly complete production implementations. Compile, lint, typecheck, integration-test, accessibility-test, and threat-test every deliverable.
5. Use exact database naming convention: PostgreSQL snake_case; entity-based PK names (`organization_branding_id`, `dashboard_widget_definition_id`, etc.); **no one-word database columns** (`logo_object_key`, not `logo`; `brand_display_name`, not `name`; `button_foreground_color`, not `color`).
6. Do not claim a worker continues after all application tabs/windows close. Support intra-app route navigation, resilient retries, resumability where supported, and server-side processing after receipt. Explicitly report limitations.
7. Do not implement a generic fetch call or direct repository access in components. API I/O is orchestrated via the shared app-level request scheduler with bounded concurrency and security-aware cancellation.
8. No frontend package, renderer, designer or design component may directly reference EF Core, Npgsql, SpiceDB, Vault, Azure SDKs, or database table models.
9. Implement complete feature-owned vertical slices, not separate batches of global controllers/repositories/pages. Every PG-### screen has a feature owner in Section 17; automated import and architectural boundary rules apply.

## 0A. First deliverable scope (ADR-PB-002)

Build UI and supporting features **after the Stage A security foundation passes**: an authenticated role-aware dashboard, actual Designer publication of `Accused` and reusable `AccusedAddress`, a functioning record and related-address editor, a multi-document upload/download collection, and share/revoke UX. Every delivered dashboard card loads independently and offers per-card refresh. Uploads continue across SPA navigation and report final server-side outcome; resumability is tested under network interruption. Tenant logo, black default primary buttons, validated accessible color pairs, readable typography and responsive layout must work in this slice. Limit Stage B UI construction to its end-to-end routes; the remaining PG-### screens are backlog, not dummy route placeholders. Preserve per-screen ownership and the Level 2 permissions and confidentiality rules. See `docs/decisions/ADR-PB-002-SECURITY-FIRST-ACCUSED-VERTICAL-SLICE.md`.

## 0B. Security proof gate before protected UI enablement (ADR-PB-003)

Claude must keep protected Stage B routes and related APIs disabled until Stage A security evidence qualifies the exact deployed test commit/images. Implement user-visible access-denied, integrity failure, temporarily unavailable, upload-aborted/revoked and recovery-pending states without leaking protected record details. A client-side role flag, successful login, worker retry, local cache or successful card refresh is not a substitute for fresh server-side authority checks. Revoked access invalidates card queries, active document transfers and upload authorizations as prescribed by the Authority. Do not treat mocked permission providers as production behavior. The UI feature tests must assert that security outages fail closed with an accessible, actionable status rather than a silent empty view. The binding test matrix is `security_validation/SECURITY_PROOF_TEST_MATRIX.csv`; it is not implementation evidence.

## 0C. Encrypted fields and trustworthy search UX (ADR-PB-004)

The React search builder and Designer must consume a **server-authoritative per-field capability DTO**, not infer searchability from field type or client-side classification. Unclassified free text and new documents default to **Confidential application encryption**. Only organization-policy-approved Public/Internal typed fields may present server-supported exact/contains/prefix/range/sort controls. Never show a Confidential/Restricted value as searchable; even exact-match is unsupported in V1. Designer labels must disclose that approved plaintext-indexed fields can be read directly by a privileged BusinessDb administrator; prohibit publishing below the organization encryption floor. Displaying a known, authorized encrypted field on a record detail page is still allowed and requires independent authorization and decryption; this does **not** create permission to query by its value.

For confidential/protected fields use explanatory text **“Encrypted for privacy — not searchable”**, avoid suggesting a disabled control will later run plaintext search. Do not offer global/full-text search across protected values. Search cards and widgets are independently loaded/refreshed through the app queue, but every refresh/retry uses the same authority-bound verification and scoped cancellation rules. API can return typed `SEARCH_FIELD_NOT_ALLOWED`, `SEARCH_OPERATOR_NOT_SUPPORTED`, `SEARCH_VERIFICATION_LIMIT`, `SEARCH_SNAPSHOT_CHANGED`, `SEARCH_VERIFICATION_UNAVAILABLE`; render a safe accessible explanation with retry/narrow-scope guidance where appropriate, **never** a false “no results” or a partial-results page if completeness is unproven. The client must not reveal whether a hidden predicate field exists. Show accepted snapshot/verified-scope context only when authorized. Invalidate paginated results, tenant card caches and user cursors after ACL revocation/policy/organization changes. Query parameters, analytics and telemetry may not leak plaintext confidential predicates.

The Search feature owns UI/API integration, capability description, error display, and pagination; Forms owns published field policy, Organizations owns floor approval, Authority owns verifiable candidate enumeration and current authorization, and Record engine owns signed content/typed storage. No feature may implement its own SQL shortcut. Full acceptance details: `security_hardening/SEARCH_SECURITY_ACCEPTANCE_TESTS.md` and `docs/decisions/ADR-PB-004-ENCRYPTION-SEARCH-CAPABILITY.md`.

## 1. Technology decision (ADR-UI-001)

### Selection

| Layer | Required choice | Rationale |
|---|---|---|
| Backend and BFF | **ASP.NET Core / .NET 10** | Domain/application logic, federated OIDC, same-origin session, API authorization |
| Web UI | **React + TypeScript + Vite** | Highly modular composition, large accessible component ecosystem, fine-grained widget loading |
| Styling | **Tailwind CSS v4** + CSS custom properties | Utility-based mobile-first design, runtime-safe theme token layer |
| Headless accessibility | **Radix UI primitives** or equally mature accessible primitives; shadcn-style components can be *owned/copied* into a local package | Accessible behavior with styling controlled in-house, no external visual dependency |
| Forms | React Hook Form + Zod where appropriate; JSON schema/typed validator generated from authoritative form contract | Predictable controlled dynamic forms and validation |
| Client server-state | TanStack Query (React Query) | Independent queries, invalidation and refresh for dashboard widgets |
| Client navigation | TanStack Router or React Router; select one after repository inspection | SPA navigation without terminating app shell or upload coordinator |
| Shared ephemeral UI state | Small Zustand or React Context store; **not** another copy of server business state | Upload tray, notifications, side-nav, organization theme state |
| API contract | OpenAPI with generated TypeScript client; typed DTOs | Framework-independent backend contract and testability |
| Client operation coordinator | Dedicated **Web Worker** with typed request protocol and bounded lane scheduler; app-shell manager + optional cross-tab coordination | Network operations isolated from component lifecycle; navigation resilient |
| Server background work | .NET `BackgroundService` and reliable transactional outbox / durable job store | Scanning, encryption, attachment finalization, projections and notifications after ingress |
| Database | PostgreSQL 18 (existing core) | Existing Level 2 requirements; no client schema duplication |
| Auth | Entra/OIDC via BFF; provider-approved FIDO2 assurance | No password UX, no tokens in browser storage |
| Hosting | Docker images, reverse proxy, same-origin `/api` | Portable deployments, consistent auth and security headers |
| Local tools | Visual Studio Code, `dotnet`, Node LTS, package-manager lockfile, Docker Compose | Portable reproducible build and debug |

**Why not Angular?** Angular is viable, but for this application the form renderer, designer, role-aware dashboard widgets, and replaceable UI are best delivered as decoupled React packages. Angular's strengths do not require choosing it for this product. **Why not Next.js by default?** Most pages are authenticated, permission-sensitive, highly interactive and cannot use generic edge/shared caching. A Vite SPA behind a same-origin .NET BFF reduces SSR auth/cache complexity. Introduce SSR separately only if a concrete SEO/public-rendering use case emerges.

**Replaceability is a hard requirement:** the .NET Domain/Application/Infrastructure layers and OpenAPI endpoints must work if React is replaced with Angular, another SPA, mobile clients or a new visual design. The React renderer and designer are separate packages; the Tailwind theme is a presentation implementation only.

## 2. Visual identity and enterprise UX

### 2.1 Default visual direction: quiet, credible, accessible

- Visual character: professional, calm, high-trust, restrained use of color, generous whitespace, distinct information hierarchy. Avoid novelty gradients, neon, glassmorphism and decorative motion.
- **Default primary button:** near-black `#111111` background, white `#FFFFFF` text, clear focus indicator; not pure-black everywhere else.
- Default page surface `#F8FAFC` with white `#FFFFFF` cards; primary copy `#111827`, secondary copy `#334155`, border `#CBD5E1`; avoid light gray text on white. Card radius 12 px, button radius 8 px, subtle borders, minimal shadows.
- Default typography: system sans / Inter-like stack using locally provisioned fonts only if licensed; body 16 px equivalent, line height ~1.5, headings ~20–32 px responsive; use rem for zoom and user font settings.
- Primary buttons: solid dark; secondary buttons: white surface, dark text, 1 px dark border; tertiary/ghost: underlined or visible on hover **and focus**; destructive: text label + icon, confirmation for irreversible action. Avoid color as only status indicator.
- Checkboxes: 18–20 px visible control with clear black outline; checked state dark fill and white checkmark; keyboard-visible focus ring and accessible label; native checkbox semantics provided by headless primitive.
- Minimum comfortable targets: aim at least 44×44 CSS px for frequent/touch controls; WCAG 2.2 Level AA baseline is smaller for certain target-size conditions, but design for practical touch use.
- Responsive navbar includes organization logo **top-left** and organization display name as text fallback. The logo may not obscure the app name or consume excessive mobile width.
- Persistent side navigation on desktop; collapsible drawer/mobile tab or compact navigation on narrow screens. Breadcrumbs for form/record editors.
- Avoid information leakage in titles, nav badges, toast contents, notifications, logs and browser title: never show unauthorized record details.

### 2.2 Shell wireframe

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ [Org Logo] Organization  | Workspace / Breadcrumb      🔔 Uploads (2) [Me] │
├───────────────────┬──────────────────────────────────────────────────────────┤
│ Dashboard         │  Welcome back                                     [Help]│
│ My Records        │  ┌────────────────────┐ ┌──────────────────────────────┐│
│ Form Catalog      │  │ My Assigned Records│ │ Upload Activity  ↻          ││
│ Form Designer*    │  │             ↻      │ │ 2 uploading, 1 scanning       ││
│ Teams*            │  └────────────────────┘ └──────────────────────────────┘│
│ Organization*     │  ┌────────────────────┐ ┌──────────────────────────────┐│
│ Security*         │  │ Recent Forms    ↻  │ │ Latest Activity          ↻    ││
│ Audit*            │  └────────────────────┘ └──────────────────────────────┘│
│                   │  Only cards authorized for the current user are issued  │
├───────────────────┴──────────────────────────────────────────────────────────┤
│ Upload tray (persistent within SPA): contract.pdf 64% [Pause] [Cancel]      │
└──────────────────────────────────────────────────────────────────────────────┘
* Navigation items require explicit granted permissions, not UI-only role guesses.
```

At 320–480 CSS px: top bar compresses logo/name; nav becomes drawer; cards occupy one column; the upload tray is a compact accessible panel; no horizontal body overflow. The existing **4-column form-designer grid** remains separately governed by `CLAUDE.md` (desktop 4, tablet 2, mobile 1; explicit column spans and row breaks).

### 2.3 Organization logo and theming

The `OrganizationOwner` may upload a logo and choose from a safe set of design-system tokens, **not arbitrary CSS**. Store organization branding alongside existing organization preferences (do not invent another tenant identity model).

- Allowed assets: SVG only after rigorous sanitizer/security review; recommended v1 **PNG/WebP** with server-side decode/re-encode, e.g. max 2 MiB and reasonable dimensions. Reject scripts, external references, animated/active payloads, oversize decompression, dangerous polyglots; private authenticated object storage. Generate optimized variants, preserve original under controlled retention. Accessible alternative text is the organization name; brand is still legible if logo fails.
- Logo in upper-left, scoped to authenticated tenant. Never let a client-supplied query string choose another tenant logo or theme. No cross-tenant image URL leakage.
- Branding settings use an organization-level revision, who/when audit event and optimistic concurrency. Only OrganizationOwner or explicitly delegated branding administrator may update it. Changes propagate to open sessions by revision invalidation, not page reload.
- The Organization Owner selects background + foreground *pairs* from prevalidated accessible presets, or enters custom hex values validated on the server and client. Reject a theme if **any** required state violates contrast/visibility: normal, hover, focus, pressed, selected, disabled affordance, error, success, warning, link on surfaces, checkbox checked/unchecked and input borders.
- Prefer two approved color modes: `Light` (initial default) and `Dark` (optional), with role-aware organization branding independent of light/dark. A near-black primary button on a dark background must have a visible contrasting outline/surface; do not assume it remains perceivable automatically.
- Permit organization default text size `Normal` (100%), `Comfortable` (112.5%), `Large` (125%), `ExtraLarge` (150%). User may select browser/default/explicit size subject to the previously established preference rules; never override browser zoom.
- Allow button radius/density only from vetted presets. No freeform HTML, style tag, script, remote font or raw organization CSS injection.

**Suggested PostgreSQL extension** (illustrative, integrate with existing organization/tenant naming, do not blindly duplicate tables):

```sql
CREATE TABLE organization_brandings (
    organization_branding_id UUID PRIMARY KEY DEFAULT uuidv7(),
    tenant_id UUID NOT NULL UNIQUE,
    organization_logo_object_key TEXT NULL,
    logo_alternative_text VARCHAR(200) NULL,
    primary_button_background_color CHAR(7) NOT NULL DEFAULT '#111111',
    primary_button_foreground_color CHAR(7) NOT NULL DEFAULT '#FFFFFF',
    secondary_button_background_color CHAR(7) NOT NULL DEFAULT '#FFFFFF',
    secondary_button_foreground_color CHAR(7) NOT NULL DEFAULT '#111111',
    secondary_button_border_color CHAR(7) NOT NULL DEFAULT '#111111',
    organization_default_text_scale_percent NUMERIC(5,1) NOT NULL DEFAULT 100.0,
    organization_color_mode VARCHAR(12) NOT NULL DEFAULT 'Light',
    branding_revision BIGINT NOT NULL DEFAULT 1,
    branding_modified_on TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    branding_modified_by_user_id UUID NOT NULL,
    CONSTRAINT ck_organization_default_text_scale
      CHECK (organization_default_text_scale_percent IN (100.0, 112.5, 125.0, 150.0))
);
```

Keep database constraints for shape and allowed values, but enforce WCAG pairwise contrast and complete state coverage with a shared server-side validation service, along with unit tests. Use proper same-tenant foreign keys, permissions, signing, and audit requirements from the core specification. Avoid exposing unapproved branding while pending verification.

### 2.4 Accessibility and AODA

- **Target WCAG 2.2 AA** for all product UI, which covers and exceeds many applicable criteria from Ontario's AODA public website WCAG 2.0 AA obligations; compliance applicability varies by organization and content. Independent audit is required before asserting legal compliance.
- Minimum color contrast **4.5:1** for normal text, **3:1** for large text, **3:1** for essential graphical control boundaries/states; test the actual rendered states, not only stored colors. Aim for visible focus contrast and avoid relying on color alone.
- Support keyboard-only flows, visible focus, consistent landmarks, screen readers, accessible labels/field errors, dialog focus trap/restore, live-region notices (non-spammy), table semantics, predictable tab order, skip-to-content, input autocomplete, reduced motion, and 200%+ text resize without clipped content.
- 4-column form layout must reflow at narrow viewport and up to 400% zoom; supporting documents, conditional fields and repeated child lists require meaningful screen-reader announcements.
- Every server-data-bound section on every page (not only dashboard cards) must load independently where practical and have a scoped refresh or reload action; purely static text/decorative sections need no artificial refresh control. Every card's refresh button must have an explicit accessible name (e.g. `Refresh My Records`), show busy state only within its card, expose `aria-busy`, and not steal keyboard focus after refresh.
- Upload progress: `progressbar` with name and percent, queued/scanning/ready as accessible status text; error is announced without automatically dismissing critical information.
- RTL layout must work for Arabic without reversing meaningful icon direction or keyboard order; translations are for UI/form labels only, never user-entered values or documents.
- Add automated axe-core checks and manual NVDA, VoiceOver, keyboard-only and zoom tests. **Passing automated contrast checks alone is not an AODA certification.**

### 2.5 Design token contract (Tailwind)

Keep compile-time Tailwind classes static. Dynamic organization choices become vetted CSS custom properties, not interpolated `bg-${value}` utilities or arbitrary styles. Serve a validated tenant-scoped theme stylesheet or CSP-compatible runtime CSSOM update; never inject arbitrary stylesheet text from tenant data.

```css
/* packages/design-tokens/src/theme.css — reference starter */
@import 'tailwindcss';

:root {
  --ui-primary-bg: #111111;
  --ui-primary-fg: #ffffff;
  --ui-secondary-bg: #ffffff;
  --ui-secondary-fg: #111111;
  --ui-secondary-border: #111111;
  --ui-surface: #ffffff;
  --ui-page: #f8fafc;
  --ui-text: #111827;
  --ui-muted-text: #334155;
  --ui-border: #cbd5e1;
  --ui-focus: #1d4ed8;
  --ui-text-scale: 1;
  color-scheme: light;
}

@theme inline {
  --color-app-primary: var(--ui-primary-bg);
  --color-app-primary-text: var(--ui-primary-fg);
  --color-app-surface: var(--ui-surface);
  --color-app-page: var(--ui-page);
  --color-app-text: var(--ui-text);
  --color-app-border: var(--ui-border);
}

html { font-size: calc(100% * var(--ui-text-scale)); }
body { background: var(--ui-page); color: var(--ui-text); }
```

Implement components with tokens, e.g. `bg-app-primary text-app-primary-text` and `focus-visible:ring-2 focus-visible:ring-offset-2`. Build a visual component gallery, light/dark snapshots, theme contrast validator and screenshot regression suite. Text scale must not cause double scaling, layout clipping or fixed pixel-height controls.

**Minimal reusable button reference:**

```tsx
// packages/ui/src/Button.tsx — illustrative; add loading/disabled/forwardRef in implementation
import type { ButtonHTMLAttributes } from 'react';

type ButtonVariant = 'primary' | 'secondary' | 'ghost';
type Props = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant;
};

const classes: Record<ButtonVariant, string> = {
  primary: 'bg-app-primary text-app-primary-text border border-transparent',
  secondary: 'bg-[var(--ui-secondary-bg)] text-[var(--ui-secondary-fg)] border border-[var(--ui-secondary-border)]',
  ghost: 'bg-transparent text-slate-950 border border-transparent underline-offset-4 hover:underline'
};

export function Button({ variant = 'primary', className = '', ...props }: Props) {
  return <button
    {...props}
    className={[
      'inline-flex min-h-11 items-center justify-center gap-2 rounded-lg px-4 py-2 font-semibold',
      'focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-700',
      'disabled:cursor-not-allowed disabled:opacity-60',
      classes[variant], className
    ].join(' ')}
  />;
}
```

This is a design sketch: in production, use prevalidated organization secondary color tokens, explicit pressed/hover states, sound `aria-busy`, visible disabled explanation where useful, and generated component tests. A disabled component is exempt from some contrast requirements, but its purpose should remain understandable.

## 3. User journeys and role-aware landing page

> This section is an overview; the complete, normative route inventory and individual page contracts are in **Section 16**.

### 3.1 First login

1. Unauthenticated requests go to ASP.NET Core BFF OIDC challenge; identity provider controls passwordless/FIDO2. The SPA **never displays a local password form**.
2. BFF validates issuer, audience, subject, organization mapping, assurance and current organization security policy; normal session is HttpOnly + Secure cookie and server-side user session. No access or refresh tokens in `localStorage`, `sessionStorage`, or worker messages.
3. If multiple organizations are authorized, present a neutral organization chooser. Do not permit tenant switching by changing a query parameter; switching requires server-side proof and cleared cached state.
4. Load `/api/v1/bootstrap` with current user, safe organization branding, effective UI preference defaults, current authority epoch/revision and navigation capability flags. Reject invalid/stale settings and fall back to accessible default theme, *not* stale permissions.
5. Navigate to `/app/dashboard`. A brand-new user with no records sees an empty authorized-record state and permitted form catalog; no default display of organization-wide data.
6. Show first-time guidance as a dismissible inline checklist, not an intrusive modal tour.
7. Re-auth/step-up for sensitive actions as required by the existing security policies; app shell does not pretend a login alone authorizes protected record contents.

### 3.2 Landing page differences

Dashboards are **composed from permissions/capabilities** returned by the BFF/authority, not from a single client-side role label. Users may hold multiple roles; merge eligible widgets without duplicates. Examples:

| Capability/persona | Example authorized widgets | Primary action |
|---|---|---|
| Standard User / Reader | My accessible records, assigned work, recent authorized activity | Open permitted record |
| Record Owner / Contributor | My owned/team records, drafts, records shared with me, upload activity | Create/edit record |
| Designer | My forms, drafts requiring review, recent publish events, form usage metadata within authorized scope | Create/edit form |
| OrganizationOwner | Branding/theme, regional defaults, options enabled/disabled, teams, policy notices | Organization settings |
| System Administrator | Ownership-transfer requests, tenant-scoped security operations, audit/health (only where explicitly authorized) | Secure administration |
| Security Auditor | Authorized compliance events and archive health; no default plaintext record access | Review audit timeline |

**Strong privacy rule:** widget counts, lists, suggestions, and errors are computed only over discoverable/authorized resources. Designer does not gain submitted-content access; administrator does not gain automatic read privileges. No cross-tenant counts or forbidden-resource existence hints.

### 3.3 Navigation map

```text
/app/dashboard                        role-aware landing page
/app/forms                            discover/use permitted forms
/app/forms/:formDefinitionId          permitted form summary / create record
/app/records                          authorized search and list
/app/records/:formRecordId            authorized projection / access challenge
/app/records/:formRecordId/edit       version-aware editor
/app/records/:formRecordId/sharing    scoped field/document ACL editor
/app/records/:formRecordId/activity   authorized record audit timeline
/app/designer                         authorized designer catalog
/app/designer/:formDefinitionId       responsive designer/draft/publish
/app/organization/branding            OrganizationOwner logo/theme
/app/organization/preferences         org defaults and language/format catalogs
/app/organization/teams               team management
/app/security/ownership               approved transfer workflow
/app/security/audit                   authorized security audit
/app/settings                         user language/time zone/number/date/text-size
```

The map above is only the initial navigation overview. Section 16 lists every required route, including related-record, document, governance, access-request and operational pages. Legacy `/app/settings` redirects to `/app/settings/preferences`. Routes hide unauthorized navigation but **every API still enforces authorization independently**. Avoid a route-level gate that caches stale `Allow` across revocations.

## 4. Dashboard: independently asynchronous cards

**Hard rule:** Every card/section that fetches server data owns an independent async query and **its own refresh control**. One failed card never blanks the whole page. Use loading skeleton, error state with retry, accessible empty state, data timestamp and per-card refresh.

Card descriptor supplied by the server or selected from a server-authorized allowlist:

```ts
export type DashboardWidgetDefinition = {
  widgetIdentifier: string;            // stable catalog identifier, not arbitrary URL
  widgetTitle: string;                 // localized, safe presentation text
  widgetType: 'record-list' | 'count' | 'activity' | 'upload-status' | 'action-list';
  minimumColumnSpan: 1 | 2 | 3 | 4;
  permittedActions: string[];          // informational only; API re-checks permissions
  dataEndpointKey: string;             // lookup into trusted static endpoint registry
};
```

Do not allow organization admins or Designers to create arbitrary fetch URLs or arbitrary JavaScript widget code. If an extensibility system is ever introduced, isolate third-party UI, apply CSP and a separately reviewed capability contract.

**Reference widget wrapper:**

```tsx
import { useQuery } from '@tanstack/react-query';
import { Button } from '@product/ui';
import { appApi } from '@product/api-client';

export function MyRecordsCard({ tenantId }: { tenantId: string }) {
  const query = useQuery({
    queryKey: ['dashboard', tenantId, 'my-records'],
    queryFn: ({ signal }) => appApi.getMyRecordsSummary({ signal }),
    staleTime: 30_000,
    retry: 1
  });

  return <section aria-labelledby="my-records-title" aria-busy={query.isFetching}>
    <header className="flex items-center justify-between gap-3">
      <h2 id="my-records-title" className="text-lg font-semibold">My Records</h2>
      <Button variant="secondary" type="button"
        aria-label="Refresh My Records" disabled={query.isFetching}
        onClick={() => void query.refetch()}>Refresh</Button>
    </header>
    {query.isPending ? <p role="status">Loading records…</p> :
     query.isError ? <p role="alert">Unable to load records. Try Refresh.</p> :
     <p>{query.data.authorizedRecordCount} records available to you</p>}
  </section>;
}
```

`appApi` above is **backed by the shared request coordinator**, not direct component-level `fetch`. Avoid publishing record counts that are not authorized. The production card includes skeletons, relative/absolute timestamp, empty state, and locale formatting. Queries invalidate on permission changes and organization switch; do not persist sensitive query cache to local storage.

**Asynchronous strategy:** `Promise.allSettled`/independent TanStack queries, not a single `Promise.all` that fails the whole page. When refreshed, only the selected card's query key is refetched. Use prioritized scheduler lanes so long uploads do not starve card reads or interactive saves. Role-specific widget layouts can be configured within a safe built-in catalog, but card data access remains independent of layout metadata.

## 5. Global background request coordinator — NON-NEGOTIABLE

### 5.1 Interpret “background thread and queue” safely

All browser-originated business **reads and writes** enter one logical `IRequestCoordinator` / `IOperationQueue` abstraction. Execution uses an app-shell-owned **Dedicated Web Worker** with a bounded priority scheduler. For unsupported worker operations, use a reviewed main-thread adapter that still enters the same logical queue and preserves its policy. Do **not** serialize all network requests through a single active operation: one huge document would otherwise freeze UI data fetching.

Queue lanes and suggested **initial tuning**, configurable and benchmarked:

| Lane | Priority | Initial concurrency | Examples |
|---|---|---:|---|
| `security` | Highest | 2 | Session verification, challenge state, revocation checks |
| `interactive-read` | High | 4 | User-opened record, card refresh, lookup (authorized) |
| `interactive-write` | High | 2 globally, 1 per resource key | Save edit, share, publish, revoke; per-resource revision locking |
| `background-read` | Normal | 3 | Inactive dashboard cards, prefetched authorized lists |
| `file-transfer` | Low/normal | 2 files, 2–4 chunks/file under connection limits | File chunks, resumed transfer |
| `polling` | Low | 2 | Scan status, durable job status (prefer SSE when appropriate) |

Priorities are **not** authorization bypasses. Apply fairness/aging to avoid starving low-priority work; cap per-origin requests, use exponential backoff + jitter and server `Retry-After`, and respond to logout, tenant switch and access revocation by clearing/aborting operations and caches appropriately.

**Operations carry:** immutable operation UUID, tenant identity from validated bootstrap (never trusted alone), request method/registered endpoint, sanitized payload metadata, resource key, lane, cancellation handle, idempotency key for retryable writes, expected resource revision, authorization epoch when appropriate, timeout, state, and correlation ID. Actual security scope is revalidated by the BFF and authority on every protected operation.

### 5.2 Worker lifecycle and actual browser guarantees

- Instantiate coordinator once at the highest persistent SPA application-shell level **before the authenticated bootstrap API request**, and route even bootstrap through it; route changes/unmounts must **not** terminate the worker or cancel active uploads.
- The dedicated worker survives navigation inside the SPA as long as the app document remains open. Full tab reload, navigation to another origin, browser shutdown, OS process suspension, network loss and device sleep **can interrupt it**.
- **Never promise guaranteed file upload after the last tab closes.** Background Sync/Background Fetch are not sufficiently portable or dependable for long-running uploads; do not use them as the sole mechanism.
- Optional SharedWorker/BroadcastChannel can coordinate several same-origin tabs, but is progressive enhancement, not a requirement or security boundary; browser support, lifetime and race conditions require tests. At baseline, prevent duplicate submissions via server idempotency and upload session ownership.
- Persist **non-sensitive upload session metadata** and resumable chunk receipts to IndexedDB only when permitted. Do not persist bearer tokens, encrypted field plaintext, record DTOs, or document content without separate opt-in threat assessment. For an interrupted upload, the original local `File` may need re-selection; match size and cryptographic content identity before resuming. Do not promise file handles survive browser reload.
- Once all bytes are accepted by the server, durable server-side workers continue virus scanning/encryption/manifest commit after the browser closes. Client displays `Uploading` then `Processing` then `Ready`, and on return queries status.

### 5.3 Request operation state model

```text
Created → Queued → Running → Succeeded
                │        ├→ Retrying → Queued
                │        ├→ Failed (terminal)
                │        ├→ Cancelled
                │        └→ AwaitingUserAction → Queued  (reauth, reselect file)
                └→ Cancelled
```

Requests aren't retried automatically if a mutation is unsafe to repeat. Retryable POST/PUT operations **must** carry durable server-recognized idempotency keys; edits must carry expected revision (`If-Match`/domain revision). A local queued operation is NOT a persisted server commit and must not be shown as success until the backend confirms a durable accepted revision.

### 5.4 Worker contracts (reference TypeScript)

```ts
// packages/work-queue/src/contracts.ts
export type OperationLane =
  | 'security' | 'interactive-read' | 'interactive-write'
  | 'background-read' | 'file-transfer' | 'polling';

export type ApiOperation = {
  operationId: string;
  lane: OperationLane;
  method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  endpointKey: string;               // strictly mapped to trusted API endpoints
  payload?: unknown;                 // validated DTO; never executable code
  expectedRevision?: string;
  idempotencyKey?: string;
  resourceKey?: string;
  timeoutMilliseconds?: number;
};

export type OperationCommand =
  | { type: 'enqueue'; operation: ApiOperation }
  | { type: 'cancel'; operationId: string }
  | { type: 'clear-security-context' };

export type OperationEvent =
  | { type: 'succeeded'; operationId: string; result: unknown }
  | { type: 'failed'; operationId: string; errorCode: string; retryable: boolean }
  | { type: 'progress'; operationId: string; completedBytes: number; totalBytes: number }
  | { type: 'state'; operationId: string; state: string };

export interface IRequestCoordinator {
  execute<TResponse>(operation: ApiOperation, signal?: AbortSignal): Promise<TResponse>;
  cancel(operationId: string): void;
  subscribe(listener: (event: OperationEvent) => void): () => void;
}
```

Implement a versioned message envelope between browser main thread and Web Worker. Restrict endpointKey to a compiled allowlist (no arbitrary URLs or HTTP header injection). CSRF/session state is handled by same-origin BFF cookies and anti-forgery proof on mutations. The worker may use `fetch` with same-origin credentials; it receives no OAuth tokens and is not trusted to authorize requests. Use `AbortController`, structured-clone-safe messages, chunk-level acknowledged byte progress, bounded queue sizes and backpressure.

**Operational exceptions:** OIDC top-level redirects, static assets/logo `<img>` fetches, and browser-managed streams/save dialogs cannot literally run through a Web Worker. They are still coordinated through the app shell/BFF lifecycle and security policies; all application-owned API reads and writes enter the logical request queue. If worker support is unavailable, a reviewed scheduler adapter preserves the queue semantics on the main thread. **Authorization is server-side.** Workers are schedulers, not a background security authority. A stale queue job must fail after share revocation, logout, tenant switch, policy change, or form invalidation.

### 5.5 Server-side durable work is a separate queue

Do not confuse a browser Web Worker with .NET background processing. The backend needs an **independently durable** job state for completed ingress uploads, malware scanning, encryption, document version processing, audit archive delivery, Merkle content commits, and search projections. Implement transactional outbox / leased durable jobs, `BackgroundService` processors, idempotent handlers, poison/dead-letter state, monitoring and retry policies. A volatile in-memory `Channel<T>` alone is insufficient for durable work. Infrastructure may later use Service Bus or another broker; the application contract must remain transport-independent.

For Level 2, a privileged business-DB attacker can tamper with business job rows: security-relevant transitions still must verify the independent Authorization Authority's accepted revisions before releasing content. Durable jobs are not permission authority.

## 6. Resumable file uploads while navigating

### 6.1 User experience

- Upload via form `fileUpload`, collection list or child document record. Selecting a file puts it in a global **Upload Center** instantly, with file name (redacted in notifications if classified), size, progress, phase, pause/resume (when supported), cancel, retry, and accessibility announcements.
- A transfer must continue when the user navigates from Accused → Addresses → Dashboard using SPA routes; no interruption from form component unmount.
- Individual cards and navigation remain interactive during transfer. Upload task status is visible on any page via top-bar badge and persistent tray.
- Browser close/reload: record durable server session identifier and acknowledged ranges; on return, show `Interrupted`/`Needs file re-selection` if local file bytes unavailable. Never claim success before `Ready` from server.
- On server completion, optional durable notification/inbox item informs user of success/failure when they next sign in. Browser push notifications are optional and require consent; do not put classified filenames/details in push payloads.
- If user loses record access during upload, stop and revoke/fail subsequent chunks/finalize; quarantined bytes are cleaned according to policy. Logout stops further requests and clears in-memory File references. Previously accepted immutable data remains subject to backend retention rules.

### 6.2 Protocol

```text
File picker (main thread) → Upload Center (app shell)
    → request coordinator / worker
    → POST /api/v1/upload-sessions       (authorized, idempotent)
    → PUT  /api/v1/upload-sessions/{id}/chunks/{chunkIndex}  (chunked/streamed)
    → GET  /api/v1/upload-sessions/{id} (acknowledged chunk/status)
    → POST /api/v1/upload-sessions/{id}/complete (idempotent)
    → Durable server job: quarantine → malware scan → encrypt if configured
       → verified storage → trusted content manifest commit → Ready
    → server event stream or status poll → Upload Center reports success/failure
```

Suggested initial chunk size **4–8 MiB**, adapt after measurement and server limits. Permit parallel chunks with finite bounds, per-chunk integrity checks and checksums; server tracks authoritative accepted chunks and total file hash. Do not trust client hash alone. Sessions have expiry, size limits, per-tenant quotas, a stable idempotency key, authenticated owner and intended record/field/group binding; finalize checks current permissions and the latest security revision. Reject cross-tenant session reuse or wrong association.

**Security-first path:** stream all uploads through ASP.NET Core controlled ingestion for encrypted/restricted material, so we can quarantine, scan in an isolated service, encrypt, and complete the Level 2 manifest. Do not offer direct public or unscoped blob upload URLs. A narrowly scoped direct-to-staging Azure Blob transfer may be considered later for lower-classification content, but only after same authorization/scanning/encryption guarantees are demonstrated. Azure Block Blob supports staged blocks and commit via Put Block List; storage-service continuation is independent from browser worker lifetime only after blocks have actually arrived.

**Progress semantics:** differentiate `bytes uploaded`, `server processing`, and `ready`. Upload completion is not finished when final chunk is acknowledged: scanning, encryption and trusted manifest acceptance must succeed.

### 6.3 Server API endpoints and SSE

```text
POST   /api/v1/upload-sessions
PUT    /api/v1/upload-sessions/{uploadSessionId}/chunks/{chunkIndex}
GET    /api/v1/upload-sessions/{uploadSessionId}
POST   /api/v1/upload-sessions/{uploadSessionId}/complete
DELETE /api/v1/upload-sessions/{uploadSessionId}
GET    /api/v1/my-upload-jobs?states=pending,processing,failed,ready
GET    /api/v1/events/operations               (SSE when permitted)
```

BFF must enforce CSRF, safe streaming request size, chunk indexes, bounded concurrency, virus-scanning status, content integrity and authority permission checks. `GET` upload status must not leak filenames, file metadata or record existence to unauthorized users. SSE streams must use the same authenticated/authorized session and disconnect on logout/revocation; polling fallback mandatory.

**Downloads:** a browser Worker cannot replace the secure download interaction for arbitrary huge streams without excessive copying. The queue coordinates authorization and issues a short-lived server-controlled download handle; the browser uses same-origin streaming endpoint/stream-to-disk mechanism with native download UX. The server rechecks access, audits release, decrypts when needed and never exposes a raw ciphertext object via a public URL. Keep the same logical queue for initiation and cancellation; do not buffer entire protected documents into worker memory.

## 7. Form Designer and Renderer visual contracts

### 7.1 Form Designer

- Designer home: cards/list with draft/published badge, last edited by, form version, preview/publish actions.
- WYSIWYG-style **four-column desktop grid**; tablet 2, mobile 1. Drag-and-drop is optional; **keyboard-accessible add/move/reorder controls are required** regardless of drag availability.
- Property inspector: label and language translations; type; validation; encryption policy; conditional rule builder; default and dependent dropdown options; column span (1–4), explicit start-on-new-row; section; related-form list settings; file upload limits; accessibility preview.
- Changes create draft revisions; all published versions immutable. Unsaved changes indicator and explicit Save Draft/Publish. Concurrency notices with conflict comparison, not silent overwrite.
- Preview panes: 360 px, 768 px, 1440 px, keyboard simulation, large text, RTL language; show organization theme/accessibility errors before publishing.
- Designer sees **no real submitted values** unless separately authorized as a record user. Use synthetic examples for previews and screenshots.

### 7.2 Form Renderer

- Consume the server's **authorized projection**, never full form JSON plus hidden client-side data. The projected layout contains only fields, options, related child record summaries and documents user may access.
- Reuse JSON version behavior Historical / Latest / Hybrid exactly as core defines; server selects version.
- Provide field-level error summary with anchor links, context help, required markers accompanied by text, date/number localization without modifying stored UTC/date-only semantics, proper input modes, and deterministic dropdown sorting from organization catalogs.
- Related Form Lists: accessible table/card views, add/edit/unlink actions gated by permissions, child records separately authorized, stable IDs, independent async refresh per section.
- File list: attachment status, version, scan/encryption state where permitted; independently shareable file items; no guessed blob URLs.
- Scoped shares: no hidden fields, field names, conditions or counts returned. All backend actions (including auto-save, previews and downloads) independently check effective permissions and protection challenges.
- Avoid large DOM on huge record lists: virtualize only with screen-reader-safe fallback, server paging, and verified search semantics.

## 8. ADR-ARCH-001 — Feature-first repository layout and Clean Architecture boundaries (NORMATIVE)

Use the existing solution where possible. For greenfield or incrementally refactored code, implement a **feature-based modular monolith** for the business application. Maintain a *small fixed set of Clean Architecture projects* with corresponding `Features/` namespaces and **vertical use-case folders**, rather than dozens of microservices or one `.csproj` per capability. The separate Level 2 Authorization Authority is an independent service and deployment boundary; its trust data, cryptographic keys, and SpiceDB access must never be absorbed into the business API.

```text
repo/
├── CLAUDE.md
├── CLAUDE-DESIGN.MD
├── README.md
├── global.json
├── Directory.Build.props
├── .editorconfig
├── .vscode/                           # launch.json, tasks.json, extensions.json
├── docs/
│   ├── architecture/
│   │   ├── ADR-ARCH-001-feature-vertical-slices.md
│   │   └── FEATURE_OWNERSHIP_MATRIX.md
│   ├── ui/SCREEN_IMPLEMENTATION_MATRIX.md
│   └── runbooks/
├── src/
│   ├── backend/
│   │   ├── EnterpriseForms.Domain/
│   │   │   ├── Features/
│   │   │   │   ├── FormDefinitions/
│   │   │   │   ├── FormRecords/
│   │   │   │   ├── RelatedForms/
│   │   │   │   └── Documents/
│   │   │   └── SharedKernel/           # tiny, provider-free domain primitives
│   │   ├── EnterpriseForms.Application/
│   │   │   ├── Features/
│   │   │   │   ├── FormRecords/
│   │   │   │   │   ├── CreateFormRecord/  # command, validator, handler, result
│   │   │   │   │   ├── UpdateFormRecord/
│   │   │   │   │   ├── GetFormRecord/
│   │   │   │   │   └── Public/           # supported cross-feature ports
│   │   │   │   ├── FormDesigner/
│   │   │   │   ├── RelatedForms/
│   │   │   │   ├── Documents/
│   │   │   │   ├── Sharing/
│   │   │   │   ├── Search/
│   │   │   │   ├── Organizations/
│   │   │   │   ├── Identity/
│   │   │   │   ├── Teams/
│   │   │   │   ├── Audit/
│   │   │   │   ├── UserPreferences/
│   │   │   │   ├── Dashboard/
│   │   │   │   ├── Jobs/
│   │   │   │   ├── Exports/
│   │   │   │   └── SecurityOperations/
│   │   │   └── Common/                # minimal cross-cutting ports only
│   │   ├── EnterpriseForms.Infrastructure/
│   │   │   ├── Features/
│   │   │   │   ├── FormRecords/         # EF mappings and port adapters
│   │   │   │   ├── Documents/           # storage provider adapter
│   │   │   │   └── .../
│   │   │   └── Persistence/            # shared DbContext/migrations registration
│   │   ├── EnterpriseForms.Contracts/
│   │   │   └── Features/               # versioned request/response contracts
│   │   ├── EnterpriseForms.Api/
│   │   │   ├── Features/               # feature-local endpoint registration
│   │   │   └── Composition/            # startup, DI, auth, OpenAPI
│   │   ├── EnterpriseForms.Worker/
│   │   │   └── Features/               # background handlers by owner
│   │   └── EnterpriseForms.slnx
│   ├── authority/                       # separately operated + separately deployed
│   │   ├── AuthorizationAuthority.Domain/
│   │   ├── AuthorizationAuthority.Application/Features/
│   │   ├── AuthorizationAuthority.Infrastructure/Features/
│   │   ├── AuthorizationAuthority.Contracts/
│   │   └── AuthorizationAuthority.Api/Features/
│   └── frontend/
│       ├── apps/web/src/
│       │   ├── app/                    # shell/providers/router/lazy routes
│       │   ├── features/
│       │   │   ├── dashboard/
│       │   │   ├── form-catalog/
│       │   │   ├── form-designer/
│       │   │   ├── form-records/
│       │   │   ├── related-forms/
│       │   │   ├── documents/
│       │   │   ├── sharing/
│       │   │   ├── search/
│       │   │   ├── organizations/
│       │   │   ├── teams/
│       │   │   ├── identity/
│       │   │   ├── user-preferences/
│       │   │   ├── audit/
│       │   │   ├── jobs/
│       │   │   ├── exports/
│       │   │   ├── security-operations/
│       │   │   └── support/
│       │   └── main.tsx
│       └── packages/
│           ├── ui/                     # accessible primitives; no domain HTTP
│           ├── design-tokens/          # branding and WCAG validation
│           ├── api-client/             # generated OpenAPI transport
│           ├── work-queue/             # global queued request coordinator
│           ├── upload-manager/         # route-independent resumable upload UX
│           ├── form-renderer/          # reusable schema-driven UI
│           ├── form-designer/          # reusable designer components
│           ├── authorization-ui/       # capability/step-up presentation
│           ├── localization/
│           └── shared-types/
├── contracts/                           # OpenAPI + JSON schemas, not DB entities
├── tests/
│   ├── architecture/                    # .NET project + module boundaries
│   ├── features/                        # unit/integration by feature+use case
│   ├── authority.adversarial/
│   ├── frontend.component/
│   ├── frontend.e2e/
│   ├── accessibility/
│   └── load/
├── infra/
│   ├── docker/                          # API, worker, frontend, Authority images
│   ├── compose/                         # local/test dependencies incl. Vault Transit
│   ├── azure/
│   └── observability/
└── scripts/
    ├── validate-themes.*
    ├── check-openapi.*
    └── smoke-test.*
```

**No global frontend `src/pages/` folder.** Screens are owned by feature-local `pages/`; `app/` registers routes and composes independent widgets. Within a frontend feature prefer:

```text
features/form-records/
  pages/RecordListPage.tsx
  pages/RecordDetailsPage.tsx
  components/RecordSummaryCard.tsx
  api/recordQueries.ts              # typed client wrappers, no raw fetch
  hooks/useRecordPermissions.ts
  schemas/recordViewSchema.ts
  types/recordView.ts
  tests/RecordDetailsPage.test.tsx
  index.ts                         # only supported public exports
```

**Dependency graph:** `Domain ← Application ← Infrastructure / Api / Worker`, with provider implementations depending inward; `Contracts` contains stable wire types (no EF entities). The frontend depends on OpenAPI contracts, coordinator and component packages, **not** on the backend's persistence or domain model. Only the authorized Trust Authority adapter can call authority APIs, and only through its published contract; business features cannot query or modify the authority store or SpiceDB.

**Feature ownership:** See `CLAUDE.md` Section 4U and this file's Section 17. One module owns each business entity/table, endpoint/use case and page. Prefer a shared business `DbContext` and unified migrations stream with feature-local mappings; other modules use published application contracts, not direct DbSet access to another module's data. Where a legacy codebase makes this difficult, migrate incrementally and add tests before removing adapters. No sweeping rewrites without the existing repository being inspected.

**Worker/queue ownership:** `work-queue` and `upload-manager` remain global platform packages, not duplicated by features. Features submit typed commands and query hooks; background .NET workers consume authorized commands with idempotent retries and audits. Browser queue is not a substitute for server-side durable ingestion; it may not outlive all tabs closing.

**Architectural enforcement:** CI must run .NET project-reference and namespace-boundary tests (ArchUnitNET/NetArchTest or equivalent), TypeScript ESLint import/public-entrypoint checks, no raw `fetch`/`axios` in feature components, generated OpenAPI contract tests, and a feature/table/page ownership matrix check. Preserve the complete Level 2 fail-closed trust boundaries across every slice.

### 8.1 Minimal backend interface boundaries

```csharp
public interface IOrganizationBrandingService
{
    Task<OrganizationBrandingDto> GetEffectiveBrandingAsync(
        Guid tenantId, CancellationToken cancellationToken);
    Task<OrganizationBrandingDto> UpdateBrandingAsync(
        Guid tenantId, UpdateOrganizationBrandingCommand command,
        long expectedRevision, CancellationToken cancellationToken);
}

public interface IUploadSessionService
{
    Task<UploadSessionDto> BeginAsync(
        BeginUploadCommand command, CancellationToken cancellationToken);
    Task<UploadChunkReceiptDto> AcceptChunkAsync(
        Guid uploadSessionId, int chunkIndex, Stream chunkContent,
        CancellationToken cancellationToken);
    Task<UploadSessionDto> CompleteAsync(
        Guid uploadSessionId, string idempotencyKey,
        CancellationToken cancellationToken);
}

public interface IDashboardCompositionService
{
    Task<AuthorizedDashboardDto> GetDashboardAsync(
        Guid requestingUserId, Guid tenantId, CancellationToken cancellationToken);
}
```

Full DTO contracts must enforce actor identity from validated server auth context, not from caller-supplied user GUIDs. Do not implement a public endpoint accepting arbitrary `tenantId` to read branding or user dashboard. All protected record reads/writes retain authority checks, HMAC/Merkle integrity and verifiable search restrictions.

## 9. API / BFF contract additions

Use the existing API and auth architecture as the starting point. New HTTP endpoints are namespaced and versioned, with OpenAPI contract tests:

| Endpoint | Method | Purpose / authorization |
|---|---|---|
| `/api/v1/bootstrap` | GET | Authenticated user, safe capabilities, org branding revision, locale settings; no broad record counts |
| `/api/v1/dashboard` | GET | Authorized widget descriptors, no inline raw unauthorized data |
| `/api/v1/dashboard/widgets/{widgetIdentifier}` | GET | Authorized, paged widget data; no arbitrary URL routing |
| `/api/v1/organization/branding` | GET | Effective safe theme and accessible logo reference |
| `/api/v1/organization/branding` | PUT | OrganizationOwner + optimistic concurrency + audit + approval policy |
| `/api/v1/organization/branding/logo` | POST | Quarantined/re-encoded logo workflow |
| `/api/v1/me/ui-preferences` | GET/PUT | Locale/timezone/font preferences, user-scoped |
| `/api/v1/upload-sessions` | POST | Begin authorized upload; idempotency required |
| `/api/v1/upload-sessions/{uploadSessionId}/chunks/{chunkIndex}` | PUT | Bounded chunk ingress, per-chunk authorization |
| `/api/v1/upload-sessions/{uploadSessionId}` | GET | Owner-scoped session status |
| `/api/v1/upload-sessions/{uploadSessionId}/complete` | POST | Idempotent finalize and durable processing |
| `/api/v1/my-upload-jobs` | GET | Authenticated user's safe job status |
| `/api/v1/events/operations` | GET | Authorized server events; polling fallback |

Use problem-details errors with stable machine-readable codes, localized safe text, correlation ID and retry classification. Do not leak existence or identities of forbidden records in 403/404 responses. For protected resources, respond according to existing non-inference policy. Rate-limit branding/logo and chunk initialization endpoints. Prevent CSRF on all state-changing cookie-authenticated APIs, including upload chunks where applicable. Never render tenant-entered label strings as executable HTML.

## 10. Docker and deployment requirements

- `Dockerfile.web`: build TypeScript bundle in Node build stage; serve static assets with a hardened unprivileged nginx/Caddy container or through API static-hosting adapter. SPA fallback for `/app/*`, no fallback for `/api/*`.
- `Dockerfile.api`: multi-stage .NET SDK → aspnet runtime image; run non-root, read-only root FS when feasible, health endpoints, request limits, graceful shutdown, externally supplied secrets. Keep BFF/API behind same-origin ingress.
- `Dockerfile.worker`: separate .NET worker image using same application abstractions, only necessary database/storage permissions.
- `Dockerfile.authority`: **separately deployed** service, identities and database; Docker Compose can emulate isolation locally but is **not** equivalent to the Level 2 production trust boundary.
- Local `compose.dev.yaml`: web, business API, business PostgreSQL, authority API, isolated authority PostgreSQL, SpiceDB, Vault Transit (DEV ONLY), Azurite, durable job processor, observability optional. Use named volumes for local DB/storage; do not commit dev secrets.
- Production: immutable image tags/digests, SBOM, dependency/container scan, signed images where feasible, controlled config, private networking, restricted origin, TLS, managed identity, HSM, protected authority infrastructure, egress restriction and audit/ledger requirements from `CLAUDE.md`.
- Use runtime environment config at deployment to avoid baking tenant-specific secrets or themes into the bundle. Organization branding is retrieved per authorized session and revision.
- Health probes: liveness excludes external dependencies; readiness checks mandatory dependencies without leaking internal details. Authority unavailable ⇒ protected workflows fail closed, not silently enter local-only mode.
- Reverse proxy `/api` and OIDC callbacks same site. Strong content security policy, strict transport security, no-store for protected content, sensible static asset caching only; verify CSP with approved token injection.
- No public bucket/container, no direct browser KMS access, no managed HSM credentials inside client bundles. Vault Transit development mode forbidden in production.

### Visual Studio Code developer experience

Commit `.vscode/extensions.json` recommendations (C# Dev Kit, ESLint, Prettier, Tailwind IntelliSense, Docker, Playwright); `launch.json` for .NET API + Vite UI compound debugging; `tasks.json` for `dotnet build`, `dotnet test`, `npm ci`/package install, dev server, e2e and Docker Compose. Do not commit user-specific absolute paths or credentials. Document exact `dotnet`, Node LTS and Docker versions tested and keep lockfiles.

## 11. Performance, concurrency and failure policies

- UI must remain responsive under multiple concurrent uploads and widget requests; do not perform hashing/encryption of multi-GB files on main thread. Browser crypto isn't authorization boundary; server remains responsible for final integrity and encryption acceptance.
- Upload queue survives **SPA route** navigation; child record list refresh does not cancel a different widget's uploads.
- Interactive reads/writes have higher scheduling priority than queued background transfers; enforce fair execution, per-resource serialization and cancellation, request abort and visible status.
- Implement backpressure: cap memory, staged bytes, queue depth, file/chunk concurrency, background requests per tenant and per user. Huge uploads cannot exhaust the .NET thread pool or authority HSM quotas.
- Use `ETag`/revision, HTTP conditional requests where appropriate; don't expose stale client cache past revocation. Preserve existing independently accepted authority revision behavior.
- Write operations report `Pending`, `Accepted`, `Committed`, `Failed`, not success on enqueue. For forms/record updates, waiting for accepted integrity commit may require status refresh, not blind optimistic success.
- Debounce searchable lookups; server-controlled pagination and input validation; per-tenant quotas; no full-record prefetch, no unauthorized query key enumeration.
- Server `SSE` may carry only operation IDs and safe status; full record contents require separately authorized GET; fallback to bounded polling.
- Error isolation: upload failure never blanks dashboard, one card error never blanks page, refresh cannot cancel unrelated work, authority outage shows safe degraded banner with no protected data release.
- Rate/latency targets must be measured, not promised untested. Define P50/P95/P99 for card readiness, auth barrier check, upload throughput, HSM calls, memory and tenant concurrency; test on 320 px low-end device and slow network.

## 12. Accessibility and quality test matrix

### 12.1 UI acceptance tests (minimum)

| ID | Scenario | Required outcome |
|---|---|---|
| UI-001 | New organization without branding | Black/white primary buttons, readable accessible default design |
| UI-002 | Organization owner uploads logo | Safe optimized logo upper-left, fallback name, correct tenant scoping |
| UI-003 | Unauthorized user attempts branding update | API rejects, audit event, no effective change |
| UI-004 | Set inaccessible foreground/background pair | Server rejects with contrast reason; old safe theme remains effective |
| UI-005 | Test alternate hover/focus/error/checkbox states | Every required control state meets WCAG target, visible focus |
| UI-006 | Increase text to 150% and browser zoom to 400% | No loss of controls, clipping or inaccessible horizontal overflow |
| UI-007 | Mobile first view 320 px | Single-column cards/forms, usable menu and upload tray |
| UI-008 | Organization chooses dark palette | Primary black button remains visually distinguishable and focused |
| UI-009 | Designer attempts unapproved custom CSS/script | Rejected and safely escaped; CSP holds |
| UI-010 | First login user with no record grants | Dashboard shows no record existence or unauthorized counts |
| UI-011 | Multiple roles | Union of authorized widgets/actions, no duplicate cards, no role-label bypass |
| UI-012 | Designer lacks data rights | Can design permitted forms but cannot view actual submitted record data |
| UI-013 | One widget returns 500 | Other widgets load, failed widget retains accessible retry |
| UI-014 | Refresh one card | Only target query reruns, others and uploads unaffected |
| UI-015 | Tab/route changes during upload | Within-app route changes preserve active transfer and progress |
| UI-016 | Close tab during upload | App does not promise completion; on return resumes/reselects as permitted |
| UI-017 | Upload encrypted document | Server quarantines, scans, encrypts, commits; raw blob cannot yield plaintext |
| UI-018 | Upload scanner unavailable | Not marked ready; safe failed/pending state, alert, retry handling |
| UI-019 | Revocation while uploading | Subsequent chunk/finalize denied, stale queued jobs cannot release data |
| UI-020 | Queue saturates with large uploads | Interactive card read and record save still responsive; backpressure limits |
| UI-021 | Worker crashes during upload | Operation reconciles from server state; no duplicate committed document |
| UI-022 | Network drop/retry/duplicate chunk | Idempotent chunk receipt; no mixed/corrupted file |
| UI-023 | User logs out/switches tenant | Worker jobs aborted/cleared; no stale protected result displayed |
| UI-024 | Cookie-authenticated mutation without CSRF proof | Server rejects; worker cannot bypass CSRF |
| UI-025 | File attachment shared partially | Only approved fields/documents render, download requires current permission |
| UI-026 | Form designer keyboard field reorder | All placement operations work without drag-and-drop |
| UI-027 | RTL Arabic labels | Correct logical layout and screen reader order; data/documents unchanged |
| UI-028 | Date/time/number organization/user locale | Browser/default/explicit hierarchy respected, UTC unchanged |
| UI-029 | Accessible live upload progress | Percent/stages/error announced appropriately, no focus traps |
| UI-030 | Authority unavailable | Protected reads/writes fail closed; safely handle dashboard errors |
| UI-031 | Direct business DB ownership tampering | UI cannot access target without authority approval and verified integrity |
| UI-032 | Organization branding stale cache | Revision invalidates theme safely, no tenant mix-up |
| UI-033 | Data save retry | Idempotency and optimistic concurrency prevent duplicate updates |
| UI-034 | Broken logo URL or malformed theme config | Safe default fallback with observability, no blank shell |
| UI-035 | Cards and navigation with screen reader | Landmarks, headings, status and button labels understandable |
| UI-036 | Upload resumed after file re-selection | File identity checked; wrong file rejected; accepted chunk receipts reused safely |
| UI-037 | Partial share changes while card is loading | Response re-authorized, no cached unauthorized content shown |
| UI-038 | Browser unsupported Web Worker feature | Reviewed coordinator fallback or clear supported-browser error, not silent data loss |
| UI-039 | Real staging HSM and authority deployment | Local Vault behavior not mistaken for production trust isolation |
| UI-040 | Theme against automated and human accessibility audit | Axe, contrast and manual keyboard/screen reader audit pass before release |

### 12.2 Automation and test tooling

- `dotnet test` with service/application unit tests, Testcontainers-backed integration tests, BFF auth/CSRF and mock IdP, failure injection for durable work.
- Vitest (or comparable) for UI/theme/queue algorithms; React Testing Library for keyboard semantics; Playwright for full browser journeys, route change during active upload, screen sizes, impersonation and revocation.
- axe-core integration on every main route, light/dark theme and user text-scale preset; pixel/screenshot regression snapshots for component gallery.
- Contract tests ensure generated OpenAPI TypeScript SDK matches .NET APIs; JSON-schema versioning for theme/widget/form layout; no frontend EF/entity leakage.
- Docker Compose smoke/e2e pipeline; full Level 2 security tests in separately isolated staging as required by core documentation.
- Dependency audits, SAST, DAST, container scans, CSP, link/header verification, accessibility findings tracked to closure.

## 13. Implementation phases for Claude

**Feature-slice delivery gate (ADR-ARCH-001):** Sections 8 and 17 prescribe named owning modules. Phases D0–D9 remain dependency/security milestones, **not** mandates to create all backend projects first and all frontend pages last. Complete an authorized API + application handler + infrastructure + UI + tests slice at a time within prerequisite security gates. A page is not done unless its feature owner and interface/test obligations are recorded.

**Full page coverage gate:** In addition to the phase-specific features below, implement the routable screens and end-to-end journeys in Section 16. `docs/ui/SCREEN_IMPLEMENTATION_MATRIX.md` must be updated continuously; do not mark the whole web application complete until all Core pages are functional and all Controlled Operations screens are verified for Level 2 release (where deployed separately, record that surface and its contract tests).

| Phase | Deliverables | Exit gate |
|---|---|---|
| D0 — Baseline | Inspect existing core repo/contracts and reconcile overlaps, record ADRs | No conflicting security or domain model |
| D1 — Foundations | UI package, Tailwind tokens, theme validation, component gallery, typography, WCAG checks | Accessible black-button design demonstrated |
| D2 — Shell/auth | BFF OIDC, secure cookie/CSRF, logo header, navigation, bootstrap, user preferences | First login correct; no token in browser storage |
| D3 — Queue | Typed coordinator interface, Web Worker scheduler, cancellation, retry/idempotency rules, safety tests | All API calls through coordinator; concurrent loads work |
| D4 — Dashboard | Role/capability-driven cards, card refresh, loading/errors, live-safe counts | Independent async cards with per-card refresh |
| D5 — Forms | Renderer and Designer packages, 4-column layout, related child lists, localization | Data never leaked through unauthorized projection |
| D6 — Uploads | Global Upload Center, chunking/resume, server durable jobs, scanning/encryption, object storage | SPA navigation during upload; secure finalization |
| D7 — Organization theme | Branding CRUD, vetted presets, safe logo, user text-scale controls, auditing | Server refuses inaccessible themes |
| D8 — Containers | Dockerfiles, local Compose, scripts, VS Code debug configurations, CI | Repeatable dev and container deployment |
| D9 — Hardening | Auth/authority adversarial tests, long uploads, 400% zoom, axe/manual review, load/recovery | Security review and accessibility sign-off |

**Do not shortcut D3 by scattering `fetch` calls through components or D6 by keeping upload state inside a route component.** Do not add stateful frontend business logic that should reside in the .NET domain/application layers.

## 14. Definition of done

The web design is complete only when all these conditions are demonstrated:

1. The application looks professional and consistent with **black primary buttons by default**, readable checkbox controls, properly spaced mobile-first pages, and accessible focus states.
2. Each tenant can have an authorized, safe top-left logo, approved button color pairs, and text size defaults; accessibility validation occurs server-side before publishing branding.
3. Sign-in is external OIDC/FIDO2 as required. Authenticated landing page is appropriately different according to effective permissions, not role names alone.
4. Every data card/section loads independently and has an accessible **Refresh** button that refetches only its own data.
5. Browser-side reads and writes pass through a common queued coordinator with cancellation, fairness, backpressure, secure session handling, current authority validation and idempotent retry semantics.
6. File uploads continue through **SPA navigation**, report accurate phase-based status, remain resumable where browser/file availability permits, and finish server processing reliably after ingress. No deceptive guarantee of upload after browser shutdown.
7. Frontend framework and visual design can be replaced without modifying core .NET use cases, PostgreSQL structure, the independent Authorization Authority or the functional OpenAPI contract.
8. Docker images build and operate through local Compose and separately deployed production security boundaries; VS Code tooling works.
9. Mandatory security, performance, disaster-recovery and accessibility release gates in `CLAUDE.md` remain intact, and this document adds browser/client-specific verification.
10. No security claim, AODA compliance claim, cross-tab upload guarantee, or HSM equivalence claim is made without actual staging tests and review.
11. Feature-based modular boundaries from ADR-ARCH-001 are enforced in CI and evidenced by the owned-screen matrix, architecture dependency tests, and no global page/feature-private import violations.

---

## 15. Technology references to verify during build

Use current supported stable versions and pinned lockfiles; do not assume an old framework version is still supported. Key public source references at specification date (2026-10-17):

- Ontario public website accessibility requirements: https://www.ontario.ca/page/how-make-websites-accessible
- W3C WCAG 2.2 AA: https://www.w3.org/TR/WCAG22/
- Tailwind CSS design tokens: https://tailwindcss.com/docs/theme/
- TanStack Query invalidation: https://tanstack.com/query/latest/docs/framework/react/guides/query-invalidation
- MDN limited Background Fetch API: https://developer.mozilla.org/en-US/docs/Web/API/Background_Fetch_API
- MDN Background Sync limitations: https://developer.mozilla.org/en-US/docs/Web/API/Background_Synchronization_API
- Azure Blob Put Block List: https://learn.microsoft.com/en-us/rest/api/storageservices/put-block-list

---

## 16. Complete page inventory and screen-by-screen implementation contracts (NORMATIVE)

> **This section closes the previous page-coverage gap.** Every route below is a defined screen or explicit workflow surface, not a suggestion. Implement every **Core** page before declaring the full enterprise web application complete. **Controlled Operations** pages belong in the separate operational administration surface and are required before Level 2 production approval; do not expose them to ordinary tenant administrators. Integrate them into phase delivery as the corresponding backend vertical slices become available. Backend contracts in `CLAUDE.md` and the referenced OpenAPI files take precedence over illustrative endpoint names here. Where an endpoint does not exist, add a documented typed contract instead of inventing a data response.

### 16.1 Global page contract — mandatory for every screen below

1. **App shell:** Organization logo at top left, organization identity, active tenant, accessible menu and breadcrumbs, search only where authorized, notices, user-menu, global upload activity, and responsive navigation. On screens requiring step-up, keep protected content out of initial HTML, preload, browser history, breadcrumbs, page title, notifications, and analytics until authorization is satisfied.
2. **Role and permission checks:** Routes are discovered from server-computed capabilities and may disappear on revocation. The server always rechecks tenant scope, owner/team/sharing relationships, challenge and FIDO2 requirements, row/manifest integrity and authority state. Do not infer ability from client route visibility. Designer privilege grants no record-data privilege; OrganizationOwner privilege grants no record-data privilege; SystemAdministrator does not get automatic decrypted access.
3. **Asynchronous sections:** Every server-backed card, table, subpanel, editor dataset and status area uses an independent query key and accessible `Refresh <section name>` control, with skeleton, empty, loading, stale, error/retry, access-changed, and service-degraded states. Never let failure of one section blank unrelated sections. Static text or decorative areas do not need artificial refresh.
4. **Network queue:** Reads and writes use the shared request coordinator; the coordinator enforces lane priorities, idempotency and request cancellation. A submitted mutation is only shown as successful after a verified server acknowledgement. It is *not* acceptable to replay a protected mutation automatically after authorization is revoked, an epoch changes, a session expires, or a conditional-write precondition fails. Confirmation prompts and secure browser downloads are appropriate UI exceptions to routing every byte through the Worker.
5. **Responsive/accessibility:** Mobile first, meaningful 1-column narrow layout, accessible labels, black default primary buttons, high-contrast checkboxes, visible keyboard focus, error summary, explicit validation, and no horizontal page overflow at 320 px or 400% zoom. Data grids have a card or accessible horizontal-scroll presentation with persistent row actions. Do not rely on color, hover, mouse drag, or icons alone. Use organization-configured tokens only after WCAG 2.2 AA server validation.
6. **Scope and privacy:** Search, count badges, sorting, exports, filter suggestions, filenames, thumbnails, upload progress and error responses disclose only authorized information. Hidden record fields and documents are *not present* in API projections; no browser-side filtering of secret data. Use the latest trusted security revision for protected actions, including child-form list entries.
7. **Audit:** Mutations and sensitive reads/downloads/searches use the canonical audit event model; no duplicate per-feature event stores. Display localized times according to resolved user/organization preferences, but preserve UTC instants and date-only semantics. Exclude encrypted plaintext, access codes, tokens, and sensitive query values from logs.
8. **UX state contracts:** Explicitly design initial loading, empty, no-permission, record removed, option disabled, conflict, stale layout, invalid signature, recovery quarantine, expired access challenge and offline/network error states. Fail closed and offer safe retry or support direction rather than leaking record existence.
9. **Localization:** Labels, dropdown choices, validation, navigation and help honor the user language with English fallback; record-entered data and uploaded documents are not machine translated. IANA timezone and organization-defined choice restrictions apply everywhere.
10. **Traceability:** Each page has a route ID (`PG-###`), exactly one owning feature from Section 17, contract test and Playwright scenario. Navigation flows, deep links, breadcrumbs and refresh must all work with a browser back/forward history and during an ongoing upload. All routes are illustrative `/app/...` conventions but should be implemented consistently rather than duplicated under alternative paths.

### 16.2 Shared page templates, routing, and permission ownership

- **Workspace:** `WorkspacePageLayout` with title, purpose, breadcrumbs, primary/secondary actions, optional filters, independent card regions, loading and security-safe error states.
- **Record workspace:** `RecordWorkspaceLayout` with record metadata header; permissions-aware tabs for Summary, Edit, Related Lists, Documents, Sharing, Activity. Never construct a tab from unauthorized child metadata. Document preview uses a distinct secure viewer flow.
- **Designer workspace:** `DesignerWorkspaceLayout` with accessible canvas, palette, property inspector and preview; route transitions retain draft state and revision conflicts. Designers cannot inspect live submissions without additional record permissions.
- **Settings workspace:** `SettingsWorkspaceLayout` with organization/user scoped navigation, safe editable vs effective value comparison, optimistic concurrency and save/revert status.
- **Security operations:** `SecurityOperationsLayout` with explicit privileged posture, step-up indication, actor identity, authority revision, audit correlation ID and required change reason. Level 2 diagnostic pages belong behind separate operator entitlements.
- **Detail/edit:** Split `GET` from mutation permission; a user may view without editing, or discover without passing the challenge required to view values. Never allow disabled selections to be silently saved or defaults to silently overwrite pre-existing choices.
- **Search:** A globally reusable `AuthorizedSearch` experience must validate *both* predicates and projected values. A search page cannot reuse business-database-only results to claim completeness under hostile-DBA Level 2 requirements.
- **Dialogs:** Confirmation and small pickers may be dialogs rather than extra pages. Any complex workflow, audit history, multi-step authorization, or bulk action uses a routable page with back/forward support.

**Capability naming:** The labels below describe user-facing intent; map them to the authoritative permission catalog in `CLAUDE.md` and the trust authority, not literal hardcoded roles. A user with several roles receives the union of allowed features without double-counted widgets. Owner and team owner derive permissions only from the independent authority; mere possession of a URL grants nothing.

### 16.3 Authentication, onboarding, and common account pages

#### PG-001 — Federated sign-in handoff (`/auth/sign-in`) · Core
- **Audience/permission:** Anonymous; start only approved organization identity-provider flow. No local password fields, password reset, or application-managed passkeys.
- **Sections:** Minimal trusted product identity and organization logo if safely resolved, permitted provider action(s), privacy/support references. No user or record hints.
- **Actions:** Continue to provider, retry cancelled flow. Server generates and validates OIDC state, nonce, PKCE where applicable; cookies use secure BFF model.
- **Async/edge:** Independently load safe public branding if needed; handle unknown organization, IdP outage, rejected assurance, expired state. Do not display claims or tokens in query strings or messages.

#### PG-002 — Authentication callback and bootstrap (`/auth/callback`, `/app/loading`) · Core
- **Audience/permission:** Validated identity/session only; never render protected data during bootstrap.
- **Sections:** Accessible progress, session/tenant binding, assurance check, organization policy, safe theme and preferences, capability navigation.
- **Actions:** Finish bootstrap, go to organization chooser or dashboard; safe retry or sign out on failures.
- **Async/edge:** Bootstrap through app-wide request coordinator; stale tenant and current authority epoch cause session context reset. Never cache protected UI from another user.

#### PG-003 — Organization chooser (`/app/select-organization`) · Core
- **Audience/permission:** Users authorized for multiple organizations; single-tenant users bypass.
- **Sections:** Authorized organization cards with logo/name, active availability and allowed switch action. Do not list unauthorized tenant identities.
- **Actions:** Choose organization, revalidate policy/FIDO2 assurance, clear tenant-scoped query caches and previous unlocks; switch server-side session context.
- **Async/edge:** Each organization summary refreshes independently; errors isolated. Navigation and upload tasks must not silently cross the tenant boundary; pause/cancel tenant-bound operations safely.

#### PG-004 — Welcome / first-use checklist (`/app/welcome`) · Core
- **Audience/permission:** First-time user; tasks are capability-scoped and dismissible.
- **Sections:** Organization and role-aware orientation, permitted forms, profile completion, passwordless enrollment guidance where approved, accessibility preference link.
- **Actions:** Open next permitted task; dismiss or revisit. Do not automatically grant access to existing records.
- **Async/edge:** Checklist items refresh separately from current capability state; missing record permissions yield a positive empty state, not organization-wide data.

#### PG-005 — Session step-up / record challenge (`/app/security/verify`) · Core
- **Audience/permission:** Already authenticated user performing an action requiring additional verified assurance or record-scoped challenge.
- **Sections:** Reason for challenge without exposing protected data; provider-hosted FIDO2 handoff or authorized passphrase challenge; expiration and attempt handling.
- **Actions:** Verify, cancel, recover via approved IdP flow; resume original action only after *fresh* server permission and current epoch check.
- **Async/edge:** Rate limits, challenge lockout, revocation and tenant switch invalidate unlock; never store passphrases, OTPs or token material in browser storage, history or telemetry.

#### PG-006 — Role-based dashboard (`/app/dashboard`) · Core
- **Audience/permission:** Authenticated; compose by effective capabilities, not a single role label.
- **Sections:** My records, assigned/team work, recent authorized activity, pending uploads, Designer work, organization settings, security tasks, audit/health—only when permitted.
- **Actions:** Open applicable card/record, refresh each card separately, launch permitted creation or administration journey.
- **Async/edge:** Each widget loads asynchronously with independent refresh/error/empty timestamp; no tenant-wide counts for unauthorised users. Saved dashboard composition is presentation data, never authorization.

#### PG-007 — Personal preferences (`/app/settings/preferences`) · Core
- **Audience/permission:** Current authenticated user only.
- **Sections:** Language, timezone, date/time format, number format, text scaling; `Use Organization Default`, `Use Browser`, and explicit selection independently for each setting; preview.
- **Actions:** Save with concurrency token, restore defaults, test preview. Resolve IANA zone and locale without changing stored dates/numbers.
- **Async/edge:** Organization-disabled historic choice shown as unavailable; require valid replacement when saving affected editable settings. Refresh choices and effective resolved settings separately.

#### PG-008 — My account and sign-in methods (`/app/settings/account`) · Core
- **Audience/permission:** Current user only.
- **Sections:** Display name, provider-bound identity, approved authentication methods summary, current organization context, recent own session events, sign-out controls.
- **Actions:** Open provider-hosted method management, request permitted session revocation, sign out; no local password fields.
- **Async/edge:** No display of passkey secrets or unapproved account details. Each own-account panel independently refreshable; IdP out-of-sync errors safe.

#### PG-009 — Notifications and task inbox (`/app/notifications`) · Core
- **Audience/permission:** Current user; notification targets checked against present permissions on opening.
- **Sections:** Upload completion/failure, access requests, shared-record actions, publishing reviews, security alerts if assigned; filter/read status.
- **Actions:** Open authorized target, dismiss/read, refresh list. Never embed protected field values or confidential filenames into notification payloads.
- **Async/edge:** Paginate; a revoked target becomes generic unavailable, never an existence leak.

#### PG-010 — Global Upload Center (`/app/uploads`) · Core
- **Audience/permission:** Current user's upload jobs for active organization.
- **Sections:** Active, queued, uploading, scanning, encrypting, completed, failed, resumed items; each item includes permitted destination and safe progress/stage.
- **Actions:** Add files in permitted context, pause/resume/cancel/retry, inspect permitted result and failure reason; global tray persists during SPA navigation.
- **Async/edge:** Each upload task loads and refreshes separately; enforce tenant/record/field auth on every stage. Do not claim continued upload after browser close; resume requires file handle/reselection where necessary.

#### PG-011 — Help, accessibility, and application support (`/app/help`) · Core
- **Audience/permission:** Authenticated; public accessible help link also supported at sign-in.
- **Sections:** Navigation help, keyboard shortcuts, accessible display options, upload limits/resume facts, permitted contact/support route, safe diagnostics ID.
- **Actions:** Search help, copy correlation ID, submit support report without attaching protected data automatically.
- **Async/edge:** Support knowledge cards independently refresh; assistance content localized with English fallback.

### 16.4 Daily work, records, child forms, documents, and sharing

#### PG-012 — Permitted form catalog (`/app/forms`) · Core
- **Audience/permission:** `forms.use` for visible definitions; Designer status alone does not imply record use.
- **Sections:** Searchable permitted published forms, categories, descriptions, form version/status, last used; organization-approved defaults.
- **Actions:** Open form details, start record creation; filter only authorized catalog entries.
- **Async/edge:** Catalog/list facets refresh independently. No unpublished or retired form leakage; empty state for users with no available forms.

#### PG-013 — Published form details (`/app/forms/:formDefinitionId`) · Core
- **Audience/permission:** Form use/metadata privilege for specified form.
- **Sections:** Purpose, requirements, accessible field overview where metadata may be disclosed, authorized creation action, guidance and form version.
- **Actions:** Create record or return to catalog. No real record previews for a Designer without data rights.
- **Async/edge:** Refresh summary and policy panels separately; disabled form/version produces a safe not-available state.

#### PG-014 — New record wizard (`/app/forms/:formDefinitionId/new`) · Core
- **Audience/permission:** Current user's right to create with this form; permission to select owning team verified separately.
- **Sections:** Secure four-column form renderer, required fields, dependent options, repeating sections, related lists where permitted, file-upload widgets, owner selection and initial shares.
- **Actions:** Save draft, validate, submit, create optional child items, select owner and scoped recipients within allowed scope.
- **Async/edge:** Queue uploads independently; saving cannot assume an unfinished upload is committed. Recheck published version, enabled options, encryption floor and ownership; partial save or conflict clearly reported.

#### PG-015 — My records and authorized search (`/app/records`) · Core
- **Audience/permission:** Discover permission across owned, team and shared records only.
- **Sections:** Filter builder (only searchable authorized fields), presets, selected form/category, paginated/verified results, safe columns and sorting.
- **Actions:** Search, clear, save personal query (without sensitive terms), open permitted result, initiate authorized export.
- **Async/edge:** Filters/results/available columns refresh separately. Trusted-search completeness restrictions apply; on unavailable verification show a clear failure instead of potentially incomplete results.

#### PG-016 — Record overview and authorized projection (`/app/records/:formRecordId`) · Core
- **Audience/permission:** Record discover/read, record access challenge, content integrity, field/document scope and effective tenant.
- **Sections:** Allowed record header/status, authorized field sections, permitted child collection cards, permitted document summary, safe action bar.
- **Actions:** View/edit if allowed, view child list, open sharing and activity, refresh each section without reloading unrelated content.
- **Async/edge:** API returns *only* authorized fields and child references. Never show counts/names of hidden values. Signature or root mismatch quarantines view; expired challenge re-verifies.

#### PG-017 — Record editor (`/app/records/:formRecordId/edit`) · Core
- **Audience/permission:** Edit permission for each field and permitted form version; ability to see a value does not imply edit permission.
- **Sections:** Version-aware editable projection, old invalid dropdown selections with explicit replace-before-save, validation summary, revisions/unsaved changes, attachment upload states.
- **Actions:** Save draft/commit, discard local changes, resolve concurrency conflict, open related child editor; do not override protected history on rendering.
- **Async/edge:** Failed save remains unsaved, exact field errors; when authority/share is revoked during edit, stop protected mutation and clear unauthorized local drafts appropriately.

#### PG-018 — Record related collections (`/app/records/:formRecordId/related/:relationshipDefinitionId`) · Core
- **Audience/permission:** Parent and child discovery permissions resolved independently; relationship metadata must also be authorized.
- **Sections:** Configured list heading, authorized child record rows, allowed summary columns, pagination, status and sort order, per-row action menu.
- **Actions:** Create permitted child, open/edit child, link/unlink, reorder or change sort where authorized; confirmation for unlink without deleting underlying data.
- **Async/edge:** List and action-status cards refresh separately; enforce tenant/type/relationship/cardinality and cycle rules on server; no hidden child counts.

#### PG-019 — Related child record create/edit (`/app/records/:formRecordId/related/:relationshipDefinitionId/new` and `/app/records/:childFormRecordId/edit`) · Core
- **Audience/permission:** Permission to create/edit child and link it to particular parent; child can have its own form version and access challenge.
- **Sections:** Child form renderer (e.g., AccusedAddress), parent context, child ownership/security inheritance summary, list position, protected file controls.
- **Actions:** Save child and link transactionally via approved trusted workflow; revise existing child; return to parent list.
- **Async/edge:** A parent share does not automatically authorize child fields. Handle parent unlink/reparent, conflicts, failed scans, and authorized inheritance changes.

#### PG-020 — Record document collection (`/app/records/:formRecordId/documents`) · Core
- **Audience/permission:** Explicit document/field scope with document view and upload permissions.
- **Sections:** Permitted documents, versions, upload progress, scan status, file metadata safe to disclose, per-document share badge and actions.
- **Actions:** Upload to allowed field/list, download, view history, replace, revoke document share where authorized, request delete/restore.
- **Async/edge:** Each document/list pane independently refreshable; no public object URLs, no hidden filename/count leakage. Downloads re-check current permission and challenge.

#### PG-021 — Secure document viewer and version history (`/app/records/:formRecordId/documents/:fileAttachmentId`) · Core
- **Audience/permission:** Exact file version view/download permission, current trusted manifest and step-up where required.
- **Sections:** Document metadata, authorized version timeline, preview only when approved safe file type and processing status, audit-aware download.
- **Actions:** Open preview, download, switch authorized version, replace/delete/restore when permitted.
- **Async/edge:** Never retain decrypted data in shared caches or analytics; long stream may use server-controlled route outside Worker; fail closed on revocation, tampering, infected file, key outage.

#### PG-022 — Record sharing manager (`/app/records/:formRecordId/sharing`) · Core
- **Audience/permission:** Owner or delegated Share Manager for in-scope resources only.
- **Sections:** Current owner, recipient user/team grants, effective permissions preview, explicit allowlisted fields, repeated-group instances and individual documents, expiration and delegation.
- **Actions:** Grant share, edit scope/role, revoke share, view share lineage, preview what recipient can see. Owner transfer is a separate workflow.
- **Async/edge:** All fields/documents are explicitly represented in share metadata. No further delegation beyond grantor's allowed scope; conflict/revocation propagates. Independent recipient/scope panels refresh.

#### PG-023 — Share details and effective permissions (`/app/records/:formRecordId/sharing/:recordShareId`) · Core
- **Audience/permission:** Authorized Share Manager, Owner or qualified auditor within tenant and share scope.
- **Sections:** Who granted, recipient/team, allowed fields/documents, derived vs direct permissions, upstream chain, expiration, latest revision, safe activity history.
- **Actions:** Edit/revoke within delegable rights, inspect effect of changes, verify current permissions.
- **Async/edge:** Never falsely indicate revocation is complete while trusted projection is behind; distinguish disabled grant from independent parallel grants. Data and audit panels refresh separately.

#### PG-024 — Record security and additional access (`/app/records/:formRecordId/security`) · Core
- **Audience/permission:** Owner or authorized security manager for discretionary challenge settings; cannot weaken mandatory form/org policy.
- **Sections:** Current effective FIDO2 step-up requirement, optional owner passphrase setting, unlock TTL and failed-challenge overview (non-secret), encryption/security status.
- **Actions:** Enable/rotate/disable owner-managed challenge where allowed, request verified re-authentication.
- **Async/edge:** Do not reveal secrets or passphrase hash, and never weaken established organization floor. Changes revoke relevant unlocks immediately.

#### PG-025 — Record activity timeline (`/app/records/:formRecordId/activity`) · Core
- **Audience/permission:** Authorized record-level audit permission; Designer privilege alone insufficient.
- **Sections:** Chronological access/change/download/share events, actor/time/action/result, redacted field diffs, audit correlation IDs, filters.
- **Actions:** Filter, expand safe detail, refresh timeline, view related authorized version/file.
- **Async/edge:** Audit values are subject to redaction; no protected search terms or confidential plaintext. Timeline query and event detail independently refreshable.

#### PG-026 — Personal exports and background jobs (`/app/jobs`) · Core
- **Audience/permission:** Jobs launched by current user and authorized to remain visible.
- **Sections:** Search exports, background processing, upload finalization, job status, progress, expiration and safe result links.
- **Actions:** Cancel authorized job, retry safe operations where server allows, download completed verified export.
- **Async/edge:** Revalidate access when job executes *and* when downloading result; a revoked permission invalidates pending exports. Each job card individually refreshable.

### 16.5 Designer and form-management pages

#### PG-027 — My designs / form designer catalog (`/app/designer`) · Core
- **Audience/permission:** Form design capability for listed definitions, not access to submitted records.
- **Sections:** Forms, drafts, published versions, lifecycle/status, ownership of design, designer assignments, safe usage metadata if authorized.
- **Actions:** Create form, edit draft, compare versions, archive/manage authorized definition; per-item refresh.
- **Async/edge:** No real record values or confidential aggregate counts without additional explicit authority.

#### PG-028 — Create form wizard (`/app/designer/new`) · Core
- **Audience/permission:** `forms.create` in active organization.
- **Sections:** Name, description, category, form version behavior (Historical/Latest/Hybrid), optional template, initial permissions, regional defaults.
- **Actions:** Create draft, choose reusable definitions, open designer canvas.
- **Async/edge:** Form name/key uniqueness, permission limits and safe defaults enforced server-side; no immediate publishing without validation.

#### PG-029 — Form designer canvas (`/app/designer/:formDefinitionId`) · Core
- **Audience/permission:** Assigned Designer on this form.
- **Sections:** Four-column desktop grid, add-field palette, sections, responsive canvas, property inspector, translations, selected control configuration, draft revision indicator, preview.
- **Actions:** Add/delete/reorder fields, same-line/next-line placement, save draft, navigate deeper field settings, start publish review.
- **Async/edge:** Each side panel refreshes independently when server backed; keyboard reorder and 400% zoom; concurrency conflict compares revisions rather than silently overwriting.

#### PG-030 — Field catalog and properties (`/app/designer/:formDefinitionId/fields`) · Core
- **Audience/permission:** Authorized Designer; stable reusable field identities.
- **Sections:** Active and retired field definitions, type, concept, encryption policy, validation, searchability, where used across versions.
- **Actions:** Add new field, reuse historical field, edit allowed version-specific property, retire from future layout, inspect referential impact.
- **Async/edge:** Storage type immutable after data exists; never physically delete historic values; sensitive policy changes require authorized migration.

#### PG-031 — Dropdown options and values (`/app/designer/:formDefinitionId/options`) · Core
- **Audience/permission:** Authorized Designer for form-managed options, with organization catalog permissions checked separately.
- **Sections:** Choice lists, values versus translated display labels, stable option IDs, database sort order, enabled/disabled, historical references.
- **Actions:** Add/retire/reorder options, choose form default where allowed, test disabled historic selections.
- **Async/edge:** Organization restrictions take precedence; never write disabled values; option CRUD/audit panels independently refreshable.

#### PG-032 — Dependent list rule builder (`/app/designer/:formDefinitionId/dependencies`) · Core
- **Audience/permission:** Form Designer for form conditions; OrganizationOwner only for shared organization catalog associations.
- **Sections:** Parent/child dropdown mapping diagram, allowed combinations, exclusion states, fallback/empty states and sample validation.
- **Actions:** Link fields, choose existing organization-approved catalog dependency, preview Canada→Ontario or US→state choices, validate cycles.
- **Async/edge:** Nonexistent/disabled relationships reject saves, hidden parent values never leak to another recipient; separate catalog data refresh.

#### PG-033 — Related form/list designer (`/app/designer/:formDefinitionId/relationships`) · Core
- **Audience/permission:** Designer for parent and authorized child form definitions.
- **Sections:** Related form selector, relationship definition, child summary display columns, min/max cardinality, sort, create/link/unlink/inheritance policy.
- **Actions:** Add AccusedAddressList/AccusedDocumentList, change list settings, test nested children and safe recursion policy.
- **Async/edge:** Type and cycle checks, no cross-tenant relationships; child forms have independent identities/versions/permissions. Preview uses synthetic data.

#### PG-034 — File-upload field settings (`/app/designer/:formDefinitionId/uploads`) · Core
- **Audience/permission:** Assigned Designer subject to tenant security floor.
- **Sections:** Allowed MIME/extension policy, count/size caps, single/multiple mode, scanning, retention, encryption classification, version and collection policies.
- **Actions:** Configure file field; test synthetic upload validation; inspect impact for existing attachment data.
- **Async/edge:** Designer cannot turn off mandatory malware scanning or encryption floor; defaults must not silently expose prior attachments.

#### PG-035 — Conditional rules and validation (`/app/designer/:formDefinitionId/rules`) · Core
- **Audience/permission:** Assigned Designer.
- **Sections:** Typed, non-executable conditional visibility, required rules, calculated fields, validation dependencies, field references and error preview.
- **Actions:** Add/edit/delete rule, test example data, identify cycles and disallowed hidden-field dependencies.
- **Async/edge:** Server compiles/validates rules; no arbitrary JS/CSS/executable expressions; rule preview never uses unauthorized production values.

#### PG-036 — Form localization editor (`/app/designer/:formDefinitionId/translations`) · Core
- **Audience/permission:** Assigned Designer and approved translation contributors.
- **Sections:** English source label and 10 initial locales for field labels, section titles, dropdown labels, instructions, validation/help; missing-translation indicator.
- **Actions:** Edit/preview translations, filter untranslated fields, validate placeholders and RTL behavior.
- **Async/edge:** Missing translation falls back to English; never translate submitted data or uploaded document contents. Each locale section separately refreshable.

#### PG-037 — Responsive and security preview (`/app/designer/:formDefinitionId/preview`) · Core
- **Audience/permission:** Assigned Designer; synthetic data only unless also authorized for specific records.
- **Sections:** Device-size toggle 360/768/1440, 4→2→1 column preview, text scaling, keyboard/screen reader simulation guidance, role/share-scoped synthetic projection and challenge states.
- **Actions:** Switch language, device, role simulation, disabled catalog values and partial-share scenarios.
- **Async/edge:** Simulation must never forge real permissions or fetch hidden actual data; accessible preview errors show why publish is blocked.

#### PG-038 — Publishing review and diff (`/app/designer/:formDefinitionId/publish`) · Core
- **Audience/permission:** Explicit form publish permission; create/edit permission not sufficient.
- **Sections:** Draft vs current published version, added/removed/changed fields, encryption/visibility changes, validation impact, options removed, related forms and translations, affected data counts if authorized.
- **Actions:** Validate, record change reason, publish confirmed immutable version or return to draft.
- **Async/edge:** Publish transaction is atomic with audit and security policy checks; high-risk encryption/security changes need migration approval. Diff and preflight cards refresh independently.

#### PG-039 — Version history and restoration (`/app/designer/:formDefinitionId/versions`) · Core
- **Audience/permission:** Designer history view, publish/restore authorization separately.
- **Sections:** Chronological versions, creator, change reason, immutable JSON summary, semantic diff, restored-from relationship.
- **Actions:** Compare, view snapshot, create a *new* draft from older version, publish restored version; never overwrite a published version.
- **Async/edge:** Restoration does not delete any stored data or undo current encryption/organization security floors; version list/diff refresh independently.

#### PG-040 — Form usage and design permissions (`/app/designer/:formDefinitionId/access`) · Core
- **Audience/permission:** Assigned form Manager or authorized Designer with access-management permission.
- **Sections:** Who can use form to create records, who can design/publish it, authorized team grants, current status/retirement and version behavior.
- **Actions:** Grant/revoke form-use or design access within delegable authority, change version behavior with impact confirmation.
- **Async/edge:** Designer grants do not confer submission data access; no unauthorized counts/record details. Version-behavior transitions preserve historical version IDs.

### 16.6 Organization configuration, users and teams

#### PG-041 — Organization overview (`/app/organization`) · Core
- **Audience/permission:** OrganizationOwner or separately assigned organization-settings viewer.
- **Sections:** Safe branding preview, country and region, effective default formatting/language, enabled catalogs summary, team directory status, authentication-policy status, administrative checklist.
- **Actions:** Open specific settings page, refresh each card separately.
- **Async/edge:** No automatic record access, confidential audit content or encryption keys shown.

#### PG-042 — Branding, logo and typography (`/app/organization/branding`) · Core
- **Audience/permission:** OrganizationOwner or branding administrator.
- **Sections:** Logo uploader/crop safe preview, brand name, approved black-default button/theme combinations, accessible contrast checks, typography scale, live component gallery.
- **Actions:** Upload/replace logo, test theme, publish/revert branded settings, preview dark/light surfaces.
- **Async/edge:** Server verifies WCAG 2.2 AA contrast and readable focus/disabled states; no arbitrary CSS/JS. Branding rev refreshes; failed upload does not corrupt current logo.

#### PG-043 — Organization regional defaults (`/app/organization/preferences`) · Core
- **Audience/permission:** OrganizationOwner.
- **Sections:** Country/region, IANA timezone, default language, date/number formatting, time example and country-centric ordering preview.
- **Actions:** Choose country and region, select allowed default timezone, save with reason/optimistic concurrency.
- **Async/edge:** Canadian organizations prioritize but do not automatically exclude Canadian/non-Canadian options; explicit disable rules separately restrict choice. Cannot set disabled default.

#### PG-044 — Organization catalogs and availability (`/app/organization/catalogs`) · Core
- **Audience/permission:** OrganizationOwner with catalog-management capability.
- **Sections:** Supported countries/Europe region, timezones, language and date/number formats, enabled vs disabled catalog options, prioritized sort order, history impact.
- **Actions:** Enable/disable, bulk change, select valid default, filter by region/country and see historical use warning.
- **Async/edge:** Disabled values hidden for new selection; historical values visible as unavailable and prevented on affected saves. Other tenants unaffected; separate sections refresh independently.

#### PG-045 — Organization-dependent mappings (`/app/organization/catalogs/dependencies`) · Core
- **Audience/permission:** OrganizationOwner.
- **Sections:** Country→province/state, region→country and optional custom dependent-list maps; mapping preview, disabled child options and circular-relationship errors.
- **Actions:** Create/update/remove mappings, reorder options, validate association before publishing.
- **Async/edge:** Never infer Europe is a country; keep stable country/province IDs and historical selections. Mapping/preview refresh independently.

#### PG-046 — Organization data protection floor (`/app/organization/security/data`) · Core
- **Audience/permission:** OrganizationOwner plus additional high-assurance permissions/approvals for security policy changes.
- **Sections:** Minimum encryption requirements, file protection defaults, PII classifications, record challenge minimum, retention/legal-hold summary, drift/impact assessment.
- **Actions:** Propose stronger controls, view validation and migration status, submit separately approved high-risk policy change.
- **Async/edge:** No UI to silently weaken irreversible settings or decrypt contents; policy effectiveness verified against independent authority.

#### PG-047 — Organization identity and FIDO2 policy (`/app/organization/security/authentication`) · Core
- **Audience/permission:** OrganizationOwner with approved authentication-policy privileges and independent second approver.
- **Sections:** IdP binding, supported passkey/FIDO2 methods, Preparing→Ready→Enforced states, provider verification, enrollment readiness, recovery and second approval.
- **Actions:** Prepare, run readiness verification, approve, enforce; once enforced provide status/investigation only, not downgrade.
- **Async/edge:** Distinguish configured policy from externally verified enforcement; provider outage/drift fails closed and alerts. Never manage Entra passwords locally.

#### PG-048 — Team directory (`/app/organization/teams`) · Core
- **Audience/permission:** Authorized team viewers/managers; no automatic view of records owned by teams.
- **Sections:** Authorized team list, membership counts only where permitted, ownership-indicator summary without confidential data.
- **Actions:** Create permitted team, search, open team detail; each team/list panel refresh independently.
- **Async/edge:** Team name lookup cannot reveal private cross-tenant groups.

#### PG-049 — Team details and membership (`/app/organization/teams/:teamId`) · Core
- **Audience/permission:** OrganizationOwner/team manager and team members with separately allowed viewing rights.
- **Sections:** Team name and status, membership roster, manager role, effective capabilities, record-ownership impact warning, audit subset.
- **Actions:** Add/remove member, change team manager, deactivate team only after reassigning owned records.
- **Async/edge:** Membership changes are trusted security commands, not editable business DB fields. Revocation invalidates effective access and pending exports; roster and activity independently refresh.

#### PG-050 — Organization user directory (`/app/organization/users`) · Core
- **Audience/permission:** User directory management/view privileges.
- **Sections:** Tenant-bound users, status, IdP binding metadata (nonsecret), team affiliation, assignment summary.
- **Actions:** Search, invite/provision through approved identity flow, open user detail, start deactivation with ownership-impact check.
- **Async/edge:** Never use email as identity key or disclose users across organizations. Search and user-status panels refresh separately.

#### PG-051 — User details and system roles (`/app/organization/users/:userId`) · Core
- **Audience/permission:** OrganizationOwner or separately authorized role administrator, step-up for privileged grants.
- **Sections:** User identity, active status, teams, Designer/OrganizationOwner/SystemAdministrator roles where permitted, form-specific grants, ownership transfer prerequisites, security changes timeline.
- **Actions:** Grant/revoke allowed role, suspend access, trigger approved ownership reassignment, view audit with redaction.
- **Async/edge:** Cannot self-grant privilege; authority approves roles and second approval where required. Changes effective only when trusted revision reconciles.

### 16.7 Security operations, auditing and recovery

#### PG-052 — Security administration home (`/app/security`) · Controlled Operations
- **Audience/permission:** Tenant SystemAdministrator, delegated security operator or auditor; each widget separately authorized.
- **Sections:** Ownership transfer requests, role-policy changes, incidents, authority projection health, audit archive health, stuck revisions, trusted search status.
- **Actions:** Open specific protected operation, refresh each widget; no automatic record contents.
- **Async/edge:** Strong step-up, no privileged plaintext in dashboard counts/tooltips; degraded security state blocks unsafe writes.

#### PG-053 — Ownership transfer workflow (`/app/security/ownership`) · Core
- **Audience/permission:** SystemAdministrator with `records.transfer_ownership`, or permitted owner-initiated request subject to policy.
- **Sections:** Authorized record selector, current authoritative user/team owner, proposed owner, impact and live permissions, required transfer reason, approvals.
- **Actions:** Submit transfer, approve/reject where authorized, inspect operation state Pending→Authorized→Applied→Completed.
- **Async/edge:** Verified current owner comes from authority, not business DB. Before/after panels independently refresh; failed projection never silently grants access.

#### PG-054 — Canonical audit explorer (`/app/security/audit`) · Core
- **Audience/permission:** `audit.system.view` or smaller explicitly authorized audit scope.
- **Sections:** Chronological events, date/actor/action/result/entity filters, correlation view, redacted field diffs, independently verifiable archive/checkpoint status.
- **Actions:** Filter/export permitted audit subset, inspect event, refresh events and archive integrity separately.
- **Async/edge:** No full unredacted data values, search secrets or documents leaked; retained sequence ordering is not falsely claimed as exact transaction-commit order.

#### PG-055 — Audit event detail (`/app/security/audit/:auditEventId`) · Core
- **Audience/permission:** Explicit event-scope audit permission; access to event does not grant access to target record contents.
- **Sections:** Structured actor/action/outcome, UTC/local timestamp, safe before/after metadata, correlation and event-schema version, archive verification receipt.
- **Actions:** Follow authorized target link, verify receipt/status, copy safe event identifier.
- **Async/edge:** Redaction applies to both JSON and rendered details. Corrupt/missing event evidence marked unverified, not silently omitted.

#### PG-056 — Security incidents and integrity alerts (`/app/security/incidents`) · Controlled Operations
- **Audience/permission:** Incident responders and qualified auditors.
- **Sections:** HMAC/Merkle mismatches, replay alerts, permission policy drift, HSM faults, unauthorized DBA activity, enrollment drift, remediation state.
- **Actions:** Acknowledge/assign, open incident, begin controlled quarantine/recovery workflow, refresh independently.
- **Async/edge:** No automatic repair/resign button for invalid signatures. Display current severity/state without protected document content.

#### PG-057 — Authority consistency and trust health (`/app/security/authority`) · Controlled Operations
- **Audience/permission:** Separate Level 2 security operators; not general tenant administrators.
- **Sections:** Authority revision, SpiceDB applied revision, fail-closed gate, trusted checkpoints, signing provider state, security journal/archive lag.
- **Actions:** Diagnose, run read-only verification, request controlled reconciliation with approval; independently refresh components.
- **Async/edge:** No raw HSM keys, unrestricted signing endpoints, or manual permission-table changes. Authority outage is a safe locked state.

#### PG-058 — Disaster recovery operations (`/app/security/recovery`) · Controlled Operations
- **Audience/permission:** Separately approved recovery operators, with two-person authorization and current FIDO2 assurance.
- **Sections:** Recovery epoch, component backup status, replayable journal, manifest verification, SpiceDB rebuild, HSM key recovery checks, quarantine and release gates.
- **Actions:** Begin approved recovery, view phase results, require two distinct approvals for reopening, download safe operator evidence.
- **Async/edge:** This screen is a view onto controlled operator workflows, not an unrestricted browser backup/restore button. Old deployments are fenced; no stale state accepted.

#### PG-059 — Job and queue operations (`/app/security/operations`) · Controlled Operations
- **Audience/permission:** Authorized infrastructure operators only.
- **Sections:** Durable outbox backlog, failed scans/encryption, archive forwarding lag, projection jobs, bounded retries, idempotency and poison messages.
- **Actions:** Inspect, retry via safe idempotent command, quarantine job and acknowledge alarms; per-queue refresh.
- **Async/edge:** Never use job retry to bypass authorization or mutation preconditions; no access to raw files or secrets by default.

### 16.8 Shared exceptional-state pages and overlays

#### PG-060 — No permission (`/app/forbidden`) · Core
- **Audience/permission:** Any user without required capability, or whose access has been revoked.
- **Sections:** Generic access-not-available message and safe navigation back to authorized home; no object title, filename, row count or existence hint.
- **Actions:** Back to dashboard, request access through approved generic workflow where provided.
- **Async/edge:** Do not disclose whether ID exists; if a challenge is available route to verification rather than rendering protected content.

#### PG-061 — Not found / removed (`/app/not-found`) · Core
- **Audience/permission:** Any caller for unknown or non-discoverable route/resource.
- **Sections:** Generic not-found message, help link and safe return action.
- **Actions:** Back, dashboard, help.
- **Async/edge:** Responses do not enable enumeration of record IDs, unpublished forms or hidden child records.

#### PG-062 — Service degraded / integrity quarantine (`/app/unavailable`) · Core
- **Audience/permission:** Any affected user; text tailored only to safe publicly available incident context.
- **Sections:** Distinguish network outage, authority unavailable, stale epoch, document scan incomplete, security-integrity quarantine and planned maintenance without exposing secrets.
- **Actions:** Safe retry or support correlation ID; sign out if session is stale.
- **Async/edge:** No cached protected data release while failing closed; card-level partial degradations remain inline on their owning pages.

#### PG-063 — Concurrent edit conflict (`/app/conflict`) · Core
- **Audience/permission:** Authorized editor whose last save failed the expected revision or version check.
- **Sections:** Their unsaved changes, permitted remote revision summary, field-level merge guidance where safe, record/form version and who changed it if authorized.
- **Actions:** Reload latest authorized values, apply permitted field-by-field reconciliation, discard or save-as-draft where supported.
- **Async/edge:** A revoked actor cannot retrieve newer protected content via conflict diff. No last-write-wins security mutations.

### 16.8A Additional governance and utility pages

#### PG-064 — Organization provisioning / initial configuration (`/app/organization/setup`) · Core
- **Audience/permission:** Trusted provisioning administrator for newly created organization, or OrganizationOwner completing a previously authorized setup; regular users cannot self-create tenants.
- **Sections:** Organization legal/display identity, regional and Canada-first preference suggestions, default IANA zone, permitted catalogs, initial OrganizationOwner, IdP binding/readiness, security baseline and branding defaults.
- **Actions:** Save staged configuration, validate provider tenancy and approved organization defaults, request activation through independently authorized process.
- **Async/edge:** Never accept organization ID from untrusted browser selection as authorization. Failure leaves tenant unactivated, not permissively usable; region/catalog and security panels refresh independently.

#### PG-065 — Request or accept access (`/app/access-requests`) · Core
- **Audience/permission:** Authenticated actor with access-request privilege; grant approval requires Manager/Owner authority or approved team workflow.
- **Sections:** My pending requests, invitations/shares awaiting acceptance where product policy requires it, requests I may approve, request status and bounded reason text.
- **Actions:** Submit access request through a trusted invitation or known safe resource reference, approve/deny within delegable field and document scope, cancel request.
- **Async/edge:** Never reveal target resource existence to a user who cannot discover it. Request acceptance never bypasses record challenge or current share scope; all lists and request status refresh independently.

#### PG-066 — My access and shared-with-me (`/app/my-access`) · Core
- **Audience/permission:** Current user, plus active team-derived and direct shares verified against the trusted authority.
- **Sections:** Discoverable records shared directly, team-derived access, pending time-limited shares, own records, effective Reader/Contributor/Manager rights and safe expiry warnings.
- **Actions:** Open authorized record, inspect own effective permission source without disclosing other principals, request permitted additional access.
- **Async/edge:** Team revocation and upstream delegation revocation remove results even when cached; separate direct/team/owned panels with independent refresh.

#### PG-067 — Data retention and legal holds (`/app/organization/security/retention`) · Controlled Operations
- **Audience/permission:** Organization governance administrators with separate retention policy authorization and approval; ordinary record Owners cannot bypass holds.
- **Sections:** Organization retention rules, archival deadlines, file versions, legal holds, deletion eligibility, scheduled purge jobs and supporting audit evidence.
- **Actions:** Propose policy and hold changes, review impact, approve allowed purge request through protected workflow; no bulk arbitrary SQL delete.
- **Async/edge:** Legal holds override owner deletion, signing/manifest history retained as required, background purge jobs observe current authorization and are auditable. Refresh retention, holds and job sections separately.

#### PG-068 — Authorized export builder (`/app/exports/new`) · Core
- **Audience/permission:** User with explicit export permission for selected record scope and fields; read permission alone does not always permit bulk export.
- **Sections:** Approved search scope, permitted projected columns, output format/date localization, export encryption and expiry policy, expected job size and consent notice.
- **Actions:** Create idempotent export job, track on My Jobs, download after fresh authorization and integrity verification.
- **Async/edge:** Never include hidden fields in export headers or filters. Use trusted-search constraints, quotas and reauthorization during execution; no full dataset accumulation in browser memory.

#### PG-069 — Design-system component gallery (`/dev/ui-gallery`) · Development-only
- **Audience/permission:** Local/test build or explicitly secured internal design QA; unavailable in public production deployment.
- **Sections:** Black primary and organization token button states, high-contrast checkbox/radio/select/text inputs, error summary, badges, cards, tables, RTL labels, focus outlines, upload tray, responsive breakpoints.
- **Actions:** Switch approved brand tokens, typography presets, color contrast/a11y overlays, 320/768/1440 px and 400% zoom previews; capture visual tests.
- **Async/edge:** Strictly synthetic data. This gallery is not an access-control or form-preview bypass; screenshot and automated axe tests are required.

### 16.9 Critical end-to-end UX journeys (must be implemented and tested)

- **J-01 New user:** External sign-in → safe organization chooser/bootstrap → role-aware dashboard → permitted form catalog → create first record → become default owner → see only own record.
- **J-02 Designer:** Design form → add 4-column fields, conditional rules, dependent dropdown and file settings → preview mobile/RTL → publish with semantic diff → record data remains inaccessible to Designer without independent grants.
- **J-03 Owner share:** Open record → Sharing → grant team access to selected fields and single document → simulate recipient projection → recipient sees *only* those resources → revoke → old cached views/exports stop authorizing.
- **J-04 Related data:** Create Accused → add two AccusedAddress child records → link two AccusedDocument files → reorder, edit, unlink without deleting unrelated child data; parent share does not reveal child automatically.
- **J-05 Upload continuity:** Start large encrypted attachment upload → navigate across record, dashboard, organization settings → observe persistent tray/progress → server scan/encrypt/finalize → success or actionable failure independent of route.
- **J-06 Historical choice:** Organization disables America/Vancouver (or an option in a data field) → previous selection remains visibly labeled unavailable → editing affected selection requires a valid replacement → unrelated historical fields do not unexpectedly block saving.
- **J-07 Ownership transfer:** Authorized operator proposes user→team transfer → trusted authority validates → queue reconciles → old owner loses owner-derived rights; independent shares remain as defined.
- **J-08 Compromised DB:** Directly change owner/relationship/field in business DB fixture → UI cannot show protected data → integrity alert and quarantine; search does not silently claim completeness.
- **J-09 Outage:** SpiceDB or signing provider unavailable during access/download/search → fail closed, show safe scoped error; unaffected static/help content remains available.
- **J-10 Accessibility:** Complete sign-in through document upload and form publishing with keyboard/screen reader; test 320 px, 400% zoom, multiple locales, dark/light approved tokens and text-size changes.
- **J-11 Recovery:** Restore business database older than revocation → operator screen displays reconciliation; authority uses trusted current security state; user cannot exploit stale database projections.
- **J-12 Team delegated sharing:** Share Manager with a partial grant delegates subset → recipient gets only that subset → upstream share revoked → descendant rights are removed or effectively denied even before business projection catches up.

### 16.10 Per-page traceability and definition of done

Claude must create `docs/ui/SCREEN_IMPLEMENTATION_MATRIX.md` during implementation. Each `PG-###` requires a row with: route; owning feature/package; minimum privilege; related API/OpenAPI operation; independent async region query keys; mutation commands; loading/empty/error/denied and integrity states; keyboard/axe test ID; Playwright test ID; implemented/verified status. **Do not mark a page complete solely because the route exists or a static screenshot was built.**

- Treat **PG-001 through PG-055**, **PG-060 through PG-066**, and **PG-068** as required functional screens for the complete enterprise application, with capability gating; **PG-056 through PG-059** and **PG-067** are controlled operational screens required for Level 2 deployment/readiness, but may live in a separate security-operator frontend rather than the ordinary tenant SPA. **PG-069** is an internal development-only tool, never a public production route.
- If two pages are implemented as tabs within one route, preserve distinct deep links, server permissions and acceptance tests. It is acceptable to consolidate presentation but **not** to omit capabilities or weaken access checks.
- Every page containing one or more server-backed cards or sections must demonstrate *independent refresh* of those regions. Full-page-only refresh does not meet the requirement.
- Before reporting completion, run end-to-end tests for J-01 through J-12, all page permission gates, responsive layouts, organization theme contrast, and background upload navigation. Tests for undeployed Level 2 infrastructure must be marked **pending**, never falsely declared passing.

**End of controlling web design specification.**

---

## 17. ADR-ARCH-001 screen ownership and feature delivery matrix (NORMATIVE)

Every screen in Section 16 belongs to **exactly one** frontend feature. `app/` may register its route and compose feature entrypoints, but it is not a catch-all owner. `support` owns cross-application informational/error/presentation pages, not business decisions. `security-operations` represents a separately authorized operational surface; it **does not host the independent Authority implementation**. The `form-catalog` pages call the Forms module through published contracts; the `form-designer` owns draft publishing UX, not submitted data.

| Screen | Owning frontend feature | Screen name |
|---|---|---|
| PG-001 | `identity` | Federated sign-in handoff |
| PG-002 | `identity` | Authentication callback and bootstrap |
| PG-003 | `identity` | Organization chooser |
| PG-004 | `support` | Welcome / first-use checklist |
| PG-005 | `identity` | Session step-up / record challenge |
| PG-006 | `dashboard` | Role-based dashboard |
| PG-007 | `user-preferences` | Personal preferences |
| PG-008 | `identity` | My account and sign-in methods |
| PG-009 | `support` | Notifications and task inbox |
| PG-010 | `jobs` | Global Upload Center |
| PG-011 | `support` | Help, accessibility, and application support |
| PG-012 | `form-catalog` | Permitted form catalog |
| PG-013 | `form-catalog` | Published form details |
| PG-014 | `form-records` | New record wizard |
| PG-015 | `search` | My records and authorized search |
| PG-016 | `form-records` | Record overview and authorized projection |
| PG-017 | `form-records` | Record editor |
| PG-018 | `related-forms` | Record related collections |
| PG-019 | `related-forms` | Related child record create/edit |
| PG-020 | `documents` | Record document collection |
| PG-021 | `documents` | Secure document viewer and version history |
| PG-022 | `sharing` | Record sharing manager |
| PG-023 | `sharing` | Share details and effective permissions |
| PG-024 | `sharing` | Record security and additional access |
| PG-025 | `audit` | Record activity timeline |
| PG-026 | `jobs` | Personal exports and background jobs |
| PG-027 | `form-designer` | My designs / form designer catalog |
| PG-028 | `form-designer` | Create form wizard |
| PG-029 | `form-designer` | Form designer canvas |
| PG-030 | `form-designer` | Field catalog and properties |
| PG-031 | `form-designer` | Dropdown options and values |
| PG-032 | `form-designer` | Dependent list rule builder |
| PG-033 | `form-designer` | Related form/list designer |
| PG-034 | `form-designer` | File-upload field settings |
| PG-035 | `form-designer` | Conditional rules and validation |
| PG-036 | `form-designer` | Form localization editor |
| PG-037 | `form-designer` | Responsive and security preview |
| PG-038 | `form-designer` | Publishing review and diff |
| PG-039 | `form-designer` | Version history and restoration |
| PG-040 | `form-designer` | Form usage and design permissions |
| PG-041 | `organizations` | Organization overview |
| PG-042 | `organizations` | Branding, logo and typography |
| PG-043 | `organizations` | Organization regional defaults |
| PG-044 | `organizations` | Organization catalogs and availability |
| PG-045 | `organizations` | Organization-dependent mappings |
| PG-046 | `organizations` | Organization data protection floor |
| PG-047 | `organizations` | Organization identity and FIDO2 policy |
| PG-048 | `teams` | Team directory |
| PG-049 | `teams` | Team details and membership |
| PG-050 | `identity` | Organization user directory |
| PG-051 | `identity` | User details and system roles |
| PG-052 | `security-operations` | Security administration home |
| PG-053 | `security-operations` | Ownership transfer workflow |
| PG-054 | `audit` | Canonical audit explorer |
| PG-055 | `audit` | Audit event detail |
| PG-056 | `security-operations` | Security incidents and integrity alerts |
| PG-057 | `security-operations` | Authority consistency and trust health |
| PG-058 | `security-operations` | Disaster recovery operations |
| PG-059 | `security-operations` | Job and queue operations |
| PG-060 | `support` | No permission |
| PG-061 | `support` | Not found / removed |
| PG-062 | `support` | Service degraded / integrity quarantine |
| PG-063 | `support` | Concurrent edit conflict |
| PG-064 | `organizations` | Organization provisioning / initial configuration |
| PG-065 | `sharing` | Request or accept access |
| PG-066 | `sharing` | My access and shared-with-me |
| PG-067 | `organizations` | Data retention and legal holds |
| PG-068 | `exports` | Authorized export builder |
| PG-069 | `support` | Design-system component gallery |

### 17.1 Feature-to-backend traceability and permitted dependencies

- `form-records` → `FormRecords` use cases; `related-forms` → `RelatedForms`; `documents` → `Documents`; `sharing` → `Sharing`; `form-designer` → `FormDesigner` and published `FormDefinitions` contracts; `form-catalog` → published `FormDefinitions` read contracts; `search` → `Search`; `audit` → `Audit`; `dashboard` → `Dashboard`; `exports` → `Exports`.
- `organizations` → `Organizations` (including catalog, branding, regional policy); `teams` → `Teams`; `identity` → `Identity`; `user-preferences` → `UserPreferences`; `jobs` → `Jobs`; `security-operations` → restricted API adapters that consult Authority contracts. `support` may use safe app-level status services; it must not bypass protected record APIs.
- The React code for each page belongs in `apps/web/src/features/<owner>/pages/`; its feature owns its API hooks, interactive sections, tests and optional nested routes. Reusable UI stays in `packages/ui`, never copied into each feature. App shell composition imports only feature public exports.
- Backend feature owners expose narrow use-case contracts and keep implementation details private. A feature needing a record's permissions asks the authorized access port—not the `FormRecords` database or a local user/team table. A feature needing a document requests an authorized document projection—not a raw object-storage link.
- Populate and verify `docs/architecture/FEATURE_OWNERSHIP_MATRIX.md` and `docs/ui/SCREEN_IMPLEMENTATION_MATRIX.md` with owner, use case/API, table ownership, permission/capability, async card query keys, audit event, tests and deployment gate. Require one owner per `PG-###` in CI.

### 17.2 Required vertical-slice delivery checklist

For each implemented feature/use case, Claude must deliver a working route or endpoint as appropriate; authoritative permission integration, request/response validation, concurrency/idempotency, unit and integration tests, feature-local API client/hook, screen component and accessibility/Playwright verification, audit event traceability, and operational/error states. For asynchronous cards preserve each card's own data query and refresh. For uploads preserve the root upload manager and server durable workflow across SPA navigation. Do **not** mark a screen complete solely because its route exists or because its data comes from an unrestricted mock.

**Architecture fitness functions (release-blocking):** Compile-time .NET assembly dependencies; domain/application namespace rules; cross-feature `Public`-only imports; browser feature-only API coordination; no forbidden direct Authority/SpiceDB/DB calls; shared UI package no business dependency; no duplicate table owners; no forbidden one-word database columns; all 69 routes mapped and tested; production Authority deployment isolated from API and Worker.



## 18. PB-06 — Frontend/API implementation contract

**Accepted:** ADR-PB-006. All frontend features use generated TypeScript clients produced from deterministic versioned ASP.NET Core 10 Minimal API OpenAPI artifacts. Integrate these with `packages/work-queue` and TanStack Query; do not create ad hoc feature-local HTTP transports or wire schema copies. Keep `packages/ui` and theme components independent of business/API contracts.

Use standardized RFC 9457 Problem Details safe `error_code` values for localized accessible error UX; no confidential resource disclosure. Request coordinators enforce tenant/session/security-epoch cancellation, bounded concurrency and Idempotency-Key on approved retryable mutations. Use ETags/`If-Match` for record updates. Cursor pagination retains permission binding and never asserts completeness unless ADR-PB-004's verified search boundary succeeds.

Upload views initiate/resume upload sessions, send verified bounded chunks, finalize idempotently and show distinct *transferring*, *received*, *quarantined/scanning*, *committed*, and *failed* states from server truth. `202 Accepted` means queued, not completed. UI can navigate while uploads run within the SPA; closing every browser tab cannot be guaranteed to continue transfer. Internal Trust Authority methods are never called directly by browser code.

Tests must verify that generated SDK builds against published OpenAPI, authenticated BFF cookies and CSRF work with the queued transport, stale writes and duplicate submissions cannot silently succeed, and unauthorized/partial records are never disclosed. Contract breakage fails CI. Refer to `docs/api/PB06_API_CONTRACT_STANDARD.md`.


## 19. PB-07 — Binding request coordinator and upload UX acceptance

**Approved:** ADR-PB-007 supersedes any older example that implies one global serial HTTP lane, worker-based authorization, guaranteed upload continuation after browser close, or durable background jobs held only in browser memory. Implement `docs/architecture/PB07_BROWSER_REQUEST_COORDINATOR_STANDARD.md` and execute the 24 scenarios in `docs/testing/PB07_REQUEST_UPLOAD_ACCEPTANCE_TESTS.md`.

Each data-dependent dashboard card/page section fetches independently through the generated coordinator-backed client and TanStack Query with a local refresh button and accessible busy/error states. The scheduler has fairness, global/per-lane concurrency budgets, backpressure and cancellation. Interactive saves and active-card reads must remain responsive while large files are transferred. All protected operations are re-authorized by the BFF/Trust Authority, regardless of worker context.

A persistent SPA Upload Center shows queued, transferring, interrupted, awaiting-file-reselection, server-received, scanning, encrypting, committing, ready, failed, and cancelled stages from authoritative upload/session status. Route changes cannot destroy transfer state. A browser reload may require file re-selection; closing all tabs can stop client bytes. Never persist protected file bytes or bearer credentials in IndexedDB. After secure full receipt, durable backend jobs continue safely, and `Ready` is announced only after successful scanning, encryption, signed manifest and audit commitment. Logouts, tenant switches, revoked grants or stale authorization epochs cancel/quarantine pending work and clear scoped UI caches.

Do not mark PB-07 implemented until Chrome/Edge/Firefox/Safari cross-browser contract tests and live-stack failure injection pass, and until PB-09 performance budgets are explicitly approved and measured. The specification may be complete while runtime proof remains outstanding.


## 20. PB-08 — Identity-owned page integration requirements

ADR-PB-008 and `docs/identity/PB08_IDENTITY_ASSURANCE_STANDARD.md` govern the existing PG-001/002/003/004/005/047 and PG-058 identity, organization-security and recovery screens. The login UI **redirects to the Entra workforce provider**, not a local password/passkey handler. The BFF owns all OIDC state and session secrets; the frontend receives only safe session/capability DTOs. Never merge same-email user accounts, infer app Organization membership from issuer tenant/domain, or expose internal Authority tokens.

The organization selector obtains only currently permitted organizations from server/Authority. Switching requires server reauthorization and new context, cancellation/discard of stale query and upload work from previous tenant, cache and recent-step-up invalidation, and accessible status messaging. FIDO2 policy screen distinguishes `Preparing`/`Ready`/`Enforced`, displays readiness and two-person approval, and blocks irreversible enforcement without independently proved recovery. The challenge page uses an IdP redirect/claims challenge with freshness verification, not a client-controlled `mfa` flag or imitation passkey popup. Any unavailable or unverifiable assurance must deny protected operations with a safe accessible error.

All authorization remains in .NET API + independent Authority; Designers, Organization Owners and System Administrators do not gain protected records solely from their roles. Real Entra staging tests (not only test-provider mocks) gate production. `docs/testing/PB08_IDENTITY_SECURITY_TESTS.md` supplies the negative cases.


## 21. PB-09 — UI scale and performance acceptance

React feature modules must support the ADR-PB-009 envelope without client-side expansion of all related records, hidden protected fields or all record versions. Paginate/virtualize 250-item related-form collections and 100-document lists as needed; data-backed cards must fetch asynchronously and expose isolated refresh/loading/failure states. Operate two concurrent document uploads per user using bounded transfer lanes so no giant file starves interactive operations; a controlled 2-GB file must remain resumable and scanned before record visibility.

Benchmark the visible shell p95 <2s and each dashboard card retrieval p95 <2s using explicit target devices, network conditions, tenant data sizes and full authorization paths; log p50/p95/p99 separately from server-only metrics. Render/accessibility correctness and tenant/session isolation are non-negotiable under load. Use `docs/performance/PB09_BENCHMARK_ACCEPTANCE_MATRIX.csv` and the agreed security gates. No benchmark target is considered achieved merely by stating it in a UI specification.


## 22. PB-10 — UI data-residency controls and consent/retention boundaries

The UI must not make direct browser calls to out-of-residency upload processors, search vendors, translation APIs, analytics, crash reporting or file-preview services that might receive customer document/field/identity metadata. All document chunks go through the approved application path and Canadian-region storage/quarantine under the authorized upload protocol. Any future direct-to-Blob protocol must preserve Canada-only locations, scoped limited-life grants, malware scanning and the Level 2 authority gate. Do not expose external document URLs or object keys in client diagnostics.

Use centralized PII-safe telemetry and error mapping. Branding/logo asset storage, tenant analytics, dashboard cards and role-specific settings must obey the same residency matrix; third-party fonts/CDNs must be risk-reviewed and preferably self-hosted in the permitted deployment. Retention, legal hold, privacy rights and controlled exports must use server-enforced organization policy, not editable UI-only toggles. A privacy/retention operator screen is not proof of legal compliance; require documented policy and tested workflows before production. On tenant-switch, logout or user authorization loss, cancel queued uploads and protected reads without leaving unapproved local copies. See ADR-PB-010 and `docs/testing/PB10_DATA_RESIDENCY_ACCEPTANCE_TESTS.md`.


## 23. PB-11 — Frontend delivery, provenance, and release checks

React UI modules and the shared design system must pass pinned tooling/lockfile build, formatting, lint, generated OpenAPI TypeScript client typecheck, unit/contract tests, authorization and security regression tests, and automated accessibility checks before any image is signed or deployed. Production web containers are non-root, minimized, served without external sensitive-data processing/CDN dependencies, configured via safe public runtime settings and deployed from **the approved tested ACR digest**, not a floating tag. No API or frontend package stores secrets in built assets or client-visible environment variables.

The SPA must not display a deployment as successful or safe merely because GitHub Actions reports success: the environment must pass post-deploy smoke tests and independent release evidence approval. Immutable signed image provenance establishes origin, not user- or record-level trust. The browser does not communicate with the internal Trust Authority. Frontend/test pipelines use synthetic fixtures and do not place actual customer identity, record data, attachments or security evidence in GitHub-hosted artifacts. Authority release approval remains isolated from business/web release. See ADR-PB-011 and `docs/delivery/PB11_CICD_ACCEPTANCE_TESTS.csv`.


## 24. PB-12 — Release Security Dashboard and operations UX

The approved Release Security Dashboard is an *operator-only* feature-owned view, not a source of authorization. Render independent, async, refreshable evidence-status cards for: environment build/digest lineage; required tests and latest execution; independent security/Authority deployment approval; open blockers and exception expiries; Canadian evidence custody; and release history. Every card has loading, empty, error, stale-data and access-denied UX, keyboard/focus/screen-reader support, and does not expose restricted customer data.

A release can be shown as `Ready for review`, `Approved`, `Blocked` or `Unknown` only from the authenticated protected policy evaluator, not from frontend calculations or a test JSON field alone. Missing evidence or expired reviewer permissions must clearly display a block. Deep links to protected evidence require fresh authenticated authorization, with no raw signed event bodies exposed to broad admins. Do not introduce a 'force release', 'skip security tests', or 'override critical controls' button.

The UI is a later operator capability; PB-03 executable Stage A testing and PB-11 CI policy gates must exist first. Under the PB-02 first milestone, implement the evidence registry and policy gate before broadening the 69-screen dashboard backlog.


## 25. PB-13 — Import/Export/Offboarding UX

The `imports`, `exports`, and `organization-offboarding` feature modules own their screens and browser-facing `/api/v1` generated clients. Import Center must support CSV/JSON uploads, per-field mapping (including type and version information), dry-run preview, validation summaries, review/approval, per-batch progress, clear partial-success reporting, safe cancellation and reattempt. Child AccusedAddress and associated documents are first-class imported relationships and must not lose form version, retention or encryption metadata.

Export Center offers explicitly authorized scoped record selections, visibly classified file/field scopes, required fresh step-up and independent governance review for tenant-wide export, durable job progress, expiration and controlled encrypted download. Do not suggest an Organization Owner can export protected material by role alone. If a scope is unauthorized, provide a non-enumerating denial rather than a partial unlabelled export. Distinguish lossy CSV convenience from a full-fidelity re-importable JSON+binary archive.

Organization Offboarding supports authenticated request, review, freeze/progress, export receipt, legal-hold review, revocation and detailed disposition states, but **no instant delete-everything** button or bypass of immutable security journal and audit retention. Use explicit two-person approval and safe re-authentication for destructive transitions, with an independent trusted status source; a forged BusinessDb status cannot show completed deletion. All sections/cards load asynchronously and have refresh, loading, error, empty, cancellation and access-denied states. Track route and feature ownership in the existing 69-screen catalog before creating new routes.

Run the PB13 acceptance scenarios and human accessibility review before production tenant onboarding; do not insert empty full-page placeholder routes instead of working first-release workflows. PB-02 Stage A/B retain priority.


## 26. PB-14 — Final enterprise UI design validation and three-screen acceptance

Binding decision ADR-PB-014 and `docs/ui/PB14_ENTERPRISE_DESIGN_VALIDATION_STANDARD.md` govern `packages/design-tokens`, `packages/ui`, the protected PG-069 gallery, and three functional representative journeys in `docs/ui/PB14_REPRESENTATIVE_SCREEN_CONTRACTS.md`. **Do not scale implementation to every PG-### screen until design review of the representative screens and executed accessibility checks pass.** Every page remains bound to the owning feature and documented authorization; the UI cannot invent policy.

The gallery must cover normal/hover/focus/active/checked/selected/loading/error/disabled appearances, theme validation, keyboard and focus management, user-overridable text size and safe organization logos. Contrast requirements apply to all effective foreground/background/state combinations and essential non-text indicators, not one generic primary button swatch. Unsafe settings are rejected by API validation and never rendered as live theme values. Default is near-black action background `#111111` with white text; user may override accessible typography. Validate at widths 320, 768, 1440; zoom 200% and 400%; mouse, touch, keyboard, NVDA and VoiceOver; forced-colors where supported. Automated contrast and Playwright/axe checks are mandatory but not substitutes for manual review.

Build actual role-aware dashboard cards with independent refresh; Accused records with editable AccusedAddress child entries, versioned document attachments and an upload manager surviving SPA route navigation; and form designer with 4/2/1 responsive columns, accessible reordering and safe synthetic preview. Tests and documented human approval are required before broad UI implementation. Reference helper files `docs/ui/PB14_COLOR_CONTRAST.reference.ts` and `docs/ui/PB14_DESIGN_TOKENS.reference.css` are **non-production starters**, not proof of legal AODA compliance.
