import { DOWNLOAD_PATH, presentError, resolvePath, useApi } from "@enterpriseforms/app-runtime";
import type { RecordRestoreResponse, RecordVersionViewResponse } from "@enterpriseforms/api-client";
import { FormRenderer, type FormLayout } from "@enterpriseforms/form-renderer";
import { ActionIcons, Badge, Button, Card, DescriptionList, LoadingBlock, PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { Link, useParams } from "@tanstack/react-router";
import { ErrorPanel } from "../../support";
import { actorText, changeText, otherRecordedValues } from "../components/historyModel";
import { RestoreVersionAction } from "../components/RecordRestore";
import { formatDateTime, recordStatusLabel } from "../components/recordList";
import { parseProjectionLayout, toGroupInfos, toGroups, toRendererFields, toValueMap } from "../components/recordModel";

/**
 * Phase C6 M2 — one earlier version of a record, read-only. The server shows it only after the Authorization Authority has
 * checked that version's saved content, and only to someone who can read the whole record now. Field names and the
 * arrangement of the form are today's; the values, documents and status are the version's. Phase C6 M3: the owner of a Draft
 * record can restore this version as a new version (the server checks every condition again).
 */
export function RecordVersionPage() {
  const { formRecordId, contentRevision } = useParams({ from: "/app/records/$formRecordId/versions/$contentRevision" });
  const revision = Number(contentRevision);
  const api = useApi();
  const version = useQuery<RecordVersionViewResponse>({
    queryKey: ["records", "version", formRecordId, revision],
    queryFn: ({ signal }) => api.records.version(formRecordId, revision, signal).then((r) => r.result),
    enabled: Number.isInteger(revision) && revision > 0,
    retry: false,
  });
  // The current record (permissions and status) decides whether Restore is offered; the server decides whether it happens.
  const current = useQuery({ queryKey: ["records", "projection", formRecordId], queryFn: () => api.records.projection(formRecordId).then((r) => r.result), retry: false });
  const [restored, setRestored] = useState<RecordRestoreResponse | null>(null);
  const back = (
    <Link to="/app/records/$formRecordId" params={{ formRecordId }} search={{ tab: "history" }}>
      <Button variant="secondary">Back to version history</Button>
    </Link>
  );

  if (!Number.isInteger(revision) || revision < 1) {
    return <VersionUnavailable formRecordId={formRecordId} title="This version isn't available" detail="The version number in the address isn't valid." />;
  }
  if (version.isPending) {
    return <LoadingBlock label={`Checking version ${revision}`} />;
  }
  if (version.isError) {
    const error = presentError(version.error);
    if (error.status === 404) {
      return <VersionUnavailable formRecordId={formRecordId} title="This version isn't available" detail="It doesn't exist, or you can't see this record's history." />;
    }
    if (error.code === "INTEGRITY_VERIFICATION_FAILED" || error.code === "INTEGRITY_QUARANTINE") {
      return <VersionUnavailable formRecordId={formRecordId} tone="danger" title="This version could not be verified" detail="Its saved content doesn't match what was accepted, so it can't be shown. The record has been locked for review." />;
    }
    if (error.code === "HISTORIC_VERSION_UNVERIFIABLE" || error.code === "KEY_UNAVAILABLE" || error.code === "ENCRYPTION_UNAVAILABLE") {
      return <VersionUnavailable formRecordId={formRecordId} title="This version can't be opened right now" detail="A key needed to check or decrypt it isn't available. Nothing from this version is shown." />;
    }
    return <ErrorPanel error={version.error} onRetry={() => void version.refetch()} />;
  }

  const data = version.data;
  const projection = data.projection;
  const item = data.version;
  const { layout, error } = parseProjectionLayout(projection);
  const currentRevision = Number(data.currentContentRevision);
  const change = changeText(item);
  const others = layout ? otherRecordedValues(layout, projection.record.values, projection.fields) : [];
  const documentsOf = (fieldDefinitionId: string) => projection.documents.filter((document) => document.fieldDefinitionId === fieldDefinitionId);

  return (
    <>
      <PageHeader
        title={projection.formDefinitionName}
        lead={`Version ${revision} of ${currentRevision} (read-only)`}
        actions={
          <>
            {back}
            <Link to="/app/records/$formRecordId" params={{ formRecordId }}>
              <Button>Open current record</Button>
            </Link>
          </>
        }
      />
      {restored ? (
        <div className="mb-4">
          <StatusMessage
            tone="success"
            title={`Version ${revision} was restored as version ${String(restored.contentRevision)}`}
            actions={
              <>
                <Link to="/app/records/$formRecordId/edit" params={{ formRecordId }}>
                  <Button size="sm" icon={ActionIcons.edit}>Edit the record</Button>
                </Link>
                <Link to="/app/records/$formRecordId" params={{ formRecordId }} search={{ tab: "history" }}>
                  <Button size="sm" variant="secondary">Version history</Button>
                </Link>
              </>
            }
          >
            The record now has this version's values as a new version. Every earlier version, including the one you replaced, is still in the history.
          </StatusMessage>
        </div>
      ) : null}
      <div className="mb-4 flex flex-col gap-3">
        <StatusMessage tone={item.isCurrent ? "info" : "warning"} title={item.isCurrent ? `This is the current version (${revision})` : `You are viewing an earlier version: version ${revision} of ${currentRevision}`}>
          Saved {formatDateTime(item.acceptedOn)} by {actorText(item)}. This page is read-only: nothing can be changed here.
        </StatusMessage>
        {restored ? null : (
          <div className="flex flex-wrap items-start gap-2">
            <RestoreVersionAction formRecordId={formRecordId} version={item} projection={current.data} onRestored={setRestored} />
          </div>
        )}
      </div>
      <Card title="About this version" aria-label="About this version" className="mb-4">
        <div className="flex flex-wrap gap-2">
          <Badge tone="neutral">Version {revision}</Badge>
          <Badge tone="success">Checked when opened</Badge>
          <Badge tone="neutral">Status in this version: {recordStatusLabel(projection.lifecycle?.status ?? projection.record.recordStatus)}</Badge>
        </div>
        <DescriptionList
          items={[
            { term: "Reference", detail: projection.summary?.recordReference ?? "—" },
            { term: "Saved", detail: formatDateTime(item.acceptedOn) },
            { term: "By", detail: actorText(item) },
            { term: "Change", detail: change.note ? `${change.text} (${change.note})` : change.text },
          ]}
        />
        <p className="text-sm text-muted">Field names and the arrangement of the form are shown as they are today. The values, documents and status are those saved in this version. Related records keep their own history and are not part of this version.</p>
      </Card>
      {error ? <StatusMessage tone="danger" title="The form layout could not be read">{error}</StatusMessage> : null}
      {layout ? (
        <Card title={`Details in version ${revision}`} aria-label={`Details in version ${revision}`} className="mb-4">
          <FormRenderer
            layout={layout as FormLayout}
            fields={toRendererFields(projection.fields)}
            values={toValueMap(projection.record)}
            groups={toGroups(projection.record)}
            groupInfos={toGroupInfos(projection.record.groups)}
            readOnly
            applyRules={false}
            renderRelatedList={(element) => (
              <p key={element.formRelationshipDefinitionId} className="rounded border border-line bg-page px-3 py-2 text-sm text-muted">
                {element.label || "Related records"}: related records are not part of this version.
              </p>
            )}
            renderDocumentCollection={(element, field) => {
              const documents = documentsOf(element.fieldDefinitionId);
              return (
                <div key={element.fieldDefinitionId} className="rounded border border-line bg-page px-3 py-2">
                  <p className="font-medium">{element.label || field?.fieldDisplayName || "Documents"}</p>
                  {documents.length === 0 ? (
                    <p className="text-sm text-muted">No documents in this version.</p>
                  ) : (
                    <ul className="mt-1 flex flex-col gap-1 text-sm">
                      {documents.map((document) => (
                        <li key={`${document.fileAttachmentId}-${document.currentVersionNumber}`} className="flex flex-wrap items-center gap-2">
                          <span>{document.originalFileName}</span>
                          <span className="text-muted">document version {document.currentVersionNumber}</span>
                          <a href={`${resolvePath(DOWNLOAD_PATH, { formRecordId, fileAttachmentId: document.fileAttachmentId })}?versionNumber=${document.currentVersionNumber}`} download={document.originalFileName} className="underline underline-offset-4" aria-label={`Download ${document.originalFileName}, document version ${document.currentVersionNumber}`}>
                            Download
                          </a>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              );
            }}
          />
        </Card>
      ) : null}
      {others.length > 0 ? (
        <Card title="Other values saved in this version" aria-label="Other values saved in this version" className="mb-4">
          <p className="text-sm text-muted">These fields are no longer part of the current form. Their values are shown as they were saved in this version.</p>
          <DescriptionList items={others.map((other) => ({ term: other.label + (other.retired ? " (retired field)" : ""), detail: other.value || "—" }))} />
        </Card>
      ) : null}
    </>
  );
}

function VersionUnavailable({ formRecordId, title, detail, tone = "info" }: { formRecordId: string; title: string; detail: string; tone?: "info" | "danger" }) {
  return (
    <>
      <PageHeader title="Record version" lead="Read-only" />
      <StatusMessage tone={tone} title={title}>
        {detail}
      </StatusMessage>
      <div className="mt-3 flex gap-2">
        <Link to="/app/records/$formRecordId" params={{ formRecordId }}>
          <Button variant="secondary">Back to the record</Button>
        </Link>
        <Link to="/app/records">
          <Button variant="ghost">My Records</Button>
        </Link>
      </div>
    </>
  );
}
