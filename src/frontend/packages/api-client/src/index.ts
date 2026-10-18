/**
 * Generated contract types for the business API plus a typed client whose only transport is the app-shell request
 * coordinator (packages/work-queue, ADR-PB-006/007). No raw fetch lives here.
 */
import type { IRequestCoordinator, OperationLane } from "@enterpriseforms/work-queue";
import type { components } from "./generated/enterprise-forms-v1";

export type { components as EnterpriseFormsV1Components, paths as EnterpriseFormsV1Paths } from "./generated/enterprise-forms-v1";

export const API_V1_PREFIX = "/api/v1" as const;

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
export type UpdateFieldOptionsRequest = S["UpdateFieldOptionsRequest"];
export type SaveDraftRequest = S["SaveDraftRequest"];
export type PublishFormRequest = S["PublishFormRequest"];
export type RestoreVersionRequest = S["RestoreVersionRequest"];
export type CreateRelationshipDefinitionRequest = S["CreateRelationshipDefinitionRequest"];
export type RecordListResponse = S["RecordListResponse"];
export type RecordListItemResponse = S["RecordListItemResponse"];
export type RecordProjectionResponse = S["RecordProjectionResponse"];
export type FormRecordResponse = S["FormRecordResponse"];
export type FieldValueResponse = S["FieldValueResponse"];
export type CreateFormRecordRequest = S["CreateFormRecordRequest"];
export type UpdateFormRecordRequest = S["UpdateFormRecordRequest"];
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

export type ApiResult<T> = { result: T; etag?: string; status: number };

function idempotencyKey(): string {
  return typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

/** Typed operations over the coordinator. Every call names a registered endpoint key and a lane; nothing else can reach the network. */
export function createApiClient(coordinator: IRequestCoordinator) {
  const read = <T>(endpointKey: string, pathParams?: Record<string, string>, query?: Record<string, string | number | boolean | undefined>, lane: OperationLane = "interactive-read", signal?: AbortSignal) =>
    coordinator.execute<T>({ lane, method: "GET", endpointKey, ...(pathParams ? { pathParams } : {}), ...(query ? { query } : {}) }, signal);
  const write = <T>(method: "POST" | "PUT" | "PATCH" | "DELETE", endpointKey: string, pathParams: Record<string, string> | undefined, payload: unknown, options: { idempotent?: boolean; resourceKey?: string; expectedRevision?: string; lane?: OperationLane; responseKind?: "json" | "none" } = {}) =>
    coordinator.execute<T>({
      lane: options.lane ?? "interactive-write",
      method,
      endpointKey,
      ...(pathParams ? { pathParams } : {}),
      ...(payload !== undefined ? { payload } : {}),
      ...(options.idempotent ? { idempotencyKey: idempotencyKey() } : {}),
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
      updateOptions: (formDefinitionId: string, fieldDefinitionId: string, request: UpdateFieldOptionsRequest) => write<FieldDefinitionResponse>("PUT", "forms.updateOptions", { formDefinitionId, fieldDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      saveDraft: (formDefinitionId: string, request: SaveDraftRequest) => write<FormDraftResponse>("PUT", "forms.saveDraft", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      publish: (formDefinitionId: string, request: PublishFormRequest) => write<FormVersionResponse>("POST", "forms.publish", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      restoreDraft: (formDefinitionId: string, request: RestoreVersionRequest) => write<FormDraftResponse>("POST", "forms.restoreDraft", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
      versions: (formDefinitionId: string, signal?: AbortSignal) => read<FormVersionResponse[]>("forms.versions", { formDefinitionId }, undefined, "interactive-read", signal),
      createRelationship: (formDefinitionId: string, request: CreateRelationshipDefinitionRequest) => write<RelationshipDefinitionResponse>("POST", "forms.createRelationship", { formDefinitionId }, request, { resourceKey: `form:${formDefinitionId}` }),
    },
    records: {
      list: (query: { formDefinitionId?: string; includeChildren?: boolean; pageSize?: number; continuationToken?: string }, signal?: AbortSignal) => read<RecordListResponse>("records.list", undefined, query, "interactive-read", signal),
      create: (formDefinitionId: string, request: CreateFormRecordRequest) => write<FormRecordResponse>("POST", "records.create", { formDefinitionId }, request, { idempotent: true }),
      projection: (formRecordId: string, signal?: AbortSignal) => read<RecordProjectionResponse>("records.projection", { formRecordId }, undefined, "interactive-read", signal),
      update: (formRecordId: string, request: UpdateFormRecordRequest) => write<FormRecordResponse>("PATCH", "records.update", { formRecordId }, request, { idempotent: true, resourceKey: `record:${formRecordId}`, expectedRevision: String(request.expectedContentRevision) }),
    },
    related: {
      list: (formRecordId: string, formRelationshipDefinitionId: string, signal?: AbortSignal) => read<ChildRecordListResponse>("related.list", { formRecordId, formRelationshipDefinitionId }, undefined, "interactive-read", signal),
      create: (formRecordId: string, request: CreateChildRecordRequest) => write<FormRecordResponse>("POST", "related.create", { formRecordId }, request, { idempotent: true, resourceKey: `record:${formRecordId}` }),
      unlink: (formRecordId: string, childFormRecordId: string, request: UnlinkChildRecordRequest) => write<CommandStatusResponse>("POST", "related.unlink", { formRecordId, childFormRecordId }, request, { idempotent: true, resourceKey: `record:${formRecordId}` }),
    },
    shares: {
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
      uploadLogo: (png: ArrayBuffer) => coordinator.execute<BrandingResponse>({ lane: "interactive-write", method: "POST", endpointKey: "organization.uploadLogo", binaryPayload: png, resourceKey: "branding" }),
      users: (signal?: AbortSignal) => read<DirectoryUserResponse[]>("users.list", undefined, undefined, "background-read", signal),
    },
    preferences: {
      get: (signal?: AbortSignal) => read<UiPreferenceResponse>("preferences.get", undefined, undefined, "interactive-read", signal),
      update: (request: UpdateUiPreferenceRequest) => write<UiPreferenceResponse>("PUT", "preferences.update", undefined, request, { resourceKey: "preferences" }),
    },
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
