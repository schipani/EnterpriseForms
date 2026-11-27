/**
 * Generated contract types for the business API plus a typed client whose only transport is the app-shell request
 * coordinator (packages/work-queue, ADR-PB-006/007). No raw fetch lives here.
 */
import type { ApiOperation, IRequestCoordinator, OperationLane } from "@enterpriseforms/work-queue";
import type { components } from "./generated/enterprise-forms-v1";

export type { components as EnterpriseFormsV1Components, paths as EnterpriseFormsV1Paths } from "./generated/enterprise-forms-v1";

export const API_V1_PREFIX = "/api/v1" as const;

/** My Records query (C-29). `continuationToken` is an opaque cursor from a previous page of the same filters and sort. */
export type RecordListQuery = {
  formDefinitionId?: string;
  recordStatus?: string;
  includeChildren?: boolean;
  includeArchived?: boolean;
  /** Exact record id or short reference (REC-XXXXXXXX); combined with the other filters. */
  q?: string;
  sortBy?: "modifiedOn" | "createdOn" | "formName" | "recordStatus";
  sortDirection?: "asc" | "desc";
  pageSize?: number;
  continuationToken?: string;
};

type S = components["schemas"];

export type SessionResponse = S["SessionResponse"];
export type BootstrapResponse = S["BootstrapResponse"];
export type BrandingResponse = S["BrandingResponse"];
export type UpdateBrandingRequest = S["UpdateBrandingRequest"];
export type UiPreferenceResponse = S["UiPreferenceResponse"];
export type UpdateUiPreferenceRequest = S["UpdateUiPreferenceRequest"];
export type OrganizationSettingsResponse = S["OrganizationSettingsResponse"];
export type UpdateOrganizationSettingsRequest = S["UpdateOrganizationSettingsRequest"];
export type UserPreferencesResponse = S["UserPreferencesResponse"];
export type UpdateUserPreferencesRequest = S["UpdateUserPreferencesRequest"];
export type CountryCatalogResponse = S["CountryCatalogResponse"];
export type TimeZoneCatalogResponse = S["TimeZoneCatalogResponse"];
export type TimeZoneCatalogEntry = S["TimeZoneCatalogEntry"];
export type LanguageCatalogResponse = S["LanguageCatalogResponse"];
export type DateFormatCatalogResponse = S["DateFormatCatalogResponse"];
export type NumberFormatCatalogResponse = S["NumberFormatCatalogResponse"];
export type DevelopmentPersonaResponse = S["DevelopmentPersonaResponse"];
export type DashboardResponse = S["DashboardResponse"];
export type DashboardCountResponse = S["DashboardCountResponse"];
export type DashboardRecordStatisticsResponse = S["DashboardRecordStatisticsResponse"];
export type DashboardUploadStatisticsResponse = S["DashboardUploadStatisticsResponse"];
export type DashboardUploadPageResponse = S["DashboardUploadPageResponse"];
export type DashboardUploadItemResponse = S["DashboardUploadItemResponse"];
export type DashboardActivityPageResponse = S["DashboardActivityPageResponse"];
export type DashboardFormPageResponse = S["DashboardFormPageResponse"];
export type SecurityIncidentPageResponse = S["SecurityIncidentPageResponse"];
export type SecurityIncidentListItemResponse = S["SecurityIncidentListItemResponse"];
export type SecurityIncidentSummaryResponse = S["SecurityIncidentSummaryResponse"];
export type SecurityIncidentDetailResponse = S["SecurityIncidentDetailResponse"];
export type UpdateSecurityIncidentRequest = S["UpdateSecurityIncidentRequest"];
export type AddSecurityIncidentNoteRequest = S["AddSecurityIncidentNoteRequest"];
export type AuditEventResponse = S["AuditEventResponse"];
export type RecordActivityResponse = S["RecordActivityResponse"];
export type FormDefinitionSummaryResponse = S["FormDefinitionSummaryResponse"];
export type FormDefinitionDetailResponse = S["FormDefinitionDetailResponse"];
export type FieldDefinitionResponse = S["FieldDefinitionResponse"];
export type FieldOptionResponse = S["FieldOptionResponse"];
export type FormVersionResponse = S["FormVersionResponse"];
export type FormAuthorityRegistrationResponse = S["FormAuthorityRegistrationResponse"];
export type FormDraftResponse = S["FormDraftResponse"];
export type RelationshipDefinitionResponse = S["RelationshipDefinitionResponse"];
export type CreateFormDefinitionV2Request = S["CreateFormDefinitionV2Request"];
export type AddFieldDefinitionRequest = S["AddFieldDefinitionRequest"];
export type RenameFormDefinitionRequest = S["RenameFormDefinitionRequest"];
export type ChangeVersionBehaviorRequest = S["ChangeVersionBehaviorRequest"];
export type VersionBehaviorImpactResponse = S["VersionBehaviorImpactResponse"];
export type PublishImpactResponse = S["PublishImpactResponse"];
export type VersionComparisonResponse = S["VersionComparisonResponse"];
export type LayoutComparisonResponse = S["LayoutComparisonResponse"];
export type LayoutChangeResponse = S["LayoutChangeResponse"];
export type LayoutChangeDetailResponse = S["LayoutChangeDetailResponse"];
export type PublishImpactNoticeResponse = S["PublishImpactNoticeResponse"];
/** Phase C9: which published layout a form's records are viewed and edited with. */
export type VersionBehavior = "Historical" | "Latest" | "Hybrid";
/** Phase C9: a record projection read for display (`view`) or for the editor (`edit`). */
export type RecordLayoutPurpose = "view" | "edit";
export type GroupDefinitionResponse = S["GroupDefinitionResponse"];
export type CreateGroupDefinitionRequest = S["CreateGroupDefinitionRequest"];
export type UpdateFieldDefinitionRequest = S["UpdateFieldDefinitionRequest"];
export type FieldProtectionResponse = S["FieldProtectionResponse"];
export type FieldSearchConfigurationResponse = S["FieldSearchConfigurationResponse"];
export type FieldSearchStatusResponse = S["FieldSearchStatusResponse"];
export type ApproveFieldSearchRequest = S["ApproveFieldSearchRequest"];
export type WithdrawFieldSearchRequest = S["WithdrawFieldSearchRequest"];
export type UpdateFieldSearchersRequest = S["UpdateFieldSearchersRequest"];
export type FieldSearchAdministrationResponse = S["FieldSearchAdministrationResponse"];
export type FieldSearchAdministrationFormResponse = S["FieldSearchAdministrationFormResponse"];
export type SecurityAdministratorsResponse = S["SecurityAdministratorsResponse"];
export type OwnerRecoveryNoticeResponse = S["OwnerRecoveryNoticeResponse"];
export type OwnerRecoveryNoticeCaseResponse = S["OwnerRecoveryNoticeCaseResponse"];
export type OwnerRecoveryNoticeSubjectResponse = S["OwnerRecoveryNoticeSubjectResponse"];
export type OwnerRecoveryObjectionRequest = S["OwnerRecoveryObjectionRequest"];
export type OwnerRecoveryObjectionResponse = S["OwnerRecoveryObjectionResponse"];
export type SecurityAdministratorResponse = S["SecurityAdministratorResponse"];
export type AppointSecurityAdministratorRequest = S["AppointSecurityAdministratorRequest"];
export type RemoveSecurityAdministratorRequest = S["RemoveSecurityAdministratorRequest"];
export type AppointSecurityAuditorRequest = S["AppointSecurityAuditorRequest"];
export type RemoveSecurityAuditorRequest = S["RemoveSecurityAuditorRequest"];
export type SecurityAuditorReasonCodesResponse = S["SecurityAuditorReasonCodesResponse"];
export type SecurityAuditorReasonCodeResponse = S["SecurityAuditorReasonCodeResponse"];
export type StrengthenFieldProtectionRequest = S["StrengthenFieldProtectionRequest"];
export type FieldProtectionMigrationResponse = S["FieldProtectionMigrationResponse"];
export type FieldProtectionVerificationResponse = S["FieldProtectionVerificationResponse"];
export type FieldProtectionReconciliationResponse = S["FieldProtectionReconciliationResponse"];
export type FieldProtectionAttestationResponse = S["FieldProtectionAttestationResponse"];
export type RequestFieldProtectionReconciliationRequest = S["RequestFieldProtectionReconciliationRequest"];
export type RecordLifecycleRequest = S["RecordLifecycleRequest"];
export type RecordLifecycleResponse = S["RecordLifecycleResponse"];
export type RecordLifecycleStateResponse = S["RecordLifecycleStateResponse"];
export type RecordLifecycleTransition = "submit" | "reopen" | "archive" | "unarchive";
export type RetryFieldProtectionMigrationRequest = S["RetryFieldProtectionMigrationRequest"];
export type ReclassifyFieldRequest = S["ReclassifyFieldRequest"];
export type DowngradeFieldProtectionRequest = S["DowngradeFieldProtectionRequest"];
export type CancelFieldProtectionDowngradeRequest = S["CancelFieldProtectionDowngradeRequest"];
export type OrganizationSecurityPolicyResponse = S["OrganizationSecurityPolicyResponse"];
export type UpdateOrganizationSecurityPolicyRequest = S["UpdateOrganizationSecurityPolicyRequest"];
export type UpdateFormAccessRequest = S["UpdateFormAccessRequest"];
export type FormAccessResponse = S["FormAccessResponse"];
export type FormAccessSubjectResponse = S["FormAccessSubjectResponse"];
export type DirectoryTeamResponse = S["DirectoryTeamResponse"];
export type OrganizationOverviewResponse = S["OrganizationOverviewResponse"];
export type OrganizationMemberDirectoryResponse = S["OrganizationMemberDirectoryResponse"];
export type OrganizationMemberResponse = S["OrganizationMemberResponse"];
export type TeamDirectoryResponse = S["TeamDirectoryResponse"];
export type TeamSummaryResponse = S["TeamSummaryResponse"];
export type TeamDetailResponse = S["TeamDetailResponse"];
export type TeamMembershipOperationResponse = S["TeamMembershipOperationResponse"];
export type TeamMembershipChangeResponse = S["TeamMembershipChangeResponse"];
export type TeamMemberResponse = S["TeamMemberResponse"];
export type RenameTeamRequest = S["RenameTeamRequest"];
export type CreateTeamRequest = S["CreateTeamRequest"];
export type TeamResponse = S["TeamResponse"];
export type TeamMemberRequest = S["TeamMemberRequest"];
export type UpdateFieldOptionsRequest = S["UpdateFieldOptionsRequest"];
export type FieldOptionChange = S["FieldOptionChange"];
export type OptionListResponse = S["OptionListResponse"];
export type OptionListItemResponse = S["OptionListItemResponse"];
export type CreateOptionListRequest = S["CreateOptionListRequest"];
export type UpdateOptionListRequest = S["UpdateOptionListRequest"];
export type OptionListItemChange = S["OptionListItemChange"];
export type SaveDraftRequest = S["SaveDraftRequest"];
export type PublishFormRequest = S["PublishFormRequest"];
export type RestoreVersionRequest = S["RestoreVersionRequest"];
export type CreateRelationshipDefinitionRequest = S["CreateRelationshipDefinitionRequest"];
export type RecordListResponse = S["RecordListResponse"];
export type FieldSearchCapabilitiesResponse = S["FieldSearchCapabilitiesResponse"];
export type FieldSearchCapabilityField = S["FieldSearchCapabilityField"];
export type FieldSearchNotSearchableField = S["FieldSearchNotSearchableField"];
export type FieldSearchOptionChoice = S["FieldSearchOptionChoice"];
export type FieldSearchCondition = S["FieldSearchCondition"];
export type FieldSearchRequest = S["FieldSearchRequest"];
export type FieldSearchPageRequest = S["FieldSearchPageRequest"];
export type FieldSearchResponse = S["FieldSearchResponse"];
export type RecordListItemResponse = S["RecordListItemResponse"];
export type RecordVersionItemResponse = S["RecordVersionItemResponse"];
export type RecordVersionListResponse = S["RecordVersionListResponse"];
export type RecordVersionViewResponse = S["RecordVersionViewResponse"];
export type RestorePreviewResponse = S["RestorePreviewResponse"];
export type RestoreFieldChangeResponse = S["RestoreFieldChangeResponse"];
export type RestoreSectionChangeResponse = S["RestoreSectionChangeResponse"];
export type RestoreValidationResponse = S["RestoreValidationResponse"];
export type RecordRestoreRequest = S["RecordRestoreRequest"];
export type RecordRestoreResponse = S["RecordRestoreResponse"];
export type RecordProjectionResponse = S["RecordProjectionResponse"];
export type FormRecordResponse = S["FormRecordResponse"];
export type FieldValueResponse = S["FieldValueResponse"];
export type CreateFormRecordRequest = S["CreateFormRecordRequest"];
export type UpdateFormRecordRequest = S["UpdateFormRecordRequest"];
export type RecordGroupValuesRequest = S["RecordGroupValuesRequest"];
export type RecordGroupResponse = S["RecordGroupResponse"];
export type CreateChildRecordRequest = S["CreateChildRecordRequest"];
export type ChildRecordListResponse = S["ChildRecordListResponse"];
export type ChildRecordSummaryResponse = S["ChildRecordSummaryResponse"];
export type UnlinkChildRecordRequest = S["UnlinkChildRecordRequest"];
export type CommandStatusResponse = S["CommandStatusResponse"];
export type BeginUploadRequest = S["BeginUploadRequest"];
export type UploadSessionResponse = S["UploadSessionResponse"];
export type UploadChunkReceiptResponse = S["UploadChunkReceiptResponse"];
export type DocumentSummaryResponse = S["DocumentSummaryResponse"];
export type DocumentDetailResponse = S["DocumentDetailResponse"];
export type DocumentVersionResponse = S["DocumentVersionResponse"];
export type RemoveDocumentRequest = S["RemoveDocumentRequest"];
export type DirectoryUserResponse = S["DirectoryUserResponse"];
export type CreateShareRequest = S["CreateShareRequest"];
export type ShareResponse = S["ShareResponse"];
export type RecordShareListResponse = S["RecordShareListResponse"];
export type ShareDetailsResponse = S["ShareDetailsResponse"];
export type RecordSecurityHistoryResponse = S["RecordSecurityHistoryResponse"];
export type ShareEffectiveScopeResponse = S["ShareEffectiveScopeResponse"];
export type MyAccessResponse = S["MyAccessResponse"];
export type MyAccessEntryResponse = S["MyAccessEntryResponse"];
export type TransferOwnershipRequest = S["TransferOwnershipRequest"];
export type MemberOwnedRecordsResponse = S["MemberOwnedRecordsResponse"];
export type ReassignRecordsRequest = S["ReassignRecordsRequest"];
export type ReassignRecordsResponse = S["ReassignRecordsResponse"];
export type RecordSecurityHistoryItemResponse = S["RecordSecurityHistoryItemResponse"];
export type UpdateShareRequest = S["UpdateShareRequest"];
export type UpdateShareResponse = S["UpdateShareResponse"];
export type RecordShareSummaryResponse = S["RecordShareSummaryResponse"];

