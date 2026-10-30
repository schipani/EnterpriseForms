/**
 * Generated contract types for the business API plus a typed client whose only transport is the app-shell request
 * coordinator (packages/work-queue, ADR-PB-006/007). No raw fetch lives here.
 */
import type { IRequestCoordinator, OperationLane } from "@enterpriseforms/work-queue";
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
export type DevelopmentPersonaResponse = S["DevelopmentPersonaResponse"];
export type DashboardResponse = S["DashboardResponse"];
export type DashboardCountResponse = S["DashboardCountResponse"];
export type AuditEventResponse = S["AuditEventResponse"];
export type RecordActivityResponse = S["RecordActivityResponse"];
export type FormDefinitionSummaryResponse = S["FormDefinitionSummaryResponse"];
export type FormDefinitionDetailResponse = S["FormDefinitionDetailResponse"];
export type FieldDefinitionResponse = S["FieldDefinitionResponse"];
export type FieldOptionResponse = S["FieldOptionResponse"];
export type FormVersionResponse = S["FormVersionResponse"];
export type FormDraftResponse = S["FormDraftResponse"];
export type RelationshipDefinitionResponse = S["RelationshipDefinitionResponse"];
export type CreateFormDefinitionV2Request = S["CreateFormDefinitionV2Request"];
export type AddFieldDefinitionRequest = S["AddFieldDefinitionRequest"];
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
export type SecurityAdministratorResponse = S["SecurityAdministratorResponse"];
export type AppointSecurityAdministratorRequest = S["AppointSecurityAdministratorRequest"];
export type RemoveSecurityAdministratorRequest = S["RemoveSecurityAdministratorRequest"];
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
export type RecordShareSummaryResponse = S["RecordShareSummaryResponse"];

export type ApiResult<T> = { result: T; etag?: string; status: number };

function idempotencyKey(): string {
  return typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

/** Typed operations over the coordinator. Every call names a registered endpoint key and a lane; nothing else can reach the network. */
export function createApiClient(coordinator: IRequestCoordinator) {
  const read = <T>(endpointKey: string, pathParams?: Record<string, string>, query?: Record<string, string | number | boolean | undefined>, lane: OperationLane = "interactive-read", signal?: AbortSignal) =>
    coordinator.execute<T>({ lane, method: "GET", endpointKey, ...(pathParams ? { pathParams } : {}), ...(query ? { query } : {}) }, signal);
  const write = <T>(method: "POST" | "PUT" | "PATCH" | "DELETE", endpointKey: string, pathParams: Record<string, string> | undefined, payload: unknown, options: { idempotent?: boolean; idempotencyKey?: string; resourceKey?: string; expectedRevision?: string; lane?: OperationLane; responseKind?: "json" | "none" } = {}) =>
    coordinator.execute<T>({
      lane: options.lane ?? "interactive-write",
      method,
      endpointKey,
      ...(pathParams ? { pathParams } : {}),
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
    },
    audit: {
      mine: (limit = 25, signal?: AbortSignal) => read<AuditEventResponse[]>("audit.mine", undefined, { limit }, "background-read", signal),
      record: (formRecordId: string, signal?: AbortSignal) => read<RecordActivityResponse>("audit.record", { formRecordId }, undefined, "interactive-read", signal),
    },
    forms: {
      list: (signal?: AbortSignal) => read<FormDefinitionSummaryResponse[]>("forms.list", undefined, undefined, "interactive-read", signal),
      get: (formDefinitionId: string, signal?: AbortSignal) => read<FormDefinitionDetailResponse>("forms.get", { formDefinitionId }, undefined, "interactive-read", signal),
      create: (request: CreateFormDefinitionV2Request) => write<FormDefinitionDetailResponse>("POST", "forms.create", undefined, request, { idempotent: true }),
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
      projection: (formRecordId: string, signal?: AbortSignal) => read<RecordProjectionResponse>("records.projection", { formRecordId }, undefined, "interactive-read", signal),
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
      revoke: (formRecordId: string, recordShareId: string) => write<CommandStatusResponse>("POST", "shares.revoke", { formRecordId, recordShareId }, {}, { idempotent: true, resourceKey: `record:${formRecordId}` }),
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
    organization: {
      branding: (signal?: AbortSignal) => read<BrandingResponse>("organization.branding", undefined, undefined, "interactive-read", signal),
      updateBranding: (request: UpdateBrandingRequest) => write<BrandingResponse>("PUT", "organization.updateBranding", undefined, request, { resourceKey: "branding" }),
      uploadLogo: (png: ArrayBuffer) => coordinator.execute<BrandingResponse>({ lane: "interactive-write", method: "POST", endpointKey: "organization.uploadLogo", binaryPayload: png, binaryContentType: "image/png", resourceKey: "branding" }),
      users: (signal?: AbortSignal) => read<DirectoryUserResponse[]>("users.list", undefined, undefined, "background-read", signal),
      searchApprovals: (signal?: AbortSignal) => read<FieldSearchAdministrationResponse>("organization.searchApprovals", undefined, undefined, "interactive-read", signal),
      securityAdministrators: (signal?: AbortSignal) => read<SecurityAdministratorsResponse>("organization.securityAdministrators", undefined, undefined, "interactive-read", signal),
      appointSecurityAdministrator: (request: AppointSecurityAdministratorRequest) => write<void>("POST", "organization.appointSecurityAdministrator", undefined, request, { lane: "security", resourceKey: "organization:security-administrators" }),
      removeSecurityAdministrator: (userId: string, request: RemoveSecurityAdministratorRequest) => write<void>("POST", "organization.removeSecurityAdministrator", { userId }, request, { lane: "security", resourceKey: "organization:security-administrators" }),
      securityPolicy: (signal?: AbortSignal) => read<OrganizationSecurityPolicyResponse>("organization.securityPolicy", undefined, undefined, "interactive-read", signal),
      updateSecurityPolicy: (request: UpdateOrganizationSecurityPolicyRequest) => write<OrganizationSecurityPolicyResponse>("PUT", "organization.updateSecurityPolicy", undefined, request, { resourceKey: "security-policy" }),
      /** Phase C8 M1 (PG-041): independently computed parts; owners and Security Administrators only. */
      overview: (signal?: AbortSignal) => read<OrganizationOverviewResponse>("organization.overview", undefined, undefined, "interactive-read", signal),
      /** Phase C8 M1 (PG-050, read-only): projection rows labelled as such, Security Administrator badges from the Authority roster. */
      members: (signal?: AbortSignal) => read<OrganizationMemberDirectoryResponse>("organization.members", undefined, undefined, "interactive-read", signal),
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
      addMember: (teamId: string, request: TeamMemberRequest, idempotencyKey?: string) => write<CommandStatusResponse>("POST", "teams.addMember", { teamId }, request, { idempotent: true, ...(idempotencyKey ? { idempotencyKey } : {}), resourceKey: `team:${teamId}` }),
      removeMember: (teamId: string, userId: string, idempotencyKey?: string) => write<CommandStatusResponse>("POST", "teams.removeMember", { teamId, userId }, {}, { idempotent: true, ...(idempotencyKey ? { idempotencyKey } : {}), resourceKey: `team:${teamId}` }),
    },
    preferences: {
      get: (signal?: AbortSignal) => read<UiPreferenceResponse>("preferences.get", undefined, undefined, "interactive-read", signal),
      update: (request: UpdateUiPreferenceRequest) => write<UiPreferenceResponse>("PUT", "preferences.update", undefined, request, { resourceKey: "preferences" }),
    },
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
