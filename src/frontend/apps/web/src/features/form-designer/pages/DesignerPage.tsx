import { useApi } from "@enterpriseforms/app-runtime";
import { DesignerCanvas } from "@enterpriseforms/form-designer";
import { emptyLayout, serializeLayout, tryParseLayout, type FormLayout, type RendererField } from "@enterpriseforms/form-renderer";
import { Badge, Button, Card, DataTable, Dialog, LoadingBlock, PageHeader, Select, StatusMessage, Tabs, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { ErrorPanel } from "../../support";
import { AddFieldDialog, toKey } from "../components/AddFieldDialog";

/**
 * PG-033/037 designer: fields catalog, relationships, draft layout canvas with save (draft revision CAS), publish
 * (draft + current version CAS), version history and restore-to-draft. The server validates every layout again.
 */
export function DesignerPage() {
  const { formDefinitionId } = useParams({ from: "/app/forms/$formDefinitionId/design" });
  const api = useApi();
  const queryClient = useQueryClient();
  const detail = useQuery({ queryKey: ["forms", formDefinitionId], queryFn: () => api.forms.get(formDefinitionId).then((r) => r.result) });
  const forms = useQuery({ queryKey: ["forms"], queryFn: () => api.forms.list().then((r) => r.result) });
  const versions = useQuery({ queryKey: ["forms", formDefinitionId, "versions"], queryFn: () => api.forms.versions(formDefinitionId).then((r) => r.result) });

  const [layout, setLayout] = useState<FormLayout | null>(null);
  const [baseJson, setBaseJson] = useState<string>("");
  const [parseError, setParseError] = useState<string | null>(null);
  const [addField, setAddField] = useState(false);
  const [publishOpen, setPublishOpen] = useState(false);
  const [changeReason, setChangeReason] = useState("");
  const [relationshipOpen, setRelationshipOpen] = useState(false);
  const [childFormId, setChildFormId] = useState<string | undefined>(undefined);
  const [relationshipName, setRelationshipName] = useState("");
  const [maxChildren, setMaxChildren] = useState("50");

  useEffect(() => {
    if (!detail.data || layout) {
      return;
    }
    const source = detail.data.draft?.layoutJson ?? detail.data.currentVersion?.layoutJson;
    if (!source) {
      const fresh = emptyLayout(detail.data.summary.formDefinitionName);
      setLayout(fresh);
      setBaseJson("");
      return;
    }
    const parsed = tryParseLayout(source);
    if (parsed.ok) {
      setLayout(parsed.layout);
      setBaseJson(serializeLayout(parsed.layout));
    } else {
      setParseError(parsed.error);
    }
  }, [detail.data, layout]);

  const invalidate = () => Promise.all([queryClient.invalidateQueries({ queryKey: ["forms", formDefinitionId] }), queryClient.invalidateQueries({ queryKey: ["forms"] })]);
  const addFieldMutation = useMutation({ mutationFn: (request: Parameters<typeof api.forms.addField>[1]) => api.forms.addField(formDefinitionId, request).then((r) => r.result), onSuccess: async () => { setAddField(false); await invalidate(); } });
  const saveDraft = useMutation({
    mutationFn: (current: FormLayout) => api.forms.saveDraft(formDefinitionId, { expectedDraftRevision: Number(detail.data?.draft?.draftRevision ?? 0), layoutJson: serializeLayout(current) }).then((r) => r.result),
    onSuccess: async (draft) => {
      const parsed = tryParseLayout(draft.layoutJson);
      if (parsed.ok) {
        setLayout(parsed.layout);
        setBaseJson(serializeLayout(parsed.layout));
      }
      await invalidate();
    },
  });
  const publish = useMutation({
    mutationFn: () => api.forms.publish(formDefinitionId, { expectedDraftRevision: Number(detail.data?.draft?.draftRevision ?? 0), expectedCurrentVersionNumber: Number(detail.data?.summary.currentVersionNumber ?? 0), changeReason: changeReason.trim() }).then((r) => r.result),
    onSuccess: async () => {
      setPublishOpen(false);
      setChangeReason("");
      setLayout(null);
      await invalidate();
      await queryClient.invalidateQueries({ queryKey: ["forms", formDefinitionId, "versions"] });
    },
  });
  const restore = useMutation({
    mutationFn: (fromFormVersionId: string) => api.forms.restoreDraft(formDefinitionId, { fromFormVersionId }).then((r) => r.result),
    onSuccess: async () => {
      setLayout(null);
      await invalidate();
    },
  });
  const createRelationship = useMutation({
    mutationFn: () => api.forms.createRelationship(formDefinitionId, { childFormDefinitionId: childFormId!, relationshipDefinitionKey: toKey(relationshipName), relationshipDisplayName: relationshipName.trim(), minimumChildCount: 0, maximumChildCount: Number(maxChildren) }).then((r) => r.result),
    onSuccess: async () => {
      setRelationshipOpen(false);
      setRelationshipName("");
      await invalidate();
    },
  });

  if (detail.isPending) {
    return <LoadingBlock label="Loading form" />;
  }
  if (detail.isError) {
    return <ErrorPanel error={detail.error} onRetry={() => void detail.refetch()} />;
  }
  const form = detail.data;
  const fields: RendererField[] = form.fields.map((field) => ({ fieldDefinitionId: field.fieldDefinitionId, fieldKey: field.fieldKey, fieldDisplayName: field.fieldDisplayName, fieldDataType: field.fieldDataType, options: field.options.map((option) => ({ ...option, optionSortOrder: Number(option.optionSortOrder) })) }));
  const dirty = layout !== null && serializeLayout(layout) !== baseJson;
  const canPublish = form.draft !== null && !dirty;
  const childCandidates = (forms.data ?? []).filter((candidate) => candidate.formDefinitionId !== formDefinitionId && candidate.currentVersionNumber !== null);

  return (
    <>
      <PageHeader
        title={form.summary.formDefinitionName}
        lead={form.summary.formDefinitionDescription || "Design the layout, save a draft, then publish an immutable version."}
        actions={
          <>
            <Link to="/app/forms">
              <Button variant="ghost">All forms</Button>
            </Link>
            <Button variant="secondary" busy={saveDraft.isPending} disabled={!layout || !dirty || !form.summary.canDesign} onClick={() => layout && saveDraft.mutate(layout)}>
              Save draft
            </Button>
            <Button disabled={!canPublish || !form.summary.canDesign} onClick={() => setPublishOpen(true)}>
              Publish…
            </Button>
          </>
        }
      />
      <div className="mb-4 flex flex-wrap gap-2">
        <Badge tone={form.summary.currentVersionNumber === null ? "neutral" : "success"}>{form.summary.currentVersionNumber === null ? "Not published" : `Published v${form.summary.currentVersionNumber}`}</Badge>
        <Badge tone={form.draft ? "warning" : "neutral"}>{form.draft ? `Draft revision ${form.draft.draftRevision}` : "No draft"}</Badge>
        {dirty ? <Badge tone="info">Unsaved changes</Badge> : null}
        {!form.summary.canDesign ? <Badge tone="danger">Read-only: you cannot design this form</Badge> : null}
      </div>
      {parseError ? <StatusMessage tone="danger" title="The stored layout could not be parsed">{parseError}</StatusMessage> : null}
      {saveDraft.isError ? <ErrorPanel error={saveDraft.error} /> : null}
      {restore.isError ? <ErrorPanel error={restore.error} /> : null}
      <Tabs
        label="Designer sections"
        items={[
          {
            value: "layout",
            label: "Layout",
            content: layout ? <DesignerCanvas layout={layout} onChange={setLayout} fields={fields} relationships={form.relationships.map((relationship) => ({ formRelationshipDefinitionId: relationship.formRelationshipDefinitionId, relationshipDisplayName: relationship.relationshipDisplayName, childFormDefinitionName: relationship.childFormDefinitionName }))} readOnly={!form.summary.canDesign} /> : <LoadingBlock />,
          },
          {
            value: "fields",
            label: `Fields (${form.fields.length})`,
            content: (
              <Card title="Field catalog" actions={form.summary.canDesign ? <Button size="sm" onClick={() => setAddField(true)}>Add field</Button> : null}>
                <DataTable
                  caption="Fields"
                  rowKey={(row) => row.fieldDefinitionId}
                  rows={form.fields}
                  emptyMessage="No fields yet. Add the first field to start the layout."
                  columns={[
                    { key: "name", header: "Field", isRowHeader: true, render: (row) => row.fieldDisplayName },
                    { key: "key", header: "Key", render: (row) => <code>{row.fieldKey}</code> },
                    { key: "type", header: "Type", render: (row) => row.fieldDataType },
                    { key: "options", header: "Options", render: (row) => (row.options.length === 0 ? "" : row.options.map((option) => `${option.optionDisplayText}${option.isActive ? "" : " (inactive)"}`).join(", ")) },
                    { key: "status", header: "Status", render: (row) => <Badge tone="neutral">{row.lifecycleStatus}</Badge> },
                  ]}
                />
              </Card>
            ),
          },
          {
            value: "relationships",
            label: `Related forms (${form.relationships.length})`,
            content: (
              <Card title="Child record lists" actions={form.summary.canDesign ? <Button size="sm" onClick={() => setRelationshipOpen(true)}>Add relationship</Button> : null}>
                <p className="text-sm text-muted">A relationship hosts a reusable child form (for example AccusedAddress) as a list of independent child records.</p>
                <DataTable
                  caption="Relationships"
                  rowKey={(row) => row.formRelationshipDefinitionId}
                  rows={form.relationships}
                  emptyMessage="No relationships yet."
                  columns={[
                    { key: "name", header: "List", isRowHeader: true, render: (row) => row.relationshipDisplayName },
                    { key: "child", header: "Child form", render: (row) => row.childFormDefinitionName },
                    { key: "bounds", header: "Bounds", render: (row) => `${row.minimumChildCount}–${row.maximumChildCount}` },
                    { key: "status", header: "Status", render: (row) => <Badge tone="neutral">{row.lifecycleStatus}</Badge> },
                  ]}
                />
              </Card>
            ),
          },
          {
            value: "versions",
            label: "Versions",
            content: (
              <Card title="Published versions">
                {versions.isPending ? <LoadingBlock label="Loading versions" /> : null}
                {versions.isError ? <ErrorPanel error={versions.error} onRetry={() => void versions.refetch()} /> : null}
                {versions.data ? (
                  <DataTable
                    caption="Versions"
                    rowKey={(row) => row.formVersionId}
                    rows={versions.data}
                    emptyMessage="Nothing published yet."
                    columns={[
                      { key: "v", header: "Version", isRowHeader: true, render: (row) => `v${row.versionNumber}` },
                      { key: "reason", header: "Change reason", render: (row) => row.changeReason },
                      { key: "published", header: "Published", render: (row) => new Date(row.publishedOn).toLocaleString() },
                      { key: "restored", header: "Restored from", render: (row) => (row.restoredFromFormVersionId ? "earlier version" : "") },
                      { key: "actions", header: "Actions", render: (row) => (form.summary.canDesign ? <Button size="sm" variant="secondary" busy={restore.isPending} onClick={() => restore.mutate(row.formVersionId)}>Restore to draft</Button> : null) },
                    ]}
                  />
                ) : null}
              </Card>
            ),
          },
        ]}
      />

      <AddFieldDialog open={addField} onOpenChange={setAddField} pending={addFieldMutation.isPending} error={addFieldMutation.error} onSubmit={(request) => addFieldMutation.mutate(request)} />

      <Dialog
        open={publishOpen}
        onOpenChange={setPublishOpen}
        title="Publish a new version"
        description={`This publishes draft revision ${form.draft?.draftRevision ?? 0} as version ${Number(form.summary.currentVersionNumber ?? 0) + 1}. Published versions are immutable; existing records keep their values.`}
        locked={publish.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={() => setPublishOpen(false)} disabled={publish.isPending}>
              Cancel
            </Button>
            <Button busy={publish.isPending} disabled={changeReason.trim().length < 3} onClick={() => publish.mutate()}>
              Publish
            </Button>
          </>
        }
      >
        <TextInput label="Change reason" value={changeReason} required maxLength={500} onChange={(event) => setChangeReason(event.target.value)} />
        {publish.isError ? <ErrorPanel error={publish.error} /> : null}
      </Dialog>

      <Dialog
        open={relationshipOpen}
        onOpenChange={setRelationshipOpen}
        title="Add a related form list"
        description="Choose a published child form. Each list item becomes an independent child record owned through this parent."
        locked={createRelationship.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={() => setRelationshipOpen(false)} disabled={createRelationship.isPending}>
              Cancel
            </Button>
            <Button busy={createRelationship.isPending} disabled={!childFormId || relationshipName.trim().length === 0} onClick={() => createRelationship.mutate()}>
              Add relationship
            </Button>
          </>
        }
      >
        <Select label="Child form" value={childFormId} required options={childCandidates.map((candidate) => ({ value: candidate.formDefinitionId, label: `${candidate.formDefinitionName} (v${candidate.currentVersionNumber})` }))} placeholder={childCandidates.length === 0 ? "Publish another form first" : "Choose a child form"} onValueChange={setChildFormId} />
        <TextInput label="List name" value={relationshipName} required maxLength={200} helpText={`Key: ${toKey(relationshipName) || "(derived)"}`} onChange={(event) => setRelationshipName(event.target.value)} />
        <TextInput label="Maximum items" value={maxChildren} inputMode="numeric" onChange={(event) => setMaxChildren(event.target.value)} />
        {createRelationship.isError ? <ErrorPanel error={createRelationship.error} /> : null}
      </Dialog>
    </>
  );
}
