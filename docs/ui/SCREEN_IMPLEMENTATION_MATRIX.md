# Screen Implementation Matrix — Initial Pre-Build Baseline

**Status:** All screens are currently **not implemented**. **Update (2026-11-25):** that baseline statement is out of date. The rows below now record the pages present on `main` (`fbf18bc`), verified against `src/frontend/apps/web/src/app/router.tsx`; "Implemented for Development/Testing" is not owner acceptance, independent review, WCAG 2.2 AA/AODA conformance or production readiness. Routes outside the catalogue: `/app/organization/security-administrators`, `/app/organization/search-approvals`, `/app/user` (profile). The Platform Console pages PG-070 and PG-072…PG-075 (CLAUDE-DESIGN.MD §17) are not yet listed here. Route and feature ownership come from `CLAUDE-DESIGN.MD` §§16–17. Do not mark complete until permission, request queue, tests and frontend/backend feature implementation exist.

| Screen | Feature owner | Initial route | Feature/API and test evidence | Status |
|---|---|---|---|---|
| PG-001 | `identity` | `/auth/sign-in` | `LoginPage` at `/login` (catalogue route `/auth/sign-in`); development sign-in only until PB-08 | Partial: development sign-in; Entra BFF (PB-08) not implemented |
| PG-002 | `identity` | `See CLAUDE-DESIGN.MD §16` | To map during implementation | Not implemented |
| PG-003 | `identity` | `/app/select-organization` | To map during implementation | Not implemented |
| PG-004 | `support` | `/app/welcome` | To map during implementation | Not implemented |
| PG-005 | `identity` | `/app/security/verify` | To map during implementation | Not implemented |
| PG-006 | `dashboard` | `/app/dashboard` | `DashboardPage` at `/app/dashboard` (C-90) | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding; owner visual acceptance for development (C-91) |
| PG-007 | `user-preferences` | `/app/settings/preferences` | `PreferencesPage`, `LanguagePreferencePage`, `TextSizePreferencePage` at `/app/user/regional|language|text-size` (C8 M3) | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-008 | `identity` | `/app/settings/account` | To map during implementation | Not implemented |
| PG-009 | `support` | `/app/notifications` | To map during implementation | Not implemented |
| PG-010 | `jobs` | `/app/uploads` | `UploadCenterPage` at `/app/uploads` (the file comment says PG-021: wrong id, reported separately) | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-011 | `support` | `/app/help` | To map during implementation | Not implemented |
| PG-012 | `form-catalog` | `/app/forms` | `FormsPage` at `/app/forms` (the file comment says PG-029: wrong id, reported separately); `NewFormPage` at `/app/forms/new` | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-013 | `form-catalog` | `/app/forms/:formDefinitionId` | To map during implementation | Not implemented |
| PG-014 | `form-records` | `/app/forms/:formDefinitionId/new` | `NewRecordPage` at `/app/forms/:formDefinitionId/records/new` | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-015 | `search` | `/app/records` | `RecordsPage` at `/app/records` | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-016 | `form-records` | `/app/records/:formRecordId` | `RecordPage` at `/app/records/:formRecordId` (tabs; `RecordVersionPage` at `/versions/:contentRevision`) | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-017 | `form-records` | `/app/records/:formRecordId/edit` | `EditRecordPage` at `/app/records/:formRecordId/edit` | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-018 | `related-forms` | `/app/records/:formRecordId/related/:relationshipDefinitionId` | Related lists embedded in `RecordPage` (`ChildRecordList`); no separate route | Partial: embedded in PG-016 |
| PG-019 | `related-forms` | `See CLAUDE-DESIGN.MD §16` | `NewChildRecordPage` at `/app/records/:formRecordId/related/:formRelationshipDefinitionId/new` | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-020 | `documents` | `/app/records/:formRecordId/documents` | Document collection embedded in `RecordPage`; no `/documents` route | Partial: embedded in PG-016 |
| PG-021 | `documents` | `/app/records/:formRecordId/documents/:fileAttachmentId` | To map during implementation | Not implemented |
| PG-022 | `sharing` | `/app/records/:formRecordId/sharing` | Access tab of `RecordPage` (`RecordAccessPanel`): list, create, revoke, sharing history; planned: Phase C10 (C-193) | Partial: embedded in PG-016; no expiry, lineage, document scope or separate route (C10) |
| PG-023 | `sharing` | `/app/records/:formRecordId/sharing/:recordShareId` | Share details are not available; the Access tab lists shares only; planned: Phase C10 (C-193) | Not implemented (C10) |
| PG-024 | `sharing` | `/app/records/:formRecordId/security` | Planned: Phase C15 (record challenges, wholly as catalogued; C-193). The C10 access explanation is placed in PG-022/PG-023/PG-066, not here | Not implemented |
| PG-025 | `audit` | `/app/records/:formRecordId/activity` | `MyActivityPage` at `/app/activity` and the record activity tab | Partial |
| PG-026 | `jobs` | `/app/jobs` | To map during implementation | Not implemented |
| PG-027 | `form-designer` | `/app/designer` | To map during implementation | Not implemented |
| PG-028 | `form-designer` | `/app/designer/new` | To map during implementation | Not implemented |
| PG-029 | `form-designer` | `/app/designer/:formDefinitionId` | `DesignerPage` at `/app/forms/:formDefinitionId/design` (catalogue route `/app/designer/:formDefinitionId`) | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-030 | `form-designer` | `/app/designer/:formDefinitionId/fields` | To map during implementation | Not implemented |
| PG-031 | `form-designer` | `/app/designer/:formDefinitionId/options` | To map during implementation | Not implemented |
| PG-032 | `form-designer` | `/app/designer/:formDefinitionId/dependencies` | To map during implementation | Not implemented |
| PG-033 | `form-designer` | `/app/designer/:formDefinitionId/relationships` | Relationship placement inside `DesignerPage` | Partial: inside PG-029 |
| PG-034 | `form-designer` | `/app/designer/:formDefinitionId/uploads` | To map during implementation | Not implemented |
| PG-035 | `form-designer` | `/app/designer/:formDefinitionId/rules` | To map during implementation | Not implemented |
| PG-036 | `form-designer` | `/app/designer/:formDefinitionId/translations` | To map during implementation | Not implemented |
| PG-037 | `form-designer` | `/app/designer/:formDefinitionId/preview` | Preview inside `DesignerPage` | Partial: inside PG-029 |
| PG-038 | `form-designer` | `/app/designer/:formDefinitionId/publish` | Publish review dialog inside `DesignerPage` (C9 M2) | Partial: inside PG-029 |
| PG-039 | `form-designer` | `/app/designer/:formDefinitionId/versions` | Versions tab inside `DesignerPage` (C9 M2) | Partial: inside PG-029 |
| PG-040 | `form-designer` | `/app/designer/:formDefinitionId/access` | To map during implementation | Not implemented |
| PG-041 | `organizations` | `/app/organization` | `OverviewPage` at `/app/organization` | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-042 | `organizations` | `/app/organization/branding` | `BrandingPage` at `/app/organization/branding` | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-043 | `organizations` | `/app/organization/preferences` | `RegionalSettingsPage`, `LanguageSettingsPage`, `TextSizeSettingsPage` at `/app/organization/regional|language|text-size` (C8 M3) | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-044 | `organizations` | `/app/organization/catalogs` | To map during implementation | Not implemented |
| PG-045 | `organizations` | `/app/organization/catalogs/dependencies` | To map during implementation | Not implemented |
| PG-046 | `organizations` | `/app/organization/security/data` | `DataProtectionPage` at `/app/organization/data-protection` | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-047 | `organizations` | `/app/organization/security/authentication` | To map during implementation | Not implemented |
| PG-048 | `teams` | `/app/organization/teams` | `TeamsPage` at `/app/organization/teams` | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-049 | `teams` | `/app/organization/teams/:teamId` | `TeamDetailPage` at `/app/organization/teams/:teamId` | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-050 | `identity` | `/app/organization/users` | `PeopleDirectoryPage` at `/app/organization/users` (C8 M2) | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-051 | `identity` | `/app/organization/users/:userId` | To map during implementation | Not implemented |
| PG-052 | `security-operations` | `/app/security` | `SecurityHomePage` (C8 M4-4, C-169) | Implemented; owner-accepted for Development/Testing (C-173); not independently reviewed |
| PG-053 | `security-operations` | `/app/security/ownership` | Planned: Phase C10 (C-193) | Not implemented |
| PG-054 | `audit` | `/app/security/audit` | `AuditExplorerPage` at `/app/security/audit`; `SecurityCommandTimelinePage` at `/app/security/audit/commands` (C8 M4-2, M4-3) | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-055 | `audit` | `/app/security/audit/:auditEventId` | `AuditEventPage` at `/app/security/audit/:auditEventId` (C8 M4-2) | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-056 | `security-operations` | `/app/security/incidents` | `SecurityIncidentsPage` and `SecurityIncidentPage` at `/app/security/incidents[/:securityIncidentId]` | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-057 | `security-operations` | `/app/security/authority` | To map during implementation | Not implemented |
| PG-058 | `security-operations` | `/app/security/recovery` | To map during implementation | Not implemented |
| PG-059 | `security-operations` | `/app/security/operations` | To map during implementation | Not implemented |
| PG-060 | `support` | `/app/forbidden` | `NoPermissionPage` at `/app/no-permission` (catalogue route `/app/forbidden`) | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding |
| PG-061 | `support` | `/app/not-found` | Not-found component of the router | Partial |
| PG-062 | `support` | `/app/unavailable` | To map during implementation | Not implemented |
| PG-063 | `support` | `/app/conflict` | To map during implementation | Not implemented |
| PG-064 | `organizations` | `/app/organization/setup` | To map during implementation | Not implemented |
| PG-065 | `sharing` | `/app/access-requests` | Planned: Phase C11 (C-193) | Not implemented |
| PG-066 | `sharing` | `/app/my-access` | Planned: Phase C10 (C-193) | Not implemented |
| PG-067 | `organizations` | `/app/organization/security/retention` | To map during implementation | Not implemented |
| PG-068 | `exports` | `/app/exports/new` | To map during implementation | Not implemented |
| PG-069 | `support` | `/dev/ui-gallery` | `GalleryPage` at `/app/gallery`, development builds only (catalogue route `/dev/ui-gallery`) | Implemented for Development/Testing; not independently reviewed; manual NVDA/VoiceOver review outstanding; development only |
