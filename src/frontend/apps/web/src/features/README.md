# Feature ownership (frontend)

Each folder here is one feature owner from `docs/architecture/FEATURE_OWNERSHIP_MATRIX.md` and `CLAUDE-DESIGN.MD` §17:
`audit`, `dashboard`, `documents`, `exports`, `form-catalog`, `form-designer`, `form-records`, `identity`, `jobs`,
`organizations`, `related-forms`, `search`, `security-operations`, `sharing`, `support`, `teams`, `user-preferences`.

Rules enforced by `eslint.config.js`:

- A feature exports only through its `index.ts`; other features and the app shell never import its `pages/`, `components/`, `hooks/`, `api/`, `schemas/`, `types/` or `internal/` folders.
- No raw `fetch`, `XMLHttpRequest`, `axios` or similar in features; all server I/O goes through the app-shell request coordinator (ADR-PB-007).
- Pages live in `features/<owner>/pages/`; there is no global `src/pages/` directory.

No feature folders exist in Phase 0. Features are created only together with their backend vertical slice and tests (ADR-PB-002 Stage A → Stage B).
