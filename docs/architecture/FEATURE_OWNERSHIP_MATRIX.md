# Feature Ownership Matrix — Phase 0 Baseline

**Status:** Ownership inventory derived from the 69-screen catalog (`CLAUDE-DESIGN.MD` §17) and the backend feature
catalog (`CLAUDE.md` §4U.2). Agreement between this file and the design document is enforced by
`tests/architecture/FeatureOwnershipMatrixTests.cs`. Columns marked *planned* describe ownership that becomes real
only when the vertical slice ships. **Stage A (2026-10-18) shipped the first real slices** — marked *(Stage A)* below:
`Identity` (tenants/users projection + `/api/v1/users/me`), `Teams` (teams/team_memberships projection + commands),
`FormDefinitions` (form_definitions/form_versions/field_definitions + `POST /api/v1/form-definitions`), `FormRecords`
(form_records/form_record_revisions/field_string_values + create/get/update/transfer-ownership) and `Sharing`
(record_shares/record_share_fields + create/revoke). No UI pages shipped. "Public ports" are the only cross-feature
entry points permitted by ADR-ARCH-001.

| UI feature | Owned page IDs | Backend feature | Owned tables (planned) | Public ports (planned) | Tests (planned) | Release gate |
|---|---|---|---|---|---|---|
| `audit` | PG-025, PG-054, PG-055 | `Audit` | audit_events, audit_event_types, audit_field_changes, audit_event_outbox, audit_retention_policies, audit_archive_checkpoints | audit investigation query API; audit event ingestion port | SIM-01..12 subset, timeline ordering, tenant-scoped ACL | Phase 1C |
| `dashboard` | PG-006 | `Dashboard` | none (orchestration) | authorized widget composition | per-card refresh, capability filtering | Stage B |
| `documents` | PG-020, PG-021 | `Documents` | upload_sessions, file_attachments, file_attachment_versions | authorized document projection; upload session service | §4A tests, PB-07 upload tests, MRK attachment leaves | Phase 3A |
| `exports` | PG-068 | `Exports` | export_jobs | authorized deterministic export | PB-13 export fidelity, step-up | PB-13 |
| `form-catalog` | PG-012, PG-013 | `FormDefinitions` (read contracts) | form_definitions, form_versions, field_definitions, field_concepts, field_options, group_definitions, form_relationship_definitions, form_translation_entries | published form read contracts | version immutability, option snapshot | Phase 2 |
| `form-designer` | PG-027, PG-028, PG-029, PG-030, PG-031, PG-032, PG-033, PG-034, PG-035, PG-036, PG-037, PG-038, PG-039, PG-040 | `FormDesigner` | form_drafts | publish/restore/impact commands | atomic publish, semantic diff, layout schema validation (§4I) | Phase 2 |
| `form-records` | PG-014, PG-016, PG-017 | `FormRecords` | form_records, form_record_groups, field_*_values, field_encrypted_values, value history, record_access_challenges, record_access_unlocks | authorized record projection; typed value write port | acceptance 1–17, 29–39, MRK-01..32 | Stage A/B |
| `identity` | PG-001, PG-002, PG-003, PG-005, PG-008, PG-050, PG-051 | `Identity` | users, user_system_roles (projection), identity_provider_configurations | verified principal/session bootstrap; capability projection | PB-08 identity tests | Phase 1A2 |
| `jobs` | PG-010, PG-026 | `Jobs` | background job state | job status/orchestration | PB-07 durable job tests | Phase 3A |
| `organizations` | PG-041, PG-042, PG-043, PG-044, PG-045, PG-046, PG-047, PG-064, PG-067 | `Organizations` | tenants, organization_settings, organization_authentication_policies (+approvals), geographic_regions, countries, administrative_divisions, supported_languages, time_zone_catalog, list_definitions, list_options, list_option_dependencies, organization_catalog_option_policies, branding | organization settings/catalog read contracts; theme validation | §4F/§4G acceptance 47–55, theme contrast validation | Phase 4A |
| `related-forms` | PG-018, PG-019 | `RelatedForms` | form_record_relationships | child-link lifecycle commands | REL-01..36 | Stage B |
| `search` | PG-015 | `Search` | verified search projections | trusted-completeness search API | ENC-SR-##, SIM-21..28 | Phase 4 |
| `security-operations` | PG-052, PG-053, PG-056, PG-057, PG-058, PG-059 | `SecurityOperations` | none (adapters to Authority contracts) | restricted operator adapters | PB-12 dashboard negative tests | PB-12 |
| `sharing` | PG-022, PG-023, PG-024, PG-065, PG-066 | `Sharing` | record_shares, record_share_recipients, record_share_fields, record_share_documents (projections of authority grants) | share commands → Authority; share read models | acceptance 56–62 | Stage A/B |
| `support` | PG-004, PG-009, PG-011, PG-060, PG-061, PG-062, PG-063, PG-069 | app-level status services only | none | none (no business decisions) | axe/Playwright for status pages; PG-069 gallery (PB-14) | PB-14 |
| `teams` | PG-048, PG-049 | `Teams` | teams, team_memberships (projection) | team directory read model | membership → authority command tests | Stage A |
| `user-preferences` | PG-007 | `UserPreferences` | user_preferences, date_format_presets, number_format_presets | effective presentation resolver | acceptance 43–46 | Phase 4A |

## Platform (non-feature) ownership in Phase 0

| Component | Location | Owner | Enforced by |
|---|---|---|---|
| Composition roots, probes (`/health/live`, `/health/ready`) | `src/backend/EnterpriseForms.Api/{Composition,Features/Platform}` | Platform | `EndpointOwnershipTests` (owner metadata required) |
| Authority host and signing-provider policy | `src/authority/AuthorizationAuthority.Api` | security-owners | `AuthoritySigningPolicyTests`, CODEOWNERS |
| Integrity canonicalization and Merkle v1 | `src/shared/EnterpriseForms.Integrity` | security-owners | `tests/integrity` vectors, CODEOWNERS |
| Business persistence naming rules | `src/backend/EnterpriseForms.Infrastructure/Persistence/Conventions` | Platform | `DatabaseNamingTests` |
| Design tokens and contrast validation | `src/frontend/packages/design-tokens` | Platform (UI) | vitest, ESLint no-business-dependency rule |
| Generated API types | `src/frontend/packages/api-client` | Platform (generated) | CI regeneration drift check |

Authority-owned tables (AuthorityDb, never business features): organization authorization states, security command
journal/outbox, desired relationships, record registry, accepted content heads and leaf index snapshots, content
integrity checkpoints, key catalog references, recovery epochs (`trust_authority/*.reference.sql`).
