import { presentError, useApi } from "@enterpriseforms/app-runtime";
import type { RecordProjectionResponse } from "@enterpriseforms/api-client";
import { FormRenderer, displayValue, groupPayload, validateGroups, validateValues, type ErrorMap, type FormLayout, type GroupValueMap, type RecordGroupPayload, type RendererField, type ValueMap } from "@enterpriseforms/form-renderer";
import { Button, Dialog, LoadingBlock, PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useBlocker, useNavigate, useParams } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";
import { ErrorPanel } from "../../support";
import { isDirty, mergeEdits, type EditSnapshot, type FieldConflict } from "../components/editMerge";
import { layoutValues, parseProjectionLayout, toGroupInfos, toGroups, toRendererFields, toValueMap } from "../components/recordModel";

/** The version of the record this editing session is based on: layout, fields and values all come from it (C-31). */
interface EditBase {
  projection: RecordProjectionResponse;
  snapshot: EditSnapshot;
}

interface MergeReport {
  reapplied: number;
  conflicts: FieldConflict[];
  sectionConflicts: string[];
}

const baseOf = (projection: RecordProjectionResponse): EditBase => ({ projection, snapshot: { values: toValueMap(projection.record), groups: toGroups(projection.record) } });

/** Why a shared field is view only (Phase C5); never describes a field the viewer cannot see. */
const viewOnlyReasons: Record<string, string> = {
  ViewOnlyShare: "shared with you for viewing",
  ReadOnly: "read-only in this form",
  RuleDependency: "depends on parts of the record that are not shared with you",
  Restricted: "restricted information",
  MultiSelect: "a multiple-choice field",
  FileField: "a document field",
  NotInForm: "not part of this form",
  MultipleValues: "holds several values",
  ProtectionMigrationPending: "its protection is being updated",
  ContributorWritesDisabled: "changing shared fields is not enabled",
};

/**
 * PG-017 record editor. The save is a full snapshot checked against the revision this session started from (If-Match),
 * never against whatever was fetched later, so a background refresh can never turn into a silent overwrite. A conflict
 * offers to reapply the user's own changes onto the latest version, with fields both people changed resolved by hand.
 * Shared mode (Phase C5): a person with shared edit access sees only the fields shared with them; the ones they may
 * change are inputs, the others are marked read-only, and the save sends only the changed shared values. The server and
 * the Authority decide what may change; this page never relies on hidden controls.
 */
