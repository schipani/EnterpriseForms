import { useApi } from "@enterpriseforms/app-runtime";
import type { RecordProjectionResponse } from "@enterpriseforms/api-client";
import { FormRenderer, type DocumentElement, type FormLayout, type RelatedElement } from "@enterpriseforms/form-renderer";
import { ActionIcons, Badge, Button, Card, DescriptionList, LoadingBlock, PageHeader, StatusMessage, Tabs } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link, useNavigate, useParams, useSearch } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { RecordActivity } from "../../audit";
import { DocumentCollection } from "../../documents";
import { ChildRecordList } from "../../related-forms";
import { RecordAccessPanel } from "../../sharing";
import { ErrorPanel } from "../../support";
import { CopyReference } from "../components/CopyReference";
import { LifecycleActions } from "../components/LifecycleActions";
import { RecordHistory } from "../components/RecordHistory";
import { formatDateTime, recordStatusLabel } from "../components/recordList";
import { parseProjectionLayout, toGroupInfos, toGroups, toRendererFields, toValueMap } from "../components/recordModel";
import type { RecordTab } from "../components/recordTabs";

/**
 * PG-016 record workspace (C-30): a summary of the record plus sections for its details, related records, documents,
 * sharing and activity. Everything shown comes from the verified, authorized projection — a partial (shared) view
 * receives only the shared fields and documents, never related lists, so those sections simply do not appear.
 */