export type ApiResult<T> = { result: T; etag?: string; status: number };

/** Chart period and the viewer's IANA zone (calendar buckets are computed by the server in that zone). */
export type DashboardPeriodQuery = { periodDays: 7 | 30 | 90; timeZone?: string };

/** Shared shape of the dashboard section queries: every list is paged on the server (default 15 rows, opaque cursor). */
export type DashboardPageQuery = { pageSize?: number; continuationToken?: string };
/** People directory (C8 M2): server-paged, default 15 rows. */
export type OrganizationMemberQuery = {
  q?: string;
  role?: "OrganizationOwner" | "Designer" | "SecurityAdministrator" | "SecurityAuditor" | "None";
  signInStatus?: "SignedIn" | "NotRecorded" | "NeverSignedIn";
  membershipState?: "Enabled" | "Disabled";
  pageSize?: number;
  continuationToken?: string;
};
export type MemberRoleChangeRequest = S["MemberRoleChangeRequest"];
export type MemberCommandRequest = S["MemberCommandRequest"];
export type OwnerProposalRequest = S["OwnerProposalRequest"];
export type CreateMembershipRequestRequest = S["CreateMembershipRequestRequest"];
export type MemberCommandResponse = S["MemberCommandResponse"];
export type OwnerProposalResponse = S["OwnerProposalResponse"];
export type OwnerProposalPageResponse = S["OwnerProposalPageResponse"];
export type MembershipRequestPageResponse = S["MembershipRequestPageResponse"];
export type UserProfileResponse = S["UserProfileResponse"];
export type CancelMembershipRequestRequest = S["CancelMembershipRequestRequest"];
export type ReplaceMembershipRequestRequest = S["ReplaceMembershipRequestRequest"];
export type UpdateUserProfileRequest = S["UpdateUserProfileRequest"];
export type PresenceQueryResponse = S["PresenceQueryResponse"];
export type UserPresenceResponse = S["UserPresenceResponse"];
/** Informational presence of a person; "Unknown" means it could not be determined (never "offline"). */
export type PresenceState = "Available" | "Away" | "Offline" | "Unknown";
/** Approval lists (C8 M2): two independent server-paged lists, default 5 rows, newest first. */
export type ApprovalQuery = { q?: string; status?: "Pending" | "Approved" | "Rejected" | "Expired" | "Cancelled" | "Replaced"; pageSize?: number; continuationToken?: string };
export type MembershipRequestResponse = S["MembershipRequestResponse"];