export function EditRecordPage() {
  const { formRecordId } = useParams({ from: "/app/records/$formRecordId/edit" });
  const api = useApi();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const projection = useQuery({ queryKey: ["records", "projection", formRecordId], queryFn: () => api.records.projection(formRecordId).then((r) => r.result) });
  const [base, setBase] = useState<EditBase | null>(null);
  const [values, setValues] = useState<ValueMap>({});
  const [groups, setGroups] = useState<GroupValueMap>({});
  const [errors, setErrors] = useState<ErrorMap>({});
  const [report, setReport] = useState<MergeReport | null>(null);
  const [revoked, setRevoked] = useState(false);
  // Fields the server named in its last validation failure (C-31); labelled at render time.
  const [serverFieldKeys, setServerFieldKeys] = useState<string[]>([]);
  const [reloading, setReloading] = useState(false);
  const [nothingToSave, setNothingToSave] = useState(false);
  const summaryRef = useRef<HTMLDivElement>(null);
  const formRef = useRef<HTMLFormElement>(null);
  const leaving = useRef(false);

  useEffect(() => {
    if (projection.data && base === null) {
      const initial = baseOf(projection.data);
      setBase(initial);
      setValues(initial.snapshot.values);
      setGroups(initial.snapshot.groups);
    }
  }, [projection.data, base]);

  const dirty = base !== null && !revoked && isDirty(base.snapshot, { values, groups });
  const dirtyRef = useRef(false);
  dirtyRef.current = dirty;
  // Unsaved changes block in-app navigation (with a confirmation) and closing or reloading the tab. Signing out is not
  // blocked: the session has already ended, so the changes could not be saved anyway (and are discarded with the page).
  const blocker = useBlocker({ shouldBlockFn: ({ next }) => dirtyRef.current && !leaving.current && next.pathname !== "/login", enableBeforeUnload: () => dirtyRef.current && !leaving.current, withResolver: true });

  const focusSummary = () => requestAnimationFrame(() => summaryRef.current?.focus());

  const update = useMutation({
    // Every repeating section is sent as its complete entry list (stable ids kept, new entries without one, removed ones omitted).
    mutationFn: (payload: { revision: number; values: Record<string, string>; groups: RecordGroupPayload[] }) => api.records.update(formRecordId, { expectedContentRevision: payload.revision, values: payload.values, groups: payload.groups }).then((r) => r.result),
    onSuccess: async () => {
      leaving.current = true;
      await queryClient.invalidateQueries({ queryKey: ["records"] });
      await navigate({ to: "/app/records/$formRecordId", params: { formRecordId }, search: { saved: true } });
    },
    onError: (error) => {
      const presented = presentError(error);
      // A 403 that names fields (for example a read-only field) is a validation answer, not lost access; so is "editing
      // shared fields is not enabled", which is shown as an error rather than clearing the page.
      if ((presented.kind === "access-denied" || presented.kind === "not-found") && presented.fieldKeys.length === 0 && presented.code !== "CONTRIBUTOR_WRITES_DISABLED") {
        // Access was removed while editing: stop, and drop the local changes (they can no longer be saved or shown).
        setRevoked(true);
        setValues({});
        setGroups({});
        return;
      }
      setServerFieldKeys(presented.fieldKeys);
      if (presented.fieldKeys.length > 0) {
        focusSummary();
      }
    },
  });

  if (projection.isError && base === null) {
    return <ErrorPanel error={projection.error} onRetry={() => void projection.refetch()} />;
  }
  if (base === null) {
    return <LoadingBlock label="Loading record" />;
  }
  const data = base.projection;
  if (revoked) {
    // Access changed during editing: the form (which holds content this user may no longer see) is removed entirely.
    return (
      <StatusMessage tone="danger" title="You can no longer edit this record" actions={<Link to="/app/records/$formRecordId" params={{ formRecordId }}><Button size="sm" variant="secondary">Back to the record</Button></Link>}>
        Your access changed while you were editing, so your changes were not saved and have been cleared from this page.
      </StatusMessage>
    );
  }
  const shared = !data.permissions.isCompleteProjection;
  if (!data.permissions.canEdit && !(shared && data.permissions.canEditSharedFields)) {
    return (
      <StatusMessage tone="danger" title="You cannot edit this record" actions={<Link to="/app/records/$formRecordId" params={{ formRecordId }}><Button variant="secondary">Back to the record</Button></Link>}>
        {!shared
          ? "Only the record owner (or a member of the owning team) can edit it."
          : data.permissions.contributorWritesEnabled
            ? "None of the fields shared with you can be changed with your access."
            : "Changing shared fields is not enabled. You can still view the fields shared with you."}
      </StatusMessage>
    );
  }
  const { layout: parsedLayout, error } = parseProjectionLayout(data);
  if (!parsedLayout) {
    return <StatusMessage tone="danger" title="Layout could not be parsed">{error}</StatusMessage>;
  }
  const fields = toRendererFields(data.fields);
  // Shared mode: fields the person may not change are rendered read-only (the server refuses them regardless).
  const access = shared ? (data.sharedFieldAccess ?? []) : [];
  const editableKeys = new Set(access.filter((item) => item.editable).map((item) => item.fieldKey));
  const viewOnly = access.filter((item) => !item.editable);
  const keyById = new Map(fields.map((field) => [field.fieldDefinitionId, field.fieldKey]));
  const layout: FormLayout = shared
    ? { ...parsedLayout, sections: parsedLayout.sections.map((section) => ({ ...section, elements: section.elements.map((element) => (element.kind === "field" && !editableKeys.has(keyById.get(element.fieldDefinitionId) ?? "") ? { ...element, readOnly: true } : element)) })) }
    : parsedLayout;
  const presented = update.isError ? presentError(update.error) : null;
  const conflict = presented?.kind === "conflict";
  const applyRules = data.permissions.isCompleteProjection;
  const infos = toGroupInfos(data.record.groups);
  const unresolved = report?.conflicts ?? [];

  const submit = () => {
    setNothingToSave(false);
    if (unresolved.length > 0) {
      focusSummary();
      return;
    }
    // Validation first: an unchanged record can still hold a value that is no longer valid (for example an option the
    // organization deactivated), and the user must see that rather than "nothing to save".
    const validation = { ...validateValues(layout, fields, values, "edit", { applyRules }), ...validateGroups(layout, fields, values, groups, infos, "edit", applyRules) };
    setErrors(validation);
    setServerFieldKeys([]);
    if (Object.keys(validation).length > 0) {
      focusSummary();
      return;
    }
    if (!dirty) {
      // An unchanged snapshot would still become a new revision; there is nothing to save.
      setNothingToSave(true);
      return;
    }
    if (shared) {
      // Only the changed values the person may change are sent; repeating sections are never part of a share.
      const changed = Object.fromEntries([...editableKeys].filter((key) => (values[key] ?? "") !== (base.snapshot.values[key] ?? "")).map((key) => [key, values[key] ?? ""]));
      if (Object.keys(changed).length === 0) {
        setNothingToSave(true);
        return;
      }
      update.mutate({ revision: Number(data.record.contentRevision), values: changed, groups: [] });
      return;
    }
    update.mutate({ revision: Number(data.record.contentRevision), values: layoutValues(layout, fields, values, base.snapshot.values, applyRules), groups: groupPayload(layout, fields, values, groups, infos, applyRules) });
  };

  const loadLatest = async (keepMine: boolean) => {
    setReloading(true);
    try {
      const latest = await projection.refetch();
      if (!latest.data) {
        return;
      }
      const next = baseOf(latest.data);
      if (keepMine) {
        const merged = mergeEdits(base.snapshot, { values, groups }, next.snapshot);
        setValues(merged.values);
        setGroups(merged.groups);
        setReport({ reapplied: merged.reapplied.length, conflicts: merged.conflicts, sectionConflicts: merged.sectionConflicts });
      } else {
        setValues(next.snapshot.values);
        setGroups(next.snapshot.groups);
        setReport(null);
      }
      setBase(next);
      setErrors({});
      setServerFieldKeys([]);
      update.reset();
    } finally {
      setReloading(false);
    }
  };

  const resolve = (conflictItem: FieldConflict, choice: "mine" | "theirs") => {
    setValues((current) => ({ ...current, [conflictItem.fieldKey]: choice === "mine" ? conflictItem.mine : conflictItem.theirs }));
    setReport((current) => (current ? { ...current, conflicts: current.conflicts.filter((item) => item.fieldKey !== conflictItem.fieldKey) } : current));
  };

  const focusField = (key: string) => {
    const root = formRef.current;
    const cell = root?.querySelector<HTMLElement>(`[data-field-key="${CSS.escape(key)}"]`) ?? root?.querySelector<HTMLElement>(`[data-repeating-section="${CSS.escape(key)}"]`);
    const control = cell?.querySelector<HTMLElement>("input:not([type=hidden]):not([disabled]),textarea:not([disabled]),select:not([disabled]),[role=combobox],button:not([disabled])");
    cell?.scrollIntoView({ block: "center" });
    control?.focus();
  };

  const shownErrors: ErrorMap = { ...errors };
  for (const key of update.isError ? serverFieldKeys : []) {
    shownErrors[key] = `${labelForKey(layout, fields, key)}: ${presented?.title ?? "not accepted"}`;
  }
  const errorEntries = Object.entries(shownErrors).filter(([, message]) => Boolean(message));
  return (
    <>
      <PageHeader title={shared ? `Edit shared fields — ${data.formDefinitionName}` : `Edit ${data.formDefinitionName}`} lead={`Editing content revision ${String(data.record.contentRevision)} with layout v${data.layoutVersion.versionNumber}.`} />
      {shared ? (
        <div className="mb-4">
          <StatusMessage tone="info" title={`You can change ${editableKeys.size} shared ${editableKeys.size === 1 ? "field" : "fields"}`}>
            <p>Other parts of this record are not shown. Fields marked Read-only can be viewed but not changed with your access. A shared value can be changed but not cleared.</p>
            {viewOnly.length > 0 ? (
              <ul className="mt-2 flex list-disc flex-col gap-1 pl-5" aria-label="Fields you can view but not change">
                {viewOnly.map((item) => (
                  <li key={item.fieldKey}>
                    {labelForKey(layout, fields, item.fieldKey)}: {viewOnlyReasons[item.notEditableReason ?? ""] ?? "not changeable with your access"}
                  </li>
                ))}
              </ul>
            ) : null}
          </StatusMessage>
        </div>
      ) : null}
      <form
        ref={formRef}
        noValidate
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <div ref={summaryRef} tabIndex={-1} className="flex flex-col gap-3 outline-none">
          {conflict ? (
            <StatusMessage
              tone="warning"
              title="Someone else saved this record while you were editing"
              actions={
                <>
                  <Button size="sm" busy={reloading} onClick={() => void loadLatest(true)}>
                    Review and reapply my changes
                  </Button>
                  <Button size="sm" variant="secondary" busy={reloading} onClick={() => void loadLatest(false)}>
                    Discard my changes and load the latest version
                  </Button>
                </>
              }
            >
              Your changes were not saved, so nothing they saved was overwritten. You can reapply your changes to the latest version and review them before saving again.
            </StatusMessage>
          ) : presented && presented.fieldKeys.length === 0 ? (
            <ErrorPanel error={update.error} />
          ) : null}
          {report ? (
            <StatusMessage tone={unresolved.length > 0 || report.sectionConflicts.length > 0 ? "warning" : "info"} title={unresolved.length > 0 ? `Choose a value for ${unresolved.length} ${unresolved.length === 1 ? "field" : "fields"} you both changed` : "Your changes were reapplied to the latest version"}>
              <p>
                {report.reapplied === 0 ? "None of your changes needed reapplying." : report.reapplied === 1 ? "Your change was carried onto the latest version." : `${report.reapplied} of your changes were carried onto the latest version.`} Review the form, then save.
              </p>
              {report.sectionConflicts.length > 0 ? <p className="mt-1">These sections were changed by both of you; they show the latest saved version and your changes there were not reapplied: {report.sectionConflicts.map((key) => sectionTitle(layout, infos, key)).join(", ")}.</p> : null}
              {unresolved.length > 0 ? (
                <ul className="mt-2 flex flex-col gap-2">
                  {unresolved.map((item) => (
                    <li key={item.fieldKey} className="rounded border border-line bg-surface p-2">
                      <p className="font-medium">{labelForKey(layout, fields, item.fieldKey)}</p>
                      <p>Your value: {shown(layout, fields, item.fieldKey, item.mine)}</p>
                      <p>Their saved value: {shown(layout, fields, item.fieldKey, item.theirs)}</p>
                      <div className="mt-1 flex flex-wrap gap-2">
                        <Button size="sm" variant="secondary" onClick={() => resolve(item, "mine")}>
                          Keep my value
                        </Button>
                        <Button size="sm" variant="secondary" onClick={() => resolve(item, "theirs")}>
                          Use their value
                        </Button>
                      </div>
                    </li>
                  ))}
                </ul>
              ) : null}
            </StatusMessage>
          ) : null}
          {nothingToSave && !dirty ? <StatusMessage tone="info" title="There are no changes to save" /> : null}
          {errorEntries.length > 0 ? (
            <StatusMessage tone="danger" title={`${errorEntries.length === 1 ? "1 problem needs" : `${errorEntries.length} problems need`} your attention before saving`}>
              <ul className="flex flex-col gap-1">
                {errorEntries.map(([key, message]) => (
                  <li key={key}>
                    <button type="button" className="text-left underline underline-offset-4" onClick={() => focusField(key)}>
                      {message}
                    </button>
                  </li>
                ))}
              </ul>
            </StatusMessage>
          ) : null}
        </div>
        <FormRenderer
          layout={layout}
          fields={fields}
          values={values}
          errors={shownErrors}
          applyRules={applyRules}
          groups={groups}
          groupInfos={infos}
          onGroupsChange={setGroups}
          onChange={(key, value) => setValues((current) => ({ ...current, [key]: value }))}
          renderRelatedList={(element) => <p className="text-sm text-muted">{element.label || "Related records"} are managed from the record page (Related records).</p>}
          renderDocumentCollection={(element) => <p className="text-sm text-muted">{element.label || "Documents"} are managed from the record page (Documents).</p>}
        />
        <div className="flex flex-wrap items-center gap-2">
          <Button type="submit" busy={update.isPending} disabled={conflict}>
            Save changes
          </Button>
          <Button
            variant="secondary"
            disabled={!dirty || update.isPending}
            onClick={() => {
              setValues(base.snapshot.values);
              setGroups(base.snapshot.groups);
              setErrors({});
              setServerFieldKeys([]);
              setReport(null);
              update.reset();
            }}
          >
            Discard changes
          </Button>
          <Link to="/app/records/$formRecordId" params={{ formRecordId }}>
            <Button variant="ghost">Cancel</Button>
          </Link>
          <span className="text-sm text-muted" aria-live="polite">
            {dirty ? "You have unsaved changes." : "No unsaved changes."}
          </span>
        </div>
      </form>
      <Dialog
        open={blocker.status === "blocked"}
        onOpenChange={(open) => (open ? undefined : blocker.reset?.())}
        title="Leave without saving?"
        description="You have unsaved changes to this record. If you leave now, they will be lost."
        footer={
          <>
            <Button variant="secondary" onClick={() => blocker.reset?.()}>
              Stay on this page
            </Button>
            <Button variant="danger" onClick={() => blocker.proceed?.()}>
              Leave without saving
            </Button>
          </>
        }
      >
        {null}
      </Dialog>
    </>
  );
}

