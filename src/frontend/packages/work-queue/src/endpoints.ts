import type { HttpMethod } from "./contracts";

/**
 * Compiled endpoint registry: the only URLs the coordinator will ever call (CLAUDE-DESIGN.MD §5.4). Templates use
 * `{name}` path parameters. Keys are stable identifiers shared with the typed API client.
 */
export const ENDPOINTS = {
  "session.get": { method: "GET", path: "/api/v1/session" },
  "session.personas": { method: "GET", path: "/api/v1/session/development-personas" },
  "session.developmentLogin": { method: "POST", path: "/api/v1/session/development-login" },
  "session.logout": { method: "POST", path: "/api/v1/session/logout" },
  "bootstrap.get": { method: "GET", path: "/api/v1/bootstrap" },
  "dashboard.get": { method: "GET", path: "/api/v1/dashboard" },
  "dashboard.count": { method: "GET", path: "/api/v1/dashboard/widgets/{widgetIdentifier}/count" },
  "audit.mine": { method: "GET", path: "/api/v1/my-activity" },
  "audit.record": { method: "GET", path: "/api/v1/form-records/{formRecordId}/activity" },
  "forms.list": { method: "GET", path: "/api/v1/form-definitions" },
  "forms.get": { method: "GET", path: "/api/v1/form-definitions/{formDefinitionId}" },
  "forms.create": { method: "POST", path: "/api/v1/form-definitions/designer" },
  "forms.addField": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/fields" },
  "forms.createGroup": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/groups" },
  "forms.updateField": { method: "PATCH", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}" },
  "forms.access": { method: "GET", path: "/api/v1/form-definitions/{formDefinitionId}/access" },
  "forms.protection": { method: "GET", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}/protection" },
  "forms.strengthenProtection": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}/protection" },
  "forms.retryProtectionMigration": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}/protection/retry" },
  "forms.requestProtectionReconciliation": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}/protection/reconciliations" },
  "forms.reclassifyField": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}/protection/reclassify" },
  "forms.fieldSearch": { method: "GET", path: "/api/v1/form-definitions/{formDefinitionId}/field-search" },
  "organization.searchApprovals": { method: "GET", path: "/api/v1/organization/search-approvals" },
  "organization.securityAdministrators": { method: "GET", path: "/api/v1/organization/security-administrators" },
  "organization.appointSecurityAdministrator": { method: "POST", path: "/api/v1/organization/security-administrators" },
  "organization.removeSecurityAdministrator": { method: "POST", path: "/api/v1/organization/security-administrators/{userId}/remove" },
  "forms.approveFieldSearch": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}/search-approval" },
  "forms.withdrawFieldSearch": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}/search-approval/withdraw" },
  "forms.updateFieldSearchers": { method: "PUT", path: "/api/v1/form-definitions/{formDefinitionId}/field-searchers" },
  "forms.downgradeProtection": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}/protection/downgrade" },
  "forms.cancelProtectionDowngrade": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}/protection/cancel-downgrade" },
  "organization.securityPolicy": { method: "GET", path: "/api/v1/organization/security-policy" },
  "organization.updateSecurityPolicy": { method: "PUT", path: "/api/v1/organization/security-policy" },
  "forms.updateAccess": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/access" },
  "forms.updateOptions": { method: "PUT", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}/options" },
  "forms.syncOptions": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/fields/{fieldDefinitionId}/options/sync" },
  "optionLists.list": { method: "GET", path: "/api/v1/option-lists" },
  "optionLists.create": { method: "POST", path: "/api/v1/option-lists" },
  "optionLists.update": { method: "PUT", path: "/api/v1/option-lists/{optionListId}" },
  "forms.saveDraft": { method: "PUT", path: "/api/v1/form-definitions/{formDefinitionId}/draft" },
  "forms.publish": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/publish" },
  "forms.restoreDraft": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/restore-draft" },
  "forms.versions": { method: "GET", path: "/api/v1/form-definitions/{formDefinitionId}/versions" },
  "forms.createRelationship": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/relationships" },
  "records.list": { method: "GET", path: "/api/v1/form-records" },
  // Phase C7 Advanced Field Search: predicate values travel in POST bodies only, never in URLs (ADR-PB-004).
  "search.capabilities": { method: "GET", path: "/api/v1/form-records/search-capabilities" },
  "search.fieldSearch": { method: "POST", path: "/api/v1/form-records/field-search" },
  "search.fieldSearchPage": { method: "POST", path: "/api/v1/form-records/field-search/page" },
  "records.versions": { method: "GET", path: "/api/v1/form-records/{formRecordId}/versions" },
  "records.version": { method: "GET", path: "/api/v1/form-records/{formRecordId}/versions/{contentRevision}" },
  "records.restorePreview": { method: "GET", path: "/api/v1/form-records/{formRecordId}/versions/{contentRevision}/restore-preview" },
  "records.restore": { method: "POST", path: "/api/v1/form-records/{formRecordId}/versions/{contentRevision}/restore" },
  "records.create": { method: "POST", path: "/api/v1/form-definitions/{formDefinitionId}/records" },
  "records.get": { method: "GET", path: "/api/v1/form-records/{formRecordId}" },
  "records.projection": { method: "GET", path: "/api/v1/form-records/{formRecordId}/projection" },
  "records.update": { method: "PATCH", path: "/api/v1/form-records/{formRecordId}" },
  "records.submit": { method: "POST", path: "/api/v1/form-records/{formRecordId}/submit" },
  "records.reopen": { method: "POST", path: "/api/v1/form-records/{formRecordId}/reopen" },
  "records.archive": { method: "POST", path: "/api/v1/form-records/{formRecordId}/archive" },
  "records.unarchive": { method: "POST", path: "/api/v1/form-records/{formRecordId}/unarchive" },
  "records.transferOwnership": { method: "POST", path: "/api/v1/form-records/{formRecordId}/transfer-ownership" },
  "related.list": { method: "GET", path: "/api/v1/form-records/{formRecordId}/related/{formRelationshipDefinitionId}" },
  "related.create": { method: "POST", path: "/api/v1/form-records/{formRecordId}/related" },
  "related.unlink": { method: "POST", path: "/api/v1/form-records/{formRecordId}/related/{childFormRecordId}/unlink" },
  "shares.list": { method: "GET", path: "/api/v1/form-records/{formRecordId}/shares" },
  "shares.create": { method: "POST", path: "/api/v1/form-records/{formRecordId}/shares" },
  "shares.revoke": { method: "POST", path: "/api/v1/form-records/{formRecordId}/shares/{recordShareId}/revoke" },
  "uploads.begin": { method: "POST", path: "/api/v1/upload-sessions" },
  "uploads.chunk": { method: "PUT", path: "/api/v1/upload-sessions/{uploadSessionId}/chunks/{chunkIndex}" },
  "uploads.get": { method: "GET", path: "/api/v1/upload-sessions/{uploadSessionId}" },
  "uploads.complete": { method: "POST", path: "/api/v1/upload-sessions/{uploadSessionId}/complete" },
  "uploads.commit": { method: "POST", path: "/api/v1/upload-sessions/{uploadSessionId}/commit" },
  "uploads.cancel": { method: "DELETE", path: "/api/v1/upload-sessions/{uploadSessionId}" },
  "uploads.mine": { method: "GET", path: "/api/v1/my-upload-jobs" },
  "documents.list": { method: "GET", path: "/api/v1/form-records/{formRecordId}/documents" },
  "documents.get": { method: "GET", path: "/api/v1/form-records/{formRecordId}/documents/{fileAttachmentId}" },
  "documents.remove": { method: "POST", path: "/api/v1/form-records/{formRecordId}/documents/{fileAttachmentId}/remove" },
  "organization.branding": { method: "GET", path: "/api/v1/organization/branding" },
  "organization.updateBranding": { method: "PUT", path: "/api/v1/organization/branding" },
  "organization.uploadLogo": { method: "POST", path: "/api/v1/organization/branding/logo" },
  "preferences.get": { method: "GET", path: "/api/v1/me/ui-preferences" },
  "preferences.update": { method: "PUT", path: "/api/v1/me/ui-preferences" },
  "users.list": { method: "GET", path: "/api/v1/users" },
  "teams.create": { method: "POST", path: "/api/v1/teams" },
  // Phase C8 M1: administration shell (organization overview, people directory, team directory/detail/rename/membership).
  "organization.overview": { method: "GET", path: "/api/v1/organization/overview" },
  "organization.members": { method: "GET", path: "/api/v1/organization/members" },
  "teams.directory": { method: "GET", path: "/api/v1/organization/teams" },
  "teams.get": { method: "GET", path: "/api/v1/teams/{teamId}" },
  "teams.rename": { method: "PATCH", path: "/api/v1/teams/{teamId}" },
  "teams.addMember": { method: "POST", path: "/api/v1/teams/{teamId}/members" },
  "teams.removeMember": { method: "POST", path: "/api/v1/teams/{teamId}/members/{userId}/remove" },
  // Phase C8 (C8-M1-01, R-4): durable membership operations (browsers poll these; they never re-POST) and operator actions.
  "teams.membershipOperation": { method: "GET", path: "/api/v1/teams/{teamId}/membership-operations/{operationId}" },
  "teams.membershipOperationByKey": { method: "GET", path: "/api/v1/teams/{teamId}/membership-operations/by-key/{clientKey}" },
  "teams.recheckMembershipOperation": { method: "POST", path: "/api/v1/teams/{teamId}/membership-operations/{operationId}/recheck" },
  "teams.reviewMembershipOperation": { method: "POST", path: "/api/v1/teams/{teamId}/membership-operations/{operationId}/review" },
} as const satisfies Record<string, { method: HttpMethod; path: string }>;

export type EndpointKey = keyof typeof ENDPOINTS;

/** Browser-native download route (outside the worker: the browser streams it to disk, CLAUDE-DESIGN §6.3). */
export const DOWNLOAD_PATH = "/api/v1/form-records/{formRecordId}/documents/{fileAttachmentId}/download";
export const LOGO_PATH = "/api/v1/organization/branding/logo";

export function resolvePath(template: string, params: Record<string, string> | undefined): string {
  return template.replace(/\{(\w+)\}/g, (_match, name: string) => {
    const value = params?.[name];
    if (value === undefined) {
      throw new Error(`Missing path parameter ${name}`);
    }
    return encodeURIComponent(value);
  });
}

export function buildUrl(key: EndpointKey, pathParams: Record<string, string> | undefined, query: Record<string, string | number | boolean | undefined> | undefined): string {
  const endpoint = ENDPOINTS[key];
  const path = resolvePath(endpoint.path, pathParams);
  if (!query) {
    return path;
  }
  const search = new URLSearchParams();
  for (const [name, value] of Object.entries(query)) {
    if (value !== undefined) {
      search.set(name, String(value));
    }
  }
  const text = search.toString();
  return text.length > 0 ? `${path}?${text}` : path;
}
