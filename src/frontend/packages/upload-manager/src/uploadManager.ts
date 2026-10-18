import type { ApiClient, UploadSessionResponse } from "@enterpriseforms/api-client";
import { OperationError } from "@enterpriseforms/work-queue";

export type UploadPhase = "Queued" | "Starting" | "Transferring" | "Paused" | "Finalizing" | "Processing" | "Committing" | "Ready" | "Rejected" | "Failed" | "Cancelled";

export interface UploadItem {
  /** Client identity; stable across session resumption. */
  clientUploadId: string;
  uploadSessionId: string | null;
  formRecordId: string;
  fieldDefinitionId: string;
  /** Set when this upload becomes a new immutable version of an existing document (B-05 replace). */
  replacesFileAttachmentId: string | null;
  fileName: string;
  sizeBytes: number;
  contentType: string;
  phase: UploadPhase;
  transferredBytes: number;
  chunkCount: number;
  receivedChunks: number;
  /** Server-side processing stage once transfer finished (Queued, Leased, ScanPending, Processed...). */
  processingStage: string | null;
  scanVerdict: string | null;
  fileAttachmentId: string | null;
  versionNumber: number | null;
  errorCode: string | null;
  errorDetail: string | null;
  startedAt: number;
  updatedAt: number;
  /** True when the File handle is held in memory (resumable in this tab); false for server sessions needing re-selection. */
  hasBytes: boolean;
}

export interface UploadManagerOptions {
  /** PB-09: two simultaneous uploads per user. */
  maxConcurrentUploads?: number;
  pollIntervalMs?: number;
  now?: () => number;
}

type Listener = (items: readonly UploadItem[]) => void;

/** Reads a chunk; falls back to FileReader where Blob.arrayBuffer is unavailable (older engines, jsdom). */
function readBytes(blob: Blob): Promise<ArrayBuffer> {
  if (typeof blob.arrayBuffer === "function") {
    return blob.arrayBuffer();
  }
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(reader.error ?? new Error("chunk read failed"));
    reader.onload = () => resolve(reader.result as ArrayBuffer);
    reader.readAsArrayBuffer(blob);
  });
}

/** int64 wire values may arrive as strings; everything internal is a number. */
function n(value: number | string | null | undefined): number {
  return value === null || value === undefined ? 0 : Number(value);
}

/**
 * Durable-for-the-tab upload orchestration. Navigation inside the SPA never stops an upload because this object is
 * owned by the app shell, not by a route. The client transfer finishing is NOT success: an item is Ready only when the
 * server reports the verified version committed (B-05). Scan/encryption happen server-side; the manager only polls.
 */
export class UploadManager {
  private readonly items = new Map<string, UploadItem>();
  private readonly files = new Map<string, File>();
  private readonly controllers = new Map<string, AbortController>();
  private readonly listeners = new Set<Listener>();
  private readonly maxConcurrent: number;
  private readonly pollIntervalMs: number;
  private readonly now: () => number;
  private api: ApiClient;

  constructor(api: ApiClient, options: UploadManagerOptions = {}) {
    this.api = api;
    this.maxConcurrent = options.maxConcurrentUploads ?? 2;
    this.pollIntervalMs = options.pollIntervalMs ?? 1500;
    this.now = options.now ?? (() => Date.now());
  }

  /** Tenant switch / logout: abort everything and forget file handles (ADR-PB-008). */
  reset(api?: ApiClient): void {
    for (const controller of this.controllers.values()) {
      controller.abort();
    }
    this.controllers.clear();
    this.items.clear();
    this.files.clear();
    if (api) {
      this.api = api;
    }
    this.emit();
  }

  subscribe(listener: Listener): () => void {
    this.listeners.add(listener);
    listener(this.snapshot());
    return () => this.listeners.delete(listener);
  }

  snapshot(): readonly UploadItem[] {
    return [...this.items.values()].sort((a, b) => a.startedAt - b.startedAt);
  }

  forRecord(formRecordId: string): readonly UploadItem[] {
    return this.snapshot().filter((item) => item.formRecordId === formRecordId);
  }

  enqueue(files: readonly File[], target: { formRecordId: string; fieldDefinitionId: string; replacesFileAttachmentId?: string | null }): string[] {
    const ids: string[] = [];
    for (const file of files) {
      const clientUploadId = crypto.randomUUID();
      this.files.set(clientUploadId, file);
      this.items.set(clientUploadId, {
        clientUploadId,
        uploadSessionId: null,
        formRecordId: target.formRecordId,
        fieldDefinitionId: target.fieldDefinitionId,
        replacesFileAttachmentId: target.replacesFileAttachmentId ?? null,
        fileName: file.name,
        sizeBytes: file.size,
        contentType: file.type || "application/octet-stream",
        phase: "Queued",
        transferredBytes: 0,
        chunkCount: 0,
        receivedChunks: 0,
        processingStage: null,
        scanVerdict: null,
        fileAttachmentId: null,
        versionNumber: null,
        errorCode: null,
        errorDetail: null,
        startedAt: this.now(),
        updatedAt: this.now(),
        hasBytes: true,
      });
      ids.push(clientUploadId);
    }
    this.emit();
    void this.pump();
    return ids;
  }