export type DashboardUploadQuery = DashboardPageQuery & { q?: string; status?: "InProgress" | "Completed" | "Failed" | "Cancelled"; from?: string; before?: string };

export type DashboardActivityQuery = DashboardPageQuery & { category?: string; outcome?: string; from?: string; before?: string };

export type DashboardFormQuery = DashboardPageQuery & { q?: string; access?: "create" | "design"; status?: "published" | "unpublished" };

export type SecurityIncidentQuery = DashboardPageQuery & { q?: string; type?: string; severity?: string; status?: string; from?: string; before?: string };

/** C8 M4-2 audit explorer (PG-054): ids, codes and ISO dates only; `cursor` is the opaque `nextCursor` of the previous page of the same filters. */
export type AuditExplorerQuery = {
  from?: string;
  to?: string;
  actorUserId?: string;
  action?: string;
  category?: string;
  outcome?: string;
  entityType?: string;
  entityId?: string;
  formRecordId?: string;
  correlationId?: string;
  excludeAuditAccess?: boolean;
  pageSize?: number;
  cursor?: string;
};
export type AuditExplorerPageResponse = S["AuditExplorerPageResponse"];
export type AuditExplorerEventResponse = S["AuditExplorerEventResponse"];
export type AuditEventDetailResponse = S["AuditEventDetailResponse"];

/** C8 M4-3 security-command timeline (AC-8.2): one kind code, one state code and ISO dates; `cursor` is the previous page's `nextCursor`. */
export type SecurityCommandTimelineQuery = { kind?: string; state?: string; from?: string; to?: string; pageSize?: number; cursor?: string };
export type SecurityCommandTimelinePageResponse = S["SecurityCommandTimelinePageResponse"];
export type SecurityCommandTimelineItemResponse = S["SecurityCommandTimelineItemResponse"];
export type SecurityCommandTimelineDetailResponse = S["SecurityCommandTimelineDetailResponse"];

/** C8 M4-4 Security Home (PG-052; AC-8.4, AC-8.5): one section per request, each authorized by the Authority on its own. */
export type SecurityOverviewSection = "gate" | "commands" | "archive" | "coverage";
export type SecurityOverviewSectionResponse = S["SecurityOverviewSectionResponse"];
export type SecurityOverviewGateResponse = S["SecurityOverviewGateResponse"];
export type SecurityOverviewCommandsResponse = S["SecurityOverviewCommandsResponse"];
export type SecurityOverviewCommandResponse = S["SecurityOverviewCommandResponse"];
export type SecurityOverviewArchiveResponse = S["SecurityOverviewArchiveResponse"];
export type SecurityOverviewArchiveStreamResponse = S["SecurityOverviewArchiveStreamResponse"];
export type SecurityCoverageResponse = S["SecurityCoverageResponse"];
export type SecurityCoverageDecisionResponse = S["SecurityCoverageDecisionResponse"];
export type SecurityCoverageAttestationResponse = S["SecurityCoverageAttestationResponse"];
export type AuditMetadataItemResponse = S["AuditMetadataItemResponse"];
export type AuditWithheldMetadataResponse = S["AuditWithheldMetadataResponse"];

