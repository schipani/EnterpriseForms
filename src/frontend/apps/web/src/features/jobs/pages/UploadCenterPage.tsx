import { useApi, useRuntime } from "@enterpriseforms/app-runtime";
import type { UploadItem } from "@enterpriseforms/upload-manager";
import { Badge, Button, Card, DataTable, LoadingBlock, PageHeader, ProgressBar, StatusMessage } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";
import { ErrorPanel } from "../../support";

export function useUploadItems(): readonly UploadItem[] {
  const { uploads } = useRuntime();
  const [items, setItems] = useState<readonly UploadItem[]>(() => uploads.snapshot());
  useEffect(() => uploads.subscribe(setItems), [uploads]);
  return items;
}

export function phaseTone(phase: UploadItem["phase"]): "info" | "success" | "warning" | "danger" | "neutral" {
  switch (phase) {
    case "Ready":
      return "success";
    case "Rejected":
    case "Failed":
      return "danger";
    case "Cancelled":
    case "Paused":
      return "neutral";
    case "Processing":
    case "Committing":
      return "warning";
    default:
      return "info";
  }
}

/** PG-021 Upload Center: in-tab uploads (durable across SPA navigation) plus server sessions that need a re-selected file. */
export function UploadCenterPage() {
  const { uploads } = useRuntime();
  const api = useApi();
  const items = useUploadItems();
  const serverSessions = useQuery({ queryKey: ["uploads", "mine"], queryFn: () => api.uploads.mine().then((r) => r.result), refetchInterval: 5000 });
  const fileInput = useRef<HTMLInputElement>(null);
  const [resumeTarget, setResumeTarget] = useState<string | null>(null);
  const knownSessions = new Set(items.map((item) => item.uploadSessionId));

  return (
    <>
      <PageHeader title="Upload Center" lead="Uploads continue while you move around the application. Closing every tab stops a transfer; it can be resumed by re-selecting the same file." />
      <Card title="Uploads in this tab">
        {items.length === 0 ? <p className="text-muted">No uploads started in this tab.</p> : null}
        <ul className="flex flex-col gap-3">
          {items.map((item) => (
            <li key={item.clientUploadId} className="rounded-control border border-line p-3">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <p className="font-medium">
                  <Link to="/app/records/$formRecordId" params={{ formRecordId: item.formRecordId }} search={{ tab: "documents" }} className="underline-offset-4 hover:underline">
                    {item.fileName}
                  </Link>
                </p>
                <Badge tone={phaseTone(item.phase)}>{item.phase === "Processing" ? `Processing (${item.processingStage ?? "queued"})` : item.phase}</Badge>
              </div>
              <ProgressBar label={`${item.fileName} transfer`} value={item.transferredBytes} max={item.sizeBytes} />
              {item.errorCode ? (
                <p className="mt-1 text-sm text-danger">
                  {item.errorCode}
                  {item.errorDetail ? `: ${item.errorDetail}` : ""}
                </p>
              ) : null}
              <div className="mt-2 flex gap-2">
                {item.phase === "Failed" && item.hasBytes ? (
                  <Button size="sm" variant="secondary" onClick={() => uploads.retry(item.clientUploadId)}>
                    Retry
                  </Button>
                ) : null}
                {item.phase === "Paused" && item.hasBytes ? (
                  <Button size="sm" variant="secondary" onClick={() => uploads.retry(item.clientUploadId)}>
                    Resume
                  </Button>
                ) : null}
                {item.phase === "Queued" || item.phase === "Starting" || item.phase === "Transferring" ? (
                  <Button size="sm" variant="secondary" onClick={() => uploads.pause(item.clientUploadId)}>
                    Pause
                  </Button>
                ) : null}
                {item.phase === "Queued" || item.phase === "Starting" || item.phase === "Transferring" || item.phase === "Paused" ? (
                  <Button size="sm" variant="secondary" onClick={() => uploads.cancel(item.clientUploadId)}>
                    Cancel
                  </Button>
                ) : null}
                {item.phase === "Ready" || item.phase === "Rejected" || item.phase === "Failed" || item.phase === "Cancelled" ? (
                  <Button size="sm" variant="ghost" onClick={() => uploads.dismiss(item.clientUploadId)}>
                    Dismiss
                  </Button>
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      </Card>
      <div className="mt-6">
        <Card title="Open upload sessions on the server" actions={<Button size="sm" variant="secondary" busy={serverSessions.isFetching} onClick={() => void serverSessions.refetch()}>Refresh</Button>}>
          {serverSessions.isPending ? <LoadingBlock label="Loading sessions" /> : null}
          {serverSessions.isError ? <ErrorPanel error={serverSessions.error} onRetry={() => void serverSessions.refetch()} /> : null}
          {serverSessions.data ? (
            <DataTable
              caption="Server upload sessions"
              rowKey={(row) => row.uploadSessionId}
              rows={serverSessions.data.filter((session) => !knownSessions.has(session.uploadSessionId))}
              emptyMessage="No other sessions."
              columns={[
                { key: "file", header: "File", isRowHeader: true, render: (row) => row.originalFileName },
                { key: "state", header: "State", render: (row) => <Badge tone="neutral">{row.sessionState}</Badge> },
                { key: "progress", header: "Received", render: (row) => `${row.receivedChunkIndexes.length} / ${row.expectedChunkCount} chunks` },
                { key: "expires", header: "Expires", render: (row) => new Date(row.sessionExpiresOn).toLocaleString() },
                {
                  key: "actions",
                  header: "Actions",
                  isActions: true,
                  render: (row) =>
                    row.sessionState === "Open" ? (
                      <Button
                        size="sm"
                        variant="secondary"
                        onClick={() => {
                          setResumeTarget(row.uploadSessionId);
                          fileInput.current?.click();
                        }}
                      >
                        Re-select file to resume
                      </Button>
                    ) : null,
                },
              ]}
            />
          ) : null}
          <input
            ref={fileInput}
            type="file"
            className="sr-only"
            aria-label="Re-select the file to resume"
            onChange={(event) => {
              const file = event.target.files?.[0];
              const session = serverSessions.data?.find((candidate) => candidate.uploadSessionId === resumeTarget);
              event.target.value = "";
              if (!file || !session) {
                return;
              }
              const id = uploads.resume(session, file);
              if (!id) {
                window.alert("The selected file does not match the original name and size.");
              }
            }}
          />
          <StatusMessage tone="info" title="About resumption">
            Browsers cannot continue an upload after every tab is closed; file bytes are never persisted in the browser. Re-selecting the same file resumes from the chunks the server already holds.
          </StatusMessage>
        </Card>
      </div>
    </>
  );
}
