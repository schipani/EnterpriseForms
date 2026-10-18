import type { ApiClient, UploadSessionResponse } from "@enterpriseforms/api-client";
import { OperationError } from "@enterpriseforms/work-queue";
import { describe, expect, it } from "vitest";
import { UploadManager } from "./uploadManager";

function fakeApi(script: { failChunk?: number; verdict?: "Clean" | "Infected"; conflictOnce?: boolean }) {
  const calls: string[] = [];
  const state = { received: new Set<number>(), state: "Open" as string, polls: 0, conflicted: false };
  const session = (id: string, size: number): UploadSessionResponse => ({
    uploadSessionId: id,
    formRecordId: "rec",
    fieldDefinitionId: "fld",
    originalFileName: "a.bin",
    declaredSizeBytes: size,
    chunkSizeBytes: 4,
    expectedChunkCount: Math.ceil(size / 4),
    receivedChunkIndexes: [...state.received],
    receivedBytes: state.received.size * 4,
    sessionState: state.state,
    failureReasonCode: state.state === "Rejected" ? "MALWARE_DETECTED" : null,
    processingStage: state.state === "Processing" ? "ScanPending" : state.state,
    sessionExpiresOn: "2030-01-01T00:00:00Z",
    fileAttachmentId: state.state === "Committed" ? "att" : null,
    versionNumber: state.state === "Committed" ? 1 : null,
    scanVerdict: state.state === "Rejected" ? "Infected" : state.state === "Committed" ? "Clean" : null,
  });
  const api = {
    uploads: {
      begin: async () => {
        calls.push("begin");
        return { result: session("s1", 10), status: 201 };
      },
      chunk: async (_id: string, index: number) => {
        calls.push(`chunk${index}`);
        if (script.failChunk === index && !state.received.has(99)) {
          state.received.add(99);
          throw new Error("network");
        }
        state.received.add(index);
        return { result: { uploadSessionId: "s1", chunkIndex: index, receivedBytes: [...state.received].filter((i) => i !== 99).length * 4, receivedChunkIndexes: [...state.received].filter((i) => i !== 99), complete: false }, status: 200 };
      },
      complete: async () => {
        calls.push("complete");
        state.state = "Processing";
        return { result: session("s1", 10), status: 200 };
      },
      get: async () => {
        calls.push("get");
        state.polls++;
        if (state.state === "Processing" && state.polls >= 2) {
          state.state = script.verdict === "Infected" ? "Rejected" : "Processed";
        }
        return { result: { ...session("s1", 10), receivedChunkIndexes: [...state.received].filter((i) => i !== 99) }, status: 200 };
      },
      commit: async () => {
        calls.push("commit");
        if (script.conflictOnce && !state.conflicted) {
          state.conflicted = true;
          throw new OperationError({ type: "failed", operationId: "x", status: 409, errorCode: "CONTENT_REVISION_CONFLICT", retryable: false });
        }
        state.state = "Committed";
        return { result: session("s1", 10), status: 200 };
      },
      cancel: async () => ({ result: session("s1", 10), status: 200 }),
      mine: async () => ({ result: [], status: 200 }),
    },
  } as unknown as ApiClient;
  return { api, calls, state };
}

async function waitFor(predicate: () => boolean, timeoutMs = 3000): Promise<void> {
  const started = Date.now();
  while (!predicate()) {
    if (Date.now() - started > timeoutMs) {
      throw new Error("timeout");
    }
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

describe("UploadManager (ADR-PB-007 transfer ≠ commit)", () => {
  it("chunks, completes, polls the scan, commits in the user session and reports Ready", async () => {
    const { api, calls } = fakeApi({});
    const manager = new UploadManager(api, { pollIntervalMs: 5 });
    const [id] = manager.enqueue([new File([new Uint8Array(10)], "a.bin")], { formRecordId: "rec", fieldDefinitionId: "fld" });
    await waitFor(() => manager.snapshot()[0]?.phase === "Ready");
    expect(calls.slice(0, 5)).toEqual(["begin", "chunk0", "chunk1", "chunk2", "complete"]);
    expect(calls).toContain("commit");
    expect(manager.snapshot()[0]).toMatchObject({ clientUploadId: id, fileAttachmentId: "att", scanVerdict: "Clean", transferredBytes: 10 });
  });

  it("marks an infected file Rejected with the server's reason and never reports Ready", async () => {
    const { api, calls } = fakeApi({ verdict: "Infected" });
    const manager = new UploadManager(api, { pollIntervalMs: 5 });
    manager.enqueue([new File([new Uint8Array(10)], "a.bin")], { formRecordId: "rec", fieldDefinitionId: "fld" });
    await waitFor(() => manager.snapshot()[0]?.phase === "Rejected");
    expect(manager.snapshot()[0]).toMatchObject({ errorCode: "MALWARE_DETECTED" });
    expect(calls).not.toContain("commit");
  });

  it("resumes after a failed chunk by re-reading received indexes from the server", async () => {
    const { api, calls } = fakeApi({ failChunk: 1 });
    const manager = new UploadManager(api, { pollIntervalMs: 5 });
    const [id] = manager.enqueue([new File([new Uint8Array(10)], "a.bin")], { formRecordId: "rec", fieldDefinitionId: "fld" });
    await waitFor(() => manager.snapshot()[0]?.phase === "Failed");
    manager.retry(id!);
    await waitFor(() => manager.snapshot()[0]?.phase === "Ready");
    expect(calls.filter((c) => c === "chunk0")).toHaveLength(1); // chunk 0 was not resent
    expect(calls.filter((c) => c === "begin")).toHaveLength(1);
  });

  it("retries the user-session commit after a content revision conflict", async () => {
    const { api, calls } = fakeApi({ conflictOnce: true });
    const manager = new UploadManager(api, { pollIntervalMs: 5 });
    manager.enqueue([new File([new Uint8Array(10)], "a.bin")], { formRecordId: "rec", fieldDefinitionId: "fld" });
    await waitFor(() => manager.snapshot()[0]?.phase === "Ready");
    expect(calls.filter((c) => c === "commit")).toHaveLength(2);
  });

  it("limits concurrency to two active transfers per user", async () => {
    const { api } = fakeApi({});
    const manager = new UploadManager(api, { pollIntervalMs: 5, maxConcurrentUploads: 2 });
    manager.enqueue([new File([new Uint8Array(10)], "a.bin"), new File([new Uint8Array(10)], "a.bin"), new File([new Uint8Array(10)], "a.bin")], { formRecordId: "rec", fieldDefinitionId: "fld" });
    const queued = manager.snapshot().filter((item) => item.phase === "Queued").length;
    expect(queued).toBe(1);
    await waitFor(() => manager.snapshot().every((item) => item.phase === "Ready"));
  });
});
