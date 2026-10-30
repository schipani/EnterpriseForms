# Phase C8 M1 — manual accessibility acceptance checklist (NVDA, VoiceOver, responsive, zoom)

**Status: NOT PERFORMED.** This is a checklist to be executed by a human reviewer; no manual result exists yet. Automated results are recorded separately (below) and are not a substitute (PB-14, ADR-PB-014).

## A. Automated results (recorded separately; already executed)

| Source | Scope | Result |
|---|---|---|
| `organization-administration.spec.ts` + axe-core (WCAG 2.0/2.1/2.2 A/AA tags) | Overview, People, Branding, Data protection, Security Administrators, Team detail, Security Administrator team view, no-capability message | passed on the isolated stack (see `docs/evidence/phase-c8/m1-acceptance-verification.md`) |
| Vitest component tests | `OverviewCards`, `MemberTable`, `TeamRoster` | 227 tests passed (29 files) |
| Automated checks do **not** establish screen-reader output, focus order quality, zoom/reflow or reading order. |

## B. Pages in scope

Overview `/app/organization`; People `/app/organization/users`; Teams `/app/organization/teams`; Team detail `/app/organization/teams/{id}` (Create team, Add member, Remove member, Rename dialogs and the **Pending** status); Branding `/app/organization/branding`; Data protection `/app/organization/data-protection`; Security Administrators `/app/organization/security-administrators`; the Organization navigation (collapsed strip below `lg`); the generic no-permission message; Primary navigation "Organization" link.

## C. Per page — screen reader (record tool + version + browser)

| # | Check | NVDA + Firefox/Chrome | VoiceOver + Safari |
|---|---|---|---|
| 1 | Page title and single `h1` announced; landmark list (banner, Primary and Organization navigation, main) correct | ☐ | ☐ |
| 2 | Organization navigation: current page announced (`aria-current`), skip link works | ☐ | ☐ |
| 3 | Overview: each card is a named region; its Refresh button name includes the card; refresh result/“Unavailable” part announced | ☐ | ☐ |
| 4 | People/Teams tables: caption/name, row headers, column headers announced; filter field labelled; no action column in People | ☐ | ☐ |
| 5 | Team detail: roster table reads member, verified command state; Add/Remove/Rename dialogs trap and restore focus, titles announced, Esc closes | ☐ | ☐ |
| 6 | **Pending**: the status message is announced politely when it appears (not only visually); wording does not claim completion; the “Access is being updated” error is announced | ☐ | ☐ |
| 7 | Combobox “Person” in Add member: options, selected value, empty state announced | ☐ | ☐ |
| 8 | Data protection and Security Administrators: form controls, validation and read-only explanations announced | ☐ | ☐ |
| 9 | Branding: logo upload control, success/refusal messages announced | ☐ | ☐ |
| 10 | No-permission message announced; no hidden navigation exposed | ☐ | ☐ |

## D. Keyboard, focus, responsive and zoom (each page above)

| Check | 320 px | 768 px | 1440 px | 200 % zoom | 400 % zoom |
|---|---|---|---|---|---|
| No horizontal page scroll (tables may scroll inside their container) | ☐ | ☐ | ☐ | ☐ | ☐ |
| Navigation strip usable, current item visible | ☐ | ☐ | ☐ | ☐ | ☐ |
| All actions reachable by keyboard in reading order, visible focus, no keyboard trap | ☐ | ☐ | ☐ | ☐ | ☐ |
| Dialogs fit the viewport and remain operable | ☐ | ☐ | ☐ | ☐ | ☐ |
| Text not clipped or overlapping; touch targets ≥ 24×24 px | ☐ | ☐ | ☐ | ☐ | ☐ |
| Colour is not the only carrier of state (Active/Inactive/Pending/Effective) | ☐ | ☐ | ☐ | ☐ | ☐ |

## E. Sign-off (all blank until performed)

Reviewer: ______  Date: ______  NVDA version: ______  VoiceOver/macOS version: ______  Findings recorded in: ______  Outcome: ☐ accepted ☐ accepted with findings ☐ rejected
