# UI consistency standard: before and after screenshots

Captured 2026-10-30 by a temporary, read-only Playwright script (not committed). It used the isolated security-test stack (stack identity verified by the e2e global setup) and a Vite dev server on port 5199. It ran against the synthetic seed organization; no real data appears.

- `before-*`: the untouched `main` state (`b1e1b30`).
- `after-*`: the `ui/consistency-standardization` branch.
- Widths: 1440 px, plus 320 px for My Records and New form. 320 px is the 400 % zoom width of a 1280 px window.

| Page | Persona | What to compare |
|---|---|---|
| `records` | clerk | The approved reference. It should look the same before and after (the header buttons now share the 40 px control height). |
| `forms-new` | designer | Labels, inputs, textarea, choice labels and buttons now match the reference. |
| `record-edit` | clerk | Form renderer: labels, inputs, date/time, checkbox, help text and buttons. |
| `designer` | designer | Toolbar buttons, panels and canvas radii. |
| `branding` | owner | Organization administration controls. |
| `preferences` | clerk | Selects and radio-like choices. |
| `search-approvals` | security | Security administration list and controls. |
| `record-access` | clerk | Sharing panel: legends, checkboxes, table. |

These are visual evidence only. Accessibility conformance comes from the automated axe suites listed in `docs/ui/UI_CONSISTENCY_STANDARD.md` §5. Manual NVDA/VoiceOver and manual zoom review were **not** performed.