  /** Attach a re-selected file to a server session that survived a tab closure (name and size must match). */
  resume(session: UploadSessionResponse, file: File): string | undefined {
    if (file.name !== session.originalFileName || file.size !== n(session.declaredSizeBytes)) {
      return undefined;
    }
    const clientUploadId = crypto.randomUUID();
    this.files.set(clientUploadId, file);
    this.items.set(clientUploadId, this.fromSession(clientUploadId, session, true));
    this.emit();
    void this.pump();
    return clientUploadId;
  }

  /** Stop sending chunks but keep the file and the server session; `retry` resumes from the chunks the server holds. */
  pause(clientUploadId: string): void {
    const item = this.items.get(clientUploadId);
    if (!item || !(item.phase === "Queued" || item.phase === "Starting" || item.phase === "Transferring")) {
      return;
    }
    this.controllers.get(clientUploadId)?.abort();
    this.patch(clientUploadId, { phase: "Paused" });
    void this.pump();
  }

  cancel(clientUploadId: string): void {
    const item = this.items.get(clientUploadId);
    if (!item) {
      return;
    }
    this.controllers.get(clientUploadId)?.abort();
    this.patch(clientUploadId, { phase: "Cancelled" });
    this.files.delete(clientUploadId);
    if (item.uploadSessionId && item.phase !== "Ready") {
      void this.api.uploads.cancel(item.uploadSessionId).catch(() => undefined);
    }
    void this.pump();
  }

  /** Re-queue a Failed or Paused upload; the run re-reads the server session so received chunks are never resent. */
  retry(clientUploadId: string): void {
    const item = this.items.get(clientUploadId);
    if (!item || !this.files.has(clientUploadId) || !(item.phase === "Failed" || item.phase === "Paused")) {
      return;
    }
    this.patch(clientUploadId, { phase: "Queued", errorCode: null, errorDetail: null });
    void this.pump();
  }

  dismiss(clientUploadId: string): void {
    const item = this.items.get(clientUploadId);
    if (item && (item.phase === "Ready" || item.phase === "Rejected" || item.phase === "Failed" || item.phase === "Cancelled")) {
      this.items.delete(clientUploadId);
      this.files.delete(clientUploadId);
      this.emit();
    }
  }

  private active(): number {
    return [...this.items.values()].filter((item) => item.phase === "Starting" || item.phase === "Transferring" || item.phase === "Finalizing").length;
  }

  private async pump(): Promise<void> {
    while (this.active() < this.maxConcurrent) {
      const next = this.snapshot().find((item) => item.phase === "Queued");
      if (!next) {
        return;
      }
      this.patch(next.clientUploadId, { phase: "Starting" });
      void this.run(next.clientUploadId);
    }
  }

  private async run(clientUploadId: string): Promise<void> {
    const controller = new AbortController();
    this.controllers.set(clientUploadId, controller);
    try {
      const file = this.files.get(clientUploadId);
      const item = this.items.get(clientUploadId);
      if (!file || !item) {
        return;
      }
      let session: UploadSessionResponse;
      if (item.uploadSessionId) {
        session = (await this.api.uploads.get(item.uploadSessionId)).result;
      } else {
        session = (await this.api.uploads.begin({ formRecordId: item.formRecordId, fieldDefinitionId: item.fieldDefinitionId, originalFileName: file.name, contentType: item.contentType, sizeBytes: file.size, replacesFileAttachmentId: item.replacesFileAttachmentId })).result;
      }
      this.patch(clientUploadId, { uploadSessionId: session.uploadSessionId, chunkCount: n(session.expectedChunkCount), receivedChunks: session.receivedChunkIndexes.length, transferredBytes: n(session.receivedBytes), phase: "Transferring" });

      if (session.sessionState === "Open") {
        const received = new Set(session.receivedChunkIndexes);
        for (let index = 0; index < n(session.expectedChunkCount); index++) {
          if (controller.signal.aborted) {
            return;
          }
          if (received.has(index)) {
            continue;
          }
          const start = index * n(session.chunkSizeBytes);
          const end = Math.min(file.size, start + n(session.chunkSizeBytes));
          const bytes = await readBytes(file.slice(start, end));
          const receipt = (await this.api.uploads.chunk(session.uploadSessionId, index, bytes)).result;
          this.patch(clientUploadId, { transferredBytes: n(receipt.receivedBytes), receivedChunks: receipt.receivedChunkIndexes.length });
        }
        this.patch(clientUploadId, { phase: "Finalizing" });
        session = (await this.api.uploads.complete(session.uploadSessionId)).result;
      }
      await this.trackProcessing(clientUploadId, session, controller.signal);
    } catch (error) {
      if (controller.signal.aborted) {
        return;
      }
      const code = error instanceof OperationError ? error.errorCode : "UPLOAD_FAILED";
      const detail = error instanceof OperationError ? (error.problem?.detail ?? error.message) : error instanceof Error ? error.message : String(error);
      this.patch(clientUploadId, { phase: "Failed", errorCode: code, errorDetail: detail });
    } finally {
      this.controllers.delete(clientUploadId);
      void this.pump();
    }
  }

