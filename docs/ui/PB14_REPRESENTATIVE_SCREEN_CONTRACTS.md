# PB-14 — Representative Screen Contracts

**Acceptance scope:** working feature integration for three representative enterprise experiences; not a declaration that all 69 catalog screens have been built.

## A. PG-006 — Role-aware dashboard

**Owns UI:** `features/dashboard`. **Data source:** authorized, independently projected widget endpoints in `/api/v1`; no direct Authority or SQL calls from React. **Appearance:** top-left tenant logo, brand name fallback, dark primary actions, neutral cards, accessible global side navigation, task/notification center and persistent upload tray.

**Widgets:** My Assigned Records, Recent Permitted Forms, Upload Activity, Authorized Tasks and Recent Personal Activity; do not disclose hidden record existence through counts. Widgets have their own query keys, cache scope, refresh button, skeleton, empty state, retry, error state and screen reader status; refresh must not disturb other cards. (Amended by ADR-PB-007 A1, SD-6, 2026-10-31: dashboard sections load on open, page and filter change without individual Refresh buttons; each keeps its own retry.) Designers may see design-only widgets; Organization Owners see configuration widgets only when authorized; system admins do not automatically see encrypted submissions.

**Test:** role union/minimum, tenant switching invalidates cache, throttled request does not block other cards, permission revocation removes formerly visible cards, keyboard/screen reader progress feedback, 320/768/1440 and 400% zoom.

## B. Accused record / addresses / document collection

**Screen set:** PG-016, PG-017, PG-018, PG-019, PG-020, PG-021 and PG-010 (plus sharing PG-022 when Stage B capability is enabled). **Owners:** `form-records`, `related-forms`, `documents`, `jobs`, `sharing`, integrated only through approved contracts.

**Layout:** masked, permission-scoped Accused summary/fields, editable Form Record viewer with ETag save/conflict handling, related AccusedAddressList with create/edit/reorder/unlink governed by independent Authority, child record version labels, associated document list with allowed metadata, upload and scanning statuses, permitted actions and activity timeline. Every data card/list can refresh independently. No navigation/interruption of browser-level upload manager when changing Accused pages within SPA.

**Security states:** inaccessible record must not reveal title or existence, encrypted values are masked until authorized, stale revocation/tenant switch aborts worker requests and drops cached projections; user sees actionable generic error without data leaks. Documents must remain quarantined until scan/encrypt/sign/commit; show received/scanning/ready/failure states. Long-lived transfer cannot be guaranteed after browser tab closure.

**Test:** real form/child writes, signed/protected projection, authorized list item filtering, card-only refreshing, >1 address, multiple documents, 500MB baseline/2GB gated upload scenario, route navigation mid-transfer, interrupted/restarted upload, invalid theme/contrast and keyboard flow.

## C. PG-029/033/037 — Form designer and preview

**Owns UI:** `features/form-designer`, uses published form definition contracts and `packages/ui`, distinct from record renderer/data access. **Appearance:** responsive canvas 4-column logical desktop; 2-column tablet; 1-column phone, design tools and property inspector, accessible drag/reorder alternatives and explicit new-row controls. Preview must demonstrate both organization visual tokens and user text-size preference.

**Workflow:** create new Accused and AccusedAddress definitions, assign stable FieldDefinitionId, configure child relationship `AccusedAddressList`, configure multi-file document collection and publish new version. Designer can inspect layout with synthetic data, validate rules/dependencies and see changes to future records; cannot inspect submitted user record values solely by holding Designer role.

**Test:** form 250 fields on large representative tenant, 1/2/4-column logic, drag and keyboard move parity, version history, multilingual long labels/RTL support where required, invalid publish state, any generated dependency-cycle errors, and no unauthorized production-data preview.

## Cross-screen approval rubric

For every screen: semantic headings, labeling, black primary/neutral secondary actions, clear checked/unselected styles, WCAG-contrast valid tenant theme variants, error/empty/loading/refresh, zoom/reflow, screen-reader status announcements, ability to navigate by keyboard, safe tenant-scoped cache, security projection, generated API client usage, background queue for relevant I/O and no visual design dependencies in .NET core. Keep screenshots and user review notes with PB-12 evidence.
