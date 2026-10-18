# PB-14 — Enterprise UI Design System and Acceptance Standard

**Approved** 2026-10-17 under ADR-PB-014. Implementation and audit results are pending.

## Design tokens, component ownership and boundaries

- Frontend stack remains React/TypeScript/Vite, Tailwind v4, accessible headless primitives, TanStack Query, BFF and generated TypeScript API clients. Use `packages/design-tokens`, `packages/ui`, `apps/web/src/features/<feature>`; core .NET and Authority are independent of presentation. See `CLAUDE-DESIGN.MD` §§2, 8, 16, 17.
- Theme default: `--ui-primary-bg:#111111`, `--ui-primary-fg:#FFFFFF`, neutral surfaces, strong borders, visible focus and checkbox states; primary action remains visually dominant, not every action rendered as a black button. Use clear labels (not only icon/color).
- Organization Owner edits logo and **bounded, validated token choices**. Store effective theme and a contrast-validation receipt/version; reject invalid choices **before persistence**, and revalidate after token/contrast-rule updates. Do not let users override focus semantics or field/protection policy.
- Logo: prefer sandboxed/decoded raster PNG/WebP, format and file-size limits, size/aspect-ratio constraints, image re-encode and malicious content checks; restrict and sanitize SVG if supported. Serve through safe controlled URL. Provide accessible organization name fallback in top-left.
- Accessible text sizing: user preference overrides organization default within safe readable bounds; rem-based scaling and responsive components. Avoid fixed heights and horizontal overflow; persist per-user preferences without changing encrypted record data or organization role rules.
- Tokens generate static Tailwind class references; never interpolate raw tenant input into class strings, CSS, HTML, data URL or arbitrary scripts. Validate foreground-background combinations on all states: default, hover, focus, active, disabled when semantic information conveyed, error, selected, checked and high-contrast/forced-colors equivalents.
- Automated algorithmic contrast checks use WCAG relative luminance and return ratio 1–21; normal text requires >=4.5, large qualifying text >=3, essential nontext >=3. Some control boundaries and focus appearances require more contextual verification than a single ratio. Do not claim an accessible theme solely on palette arithmetic.

## Gallery PG-069 (internal/developer only)

Show all components with *each meaningful state*: primary/secondary/tertiary/destructive buttons, checkboxes/radio/switches, selects, combobox, date/time, inputs, file picker/upload tray, toasts/banners, validation/error, dialogs, drawers, pagination, data tables and accessible grids, progress, skeleton/busy, loading/error/empty data cards, document list, related-form list, breadcrumbs/nav and typography. Enable sample organization theme preview and rejected-palette feedback. It must never appear as a public route in production or bypass normal identity boundary if operator-preview is enabled.

## Three sign-off representative workflows

Read `docs/ui/PB14_REPRESENTATIVE_SCREEN_CONTRACTS.md` for per-screen requirements and routes. Screens must bind to the feature-owned APIs and show **independent loading and refresh for data cards**. Use synthetic fixtures for component gallery, but actual PB-02 Stage B workflows require the functioning secure back end.

1. **Role-aware Dashboard** (PG-006): permissions-driven widget catalog, top-left logo and persistent upload status; independent widget queries/refreshes; no record counts or titles from unauthorized records.
2. **Accused Workspace** (PG-016/017/018/019/020/021 + PG-010): form-view/edit, nested AccusedAddressList, associated document list, accessible upload tray surviving SPA navigation, share/access, signed projection, masking and child-version UX.
3. **Form Designer** (PG-029/033/037): four-column desktop canvas, two-column tablet, one-column phone preview, span/reflow, accessible move/reorder controls, dependency rules, relationship list and publish/preview workflows.

## Manual and automated verification

- Widths: 320/768/1440 CSS pixels, plus 200/400% browser zoom and common orientation changes; no required-content clipping.
- Keyboard: Tab/Shift+Tab, Enter/Space, Escape, arrow navigation, explicit bypass link, modal focus trap/return, ordering, menu dismissal, hover/focus parity.
- Screen readers: NVDA with Chrome/Edge; VoiceOver with Safari; capture exact tested versions and outcomes. Include dynamic refresh live-region announcements, async card/error states, upload progress and validation summaries.
- Color: all applicable pairings for each permitted theme/state, dark/forced-color schemes if supported; reject low contrast and safely fall back. Human examination of logo legibility, focus visibility, information hierarchy and text scaling.
- Security: no unintended confidential values in card teasers, browser history/title, accessible labels, aria descriptions, notification text, screenshot test artifacts or analytics; theme choice cannot bypass policy.
- Test tooling: Playwright responsive snapshots, axe-core accessibility scanning, unit contrast algorithm tests, relevant component accessibility tests, cross-browser smoke. Manual review **not** replaced by axe/lighthouse pass.
- Performance: apply PB-09 p95 loaded shell/card objectives with measured device/network profile, not raw CSS build size only.
- Evidence: attach test IDs/result, browser + assistive technology, screenshots stored in approved locality if containing customer data, issue list, remediations and product-owner acceptance into PB-12 release evidence register.

## Gate

Before scaling from representative workflows to other catalog screens, require product-owner design acceptance and accessible state coverage. A component gallery alone is insufficient; passing source code checks alone is not a WCAG/AODA certification. This milestone never bypasses earlier PB-03 security proof.