function idempotencyKey(): string {
  return typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

/** Typed operations over the coordinator. Every call names a registered endpoint key and a lane; nothing else can reach the network. */
export function createApiClient(coordinator: IRequestCoordinator) {
  const read = <T>(endpointKey: string, pathParams?: Record<string, string>, query?: Record<string, string | number | boolean | undefined>, lane: OperationLane = "interactive-read", signal?: AbortSignal) =>
    coordinator.execute<T>({ lane, method: "GET", endpointKey, ...(pathParams ? { pathParams } : {}), ...(query ? { query } : {}) }, signal);
  const write = <T>(method: "POST" | "PUT" | "PATCH" | "DELETE", endpointKey: string, pathParams: Record<string, string> | undefined, payload: unknown, options: { idempotent?: boolean; idempotencyKey?: string; resourceKey?: string; expectedRevision?: string; lane?: OperationLane; responseKind?: "json" | "none"; query?: ApiOperation["query"] } = {}) =>
    coordinator.execute<T>({
      lane: options.lane ?? "interactive-write",
      method,
      endpointKey,
      ...(pathParams ? { pathParams } : {}),
      ...(options.query ? { query: options.query } : {}),
      ...(payload !== undefined ? { payload } : {}),
      ...(options.idempotent ? { idempotencyKey: options.idempotencyKey ?? idempotencyKey() } : {}),
      ...(options.resourceKey ? { resourceKey: options.resourceKey } : {}),
      ...(options.expectedRevision ? { expectedRevision: options.expectedRevision } : {}),
      ...(options.responseKind ? { responseKind: options.responseKind } : {}),
    });

  return {
    session: {
      get: (signal?: AbortSignal) => read<SessionResponse>("session.get", undefined, undefined, "security", signal),
      personas: () => read<DevelopmentPersonaResponse[]>("session.personas", undefined, undefined, "security"),
      developmentLogin: (personaKey: string) => write<SessionResponse>("POST", "session.developmentLogin", undefined, { personaKey }, { lane: "security" }),
      logout: () => write<void>("POST", "session.logout", undefined, undefined, { lane: "security", responseKind: "none" }),
      bootstrap: (signal?: AbortSignal) => read<BootstrapResponse>("bootstrap.get", undefined, undefined, "security", signal),
    },
    dashboard: {
      get: (signal?: AbortSignal) => read<DashboardResponse>("dashboard.get", undefined, undefined, "interactive-read", signal),
      count: (widgetIdentifier: string, signal?: AbortSignal) => read<DashboardCountResponse>("dashboard.count", { widgetIdentifier }, undefined, "background-read", signal),
      recordStatistics: (query: DashboardPeriodQuery, signal?: AbortSignal) => read<DashboardRecordStatisticsResponse>("dashboard.recordStatistics", undefined, query, "interactive-read", signal),
      uploadStatistics: (query: DashboardPeriodQuery, signal?: AbortSignal) => read<DashboardUploadStatisticsResponse>("dashboard.uploadStatistics", undefined, query, "interactive-read", signal),
      uploads: (query: DashboardUploadQuery, signal?: AbortSignal) => read<DashboardUploadPageResponse>("dashboard.uploads", undefined, query, "interactive-read", signal),
      activity: (query: DashboardActivityQuery, signal?: AbortSignal) => read<DashboardActivityPageResponse>("dashboard.activity", undefined, query, "interactive-read", signal),
      forms: (query: DashboardFormQuery, signal?: AbortSignal) => read<DashboardFormPageResponse>("dashboard.forms", undefined, query, "interactive-read", signal),
    },
    security: {
      incidents: (query: SecurityIncidentQuery, signal?: AbortSignal) => read<SecurityIncidentPageResponse>("security.incidents", undefined, query, "interactive-read", signal),
      incidentSummary: (query: DashboardPeriodQuery, signal?: AbortSignal) => read<SecurityIncidentSummaryResponse>("security.incidentSummary", undefined, query, "interactive-read", signal),
      incident: (securityIncidentId: string, signal?: AbortSignal) => read<SecurityIncidentDetailResponse>("security.incident", { securityIncidentId }, undefined, "interactive-read", signal),
      updateIncident: (securityIncidentId: string, request: UpdateSecurityIncidentRequest) => write<SecurityIncidentDetailResponse>("PATCH", "security.updateIncident", { securityIncidentId }, request, { lane: "security", resourceKey: `security-incident:${securityIncidentId}` }),
      addIncidentNote: (securityIncidentId: string, request: AddSecurityIncidentNoteRequest) => write<SecurityIncidentDetailResponse>("POST", "security.addIncidentNote", { securityIncidentId }, request, { lane: "security", resourceKey: `security-incident:${securityIncidentId}` }),
      /** C8 M4-4: one Security Home section (gate, commands, archive, coverage); authorized from the Authority's committed state, so it answers while the gate is closed. */
      overview: (section: SecurityOverviewSection, signal?: AbortSignal) => read<SecurityOverviewSectionResponse>("security.overview", { section }, undefined, "interactive-read", signal),
    },
    audit: {
      mine: (limit = 25, signal?: AbortSignal) => read<AuditEventResponse[]>("audit.mine", undefined, { limit }, "background-read", signal),
      record: (formRecordId: string, signal?: AbortSignal) => read<RecordActivityResponse>("audit.record", { formRecordId }, undefined, "interactive-read", signal),
      /** PG-054: every page is authorized and recorded by the Authority (Security Auditors only). */
      events: (query: AuditExplorerQuery, signal?: AbortSignal) => read<AuditExplorerPageResponse>("audit.events", undefined, query, "interactive-read", signal),
      /** PG-055: one event, redacted by the server's catalog. */
      event: (auditEventId: string, signal?: AbortSignal) => read<AuditEventDetailResponse>("audit.event", { auditEventId }, undefined, "interactive-read", signal),
      /** AC-8.2: read and served by the Authority after it recorded the access (Security Auditors only). */
      securityCommands: (query: SecurityCommandTimelineQuery, signal?: AbortSignal) =>
        read<SecurityCommandTimelinePageResponse>("audit.securityCommands", undefined, query, "interactive-read", signal),
    },
    forms: {
      list: (signal?: AbortSignal) => read<FormDefinitionSummaryResponse[]>("forms.list", undefined, undefined, "interactive-read", signal),
      get: (formDefinitionId: string, signal?: AbortSignal) => read<FormDefinitionDetailResponse>("forms.get", { formDefinitionId }, undefined, "interactive-read", signal),
      create: (request: CreateFormDefinitionV2Request) => write<FormDefinitionDetailResponse>("POST", "forms.create", undefined, request, { idempotent: true }),
      rename: (formDefinitionId: string, request: RenameFormDefinitionRequest) => write<FormDefinitionDetailResponse>("PATCH", "forms.rename", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      /** Phase C9: sets the form's version behaviour (audited; optimistic on the form revision; no record or version changes). */
      changeVersionBehavior: (formDefinitionId: string, request: ChangeVersionBehaviorRequest) => write<FormDefinitionDetailResponse>("PUT", "forms.changeVersionBehavior", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      /** Phase C9: approximate impact of switching to `target`, over the records the caller can discover only. */
      versionBehaviorImpact: (formDefinitionId: string, target: VersionBehavior, signal?: AbortSignal) => read<VersionBehaviorImpactResponse>("forms.versionBehaviorImpact", { formDefinitionId }, { target }, "interactive-read", signal),
      addField: (formDefinitionId: string, request: AddFieldDefinitionRequest) => write<FieldDefinitionResponse>("POST", "forms.addField", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      createGroup: (formDefinitionId: string, request: CreateGroupDefinitionRequest) => write<GroupDefinitionResponse>("POST", "forms.createGroup", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      updateField: (formDefinitionId: string, fieldDefinitionId: string, request: UpdateFieldDefinitionRequest) => write<FieldDefinitionResponse>("PATCH", "forms.updateField", { formDefinitionId, fieldDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      protection: (formDefinitionId: string, fieldDefinitionId: string, signal?: AbortSignal) => read<FieldProtectionResponse>("forms.protection", { formDefinitionId, fieldDefinitionId }, undefined, "interactive-read", signal),
      strengthenProtection: (formDefinitionId: string, fieldDefinitionId: string, request: StrengthenFieldProtectionRequest) => write<FieldProtectionResponse>("POST", "forms.strengthenProtection", { formDefinitionId, fieldDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      retryProtectionMigration: (formDefinitionId: string, fieldDefinitionId: string, request: RetryFieldProtectionMigrationRequest) => write<FieldProtectionResponse>("POST", "forms.retryProtectionMigration", { formDefinitionId, fieldDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      requestProtectionReconciliation: (formDefinitionId: string, fieldDefinitionId: string, request: RequestFieldProtectionReconciliationRequest) => write<FieldProtectionResponse>("POST", "forms.requestProtectionReconciliation", { formDefinitionId, fieldDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      reclassifyField: (formDefinitionId: string, fieldDefinitionId: string, request: ReclassifyFieldRequest) => write<FieldProtectionResponse>("POST", "forms.reclassifyField", { formDefinitionId, fieldDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      fieldSearch: (formDefinitionId: string, signal?: AbortSignal) => read<FieldSearchConfigurationResponse>("forms.fieldSearch", { formDefinitionId }, undefined, "interactive-read", signal),
      approveFieldSearch: (formDefinitionId: string, fieldDefinitionId: string, request: ApproveFieldSearchRequest) => write<FieldSearchConfigurationResponse>("POST", "forms.approveFieldSearch", { formDefinitionId, fieldDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      withdrawFieldSearch: (formDefinitionId: string, fieldDefinitionId: string, request: WithdrawFieldSearchRequest) => write<FieldSearchConfigurationResponse>("POST", "forms.withdrawFieldSearch", { formDefinitionId, fieldDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      updateFieldSearchers: (formDefinitionId: string, request: UpdateFieldSearchersRequest) => write<void>("PUT", "forms.updateFieldSearchers", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      downgradeProtection: (formDefinitionId: string, fieldDefinitionId: string, request: DowngradeFieldProtectionRequest) => write<FieldProtectionResponse>("POST", "forms.downgradeProtection", { formDefinitionId, fieldDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      cancelProtectionDowngrade: (formDefinitionId: string, fieldDefinitionId: string, request: CancelFieldProtectionDowngradeRequest) => write<FieldProtectionResponse>("POST", "forms.cancelProtectionDowngrade", { formDefinitionId, fieldDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      access: (formDefinitionId: string, signal?: AbortSignal) => read<FormAccessResponse>("forms.access", { formDefinitionId }, undefined, "interactive-read", signal),
      updateAccess: (formDefinitionId: string, request: UpdateFormAccessRequest) => write<FormAccessResponse>("POST", "forms.updateAccess", { formDefinitionId }, request, { idempotent: true, resourceKey: `form-access:${formDefinitionId}` }),
      updateOptions: (formDefinitionId: string, fieldDefinitionId: string, request: UpdateFieldOptionsRequest) => write<FieldDefinitionResponse>("PUT", "forms.updateOptions", { formDefinitionId, fieldDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      syncOptions: (formDefinitionId: string, fieldDefinitionId: string) => write<FieldDefinitionResponse>("POST", "forms.syncOptions", { formDefinitionId, fieldDefinitionId }, {}, { resourceKey: `form:${formDefinitionId}` }),
      saveDraft: (formDefinitionId: string, request: SaveDraftRequest) => write<FormDraftResponse>("PUT", "forms.saveDraft", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      publish: (formDefinitionId: string, request: PublishFormRequest) => write<FormVersionResponse>("POST", "forms.publish", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      restoreDraft: (formDefinitionId: string, request: RestoreVersionRequest) => write<FormDraftResponse>("POST", "forms.restoreDraft", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      versions: (formDefinitionId: string, signal?: AbortSignal) => read<FormVersionResponse[]>("forms.versions", { formDefinitionId }, undefined, "interactive-read", signal),
      /** Phase C9 M2: what publishing the draft at this revision would change (read-only). */
      publishImpact: (formDefinitionId: string, expectedDraftRevision: number, signal?: AbortSignal) => read<PublishImpactResponse>("forms.publishImpact", { formDefinitionId }, { expectedDraftRevision }, "interactive-read", signal),
      /** C-188 BF-1: whether the current published version is registered with the Authority (designers only). */
      authorityRegistration: (formDefinitionId: string, signal?: AbortSignal) => read<FormAuthorityRegistrationResponse>("forms.authorityRegistration", { formDefinitionId }, undefined, "interactive-read", signal),
      /** C-188 BF-1: completes or withdraws a pending registration without publishing again (idempotent, audited). */
      recoverAuthorityRegistration: (formDefinitionId: string) => write<FormAuthorityRegistrationResponse>("POST", "forms.recoverAuthorityRegistration", { formDefinitionId }, {}, { resourceKey: `form:${formDefinitionId}` }),
      /** Phase C9 M2: semantic comparison; `from`/`to` are version ids or "draft". */
      compareVersions: (formDefinitionId: string, from: string, to: string, signal?: AbortSignal) => read<VersionComparisonResponse>("forms.compareVersions", { formDefinitionId }, { from, to }, "interactive-read", signal),
      createRelationship: (formDefinitionId: string, request: CreateRelationshipDefinitionRequest) => write<RelationshipDefinitionResponse>("POST", "forms.createRelationship", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
    },
    optionLists: {
      list: (signal?: AbortSignal) => read<OptionListResponse[]>("optionLists.list", undefined, undefined, "interactive-read", signal),
      create: (request: CreateOptionListRequest) => write<OptionListResponse>("POST", "optionLists.create", undefined, request, { resourceKey: "option-lists" }),
      update: (optionListId: string, request: UpdateOptionListRequest) => write<OptionListResponse>("PUT", "optionLists.update", { optionListId }, request, { resourceKey: `option-list:${optionListId}` }),
    },
    records: {
      list: (query: RecordListQuery, signal?: AbortSignal) => read<RecordListResponse>("records.list", undefined, query, "interactive-read", signal),
      /** Phase C6 M2: accepted versions, oldest first (Authority-listed); `after` continues from a version number. */
      versions: (formRecordId: string, after?: number, signal?: AbortSignal) => read<RecordVersionListResponse>("records.versions", { formRecordId }, after === undefined ? undefined : { after }, "interactive-read", signal),
      /** Phase C6 M2: one earlier version, verified and read-only. */
      version: (formRecordId: string, contentRevision: number, signal?: AbortSignal) => read<RecordVersionViewResponse>("records.version", { formRecordId, contentRevision: String(contentRevision) }, undefined, "interactive-read", signal),
      /** Phase C6 M3: what restoring a version would change (owner only, nothing written). */
      restorePreview: (formRecordId: string, contentRevision: number, signal?: AbortSignal) => read<RestorePreviewResponse>("records.restorePreview", { formRecordId, contentRevision: String(contentRevision) }, undefined, "interactive-read", signal),
      /** Phase C6 M3: restores a version as a new revision; If-Match carries the reviewed head, retries reuse the idempotency key. */
      restore: (formRecordId: string, contentRevision: number, request: RecordRestoreRequest) => write<RecordRestoreResponse>("POST", "records.restore", { formRecordId, contentRevision: String(contentRevision) }, request, { idempotent: true, resourceKey: `record:${formRecordId}`, expectedRevision: String(request.expectedCurrentContentRevision) }),
      create: (formDefinitionId: string, request: CreateFormRecordRequest) => write<FormRecordResponse>("POST", "records.create", { formDefinitionId }, request, { idempotent: true }),
      /** Phase C9: `purpose` selects the view layout (default) or the edit layout of the form's version behaviour. */
      projection: (formRecordId: string, signal?: AbortSignal, purpose: RecordLayoutPurpose = "view") => read<RecordProjectionResponse>("records.projection", { formRecordId }, purpose === "edit" ? { purpose } : undefined, "interactive-read", signal),
      lifecycle: (transition: RecordLifecycleTransition, formRecordId: string, request: RecordLifecycleRequest) => write<RecordLifecycleResponse>("POST", `records.${transition}`, { formRecordId }, request, { idempotent: true, resourceKey: `record:${formRecordId}`, expectedRevision: String(request.expectedContentRevision) }),
      update: (formRecordId: string, request: UpdateFormRecordRequest) => write<FormRecordResponse>("PATCH", "records.update", { formRecordId }, request, { idempotent: true, resourceKey: `record:${formRecordId}`, expectedRevision: String(request.expectedContentRevision) }),
    },
    search: {
      /** Phase C7: server-authoritative searchable fields and operators for one form (M0: none, trusted search not enabled). */
      capabilities: (formDefinitionId: string, signal?: AbortSignal) => read<FieldSearchCapabilitiesResponse>("search.capabilities", undefined, { formDefinitionId }, "interactive-read", signal),
      /** Phase C7: advanced field search; a POST so predicate values never enter a URL. Not idempotency-keyed: a search changes nothing. */
      fieldSearch: (request: FieldSearchRequest) => write<FieldSearchResponse>("POST", "search.fieldSearch", undefined, request, { lane: "interactive-read" }),
      page: (request: FieldSearchPageRequest) => write<FieldSearchResponse>("POST", "search.fieldSearchPage", undefined, request, { lane: "interactive-read" }),
    },
    related: {
      list: (formRecordId: string, formRelationshipDefinitionId: string, signal?: AbortSignal) => read<ChildRecordListResponse>("related.list", { formRecordId, formRelationshipDefinitionId }, undefined, "interactive-read", signal),
      create: (formRecordId: string, request: CreateChildRecordRequest) => write<FormRecordResponse>("POST", "related.create", { formRecordId }, request, { idempotent: true, resourceKey: `record:${formRecordId}` }),
      unlink: (formRecordId: string, childFormRecordId: string, request: UnlinkChildRecordRequest) => write<CommandStatusResponse>("POST", "related.unlink", { formRecordId, childFormRecordId }, request, { idempotent: true, resourceKey: `record:${formRecordId}` }),
    },
    shares: {
      list: (formRecordId: string, signal?: AbortSignal) => read<RecordShareListResponse>("shares.list", { formRecordId }, undefined, "interactive-read", signal),
      create: (formRecordId: string, request: CreateShareRequest) => write<ShareResponse>("POST", "shares.create", { formRecordId }, request, { idempotent: true, resourceKey: `record:${formRecordId}` }),
      /** C10 M2: the Authority's committed share; `shareRevision` is the authoritative revision behind the share ETag. */
      get: (formRecordId: string, recordShareId: string, signal?: AbortSignal) => read<ShareDetailsResponse>("shares.get", { formRecordId, recordShareId }, undefined, "interactive-read", signal),
      /** C10 M2: the record's sharing and ownership changes from the Authority, each with its own command state. */
      history: (formRecordId: string, signal?: AbortSignal) => read<RecordSecurityHistoryResponse>("shares.history", { formRecordId }, undefined, "interactive-read", signal),
      ownershipHistory: (formRecordId: string, signal?: AbortSignal) => read<RecordSecurityHistoryResponse>("shares.ownershipHistory", { formRecordId }, undefined, "interactive-read", signal),
      /** C10 M2 (PG-023): what the recipient can do through the share now, as the Authority decides. */
      effectiveScope: (formRecordId: string, recordShareId: string, signal?: AbortSignal) => read<ShareEffectiveScopeResponse>("shares.effectiveScope", { formRecordId, recordShareId }, undefined, "interactive-read", signal),
      /** C10 M2 (PG-066): the caller's own access sources. */
      mine: (signal?: AbortSignal) => read<MyAccessResponse>("shares.mine", undefined, undefined, "interactive-read", signal),
      /** C10 M3 (PG-053): an ownership transfer is an Authority command with reason and step-up; the result says whether it is Effective yet. */
      /** C10 M2 (D8-4, PG-053): the records a member directly owns (administrators only). */
      memberOwnedRecords: (memberUserId: string, signal?: AbortSignal) => read<MemberOwnedRecordsResponse>("members.ownedRecords", { memberUserId }, undefined, "interactive-read", signal),
      /** C10 M2: one ownership transfer per owned record; per-record outcome. */
      reassignRecords: (memberUserId: string, request: ReassignRecordsRequest) => write<ReassignRecordsResponse>("POST", "members.reassignRecords", { memberUserId }, request, { idempotent: true, resourceKey: `member:${memberUserId}` }),
      /** C10 review F4: pass the same idempotency key to replay a pending transfer (the handler records its projection once, when Effective). */
      transferOwnership: (formRecordId: string, request: TransferOwnershipRequest, idempotencyKey?: string) =>
        write<CommandStatusResponse>("POST", "records.transferOwnership", { formRecordId }, request, { idempotent: true, resourceKey: `record:${formRecordId}`, ...(idempotencyKey ? { idempotencyKey } : {}) }),
      /** C10 M2: replaces the share's scope. If-Match carries the ETag "{recordShareId}:{shareRevision}" read from `get` (412 when stale). */
      update: (formRecordId: string, recordShareId: string, shareRevision: number | string, request: UpdateShareRequest) =>
        write<UpdateShareResponse>("PATCH", "shares.update", { formRecordId, recordShareId }, request, { idempotent: true, resourceKey: `record:${formRecordId}`, expectedRevision: `${recordShareId}:${shareRevision}` }),
      revoke: (formRecordId: string, recordShareId: string, useDevelopmentStepUp = false) =>
        write<CommandStatusResponse>("POST", "shares.revoke", { formRecordId, recordShareId }, {}, {
          idempotent: true,
          resourceKey: `record:${formRecordId}`,
          ...(useDevelopmentStepUp ? { query: { useDevelopmentStepUp: true } } : {}),
        }),
    },
    uploads: {
      begin: (request: BeginUploadRequest) => write<UploadSessionResponse>("POST", "uploads.begin", undefined, request, { idempotent: true, lane: "file-transfer" }),
      chunk: (uploadSessionId: string, chunkIndex: number, bytes: ArrayBuffer) =>
        coordinator.execute<UploadChunkReceiptResponse>({ lane: "file-transfer", method: "PUT", endpointKey: "uploads.chunk", pathParams: { uploadSessionId, chunkIndex: String(chunkIndex) }, binaryPayload: bytes, idempotencyKey: `${uploadSessionId}:${chunkIndex}`, timeoutMilliseconds: 300_000 }),
      get: (uploadSessionId: string, signal?: AbortSignal) => read<UploadSessionResponse>("uploads.get", { uploadSessionId }, undefined, "polling", signal),
      complete: (uploadSessionId: string) => write<UploadSessionResponse>("POST", "uploads.complete", { uploadSessionId }, undefined, { idempotent: true, lane: "file-transfer" }),
      /** Commits are serialized per record (resourceKey) because each one advances the record's content revision. */
      commit: (uploadSessionId: string, formRecordId: string) => write<UploadSessionResponse>("POST", "uploads.commit", { uploadSessionId }, undefined, { idempotent: true, lane: "interactive-write", resourceKey: `record:${formRecordId}` }),
      cancel: (uploadSessionId: string) => write<UploadSessionResponse>("DELETE", "uploads.cancel", { uploadSessionId }, undefined, { lane: "file-transfer" }),
      mine: (states?: string, signal?: AbortSignal) => read<UploadSessionResponse[]>("uploads.mine", undefined, states ? { states } : undefined, "polling", signal),
    },
    documents: {
      list: (formRecordId: string, signal?: AbortSignal) => read<DocumentSummaryResponse[]>("documents.list", { formRecordId }, undefined, "interactive-read", signal),
      get: (formRecordId: string, fileAttachmentId: string, signal?: AbortSignal) => read<DocumentDetailResponse>("documents.get", { formRecordId, fileAttachmentId }, undefined, "interactive-read", signal),
      remove: (formRecordId: string, fileAttachmentId: string, request: RemoveDocumentRequest) => write<FormRecordResponse>("POST", "documents.remove", { formRecordId, fileAttachmentId }, request, { idempotent: true, resourceKey: `record:${formRecordId}` }),
    },
    /** C8 M2: my own directory profile (non-security fields) and shared informational presence. */
    profile: {
      mine: (signal?: AbortSignal) => read<UserProfileResponse>("users.myProfile", undefined, undefined, "interactive-read", signal),
      updateMine: (request: UpdateUserProfileRequest) => write<UserProfileResponse>("PUT", "users.updateMyProfile", undefined, request, { resourceKey: "profile:me" }),
    },
    presence: {
      /** Not idempotent-keyed on purpose: a failed beat is never retried (the next beat or the expiry covers it). */
      heartbeat: (interactionAgeSeconds: number) => write<void>("POST", "presence.heartbeat", undefined, { interactionAgeSeconds }, { lane: "polling", responseKind: "none" }),
      query: (userIds: string[], signal?: AbortSignal) => read<PresenceQueryResponse>("presence.query", undefined, { userIds: userIds.join(",") }, "polling", signal),
    },
    organization: {
      branding: (signal?: AbortSignal) => read<BrandingResponse>("organization.branding", undefined, undefined, "interactive-read", signal),
      updateBranding: (request: UpdateBrandingRequest) => write<BrandingResponse>("PUT", "organization.updateBranding", undefined, request, { resourceKey: "branding" }),
      uploadLogo: (png: ArrayBuffer) => coordinator.execute<BrandingResponse>({ lane: "interactive-write", method: "POST", endpointKey: "organization.uploadLogo", binaryPayload: png, binaryContentType: "image/png", resourceKey: "branding" }),
      users: (signal?: AbortSignal) => read<DirectoryUserResponse[]>("users.list", undefined, undefined, "background-read", signal),
      searchApprovals: (signal?: AbortSignal) => read<FieldSearchAdministrationResponse>("organization.searchApprovals", undefined, undefined, "interactive-read", signal),
      securityAdministrators: (signal?: AbortSignal) => read<SecurityAdministratorsResponse>("organization.securityAdministrators", undefined, undefined, "interactive-read", signal),
      appointSecurityAdministrator: (request: AppointSecurityAdministratorRequest) => write<void>("POST", "organization.appointSecurityAdministrator", undefined, request, { lane: "security", resourceKey: "organization:security-administrators" }),
      removeSecurityAdministrator: (userId: string, request: RemoveSecurityAdministratorRequest) => write<void>("POST", "organization.removeSecurityAdministrator", { userId }, request, { lane: "security", resourceKey: "organization:security-administrators" }),
      securityAuditorReasonCodes: (signal?: AbortSignal) => read<SecurityAuditorReasonCodesResponse>("organization.securityAuditorReasonCodes", undefined, undefined, "interactive-read", signal),
      appointSecurityAuditor: (request: AppointSecurityAuditorRequest) => write<void>("POST", "organization.appointSecurityAuditor", undefined, request, { lane: "security", resourceKey: "organization:security-auditors" }),
      removeSecurityAuditor: (userId: string, request: RemoveSecurityAuditorRequest) => write<void>("POST", "organization.removeSecurityAuditor", { userId }, request, { lane: "security", resourceKey: "organization:security-auditors" }),
      /** C8 owner recovery (C-105): the open case, if the caller may see it; case is null otherwise. */
      ownerRecoveryNotice: (signal?: AbortSignal) => read<OwnerRecoveryNoticeResponse>("organization.ownerRecoveryNotice", undefined, undefined, "interactive-read", signal),
      objectToOwnerRecovery: (caseId: string, request: OwnerRecoveryObjectionRequest) => write<OwnerRecoveryObjectionResponse>("POST", "organization.objectToOwnerRecovery", { caseId }, request, { lane: "security", resourceKey: "organization:owner-recovery" }),
      securityPolicy: (signal?: AbortSignal) => read<OrganizationSecurityPolicyResponse>("organization.securityPolicy", undefined, undefined, "interactive-read", signal),
      updateSecurityPolicy: (request: UpdateOrganizationSecurityPolicyRequest) => write<OrganizationSecurityPolicyResponse>("PUT", "organization.updateSecurityPolicy", undefined, request, { resourceKey: "security-policy" }),
      /** Phase C8 M1 (PG-041): independently computed parts; owners and Security Administrators only. */
      overview: (signal?: AbortSignal) => read<OrganizationOverviewResponse>("organization.overview", undefined, undefined, "interactive-read", signal),
      /** Phase C8 M1 (PG-050, read-only): projection rows labelled as such, Security Administrator badges from the Authority roster. */
      members: (query: OrganizationMemberQuery, signal?: AbortSignal) => read<OrganizationMemberDirectoryResponse>("organization.members", undefined, query, "interactive-read", signal),
      ownerProposals: (query: ApprovalQuery, signal?: AbortSignal) => read<OwnerProposalPageResponse>("organization.ownerProposals", undefined, query, "interactive-read", signal),
      membershipRequests: (query: ApprovalQuery, signal?: AbortSignal) => read<MembershipRequestPageResponse>("organization.membershipRequests", undefined, query, "interactive-read", signal),
      /** C8 M2 (AC-7.5): cancel, or edit by replacement, a pending membership request (Authority commands; one Idempotency-Key per user action). */
      cancelMembershipRequest: (requestId: string, request: CancelMembershipRequestRequest) => write<MemberCommandResponse>("POST", "organization.cancelMembershipRequest", { requestId }, request, { idempotent: true, lane: "security", resourceKey: "organization:members" }),
      replaceMembershipRequest: (requestId: string, request: ReplaceMembershipRequestRequest) => write<MemberCommandResponse>("POST", "organization.replaceMembershipRequest", { requestId }, request, { idempotent: true, lane: "security", resourceKey: "organization:members" }),
      memberProfile: (userId: string, signal?: AbortSignal) => read<UserProfileResponse>("organization.memberProfile", { userId }, undefined, "interactive-read", signal),
      updateMemberProfile: (userId: string, request: UpdateUserProfileRequest) => write<UserProfileResponse>("PUT", "organization.updateMemberProfile", { userId }, request, { resourceKey: `profile:${userId}` }),
      // C8 M2 (AC-7): every change is an Authority security command; one Idempotency-Key per user action.
      grantMemberRole: (userId: string, request: MemberRoleChangeRequest) => write<MemberCommandResponse>("POST", "organization.grantMemberRole", { userId }, request, { idempotent: true, lane: "security", resourceKey: "organization:members" }),
      revokeMemberRole: (userId: string, roleName: string, request: MemberCommandRequest) =>
        write<MemberCommandResponse>("POST", "organization.revokeMemberRole", { userId, roleName }, request, { idempotent: true, lane: "security", resourceKey: "organization:members" }),
      proposeOwnerChange: (userId: string, request: OwnerProposalRequest) => write<MemberCommandResponse>("POST", "organization.proposeOwnerChange", { userId }, request, { idempotent: true, lane: "security", resourceKey: "organization:members" }),
      approveOwnerProposal: (proposalId: string, request: MemberCommandRequest) =>
        write<MemberCommandResponse>("POST", "organization.approveOwnerProposal", { proposalId }, request, { idempotent: true, lane: "security", resourceKey: "organization:members" }),
      disableMember: (userId: string, request: MemberCommandRequest) => write<MemberCommandResponse>("POST", "organization.disableMember", { userId }, request, { idempotent: true, lane: "security", resourceKey: "organization:members" }),
      enableMember: (userId: string, request: MemberCommandRequest) => write<MemberCommandResponse>("POST", "organization.enableMember", { userId }, request, { idempotent: true, lane: "security", resourceKey: "organization:members" }),
      requestMembership: (request: CreateMembershipRequestRequest) => write<MemberCommandResponse>("POST", "organization.requestMembership", undefined, request, { idempotent: true, lane: "security", resourceKey: "organization:members" }),
    },
    teams: {
      /** Phase C8 M1 (PG-048): directory with member counts; owners and Security Administrators only. */
      directory: (signal?: AbortSignal) => read<TeamDirectoryResponse>("teams.directory", undefined, undefined, "interactive-read", signal),
      get: (teamId: string, signal?: AbortSignal) => read<TeamDetailResponse>("teams.get", { teamId }, undefined, "interactive-read", signal),
      /** RegisterTeam Authority command (owner); the idempotency key fixes the team id, so a retry completes the same team. */
      create: (request: CreateTeamRequest) => write<TeamResponse>("POST", "teams.create", undefined, request, { idempotent: true, resourceKey: "teams" }),
      /** Display-only rename of the projection label (D8-8); owner only, audited, concurrency on expectedModifiedOn. */
      rename: (teamId: string, request: RenameTeamRequest) => write<TeamDetailResponse>("PATCH", "teams.rename", { teamId }, request, { resourceKey: `team:${teamId}` }),
      /** C8-M1-01: pass the original idempotencyKey to re-check the same Authority command (never a new one) after a Pending outcome. */
      addMember: (teamId: string, request: TeamMemberRequest, idempotencyKey?: string) => write<TeamMembershipChangeResponse>("POST", "teams.addMember", { teamId }, request, { idempotent: true, ...(idempotencyKey ? { idempotencyKey } : {}), resourceKey: `team:${teamId}` }),
      removeMember: (teamId: string, userId: string, idempotencyKey?: string) => write<TeamMembershipChangeResponse>("POST", "teams.removeMember", { teamId, userId }, {}, { idempotent: true, ...(idempotencyKey ? { idempotencyKey } : {}), resourceKey: `team:${teamId}` }),
      membershipOperation: (teamId: string, operationId: string, signal?: AbortSignal) => read<TeamMembershipOperationResponse>("teams.membershipOperation", { teamId, operationId }, undefined, "interactive-read", signal),
      membershipOperationByKey: (teamId: string, clientKey: string, signal?: AbortSignal) => read<TeamMembershipOperationResponse>("teams.membershipOperationByKey", { teamId, clientKey }, undefined, "interactive-read", signal),
      recheckMembershipOperation: (teamId: string, operationId: string) => write<TeamMembershipOperationResponse>("POST", "teams.recheckMembershipOperation", { teamId, operationId }, {}, { resourceKey: `team:${teamId}` }),
      reviewMembershipOperation: (teamId: string, operationId: string) => write<TeamMembershipOperationResponse>("POST", "teams.reviewMembershipOperation", { teamId, operationId }, {}, { resourceKey: `team:${teamId}` }),
    },
    preferences: {
      get: (signal?: AbortSignal) => read<UiPreferenceResponse>("preferences.get", undefined, undefined, "interactive-read", signal),
      update: (request: UpdateUiPreferenceRequest) => write<UiPreferenceResponse>("PUT", "preferences.update", undefined, request, { resourceKey: "preferences" }),
    },
    /** C8 M3: regional defaults, personal regional preferences (If-Match carries the revision read) and the typed catalog. */
    regional: {
      settings: (signal?: AbortSignal) => read<OrganizationSettingsResponse>("organization.settings", undefined, undefined, "interactive-read", signal),
      updateSettings: (request: UpdateOrganizationSettingsRequest, expectedRevision: number) => write<OrganizationSettingsResponse>("PUT", "organization.updateSettings", undefined, request, { resourceKey: "organization-settings", expectedRevision: String(expectedRevision) }),
      preferences: (signal?: AbortSignal) => read<UserPreferencesResponse>("regional.get", undefined, undefined, "interactive-read", signal),
      updatePreferences: (request: UpdateUserPreferencesRequest, expectedRevision: number) => write<UserPreferencesResponse>("PUT", "regional.update", undefined, request, { resourceKey: "regional-preferences", expectedRevision: String(expectedRevision) }),
      countries: (signal?: AbortSignal) => read<CountryCatalogResponse>("catalog.countries", undefined, undefined, "background-read", signal),
      timeZones: (organizationContext: boolean, signal?: AbortSignal) => read<TimeZoneCatalogResponse>("catalog.timeZones", undefined, { organizationContext }, "interactive-read", signal),
      languages: (signal?: AbortSignal) => read<LanguageCatalogResponse>("catalog.languages", undefined, undefined, "background-read", signal),
      dateFormats: (signal?: AbortSignal) => read<DateFormatCatalogResponse>("catalog.dateFormats", undefined, undefined, "background-read", signal),
      numberFormats: (signal?: AbortSignal) => read<NumberFormatCatalogResponse>("catalog.numberFormats", undefined, undefined, "background-read", signal),
    },
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
