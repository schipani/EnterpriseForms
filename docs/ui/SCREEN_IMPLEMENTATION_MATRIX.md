# Screen Implementation Matrix — Initial Pre-Build Baseline

**Status:** All screens are currently **not implemented**. Route and feature ownership come from `CLAUDE-DESIGN.MD` §§16–17. Do not mark complete until permission, request queue, tests and frontend/backend feature implementation exist.

| Screen | Feature owner | Initial route | Feature/API and test evidence | Status |
|---|---|---|---|---|
| PG-001 | `identity` | `/auth/sign-in` | To map during implementation | Not implemented |
| PG-002 | `identity` | `See CLAUDE-DESIGN.MD §16` | To map during implementation | Not implemented |
| PG-003 | `identity` | `/app/select-organization` | To map during implementation | Not implemented |
| PG-004 | `support` | `/app/welcome` | To map during implementation | Not implemented |
| PG-005 | `identity` | `/app/security/verify` | To map during implementation | Not implemented |
| PG-006 | `dashboard` | `/app/dashboard` | To map during implementation | Not implemented |
| PG-007 | `user-preferences` | `/app/settings/preferences` | To map during implementation | Not implemented |
| PG-008 | `identity` | `/app/settings/account` | To map during implementation | Not implemented |
| PG-009 | `support` | `/app/notifications` | To map during implementation | Not implemented |
| PG-010 | `jobs` | `/app/uploads` | To map during implementation | Not implemented |
| PG-011 | `support` | `/app/help` | To map during implementation | Not implemented |
| PG-012 | `form-catalog` | `/app/forms` | To map during implementation | Not implemented |
| PG-013 | `form-catalog` | `/app/forms/:formDefinitionId` | To map during implementation | Not implemented |
| PG-014 | `form-records` | `/app/forms/:formDefinitionId/new` | To map during implementation | Not implemented |
| PG-015 | `search` | `/app/records` | To map during implementation | Not implemented |
| PG-016 | `form-records` | `/app/records/:formRecordId` | To map during implementation | Not implemented |
| PG-017 | `form-records` | `/app/records/:formRecordId/edit` | To map during implementation | Not implemented |
| PG-018 | `related-forms` | `/app/records/:formRecordId/related/:relationshipDefinitionId` | To map during implementation | Not implemented |
| PG-019 | `related-forms` | `See CLAUDE-DESIGN.MD §16` | To map during implementation | Not implemented |
| PG-020 | `documents` | `/app/records/:formRecordId/documents` | To map during implementation | Not implemented |
| PG-021 | `documents` | `/app/records/:formRecordId/documents/:fileAttachmentId` | To map during implementation | Not implemented |
| PG-022 | `sharing` | `/app/records/:formRecordId/sharing` | To map during implementation | Not implemented |
| PG-023 | `sharing` | `/app/records/:formRecordId/sharing/:recordShareId` | To map during implementation | Not implemented |
| PG-024 | `sharing` | `/app/records/:formRecordId/security` | To map during implementation | Not implemented |
| PG-025 | `audit` | `/app/records/:formRecordId/activity` | To map during implementation | Not implemented |
| PG-026 | `jobs` | `/app/jobs` | To map during implementation | Not implemented |
| PG-027 | `form-designer` | `/app/designer` | To map during implementation | Not implemented |
| PG-028 | `form-designer` | `/app/designer/new` | To map during implementation | Not implemented |
| PG-029 | `form-designer` | `/app/designer/:formDefinitionId` | To map during implementation | Not implemented |
| PG-030 | `form-designer` | `/app/designer/:formDefinitionId/fields` | To map during implementation | Not implemented |
| PG-031 | `form-designer` | `/app/designer/:formDefinitionId/options` | To map during implementation | Not implemented |
| PG-032 | `form-designer` | `/app/designer/:formDefinitionId/dependencies` | To map during implementation | Not implemented |
| PG-033 | `form-designer` | `/app/designer/:formDefinitionId/relationships` | To map during implementation | Not implemented |
| PG-034 | `form-designer` | `/app/designer/:formDefinitionId/uploads` | To map during implementation | Not implemented |
| PG-035 | `form-designer` | `/app/designer/:formDefinitionId/rules` | To map during implementation | Not implemented |
| PG-036 | `form-designer` | `/app/designer/:formDefinitionId/translations` | To map during implementation | Not implemented |
| PG-037 | `form-designer` | `/app/designer/:formDefinitionId/preview` | To map during implementation | Not implemented |
| PG-038 | `form-designer` | `/app/designer/:formDefinitionId/publish` | To map during implementation | Not implemented |
| PG-039 | `form-designer` | `/app/designer/:formDefinitionId/versions` | To map during implementation | Not implemented |
| PG-040 | `form-designer` | `/app/designer/:formDefinitionId/access` | To map during implementation | Not implemented |
| PG-041 | `organizations` | `/app/organization` | To map during implementation | Not implemented |
| PG-042 | `organizations` | `/app/organization/branding` | To map during implementation | Not implemented |
| PG-043 | `organizations` | `/app/organization/preferences` | To map during implementation | Not implemented |
| PG-044 | `organizations` | `/app/organization/catalogs` | To map during implementation | Not implemented |
| PG-045 | `organizations` | `/app/organization/catalogs/dependencies` | To map during implementation | Not implemented |
| PG-046 | `organizations` | `/app/organization/security/data` | To map during implementation | Not implemented |
| PG-047 | `organizations` | `/app/organization/security/authentication` | To map during implementation | Not implemented |
| PG-048 | `teams` | `/app/organization/teams` | To map during implementation | Not implemented |
| PG-049 | `teams` | `/app/organization/teams/:teamId` | To map during implementation | Not implemented |
| PG-050 | `identity` | `/app/organization/users` | To map during implementation | Not implemented |
| PG-051 | `identity` | `/app/organization/users/:userId` | To map during implementation | Not implemented |
| PG-052 | `security-operations` | `/app/security` | To map during implementation | Not implemented |
| PG-053 | `security-operations` | `/app/security/ownership` | To map during implementation | Not implemented |
| PG-054 | `audit` | `/app/security/audit` | To map during implementation | Not implemented |
| PG-055 | `audit` | `/app/security/audit/:auditEventId` | To map during implementation | Not implemented |
| PG-056 | `security-operations` | `/app/security/incidents` | To map during implementation | Not implemented |
| PG-057 | `security-operations` | `/app/security/authority` | To map during implementation | Not implemented |
| PG-058 | `security-operations` | `/app/security/recovery` | To map during implementation | Not implemented |
| PG-059 | `security-operations` | `/app/security/operations` | To map during implementation | Not implemented |
| PG-060 | `support` | `/app/forbidden` | To map during implementation | Not implemented |
| PG-061 | `support` | `/app/not-found` | To map during implementation | Not implemented |
| PG-062 | `support` | `/app/unavailable` | To map during implementation | Not implemented |
| PG-063 | `support` | `/app/conflict` | To map during implementation | Not implemented |
| PG-064 | `organizations` | `/app/organization/setup` | To map during implementation | Not implemented |
| PG-065 | `sharing` | `/app/access-requests` | To map during implementation | Not implemented |
| PG-066 | `sharing` | `/app/my-access` | To map during implementation | Not implemented |
| PG-067 | `organizations` | `/app/organization/security/retention` | To map during implementation | Not implemented |
| PG-068 | `exports` | `/app/exports/new` | To map during implementation | Not implemented |
| PG-069 | `support` | `/dev/ui-gallery` | To map during implementation | Not implemented |
