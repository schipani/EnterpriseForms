import { useApi } from "@enterpriseforms/app-runtime";
import { DesignerCanvas, locateField, updateElement } from "@enterpriseforms/form-designer";
import { emptyLayout, serializeLayout, tryParseLayout, type FormLayout, type RendererField } from "@enterpriseforms/form-renderer";
import { Badge, Button, Card, DataTable, Dialog, LoadingBlock, PageHeader, Select, StatusMessage, Tabs, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useBlocker, useParams } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";
import { ErrorPanel } from "../../support";
import { AddFieldDialog, toKey } from "../components/AddFieldDialog";
import type { StrengthenFieldProtectionRequest } from "@enterpriseforms/api-client";
import { EditFieldDialog, type FieldEditSubmission } from "../components/EditFieldDialog";
import { FormAccessPanel } from "../components/FormAccessPanel";

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
  // Undo/redo over the draft layout (C1-UX §A toolbar): `setLayout` below is the single mutation path used by the
  // canvas, so every layout-changing action — including drag/drop, span changes and inline rename — is undoable.
  const undoStack = useRef<FormLayout[]>([]);
  const redoStack = useRef<FormLayout[]>([]);
  const [, setHistoryTick] = useState(0);
  const setLayoutTracked = (next: FormLayout | ((current: FormLayout | null) => FormLayout | null)) => {
    setLayout((current) => {
      const resolved = typeof next === "function" ? (next as (c: FormLayout | null) => FormLayout | null)(current) : next;
      if (current && resolved && serializeLayout(resolved) !== serializeLayout(current)) {
        undoStack.current = [...undoStack.current, current].slice(-50);
        redoStack.current = [];
        setHistoryTick((tick) => tick + 1);
      }
      return resolved;
    });
  };
  const undo = () => {
    const previous = undoStack.current.at(-1);
    if (!previous || !layout) {
      return;
    }
    undoStack.current = undoStack.current.slice(0, -1);
    redoStack.current = [...redoStack.current, layout];
    setLayout(previous);
    setHistoryTick((tick) => tick + 1);
  };
  const redo = () => {
    const next = redoStack.current.at(-1);
    if (!next || !layout) {
      return;
    }
    redoStack.current = redoStack.current.slice(0, -1);
    undoStack.current = [...undoStack.current, layout];
    setLayout(next);
    setHistoryTick((tick) => tick + 1);
  };
  const [addField, setAddField] = useState(false);
  const [addDocuments, setAddDocuments] = useState(false);
  const [editFieldId, setEditFieldId] = useState<string | null>(null);
  const [publishOpen, setPublishOpen] = useState(false);
  const [changeReason, setChangeReason] = useState("");
  const [relationshipOpen, setRelationshipOpen] = useState(false);
  const [childFormId, setChildFormId] = useState<string | undefined>(undefined);
  const [relationshipName, setRelationshipName] = useState("");
  const [maxChildren, setMaxChildren] = useState("50");

  const resetHistory = () => {
    undoStack.current = [];
    redoStack.current = [];
  };

  useEffect(() => {
    if (!detail.data || layout) {
      return;
    }
    resetHistory();
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
  const addFieldMutation = useMutation({ mutationFn: (request: Parameters<typeof api.forms.addField>[1]) => api.forms.addField(formDefinitionId, request).then((r) => r.result), onSuccess: async () => { setAddField(false); setAddDocuments(false); await invalidate(); } });
  const editField = useMutation({
    mutationFn: async ({ fieldId, submission }: { fieldId: string; submission: FieldEditSubmission }) => {
      const field = detail.data!.fields.find((candidate) => candidate.fieldDefinitionId === fieldId)!;
      if (submission.catalogChanged) {
        await api.forms.updateField(formDefinitionId, fieldId, { fieldDisplayName: submission.displayName, fieldDescription: submission.description, expectedFieldRevision: Number(field.fieldDefinitionRevision) });
      }
      const location = layout ? locateField(layout, fieldId) : null;
      if (layout && location && submission.element) {
        const next = updateElement(layout, location.sectionIndex, location.index, () => submission.element!);
        if (serializeLayout(next) !== serializeLayout(layout)) {
          return api.forms.saveDraft(formDefinitionId, { expectedDraftRevision: Number(detail.data?.draft?.draftRevision ?? 0), layoutJson: serializeLayout(next) }).then((r) => r.result);
        }
      }
      return null;
    },
    onSuccess: async (draft) => {
      if (draft) {
        const parsed = tryParseLayout(draft.layoutJson);
        if (parsed.ok) {
          setLayout(parsed.layout);
          setBaseJson(serializeLayout(parsed.layout));
        }
      }
      setEditFieldId(null);
      await invalidate();
    },
  });
  const strengthen = useMutation({
    mutationFn: ({ fieldId, request }: { fieldId: string; request: StrengthenFieldProtectionRequest }) => api.forms.strengthenProtection(formDefinitionId, fieldId, request).then((r) => r.result),
    onSuccess: async (_state, { fieldId }) => {
      await invalidate();
      await queryClient.invalidateQueries({ queryKey: ["forms", formDefinitionId, "protection", fieldId] });
    },
  });
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

  // Unsaved layout changes block accidental SPA navigation (and tab close) until the designer confirms (C1-UX §A/B).
  const dirtyRef = useRef(false);
  const blocker = useBlocker({ shouldBlockFn: () => dirtyRef.current, enableBeforeUnload: () => dirtyRef.current, withResolver: true });

  if (detail.isPending) {
    return <LoadingBlock label="Loading form" />;
  }
  if (detail.isError) {
    return <ErrorPanel error={detail.error} onRetry={() => void detail.refetch()} />;
  }
  const form = detail.data;
  const fields: RendererField[] = form.fields.map((field) => ({ fieldDefinitionId: field.fieldDefinitionId, fieldKey: field.fieldKey, fieldDisplayName: field.fieldDisplayName, fieldDataType: field.fieldDataType, options: field.options.map((option) => ({ ...option, optionSortOrder: Number(option.optionSortOrder) })) }));
  const dirty = layout !== null && serializeLayout(layout) !== baseJson;
  dirtyRef.current = dirty;
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
            <Button variant="secondary" disabled={undoStack.current.length === 0} onClick={undo} aria-label="Undo last layout change">
              Undo
            </Button>
            <Button variant="secondary" disabled={redoStack.current.length === 0} onClick={redo} aria-label="Redo last undone layout change">
              Redo
            </Button>
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
            content: layout ? (
              <DesignerCanvas
                layout={layout}
                onChange={setLayoutTracked}
                fields={fields}
                relationships={form.relationships.map((relationship) => ({ formRelationshipDefinitionId: relationship.formRelationshipDefinitionId, relationshipDisplayName: relationship.relationshipDisplayName, childFormDefinitionName: relationship.childFormDefinitionName }))}
                readOnly={!form.summary.canDesign}
                onOpenFullEditor={(fieldId) => {
                  editField.reset();
                  setEditFieldId(fieldId);
                }}
              />
            ) : (
              <LoadingBlock />
            ),
          },
          {
            value: "fields",
            label: `Fields (${form.fields.length})`,
            content: (
              <Card
                title="Field catalog"
                actions={
                  form.summary.canDesign ? (
                    <>
                      <Button size="sm" variant="secondary" onClick={() => setAddDocuments(true)}>
                        Add document collection
                      </Button>
                      <Button size="sm" onClick={() => setAddField(true)}>
                        Add field
                      </Button>
                    </>
                  ) : null
                }
              >
                <DataTable
                  caption="Fields"
                  rowKey={(row) => row.fieldDefinitionId}
                  rows={form.fields}
                  emptyMessage="No fields yet. Add the first field to start the layout."
                  columns={[
                    { key: "name", header: "Field", isRowHeader: true, render: (row) => row.fieldDisplayName },
                    { key: "key", header: "Key", render: (row) => <code>{row.fieldKey}</code> },
                    { key: "edit", header: "Actions", isActions: true, render: (row) => <Button size="sm" variant="secondary" aria-label={`${form.summary.canDesign ? "Edit" : "View"} ${row.fieldDisplayName}`} onClick={() => { editField.reset(); setEditFieldId(row.fieldDefinitionId); }}>{form.summary.canDesign ? "Edit" : "View"}</Button> },
                    { key: "type", header: "Type", render: (row) => row.fieldDataType },
                    { key: "protection", header: "Protection", render: (row) => (row.fieldProtectionMode === "ApplicationEncrypted" ? <Badge tone="success">{row.fieldClassification} · Encrypted</Badge> : <Badge tone="warning">{row.fieldClassification} · Plaintext</Badge>) },
                    { key: "options", header: "Options", render: (row) => (row.options.length === 0 ? "" : row.options.map((option) => `${option.optionDisplayText}${option.isActive ? "" : " (inactive)"}`).join(", ")) },
                    { key: "status", header: "Status", render: (row) => <Badge tone="neutral">{row.lifecycleStatus}</Badge> },
                  ]}
                />
              </Card>
            ),
          },
          {
            value: "access",
            label: "Form permissions",
            content: <FormAccessPanel formDefinitionId={formDefinitionId} canManage={form.summary.canDesign} />,
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
                      { key: "actions", header: "Actions", isActions: true, render: (row) => (form.summary.canDesign ? <Button size="sm" variant="secondary" busy={restore.isPending} onClick={() => restore.mutate(row.formVersionId)}>Restore to draft</Button> : null) },
                    ]}
                  />
                ) : null}
              </Card>
            ),
          },
        ]}
      />

      {editFieldId !== null && form.fields.some((candidate) => candidate.fieldDefinitionId === editFieldId) ? (
        <EditFieldDialog
          field={form.fields.find((candidate) => candidate.fieldDefinitionId === editFieldId)!}
          location={layout ? locateField(layout, editFieldId) : null}
          canEdit={form.summary.canDesign}
          pending={editField.isPending}
          error={editField.error}
          onSave={(submission) => editField.mutate({ fieldId: editFieldId, submission })}
          onClose={() => setEditFieldId(null)}
          formDefinitionId={formDefinitionId}
          onStrengthen={(request) => strengthen.mutate({ fieldId: editFieldId, request })}
          strengthenPending={strengthen.isPending}
          strengthenError={strengthen.error}
        />
      ) : null}
      <AddFieldDialog open={addField} onOpenChange={setAddField} pending={addFieldMutation.isPending} error={addFieldMutation.error} onSubmit={(request) => addFieldMutation.mutate(request)} />
      {addDocuments ? <AddFieldDialog kind="documents" open onOpenChange={setAddDocuments} pending={addFieldMutation.isPending} error={addFieldMutation.error} onSubmit={(request) => addFieldMutation.mutate(request)} /> : null}

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

      <Dialog
        open={blocker.status === "blocked"}
        onOpenChange={(open) => (open ? undefined : blocker.reset?.())}
        title="Discard unsaved layout changes?"
        description="This draft layout has changes that have not been saved. Leaving now discards them; the last saved draft (or published version) is unaffected."
        footer={
          <>
            <Button variant="secondary" onClick={() => blocker.reset?.()}>
              Stay and keep editing
            </Button>
            <Button variant="danger" onClick={() => blocker.proceed?.()}>
              Discard changes and leave
            </Button>
          </>
        }
      >
        <p className="text-sm text-muted">Use Save draft first if you want to keep this layout.</p>
      </Dialog>
    </>
  );
}
