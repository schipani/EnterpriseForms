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

**Stage B (2026-10-18) shipped the Accused vertical slice** — real pages now exist for: `identity` (PG-001 development
sign-in, session expiry), `dashboard` (PG-006 independent cards), `form-catalog` (forms list, new form), `form-designer`
(PG-029/033/037 field catalog, 4-column canvas with spans/row breaks/preview, relationships, draft save, publish, versions,
restore), `form-records` (PG-010/014/016/017 list, create, verified view, edit with If-Match), `related-forms`
(PG-018/019 child list add/view/edit/unlink, child creation page), `documents` (PG-020 collection: concurrent uploads,
progress, Ready only after server commit, download, versions, remove), `jobs` (PG-021 Upload Center), `audit` (PG-025
record activity, my activity), `organizations` (branding + logo), `user-preferences` (text size), `support` (PG-069
gallery, not-found, no-permission, error panel). Backend slices added: `FormDesigner` (form_drafts, field_options,
relationships, publish/restore), `FormRecords` typed value tables + projection, `RelatedForms`, `Documents`
(upload_sessions, file_attachments, file_attachment_versions, document_processing_jobs, worker), `Audit` projection,
`Organizations` branding, `UserPreferences`, `Dashboard`. Everything else in the table below remains *planned*. The
page-ID ownership rows are unchanged and remain the source the architecture test compares with `CLAUDE-DESIGN.MD` §17.

**Phase C7 M0 (2026-10-23, C-57)** shipped the first `Search` slice: backend `Application/Features/Search/FieldSearch` + `Api/Features/Search` (`/api/v1/form-records/search-capabilities`, `/field-search`, `/field-search/page`; no owned tables; execution gated off until Authority AC-5/AC-6) and frontend `features/search` (Advanced Search panel used by the PG-015 My Records page, which still lives in `form-records`). The Search feature has no business-store dependency (architecture test `FieldSearchHasNoDirectBusinessDatabaseAccess`).

**Phase C8 M2 part 1 (2026-10-30, C-93)**: `Identity` extends `GET /api/v1/organization/members` (server paging, search, filters, Authority-bound members for Security Administrators via the existing roster port) and owns the display column `users.last_signed_in_on`; the `identity` People page composes the existing `Organizations` Security Administrator commands. AC-7 commands are specified, not implemented.

**Form rename (2026-10-30, C-92)**: `FormDesigner` owns `PATCH /api/v1/form-definitions/{formDefinitionId}` (display-name change of the `form_definitions` catalog row, owned by `FormDefinitions`; new column `form_definition_revision`), surfaced by the `form-catalog` Forms list and the `form-designer` page through the `form-designer` public export `RenameFormDialog`. No new version, no Authority change.

**Phase C8 M1 (2026-10-29, C-79)** shipped the administration shell with **no Authority change**: `Organizations` gained `GET /api/v1/organization/overview` (PG-041, independently failing parts) and the pages `/app/organization` (overview), `/app/organization/data-protection` (PG-046, the existing policy card moved) and `/app/organization/security-administrators` (the C-70 card moved), plus the `OrganizationLayout` navigation that `identity` and `teams` compose; `Identity` gained the read-only people directory `GET /api/v1/organization/members` (PG-050, business projection labelled as such, Security Administrator badges from the Authority roster) and the page `/app/organization/users`; `Teams` gained `GET /api/v1/organization/teams`, `GET /api/v1/teams/{teamId}` and the display-only rename `PATCH /api/v1/teams/{teamId}` (D8-8, audited `TEAM_RENAMED`) with the pages `/app/organization/teams` and `/app/organization/teams/:teamId` (PG-048/049) over the existing RegisterTeam/AddTeamMember/RemoveTeamMember commands; the logo upload is audited (`ORGANIZATION_LOGO_UPLOADED`/`…_REPLACED`, F-C7-03). Administrative reads require the Authority's `manage_configuration` or `administer_security` decision (`AdministrationAccess`); team commands and the rename stay owner-only. PG-051 (user detail), owner/designer role badges and membership lifecycle wait for AC-7 (M2).