  private async trackProcessing(clientUploadId: string, initial: UploadSessionResponse, signal: AbortSignal): Promise<void> {
    let session = initial;
    for (;;) {
      this.applySession(clientUploadId, session);
      if (signal.aborted) {
        return;
      }
      switch (session.sessionState) {
        case "Processed": {
          this.patch(clientUploadId, { phase: "Committing" });
          session = await this.commitWithRetry(session);
          continue;
        }
        case "Committed":
          this.patch(clientUploadId, { phase: "Ready", transferredBytes: n(session.declaredSizeBytes) });
          this.files.delete(clientUploadId);
          return;
        case "Rejected":
          this.patch(clientUploadId, { phase: "Rejected", errorCode: session.failureReasonCode ?? "DOCUMENT_REJECTED", errorDetail: session.scanVerdict ? `Scanner verdict: ${session.scanVerdict}` : null });
          this.files.delete(clientUploadId);
          return;
        case "Failed":
        case "Expired":
        case "Cancelled":
          this.patch(clientUploadId, { phase: "Failed", errorCode: session.failureReasonCode ?? session.sessionState });
          return;
        default:
          await new Promise((resolve) => setTimeout(resolve, this.pollIntervalMs));
          session = (await this.api.uploads.get(session.uploadSessionId)).result;
      }
    }
  }

  /** Two users (or two tabs) committing to one record race on the content revision; the loser retries against the new head. */
  private async commitWithRetry(session: UploadSessionResponse): Promise<UploadSessionResponse> {
    for (let attempt = 0; ; attempt++) {
      try {
        return (await this.api.uploads.commit(session.uploadSessionId, session.formRecordId)).result;
      } catch (error) {
        if (error instanceof OperationError && error.errorCode === "CONTENT_REVISION_CONFLICT" && attempt < 4) {
          await new Promise((resolve) => setTimeout(resolve, 300 * (attempt + 1)));
          continue;
        }
        throw error;
      }
    }
  }

  private applySession(clientUploadId: string, session: UploadSessionResponse): void {
    const phase: UploadPhase = session.sessionState === "Completed" || session.sessionState === "Processing" ? "Processing" : (this.items.get(clientUploadId)?.phase ?? "Processing");
    this.patch(clientUploadId, {
      uploadSessionId: session.uploadSessionId,
      processingStage: session.processingStage,
      scanVerdict: session.scanVerdict,
      fileAttachmentId: session.fileAttachmentId,
      versionNumber: session.versionNumber === null ? null : n(session.versionNumber),
      receivedChunks: session.receivedChunkIndexes.length,
      transferredBytes: n(session.receivedBytes),
      phase,
    });
  }

  private fromSession(clientUploadId: string, session: UploadSessionResponse, hasBytes: boolean): UploadItem {
    return {
      clientUploadId,
      uploadSessionId: session.uploadSessionId,
      formRecordId: session.formRecordId,
      fieldDefinitionId: session.fieldDefinitionId,
      replacesFileAttachmentId: null,
      fileName: session.originalFileName,
      sizeBytes: n(session.declaredSizeBytes),
      contentType: "application/octet-stream",
      phase: "Queued",
      transferredBytes: n(session.receivedBytes),
      chunkCount: n(session.expectedChunkCount),
      receivedChunks: session.receivedChunkIndexes.length,
      processingStage: session.processingStage,
      scanVerdict: session.scanVerdict,
      fileAttachmentId: session.fileAttachmentId,
      versionNumber: session.versionNumber === null ? null : n(session.versionNumber),
      errorCode: null,
      errorDetail: null,
      startedAt: this.now(),
      updatedAt: this.now(),
      hasBytes,
    };
  }

  private patch(clientUploadId: string, changes: Partial<UploadItem>): void {
    const current = this.items.get(clientUploadId);
    if (!current) {
      return;
    }
    this.items.set(clientUploadId, { ...current, ...changes, updatedAt: this.now() });
    this.emit();
  }

  private emit(): void {
    const snapshot = this.snapshot();
    for (const listener of this.listeners) {
      listener(snapshot);
    }
  }
}
