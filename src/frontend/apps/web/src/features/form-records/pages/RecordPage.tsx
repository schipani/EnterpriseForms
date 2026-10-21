import { useApi } from "@enterpriseforms/app-runtime";
import { FormRenderer } from "@enterpriseforms/form-renderer";
import { Badge, Button, Card, LoadingBlock, PageHeader, StatusMessage, Tabs } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { RecordActivity } from "../../audit";
import { DocumentCollection } from "../../documents";
import { ChildRecordList } from "../../related-forms";
import { RecordAccessPanel } from "../../sharing";
import { ErrorPanel } from "../../support";
import { parseProjectionLayout, toGroupInfos, toGroups, toRendererFields, toValueMap } from "../components/recordModel";

/** PG-017 record workspace: verified projection, related lists, documents, activity. Permissions come from the projection. */
export function RecordPage() {
  const { formRecordId } = useParams({ from: "/app/records/$formRecordId" });
  const api = useApi();
  const projection = useQuery({ queryKey: ["records", "projection", formRecordId], queryFn: () => api.records.projection(formRecordId).then((r) => r.result) });
  if (projection.isPending) {
    return <LoadingBlock label="Verifying record" />;
  }
  if (projection.isError) {
    return <ErrorPanel error={projection.error} onRetry={() => void projection.refetch()} />;
  }
  const data = projection.data;
  const { layout, error } = parseProjectionLayout(data);
  const fields = toRendererFields(data.fields);
  const values = toValueMap(data.record);
  const relationshipsById = new Map(data.relationships.map((relationship) => [relationship.formRelationshipDefinitionId, relationship]));
  return (
    <>
      <PageHeader
        title={data.formDefinitionName}
        lead={data.parentFormRecordId ? "Child record" : "Record"}
        actions={
          <>
            {data.parentFormRecordId ? (
              <Link to="/app/records/$formRecordId" params={{ formRecordId: data.parentFormRecordId }}>
                <Button variant="ghost">Parent record</Button>
              </Link>
            ) : null}
            <Button variant="secondary" busy={projection.isFetching} onClick={() => void projection.refetch()}>
              Refresh
            </Button>
            {data.permissions.canEdit ? (
              <Link to="/app/records/$formRecordId/edit" params={{ formRecordId }}>
                <Button>Edit</Button>
              </Link>
            ) : null}
          </>
        }
      />
      <div className="mb-4 flex flex-wrap gap-2">
        <Badge tone={data.record.integrityStatus === "Verified" ? "success" : "danger"}>Integrity: {data.record.integrityStatus}</Badge>
        <Badge tone="neutral">Revision {String(data.record.contentRevision)}</Badge>
        <Badge tone="neutral">Layout v{data.layoutVersion.versionNumber}</Badge>
        {!data.permissions.isCompleteProjection ? <Badge tone="warning">Partial view (shared fields only)</Badge> : null}
      </div>
      {error ? <StatusMessage tone="danger" title="Layout could not be parsed">{error}</StatusMessage> : null}
      <Tabs
        label="Record sections"
        items={[
          {
            value: "details",
            label: "Details",
            content: layout ? (
              <FormRenderer
                layout={layout}
                fields={fields}
                values={values}
                groups={toGroups(data.record)}
                groupInfos={toGroupInfos(data.record.groups)}
                readOnly
                applyRules={data.permissions.isCompleteProjection}
                renderRelatedList={(element) => {
                  const relationship = relationshipsById.get(element.formRelationshipDefinitionId);
                  return relationship ? <ChildRecordList parentFormRecordId={formRecordId} relationship={relationship} label={element.label} canEdit={data.permissions.canEdit} summaryFieldKeys={element.summaryFieldKeys} /> : <p className="text-sm text-muted">{element.label}: relationship not available.</p>;
                }}
                renderDocumentCollection={(element, field) => <DocumentCollection formRecordId={formRecordId} fieldDefinitionId={element.fieldDefinitionId} label={element.label || field?.fieldDisplayName || "Documents"} documents={data.documents.filter((document) => document.fieldDefinitionId === element.fieldDefinitionId)} policy={element.upload} canEdit={data.permissions.canEdit} contentRevision={Number(data.record.contentRevision)} onChanged={() => void projection.refetch()} />}
              />
            ) : null,
          },
          {
            value: "access",
            label: "Access",
            content: <RecordAccessPanel formRecordId={formRecordId} projection={data} />,
          },
          { value: "activity", label: "Activity", content: <Card title="Audit history"><RecordActivity formRecordId={formRecordId} /></Card> },
        ]}
      />
    </>
  );
}