function elementFor(layout: FormLayout, fields: readonly RendererField[], fieldKey: string) {
  const field = fields.find((candidate) => candidate.fieldKey === fieldKey);
  const element = field ? layout.sections.flatMap((section) => section.elements).find((candidate) => candidate.kind === "field" && candidate.fieldDefinitionId === field.fieldDefinitionId) : undefined;
  return { field, element: element?.kind === "field" ? element : undefined };
}

/** Human label for an error-map key: a field key, or `groupKey[n].fieldKey` for entry n of a repeating section. */
function labelForKey(layout: FormLayout, fields: readonly RendererField[], key: string): string {
  const match = /^(.+)\[(\d+)\]\.(.+)$/.exec(key);
  const fieldKey = match ? match[3]! : key;
  const { field, element } = elementFor(layout, fields, fieldKey);
  const label = element?.label || field?.fieldDisplayName || fieldKey;
  return match ? `${label} (entry ${match[2]})` : label;
}

function shown(layout: FormLayout, fields: readonly RendererField[], fieldKey: string, value: string): string {
  const { field, element } = elementFor(layout, fields, fieldKey);
  const text = field ? displayValue(field, value, element) : value;
  return text === "" ? "(empty)" : text;
}

function sectionTitle(layout: FormLayout, infos: ReadonlyArray<{ groupDefinitionId: string; groupKey: string }>, groupKey: string): string {
  const groupId = infos.find((info) => info.groupKey === groupKey)?.groupDefinitionId;
  return layout.sections.find((section) => section.repeat?.groupDefinitionId === groupId)?.title ?? groupKey;
}
