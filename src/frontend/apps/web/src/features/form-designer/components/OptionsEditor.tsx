import type { FieldDefinitionResponse } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import type { FieldElement } from "@enterpriseforms/form-renderer";
import { ActionIcons, Badge, Button, Checkbox, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { ErrorPanel } from "../../support";
import { toKey } from "./AddFieldDialog";

interface OptionRow {
  fieldOptionId: string | null;
  optionKey: string;
  label: string;
  isActive: boolean;
  fromList: boolean;
}

const fromServer = (field: FieldDefinitionResponse): OptionRow[] =>
  [...field.options]
    .sort((a, b) => Number(a.optionSortOrder) - Number(b.optionSortOrder) || a.optionKey.localeCompare(b.optionKey))
    .map((option) => ({ fieldOptionId: option.fieldOptionId, optionKey: option.optionKey, label: option.optionDisplayText, isActive: option.isActive, fromList: option.optionListItemId != null }));

/**
 * Option editor of the Field Properties inspector (Phase C2, priority 1): add, relabel, reorder and deactivate the
 * choices of a dropdown or multi-select, and choose the default selection. Options have permanent ids and keys that
 * are independent of their labels; nothing is ever deleted, so stored selections can never silently lose their option.
 * Option changes are saved to the field catalog explicitly (Save options); the default selection is part of the draft
 * layout like every other presentation setting.
 */
export function OptionsEditor({ formDefinitionId, field, element, canDesign, onElementChange }: { formDefinitionId: string; field: FieldDefinitionResponse; element: FieldElement; canDesign: boolean; onElementChange: (next: FieldElement) => void }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const multiple = field.optionSelectionMode === "Multiple";
  const serverRows = useMemo(() => fromServer(field), [field]);
  const signature = JSON.stringify(serverRows);
  const [rows, setRows] = useState<OptionRow[]>(serverRows);
  const [newLabel, setNewLabel] = useState("");
  const [problem, setProblem] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  useEffect(() => {
    setRows(serverRows);
    // Reset only when the stored data actually changes (signature), not on every render.
  }, [signature]);

  const dirty = JSON.stringify(rows) !== signature;
  const defaults = element.defaultOptionKeys ?? [];
  const invalidate = () => Promise.all([queryClient.invalidateQueries({ queryKey: ["forms", formDefinitionId] }), queryClient.invalidateQueries({ queryKey: ["option-lists"] })]);

  const save = useMutation({
    mutationFn: () =>
      api.forms
        .updateOptions(formDefinitionId, field.fieldDefinitionId, {
          changes: rows.map((row, index) => ({ fieldOptionId: row.fieldOptionId, optionKey: row.optionKey, optionDisplayText: row.label.trim(), optionSortOrder: index, isActive: row.isActive })),
        })
        .then((r) => r.result),
    onSuccess: async () => {
      // A default that was just deactivated can no longer be offered: drop it from the draft so publishing is not refused.
      const inactive = new Set(rows.filter((row) => !row.isActive).map((row) => row.optionKey));
      if (defaults.some((key) => inactive.has(key))) {
        setDefaults(defaults.filter((key) => !inactive.has(key)));
      }
      setSaved("Options saved. Reordering, deactivation and new options apply to the field at once; publish to carry added options and new labels into the data-entry form.");
      await invalidate();
    },
  });
  const sync = useMutation({
    mutationFn: () => api.forms.syncOptions(formDefinitionId, field.fieldDefinitionId).then((r) => r.result),
    onSuccess: async () => {
      setSaved("Synchronized with the shared option list.");
      await invalidate();
    },
  });

  function setDefaults(keys: string[]) {
    const next: FieldElement = { ...element };
    if (keys.length > 0) {
      next.defaultOptionKeys = keys;
    } else {
      delete next.defaultOptionKeys;
    }
    onElementChange(next);
  }

  function update(index: number, patch: Partial<OptionRow>) {
    setSaved(null);
    setRows((current) => current.map((row, at) => (at === index ? { ...row, ...patch } : row)));
  }

  function move(index: number, delta: number) {
    setSaved(null);
    setRows((current) => {
      const target = index + delta;
      if (target < 0 || target >= current.length) {
        return current;
      }
      const next = [...current];
      [next[index], next[target]] = [next[target]!, next[index]!];
      return next;
    });
  }

  function add() {
    const label = newLabel.trim();
    const base = toKey(label);
    if (!label || !base) {
      setProblem("Enter a label that contains letters or digits");
      return;
    }
    let key = base;
    for (let suffix = 2; rows.some((row) => row.optionKey === key); suffix++) {
      key = `${base.slice(0, 60)}_${suffix}`;
    }
    setProblem(null);
    setSaved(null);
    setRows((current) => [...current, { fieldOptionId: null, optionKey: key, label, isActive: true, fromList: false }]);
    setNewLabel("");
  }

  const toggleDefault = (key: string, checked: boolean) => {
    if (multiple) {
      setDefaults(checked ? [...defaults, key] : defaults.filter((candidate) => candidate !== key));
    } else {
      setDefaults(checked ? [key] : []);
    }
  };
  const emptyLabel = rows.some((row) => row.label.trim() === "");

  return (
    <section aria-label={`Options of ${field.fieldDisplayName}`} className="flex flex-col gap-3 rounded-control border border-line bg-page p-3">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-sm font-semibold">Options</h3>
        <Badge tone="neutral">{multiple ? "Multi-select" : "Choose one"}</Badge>
        {field.optionListId ? <Badge tone="neutral">Shared list</Badge> : null}
      </div>
      <p className="text-xs text-muted">Each option keeps a permanent key and id; its label can change without affecting stored records. Options are deactivated, never deleted: a record that holds a deactivated option still shows it, marked “no longer available”.</p>
      {field.optionListId ? (
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <span>Labels come from a shared option list.</span>
          {canDesign ? (
            <Button size="sm" variant="secondary" busy={sync.isPending} disabled={dirty} onClick={() => sync.mutate()}>
              Sync from list
            </Button>
          ) : null}
        </div>
      ) : null}
      {rows.length === 0 ? <p className="text-sm text-muted">No options yet.</p> : null}
      <ol className="flex flex-col gap-2" aria-label="Option list">
        {rows.map((row, index) => (
          <li key={row.optionKey} className="flex flex-col gap-1 rounded-control border border-line bg-surface p-2" data-option-key={row.optionKey}>
            <TextInput label={`Label of option ${index + 1}`} value={row.label} maxLength={200} disabled={!canDesign || row.fromList} error={row.label.trim() === "" ? "Enter a label" : undefined} onChange={(event) => update(index, { label: event.target.value.replace(/[<>]/g, "") })} />
            <p className="text-xs text-muted">
              Key <code>{row.optionKey}</code>
              {row.fromList ? " · label managed by the list" : ""}
              {row.fieldOptionId === null ? " · not saved yet" : ""}
            </p>
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
              <Checkbox label="Active" checked={row.isActive} disabled={!canDesign} onCheckedChange={(checked) => update(index, { isActive: checked })} />
              <Checkbox label="Default" checked={defaults.includes(row.optionKey)} disabled={!canDesign || !row.isActive || row.fieldOptionId === null || dirty} onCheckedChange={(checked) => toggleDefault(row.optionKey, checked)} />
            </div>
            {canDesign ? (
              <div className="flex flex-wrap gap-2" role="group" aria-label={`Order of ${row.label || row.optionKey}`}>
                <Button size="sm" variant="secondary" disabled={index === 0} aria-label={`Move ${row.label || row.optionKey} earlier`} onClick={() => move(index, -1)}>
                  Move earlier
                </Button>
                <Button size="sm" variant="secondary" disabled={index === rows.length - 1} aria-label={`Move ${row.label || row.optionKey} later`} onClick={() => move(index, 1)}>
                  Move later
                </Button>
              </div>
            ) : null}
          </li>
        ))}
      </ol>
      {canDesign ? (
        <div className="flex flex-col gap-2 border-t border-line pt-3">
          <TextInput label="New option" value={newLabel} maxLength={200} error={problem ?? undefined} helpText="Added to the end; its key is derived from the label and then never changes." actions={<Button variant="secondary" icon={ActionIcons.add} disabled={newLabel.trim() === ""} onClick={add}>Add option</Button>} onChange={(event) => setNewLabel(event.target.value.replace(/[<>]/g, ""))} onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); add(); } }} />
          <div className="flex flex-wrap gap-2">
            <Button size="sm" busy={save.isPending} disabled={!dirty || emptyLabel} onClick={() => save.mutate()}>
              Save options
            </Button>
            <Button size="sm" variant="ghost" disabled={!dirty || save.isPending} onClick={() => { setRows(serverRows); setSaved(null); }}>
              Discard option changes
            </Button>
          </div>
          {dirty ? <p className="text-xs text-muted">Unsaved option changes. Defaults can be set once options are saved.</p> : null}
        </div>
      ) : null}
      {defaults.length > 0 ? <p className="text-xs text-muted">The default selection pre-fills new records only; it never overwrites a saved value.</p> : null}
      {saved ? (
        <p role="status" className="text-sm">
          {saved}
        </p>
      ) : null}
      {save.isError ? <ErrorPanel error={save.error} /> : null}
      {sync.isError ? <ErrorPanel error={sync.error} /> : null}
      {rows.length > 0 && rows.every((row) => !row.isActive) ? <StatusMessage tone="warning" title="No active options">A dropdown with no active option cannot be required or filled in.</StatusMessage> : null}
    </section>
  );
}
