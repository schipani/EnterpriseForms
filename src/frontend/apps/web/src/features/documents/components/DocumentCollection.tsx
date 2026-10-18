import type { DocumentSummaryResponse } from "@enterpriseforms/api-client";
import { DOWNLOAD_PATH, resolvePath, useApi, useRuntime } from "@enterpriseforms/app-runtime";
import type { LayoutUploadPolicy } from "@enterpriseforms/form-renderer";
import { Badge, Button, Card, DataTable, Dialog, LoadingBlock, ProgressBar, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState, type DragEvent } from "react";
import { phaseTone, useUploadItems } from "../../jobs";
import { ErrorPanel } from "../../support";

const formatBytes = (bytes: number | string) => {
  const value = Number(bytes);
  return value < 1024 ? `${value} B` : value < 1024 * 1024 ? `${(value / 1024).toFixed(1)} KB` : `${(value / (1024 * 1024)).toFixed(1)} MB`;
};

/**
 * PG-020 document collection on a record: concurrent uploads with progress (durable across navigation via the
 * app-shell upload manager), listing, authorized download (browser-native, server-decrypted), version history, remove.
 * A document is only shown as Ready when the server committed its verified version.
 */
export function DocumentCollection({ formRecordId, fieldDefinitionId, label, documents, policy, canEdit, contentRevision, onChanged }: { formRecordId: string; fieldDefinitionId: string; label: string; documents: readonly DocumentSummaryResponse[]; policy: LayoutUploadPolicy | undefined; canEdit: boolean; contentRevision: number; onChanged: () => void }) {
  const { uploads } = useRuntime();
  const api = useApi();
  const queryClient = useQueryClient();
  const items = useUploadItems().filter((item) => item.formRecordId === formRecordId && item.fieldDefinitionId === fieldDefinitionId);
  const input = useRef<HTMLInputElement>(null);
  const replaceInput = useRef<HTMLInputElement>(null);
  const [replaceTarget, setReplaceTarget] = useState<DocumentSummaryResponse | null>(null);
  const [dragging, setDragging] = useState(false);
  const [rejected, setRejected] = useState<string[]>([]);
  const [removeTarget, setRemoveTarget] = useState<DocumentSummaryResponse | null>(null);
  const [reason, setReason] = useState("");
  const [historyTarget, setHistoryTarget] = useState<DocumentSummaryResponse | null>(null);
  const maxFiles = policy?.maxFiles ?? 100;
  const maxBytes = policy?.maxFileSizeBytes ?? 500 * 1024 * 1024;
  const allowed = policy?.allowedContentTypes ?? [];
  const activeDocuments = documents.filter((document) => document.attachmentLifecycleState !== "Removed");

  // Re-read the projection whenever another upload becomes Ready (server-committed). The callback is kept in a ref
  // so a parent re-render does not retrigger the effect.
  const readyCount = items.filter((item) => item.phase === "Ready").length;
  const onChangedRef = useRef(onChanged);
  onChangedRef.current = onChanged;
  useEffect(() => {
    if (readyCount > 0) {
      onChangedRef.current();
    }
  }, [readyCount]);

  /** Replace = a new immutable version of an existing document through the same upload pipeline (B-05). */
  const acceptReplacement = (file: File, target: DocumentSummaryResponse) => {
    if (file.size > maxBytes) {
      setRejected([`${file.name}: larger than ${formatBytes(maxBytes)}`]);
      return;
    }
    if (allowed.length > 0 && !allowed.includes(file.type)) {
      setRejected([`${file.name}: type ${file.type || "unknown"} is not allowed`]);
      return;
    }
    setRejected([]);
    uploads.enqueue([file], { formRecordId, fieldDefinitionId, replacesFileAttachmentId: target.fileAttachmentId });
  };
  const accept = (files: FileList | File[]) => {
    const list = [...files];
    const problems: string[] = [];
    const accepted: File[] = [];
    const remaining = maxFiles - activeDocuments.length - items.filter((item) => item.phase !== "Ready" && item.phase !== "Rejected" && item.phase !== "Failed" && item.phase !== "Cancelled").length;
    for (const file of list) {
      if (accepted.length >= remaining) {
        problems.push(`${file.name}: the collection allows at most ${maxFiles} files`);
      } else if (file.size > maxBytes) {
        problems.push(`${file.name}: larger than ${formatBytes(maxBytes)}`);
      } else if (allowed.length > 0 && !allowed.includes(file.type)) {
        problems.push(`${file.name}: type ${file.type || "unknown"} is not allowed`);
      } else {
        accepted.push(file);
      }
    }
    setRejected(problems);
    if (accepted.length > 0) {
      uploads.enqueue(accepted, { formRecordId, fieldDefinitionId });
    }
  };
  const onDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setDragging(false);
    if (canEdit) {
      accept(event.dataTransfer.files);
    }
  };
  const remove = useMutation({
    mutationFn: (document: DocumentSummaryResponse) => api.documents.remove(formRecordId, document.fileAttachmentId, { expectedContentRevision: contentRevision, removalReason: reason.trim() }).then((r) => r.result),
    onSuccess: async () => {
      setRemoveTarget(null);
      setReason("");
      await queryClient.invalidateQueries({ queryKey: ["records"] });
      onChanged();
    },
  });

  return (
    <Card title={`${label} (${activeDocuments.length}${policy ? ` of ${maxFiles}` : ""})`}>
      {canEdit ? (
        <div
          role="group"
          aria-label={`Upload to ${label}`}
          onDragOver={(event) => {
            event.preventDefault();
            setDragging(true);
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={onDrop}
          className={`flex flex-col items-start gap-2 rounded border-2 border-dashed p-4 ${dragging ? "border-primary bg-page" : "border-input"}`}
        >
          <p className="text-sm">Drop files here, or</p>
          <Button variant="secondary" size="sm" onClick={() => input.current?.click()}>
            Choose files
          </Button>
          <input ref={input} type="file" multiple className="sr-only" aria-label={`Choose files for ${label}`} accept={allowed.join(",") || undefined} onChange={(event) => { if (event.target.files) { accept(event.target.files); } event.target.value = ""; }} />
          <p className="text-xs text-muted">
            Up to {maxFiles} files, {formatBytes(maxBytes)} each{allowed.length > 0 ? `; types: ${allowed.join(", ")}` : ""}. Files are scanned and encrypted before they become available.
          </p>
        </div>
      ) : null}
      {rejected.length > 0 ? (
        <StatusMessage tone="warning" title="Some files were not added">
          <ul className="list-disc pl-5">
            {rejected.map((problem) => (
              <li key={problem}>{problem}</li>
            ))}
          </ul>
        </StatusMessage>
      ) : null}
      {items.length > 0 ? (
        <ul className="flex flex-col gap-2" aria-label="Uploads in progress">
          {items.map((item) => (
            <li key={item.clientUploadId} className="rounded border border-line p-2">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="font-medium">
                  {item.fileName}
                  {item.replacesFileAttachmentId ? <span className="ml-2 text-xs text-muted">(new version)</span> : null}
                </span>
                <Badge tone={phaseTone(item.phase)}>{item.phase === "Processing" ? `Scanning/encrypting (${item.processingStage ?? "queued"})` : item.phase}</Badge>
              </div>
              {item.phase !== "Ready" ? <ProgressBar label={`${item.fileName} transfer`} value={item.transferredBytes} max={item.sizeBytes} /> : null}
              {item.errorCode ? <p className="text-sm text-[#991b1b]">{item.errorCode}{item.errorDetail ? `: ${item.errorDetail}` : ""}</p> : null}
              <div className="mt-1 flex gap-2">
                {item.phase === "Failed" && item.hasBytes ? <Button size="sm" variant="secondary" onClick={() => uploads.retry(item.clientUploadId)}>Retry</Button> : null}
                {item.phase === "Paused" && item.hasBytes ? <Button size="sm" variant="secondary" onClick={() => uploads.retry(item.clientUploadId)}>Resume</Button> : null}
                {item.phase === "Queued" || item.phase === "Transferring" || item.phase === "Starting" ? <Button size="sm" variant="secondary" onClick={() => uploads.pause(item.clientUploadId)}>Pause</Button> : null}
                {item.phase === "Queued" || item.phase === "Transferring" || item.phase === "Starting" || item.phase === "Paused" ? <Button size="sm" variant="secondary" onClick={() => uploads.cancel(item.clientUploadId)}>Cancel</Button> : null}
                {item.phase === "Ready" || item.phase === "Rejected" || item.phase === "Failed" || item.phase === "Cancelled" ? <Button size="sm" variant="ghost" onClick={() => uploads.dismiss(item.clientUploadId)}>Dismiss</Button> : null}
              </div>
            </li>
          ))}
        </ul>
      ) : null}
      <DataTable
        caption={label}
        rowKey={(row) => row.fileAttachmentId}
        rows={activeDocuments}
        emptyMessage="No documents yet."
        columns={[
          { key: "name", header: "File", isRowHeader: true, render: (row) => row.originalFileName },
          { key: "type", header: "Type", render: (row) => row.detectedContentType },
          { key: "size", header: "Size", render: (row) => formatBytes(row.plaintextSizeBytes) },
          { key: "version", header: "Version", render: (row) => `v${row.currentVersionNumber}` },
          { key: "scan", header: "Scan", render: (row) => <Badge tone={row.scanVerdict === "Clean" ? "success" : "danger"}>{row.scanVerdict}</Badge> },
          { key: "state", header: "State", render: (row) => <Badge tone={row.attachmentLifecycleState === "Ready" && row.inAcceptedManifest ? "success" : "warning"}>{row.inAcceptedManifest ? row.attachmentLifecycleState : "Not in verified manifest"}</Badge> },
          { key: "uploaded", header: "Uploaded", render: (row) => new Date(row.uploadedOn).toLocaleString() },
          {
            key: "actions",
            header: "Actions",
            render: (row) => (
              <div className="flex flex-wrap gap-2">
                {row.attachmentLifecycleState === "Ready" && row.inAcceptedManifest ? (
                  <a href={resolvePath(DOWNLOAD_PATH, { formRecordId, fileAttachmentId: row.fileAttachmentId })} download={row.originalFileName} className="inline-flex min-h-9 items-center rounded border border-secondary-border bg-secondary px-3 text-sm font-medium text-secondary-foreground">
                    Download
                  </a>
                ) : null}
                <Button size="sm" variant="ghost" onClick={() => setHistoryTarget(row)}>
                  Versions
                </Button>
                {canEdit && row.attachmentLifecycleState === "Ready" ? (
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => {
                      setReplaceTarget(row);
                      replaceInput.current?.click();
                    }}
                  >
                    Replace
                  </Button>
                ) : null}
                {canEdit ? (
                  <Button size="sm" variant="ghost" onClick={() => setRemoveTarget(row)}>
                    Remove
                  </Button>
                ) : null}
              </div>
            ),
          },
        ]}
      />
      {canEdit ? (
        <input
          ref={replaceInput}
          type="file"
          className="sr-only"
          aria-label={`Choose the replacement file for ${replaceTarget?.originalFileName ?? "the selected document"}`}
          accept={allowed.join(",") || undefined}
          onChange={(event) => {
            const file = event.target.files?.[0];
            event.target.value = "";
            if (file && replaceTarget) {
              acceptReplacement(file, replaceTarget);
            }
            setReplaceTarget(null);
          }}
        />
      ) : null}
      <Dialog
        open={removeTarget !== null}
        onOpenChange={(open) => (open ? undefined : setRemoveTarget(null))}
        title={`Remove ${removeTarget?.originalFileName ?? "document"}`}
        description="The document is unlinked from the verified record content; its versions are retained for audit and recovery."
        locked={remove.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={() => setRemoveTarget(null)} disabled={remove.isPending}>
              Cancel
            </Button>
            <Button variant="danger" busy={remove.isPending} disabled={reason.trim().length < 3} onClick={() => removeTarget && remove.mutate(removeTarget)}>
              Remove document
            </Button>
          </>
        }
      >
        <TextInput label="Reason" value={reason} required maxLength={500} onChange={(event) => setReason(event.target.value)} />
        {remove.isError ? <ErrorPanel error={remove.error} /> : null}
      </Dialog>
      {historyTarget ? <VersionHistoryDialog formRecordId={formRecordId} document={historyTarget} onClose={() => setHistoryTarget(null)} /> : null}
    </Card>
  );
}

