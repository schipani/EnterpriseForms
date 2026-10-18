# ADR-PB-014 — Enterprise UI Design System and Representative-Screen Validation

- **Status:** Accepted by product owner
- **Approved:** 2026-10-17
- **Finding:** PB-14; final pre-build readiness finding
- **Implementation status:** Design requirements only; gallery, working screens and actual accessibility results are **not yet implemented or verified**.
- **Companion standard:** `docs/ui/PB14_ENTERPRISE_DESIGN_VALIDATION_STANDARD.md`
- **Screen definitions:** `CLAUDE-DESIGN.MD` §16 (PG-001 through PG-069)

## Decision

Before building all 69 application screens, implement and validate an enterprise design system plus **three working representative screen flows**: (1) role-aware dashboard PG-006, (2) Accused record overview/edit with related addresses and document collection PG-016/017/018/019/020/021 and upload progress PG-010, (3) form designer canvas PG-029 including relationship configuration PG-033 and responsive preview PG-037. These screens consume real secured feature APIs once the PB-02 Stage A security proof passes; visual mocks alone do not constitute Stage B functionality.

Provide a protected **internal component gallery PG-069** (never public production), illustrating variants and interaction states of all shared components in `packages/ui` and theme tokens in `packages/design-tokens`. The gallery is a validation tool, not an independent feature API or an excuse to create a second UI component library.

### Visual baseline and organization theming

1. Default primary button near-black `#111111` with white `#FFFFFF` text; neutral page surfaces, unambiguous white cards, accessible black-outlined checkboxes, clear keyboard-visible focus and restrained typography. Secondary and destructive controls must retain distinct semantics and accessible states.
2. Organization Owner can upload a safe, validated logo for top-left placement, select approved primary/secondary/foreground/border/focus combinations, and choose bounded text-size preferences. Respect user-level accessibility overrides. Do not accept tenant-authored CSS, arbitrary JS, unsafe SVG, externally hosted logo tracking pixels or raw class interpolation.
3. Require server-side **and frontend** theme validation before activation: relative luminance/contrast tests for every relevant foreground/background pairing and state, text `>=4.5:1` for ordinary text, `>=3:1` for qualifying large text and essential non-text boundaries/indicators, plus current WCAG 2.2 AA focus, reflow, keyboard, error communication and status requirements. No automatic color choice may be considered acceptable solely because one text contrast test passed.
4. Server validates and stores the effective vetted theme as organization-scoped tokens; return a safe fallback for deleted/invalid or inaccessible configurations. Ensure foreground cannot be changed independently to an invalid pairing. AODA/WCAG engineering target is WCAG 2.2 AA; automated tests alone do not constitute legal certification.
5. Continue to honor organization branding, 10-language label localization and user language/timezone/format preferences without translating submitted values or leaking content.

### Accessibility / usability proof

- Validate widths **320 px, 768 px, 1440 px**, with narrow/reflow and orientation scenarios, browser zoom at **200% and 400%**, dynamic type, long labels, empty/invalid form layouts and tablet/mobile navigation.
- Manual keyboard-only interaction, screen reader tests with NVDA (Windows) and VoiceOver (Apple) and other supported combinations; automated axe-core + Playwright checks. Record browser/OS/assistive technology and actual findings.
- Every independently loaded page card and section supports accessible loading/error/empty status and an isolated `Refresh` action (where data-driven), without keyboard focus loss or cross-tenant data leakage; test transport retries and permission revocation during load.
- Verify checkbox label click/touch, focus order, dialog focus trap/return, form errors, tooltips, tables, required inputs, passwordless redirects, masked protected record information, date/localization, upload progress and background completion notices.
- Verify user workflow efficiency (not just screenshot similarity) and have a product/design reviewer approve the three representative flows and at least one keyboard/screen-reader path before broad UI rollout.

### Replaceable UI boundary

All styling stays inside React `packages/ui` / `packages/design-tokens` and Tailwind v4/CSS variables. The .NET domain/application, independent Trust Authority, PostgreSQL contracts, OpenAPI DTOs, schema and encryption requirements are independent of the visual design. Design-system upgrades or UI framework swaps must not change record permission semantics, backend database shape or saved form definitions. Automatic architecture/import checks enforce the boundary.

### Implementation sequence

1. Stage A PB-03 security proof and API contracts are prerequisites for exposing protected data in representative screens. Meanwhile Claude may prototype inert components with synthetic fixtures in a non-production gallery.
2. Define tokens/components and contrast-validation behavior; create test fixtures for accessible organization palettes and malicious/invalid tenant themes.
3. Deliver gallery PG-069 and dashboard, Accused workflow, and designer with real feature-owned API slices and queue behavior (aligned with PB-02 and PB-07).
4. Run and retain `docs/testing/PB14_UI_DESIGN_ACCEPTANCE_TESTS.csv` evidence, representative screenshot/interaction and manual AT review; require approval before bulk implementation of remaining screens.
5. Populate a `docs/ui/SCREEN_IMPLEMENTATION_MATRIX.md` linked to PG-001..069 and status; no routes may be marked implemented solely for having empty placeholders.

## Non-negotiable protections

- Branding cannot override the accessibility floor and user accessibility preferences.
- The Designer role does not automatically grant access to record values; UI previews use synthetic data unless authority grants production data access.
- All record/doc permissions, searchable data and encrypted field rules remain enforced server-side.
- Neither performance targets nor visual approvals waive mandatory Level 2 evidence and release gates.

## Acceptance / release gate

A complete automated-and-manual reviewed design-system report, three working representative flows, accessible-theme rejection/activation tests, responsive/zoom tests, security/cache tests and a product-owner UI sign-off must be present before implementing the remaining page catalog at scale. Production requires PB-12 independently reviewed evidence; paper specifications are not a passing implementation.
