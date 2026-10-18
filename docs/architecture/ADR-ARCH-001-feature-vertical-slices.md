# ADR-ARCH-001 — Feature-first Modular Monolith with Vertical Slices

- **Status:** Accepted (normative text in `CLAUDE.md` §4U and `CLAUDE-DESIGN.MD` §8/§17; this file records the
  repository realization made in Phase 0).
- **Date recorded in repository:** 2026-10-17

## Decision

The business application is a feature-first modular monolith with a small fixed set of Clean Architecture
assemblies (`EnterpriseForms.Domain`, `.Application`, `.Infrastructure`, `.Contracts`, `.Api`, `.Worker`), each
organized by `Features/<Feature>/<UseCase>/`. The Level 2 Authorization Authority is a separately deployed service
with its own assemblies (`AuthorizationAuthority.*`), database, identities and pipeline; it is never folded into the
business monolith. The React frontend is an npm workspace with feature-owned packages and an app shell that composes
public entrypoints only.

## Realization in this repository

| Rule | Where enforced |
|---|---|
| `Domain ← Application ← Infrastructure / Api / Worker`; Contracts are wire-only | `tests/architecture/ProjectReferenceGraphTests.cs` (csproj graph allow-list), `NamespaceBoundaryTests.cs` (NetArchTest) |
| Business assemblies never reference Authority assemblies (and vice versa); only `EnterpriseForms.Integrity` is shared | same tests |
| Every routed endpoint has exactly one `FeatureOwnerMetadata`; business routes live under `/api/v1` | `tests/features/EndpointOwnershipTests.cs` |
| OpenAPI generated from compiled endpoints; committed artifact must match | build-time generation + `OpenApiSnapshotTests.cs` + CI `git diff --exit-code contracts/openapi` |
| One EF mapping owner per table; descriptive multiword snake_case columns; `<singular>_id` keys | `ModelNamingValidator` + `DatabaseNamingTests.cs` (model is empty in Phase 0; rule tests run) |
| Every PG-001..PG-069 screen has exactly one frontend owner and the matrix agrees with the design document | `FeatureOwnershipMatrixTests.cs` |
| No raw `fetch`/`axios` in features or UI packages; features import only other features' `index.ts`; UI packages have no business/API dependency | `src/frontend/eslint.config.js` |
| No locally pinned NuGet versions (central pinning only) | `ProjectReferenceGraphTests.NoProjectPinsPackageVersionsLocally` |

## Consequences

- Adding a project requires an explicit allow-list entry in the dependency graph test, which forces review.
- Features cannot be exposed without an owner, a contract snapshot and tests, matching the feature-completion gate.
- The shared integrity library is the only code both planes compile; its changes require security-owner review
  (`.github/CODEOWNERS`).