| UI feature | Owned page IDs | Backend feature | Owned tables (planned) | Public ports (planned) | Tests (planned) | Release gate |
|---|---|---|---|---|---|---|
| `audit` | PG-025, PG-054, PG-055 | `Audit` | audit_events, audit_event_types, audit_field_changes, audit_event_outbox, audit_retention_policies, audit_archive_checkpoints | audit investigation query API; audit event ingestion port | SIM-01..12 subset, timeline ordering, tenant-scoped ACL | Phase 1C |
| `dashboard` | PG-006 | `Dashboard` | none (orchestration) | authorized widget composition; section APIs `GET /api/v1/dashboard/{record-statistics,upload-statistics,uploads,activity,forms}` (server-paged, 15 rows, opaque cursors; C-90) | capability filtering; `DashboardSecurityMonitoringTests` (forms paging), `DashboardPage.test.tsx` (15 rows, independent sections, no Refresh), `DashboardWidgets.test.tsx` | Stage B; redesign C-90 (awaiting owner acceptance) |
| `documents` | PG-020, PG-021 | `Documents` | upload_sessions, file_attachments, file_attachment_versions | authorized document projection; upload session service | §4A tests, PB-07 upload tests, MRK attachment leaves | Phase 3A |
| `exports` | PG-068 | `Exports` | export_jobs | authorized deterministic export | PB-13 export fidelity, step-up | PB-13 |
| `form-catalog` | PG-012, PG-013 | `FormDefinitions` (read contracts) | form_definitions, form_versions, field_definitions, field_concepts, field_options, group_definitions, form_relationship_definitions, form_translation_entries | published form read contracts | version immutability, option snapshot | Phase 2 |
| `form-designer` | PG-027, PG-028, PG-029, PG-030, PG-031, PG-032, PG-033, PG-034, PG-035, PG-036, PG-037, PG-038, PG-039, PG-040 | `FormDesigner` | form_drafts, option_lists, option_list_items | publish/restore/impact commands, shared option lists, rule validation | atomic publish, semantic diff, layout schema validation (§4I) | Phase 2 |
| `form-records` | PG-014, PG-016, PG-017 | `FormRecords` | form_records, form_record_group_revisions (signed entity kind `form_record_groups`, Phase C3 repeating-section entries), field_*_values, field_encrypted_values, form_record_creation_attempts (F-C7-01 creation fingerprints), value history, record_access_challenges, record_access_unlocks | authorized record projection; typed value write port | acceptance 1–17, 29–39, MRK-01..32 | Stage A/B |
| `identity` | PG-001, PG-002, PG-003, PG-005, PG-008, PG-050, PG-051 | `Identity` | users, user_system_roles (projection), identity_provider_configurations | verified principal/session bootstrap; capability projection; read-only people directory `GET /api/v1/organization/members` (Phase C8 M1) | PB-08 identity tests; `AdministrationTests` (C8 M1) | Phase 1A2; PG-050 read-only C8 M1, PG-051 M2 (AC-7) |
| `jobs` | PG-010, PG-026 | `Jobs` | background job state | job status/orchestration | PB-07 durable job tests | Phase 3A |
| `organizations` | PG-041, PG-042, PG-043, PG-044, PG-045, PG-046, PG-047, PG-064, PG-067 | `Organizations` | tenants, organization_settings, organization_authentication_policies (+approvals), geographic_regions, countries, administrative_divisions, supported_languages, time_zone_catalog, list_definitions, list_options, list_option_dependencies, organization_catalog_option_policies, branding | organization settings/catalog read contracts; theme validation | §4F/§4G acceptance 47–55, theme contrast validation | Phase 4A |
| `related-forms` | PG-018, PG-019 | `RelatedForms` | form_record_relationships | child-link lifecycle commands | REL-01..36 | Stage B |
| `search` | PG-015 | `Search` | verified search projections (no business tables; approvals, published-version records and `field_searcher` are Authority-held, Phase C7 M2) | trusted-completeness search API; search capabilities; search-approval administration (`/form-definitions/{id}/field-search`, `…/search-approval`, `…/field-searchers`, relayed as Authority commands) | ENC-SR-##, SIM-21..28, AC5-T01..T15, AC6-T01..T12 | Phase 4; C7 M0–M2 |
| `security-operations` | PG-052, PG-053, PG-056, PG-057, PG-058, PG-059 | `SecurityOperations` | security_incidents, security_incident_occurrences, security_incident_notes (C-90; untrusted triage projection — the Authority journal and quarantine are the evidence); otherwise adapters to Authority contracts | restricted operator adapters; PG-056 `GET/PATCH /api/v1/security/incidents[/{id}]`, `GET …/summary`, `POST …/{id}/notes` (administer_security only); `ISecurityIncidentRecorder` port (detection from VerifiedRecordReader and historic document verification) | PB-12 dashboard negative tests; `DashboardSecurityMonitoringTests` (dedup, classification, recorder never throws, 403 for non-Security Administrators, record ids withheld, 15-row cursors) | PB-12 |
| `sharing` | PG-022, PG-023, PG-024, PG-065, PG-066 | `Sharing` | record_shares, record_share_recipients, record_share_fields, record_share_documents (projections of authority grants) | share commands → Authority; share read models | acceptance 56–62 | Stage A/B |
| `support` | PG-004, PG-009, PG-011, PG-060, PG-061, PG-062, PG-063, PG-069 | app-level status services only | none | none (no business decisions) | axe/Playwright for status pages; PG-069 gallery (PB-14) | PB-14 |
| `teams` | PG-048, PG-049 | `Teams` | teams, team_memberships (projection; written only by the Worker reconciler, C-87), authority_command_operations, team_membership_projection_states (C-87, untrusted projection class) | team directory read model; `GET /api/v1/organization/teams`, `GET /api/v1/teams/{teamId}`, display-only `PATCH /api/v1/teams/{teamId}` (Phase C8 M1); membership operations `GET …/membership-operations/{id}`, `…/by-key/{key}`, `POST …/recheck`, `…/review` (C-87); Worker-only `ITeamMembershipOutcomeAuthority` (AC-9) | membership → authority command tests; `AdministrationTests` (C8 M1); `PendingAuthorityCommandTests`, `TeamMembershipReconciliationTests` (RC suite), `TeamMembershipReconcilerServiceTests`, `TeamMembershipReconciliationUnitTests`, `TeamMembershipSingleWriterTests` (C-87); `organization-administration.spec.ts` | Stage A; PG-048/049 C8 M1 (rename only; deactivation and team managers deferred, D8-8); C8-M1-01/02 implemented C-87, closure pending independent AC-9 review |
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