export function RecordPage() {
  const { formRecordId } = useParams({ from: "/app/records/$formRecordId" });
  const search = useSearch({ from: "/app/records/$formRecordId" });
  const navigate = useNavigate();
  const api = useApi();
  const projection = useQuery({ queryKey: ["records", "projection", formRecordId], queryFn: () => api.records.projection(formRecordId).then((r) => r.result) });
  const [savedNotice, setSavedNotice] = useState(false);
  const [lifecycleNotice, setLifecycleNotice] = useState<string | null>(null);
  useEffect(() => {
    if (search.saved) {
      // Confirm once, then drop the flag from the URL so a reload does not repeat it.
      setSavedNotice(true);
      void navigate({ to: "/app/records/$formRecordId", params: { formRecordId }, search: (current) => ({ ...current, saved: undefined }), replace: true });
    }
  }, [search.saved, formRecordId, navigate]);

  if (projection.isPending) {
    return <LoadingBlock label="Verifying record" />;
  }
  if (projection.isError) {
    return <ErrorPanel error={projection.error} onRetry={() => void projection.refetch()} />;
  }
  const data = projection.data;
  const { layout, error } = parseProjectionLayout(data);
  // Phase C6 M1: Submitted and Archived records are locked; the server decides, this only follows its verified state.
  const contentEditable = data.lifecycle?.contentEditable !== false;
  const relatedElements = layout ? relatedListsOf(layout) : [];
  const documentElements = layout ? documentCollectionsOf(layout) : [];
  const relationshipsById = new Map(data.relationships.map((relationship) => [relationship.formRelationshipDefinitionId, relationship]));
  // Phase C6 M2: history is part of the record's content, so only a person who can read the whole record sees it (a shared view never does).
  const sections: RecordTab[] = ["details", ...(relatedElements.length > 0 ? (["related"] as const) : []), ...(documentElements.length > 0 ? (["documents"] as const) : []), "sharing", "activity", ...(data.permissions.isCompleteProjection ? (["history"] as const) : [])];
  const tab: RecordTab = search.tab && sections.includes(search.tab) ? search.tab : "details";
  const showTab = (next: RecordTab) => void navigate({ to: "/app/records/$formRecordId", params: { formRecordId }, search: (current) => ({ ...current, tab: next === "details" ? undefined : next }), replace: true });

  const renderDocuments = (element: DocumentElement) => {
    const field = data.fields.find((candidate) => candidate.fieldDefinitionId === element.fieldDefinitionId);
    return <DocumentCollection key={element.fieldDefinitionId} formRecordId={formRecordId} fieldDefinitionId={element.fieldDefinitionId} label={element.label || field?.fieldDisplayName || "Documents"} documents={data.documents.filter((document) => document.fieldDefinitionId === element.fieldDefinitionId)} policy={element.upload} canEdit={data.permissions.canEdit && contentEditable} contentRevision={Number(data.record.contentRevision)} onChanged={() => void projection.refetch()} />;
  };

  return (
    <>
      <PageHeader
        title={data.formDefinitionName}
        lead={data.parentFormRecordId ? "Related (child) record" : "Record"}
        actions={
          <>
            {data.parentFormRecordId ? (
              <Link to="/app/records/$formRecordId" params={{ formRecordId: data.parentFormRecordId }} search={{ tab: "related" }}>
                <Button variant="ghost">Parent record</Button>
              </Link>
            ) : null}
            <Button variant="secondary" aria-label="Refresh this record" busy={projection.isFetching} onClick={() => void projection.refetch()}>
              Refresh
            </Button>
            <LifecycleActions formRecordId={formRecordId} data={data} onDone={setLifecycleNotice} />
            {data.permissions.canEdit && contentEditable ? (
              <Link to="/app/records/$formRecordId/edit" params={{ formRecordId }}>
                <Button icon={ActionIcons.edit}>Edit</Button>
              </Link>
            ) : !data.permissions.isCompleteProjection && data.permissions.canEditSharedFields && contentEditable ? (
              <Link to="/app/records/$formRecordId/edit" params={{ formRecordId }}>
                <Button icon={ActionIcons.edit}>Edit shared fields</Button>
              </Link>
            ) : null}
          </>
        }
      />
      {savedNotice ? (
        <div className="mb-4">
          <StatusMessage tone="success" title="Changes saved" actions={<Button size="sm" variant="ghost" onClick={() => setSavedNotice(false)}>Dismiss</Button>}>
            Your changes were saved and the record was verified again.
          </StatusMessage>
        </div>
      ) : null}
      {lifecycleNotice ? (
        <div className="mb-4">
          <StatusMessage tone="success" title={lifecycleNotice} actions={<Button size="sm" variant="ghost" onClick={() => setLifecycleNotice(null)}>Dismiss</Button>}>
            The change was verified and recorded in the activity history.
          </StatusMessage>
        </div>
      ) : null}
      {!contentEditable ? (
        <div className="mb-4">
          <StatusMessage tone="info" title={data.lifecycle?.status === "Archived" ? "This record is archived" : "This record is submitted"}>
            {data.lifecycle?.status === "Archived"
              ? "Archived records are read-only. The owner can unarchive it to work on it again."
              : "Submitted records are locked: values, documents and related records can't be changed. The owner can reopen it with a reason."}
          </StatusMessage>
        </div>
      ) : null}
      <RecordSummary data={data} />
      {error ? <StatusMessage tone="danger" title="Layout could not be parsed">{error}</StatusMessage> : null}
      <Tabs
        label="Record sections"
        value={tab}
        onValueChange={(value) => showTab(value as RecordTab)}
        items={[
          {
            value: "details",
            label: "Details",
            content: layout ? (
              <FormRenderer
                layout={layout}
                fields={toRendererFields(data.fields)}
                values={toValueMap(data.record)}
                groups={toGroups(data.record)}
                groupInfos={toGroupInfos(data.record.groups)}
                readOnly
                applyRules={data.permissions.isCompleteProjection}
                renderRelatedList={(element) => <SectionPointer label={element.label || relationshipsById.get(element.formRelationshipDefinitionId)?.relationshipDisplayName || "Related records"} target="Related records" onOpen={() => showTab("related")} />}
                renderDocumentCollection={(element, field) => {
                  const count = data.documents.filter((document) => document.fieldDefinitionId === element.fieldDefinitionId).length;
                  return <SectionPointer label={element.label || field?.fieldDisplayName || "Documents"} detail={`${count} ${count === 1 ? "document" : "documents"}`} target="Documents" onOpen={() => showTab("documents")} />;
                }}
              />
            ) : null,
          },
          ...(relatedElements.length > 0
            ? [
                {
                  value: "related",
                  label: "Related records",
                  content: (
                    <div className="flex flex-col gap-4">
                      {relatedElements.map((element) => {
                        const relationship = relationshipsById.get(element.formRelationshipDefinitionId);
                        return relationship ? (
                          <ChildRecordList key={element.formRelationshipDefinitionId} parentFormRecordId={formRecordId} relationship={relationship} label={element.label} canEdit={data.permissions.canEdit && contentEditable} summaryFieldKeys={element.summaryFieldKeys} />
                        ) : (
                          <p key={element.formRelationshipDefinitionId} className="text-sm text-muted">
                            {element.label}: this list is not available.
                          </p>
                        );
                      })}
                    </div>
                  ),
                },
              ]
            : []),
          ...(documentElements.length > 0 ? [{ value: "documents", label: "Documents", content: <div className="flex flex-col gap-4">{documentElements.map(renderDocuments)}</div> }] : []),
          { value: "sharing", label: "Sharing and access", content: <RecordAccessPanel formRecordId={formRecordId} projection={data} /> },
          { value: "activity", label: "Activity", content: <Card title="Activity log"><RecordActivity formRecordId={formRecordId} /></Card> },
          ...(data.permissions.isCompleteProjection ? [{ value: "history", label: "History", content: <RecordHistory formRecordId={formRecordId} /> }] : []),
        ]}
      />
    </>
  );
}