function VersionHistoryDialog({ formRecordId, document, onClose }: { formRecordId: string; document: DocumentSummaryResponse; onClose: () => void }) {
  const api = useApi();
  const detail = useQuery({ queryKey: ["documents", formRecordId, document.fileAttachmentId], queryFn: () => api.documents.get(formRecordId, document.fileAttachmentId).then((r) => r.result) });
  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())} title={`Versions of ${document.originalFileName}`} footer={<Button variant="secondary" onClick={onClose}>Close</Button>}>
      {detail.isPending ? <LoadingBlock label="Loading versions" /> : null}
      {detail.isError ? <ErrorPanel error={detail.error} onRetry={() => void detail.refetch()} /> : null}
      {detail.data ? (
        <DataTable
          caption="Document versions"
          rowKey={(row) => row.fileAttachmentVersionId}
          rows={detail.data.versions}
          columns={[
            { key: "v", header: "Version", isRowHeader: true, render: (row) => `v${row.versionNumber}` },
            { key: "name", header: "File", render: (row) => row.originalFileName },
            { key: "size", header: "Size", render: (row) => formatBytes(row.plaintextSizeBytes) },
            { key: "scan", header: "Scanner", render: (row) => `${row.scanVerdict} (${row.scannerName})` },
            { key: "state", header: "State", render: (row) => <Badge tone={row.versionState === "Current" ? "success" : row.versionState === "Processed" ? "warning" : "neutral"}>{row.versionState === "Processed" ? "Processed (not committed)" : row.versionState}</Badge> },
            { key: "uploaded", header: "Uploaded", render: (row) => new Date(row.uploadedOn).toLocaleString() },
            { key: "dl", header: "Download", render: (row) => (row.versionState !== "Processed" ? <a href={`${resolvePath(DOWNLOAD_PATH, { formRecordId, fileAttachmentId: document.fileAttachmentId })}?versionNumber=${row.versionNumber}`} download={row.originalFileName} className="underline underline-offset-4">Download v{row.versionNumber}</a> : null) },
          ]}
        />
      ) : null}
    </Dialog>
  );
}
