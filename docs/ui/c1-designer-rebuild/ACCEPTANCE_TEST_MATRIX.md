# C1 Designer Rebuild — Required acceptance scenarios

Tests must operate against the actual React app and API in a Docker-backed development/test environment. Every check needs actual evidence: test name/run, screenshot or video where useful, exact commit SHA, and status. An automated screenshot alone does not prove persistence.

| ID | Test | Expected behavior |
|---|---|---|
| UX-01 | Open Designer at 1440x900 | Left design nav / compact titled center canvas / right inspector, visually comparable to TARGET_DESIGN.png; old giant edit cards gone |
| UX-02 | Click Last Name field | Only that tile selected, inspector shows matching stable field ID and properties |
| UX-03 | Inline rename field then save and reload | Label updated; internal field ID and values intact; history immutable |
| UX-04 | Click Add Row | New titled, renameable section created, initially with empty add-field slot; not just a line break |
| UX-05 | Add two fields into new Row section | Both placed in that section, use proper 4-col grid and desired spans |
| UX-06 | Move field from Personal Information to new section | Move persists on reload; no duplicates or orphan metadata |
| UX-07 | Reorder/collapse/rename section | Canvas and preview correctly update; survive Save Draft + browser reload |
| UX-08 | Remove nonempty section | Requires explicit field-placement disposition; underlying field definitions/values remain, no silent deletion |
| UX-09 | Save + publish new draft | Published version immutable, new section renders on actual record page, old version unchanged |
| UX-10 | New section with related child list and document collection | Existing IDs, permissions, uploads and version associations preserved |
| UX-11 | Edit Field property tabs | General/Validation/Rules/Security & Encryption/Advanced; consistent black active-tab underline; unsaved fields retained on tab navigation |
| UX-12 | Theme defaults and owner override | Black default buttons/checkboxes/toggles/tabs; organization-defined accessible colors apply everywhere through tokens |
| UX-13 | Data tables | Rightmost Actions column, consistent buttons/overflow, accessible with keyboard and at narrow widths |
| UX-14 | Keyboard-only | Focus, add section, add/move field, rename, save, tabs and menus possible without dragging |
| UX-15 | 320 / 768 / 1440 + 200/400% zoom | No page-level overflow; inspector sheet usable; no clipped save/section controls |
| UX-16 | Security regression | Existing grants, field encryption/decryption migrations, signed manifests, document uploads, SEC-P test suite still pass |
| UX-17 | Visual browser screenshots | Capture live 1440px Design screen and compare against supplied target; enumerate/resolution of structural discrepancies |
| UX-18 | Field edit and publish across versions | Stable IDs, draft rules, no unauthorized mutation of published form |

**Mandatory evidence**: compile/build/lint; frontend tests; backend API integration, Docker form persistence tests; Playwright coverage for UX-01 through UX-15 as automatable; SEC-P suite and relevant Stage B coverage; human/manual NVDA VoiceOver and subjective UX remain PENDING until owner completes them.

**Stop gate**: Claude must not label this accepted merely because tests pass. It must report UI screenshot side-by-side and request owner visual acceptance of the actual result.