function RecordSummary({ data }: { data: RecordProjectionResponse }) {
  const summary = data.summary;
  const complete = data.permissions.isCompleteProjection;
  const sharedEditable = (data.sharedFieldAccess ?? []).filter((item) => item.editable).length;
  const access = complete
    ? data.permissions.canEdit && data.lifecycle?.contentEditable !== false
      ? "Full access: you can view and edit this record"
      : "You can view this record"
    : sharedEditable > 0
      ? `Shared fields only: you can see the fields and documents shared with you and change ${sharedEditable} of the shared ${sharedEditable === 1 ? "field" : "fields"}`
      : "Shared fields only: you can see the fields and documents shared with you";
  return (
    <Card title="Record summary" aria-label="Record summary" className="mb-4">
      <div className="mb-3 flex flex-wrap gap-2">
        <Badge tone={data.record.integrityStatus === "Verified" ? "success" : "danger"}>Integrity: {data.record.integrityStatus}</Badge>
        <Badge tone="neutral">Revision {String(data.record.contentRevision)}</Badge>
        <Badge tone="neutral">Layout v{data.layoutVersion.versionNumber}</Badge>
        <Badge tone={data.lifecycle?.status === "Archived" ? "warning" : data.lifecycle?.status === "Submitted" ? "info" : "neutral"}>{recordStatusLabel(data.lifecycle?.status ?? data.record.recordStatus)}</Badge>
        {!complete ? <Badge tone="warning">Partial view (shared fields only)</Badge> : null}
      </div>
      <DescriptionList
        items={[
          { term: "Reference", detail: summary?.recordReference ? <CopyReference reference={summary.recordReference} /> : "—" },
          { term: "Status", detail: recordStatusLabel(data.lifecycle?.status ?? data.record.recordStatus) },
          { term: "Created", detail: summary?.createdOn ? `${formatDateTime(summary.createdOn)}${summary.createdByCurrentUser ? " by you" : summary.createdByDisplayName ? ` by ${summary.createdByDisplayName}` : ""}` : "—" },
          { term: "Last changed", detail: summary?.modifiedOn ? formatDateTime(summary.modifiedOn) : "—" },
          { term: "Your access", detail: access },
        ]}
      />
    </Card>
  );
}

/** Stands in the Details form where the designer placed a related list or document collection; the content lives in its own section. */
function SectionPointer({ label, detail, target, onOpen }: { label: string; detail?: string; target: string; onOpen: () => void }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-2 rounded border border-line bg-page px-3 py-2">
      <span>
        <span className="font-medium">{label}</span>
        {detail ? <span className="text-sm text-muted"> · {detail}</span> : null}
      </span>
      {/* The accessible name starts with the visible text (WCAG 2.5.3) and adds which list or collection it opens. */}
      <Button size="sm" variant="secondary" onClick={onOpen} aria-label={`View in ${target}: ${label}`}>
        View in {target}
      </Button>
    </div>
  );
}

function relatedListsOf(layout: FormLayout): RelatedElement[] {
  return layout.sections.flatMap((section) => section.elements.filter((element): element is RelatedElement => element.kind === "relatedFormList"));
}

function documentCollectionsOf(layout: FormLayout): DocumentElement[] {
  return layout.sections.flatMap((section) => section.elements.filter((element): element is DocumentElement => element.kind === "documentCollection"));
}
