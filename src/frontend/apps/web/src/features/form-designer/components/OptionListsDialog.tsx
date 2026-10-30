import type { OptionListResponse } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import { ActionIcons, Badge, Button, Checkbox, Dialog, LoadingBlock, Select, TextArea, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { ErrorPanel } from "../../support";
import { toKey } from "./AddFieldDialog";

interface ItemRow {
  optionListItemId: string | null;
  optionKey: string;
  label: string;
  isActive: boolean;
}

const rowsOf = (list: OptionListResponse): ItemRow[] =>
  [...list.items].sort((a, b) => Number(a.optionSortOrder) - Number(b.optionSortOrder)).map((item) => ({ optionListItemId: item.optionListItemId, optionKey: item.optionKey, label: item.optionDisplayText, isActive: item.isActive }));

/**
 * Shared option lists (Phase C2): reusable sets of dropdown choices. A list is copied into a field when the field is
 * created from it, so editing a list never changes a published form or a stored record; each field pulls list changes in
 * with “Sync from list”. Keys are permanent and items are deactivated, never deleted.
 */
export function OptionListsDialog({ open, onOpenChange, canDesign }: { open: boolean; onOpenChange: (open: boolean) => void; canDesign: boolean }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const lists = useQuery({ queryKey: ["option-lists"], queryFn: () => api.optionLists.list().then((r) => r.result), enabled: open });
  const [selectedId, setSelectedId] = useState<string>("");
  const selected = lists.data?.find((list) => list.optionListId === selectedId);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [rows, setRows] = useState<ItemRow[]>([]);
  const [newLabel, setNewLabel] = useState("");
  const [creating, setCreating] = useState(false);
  const [createName, setCreateName] = useState("");
  const [createItems, setCreateItems] = useState("");
  const signature = selected ? `${selected.optionListId}:${selected.optionListRevision}` : "";
  useEffect(() => {
    if (selected) {
      setName(selected.optionListName);
      setDescription(selected.optionListDescription);
      setRows(rowsOf(selected));
    }
    // Reset only when the stored data actually changes (signature), not on every render.
  }, [signature]);

  const refresh = () => queryClient.invalidateQueries({ queryKey: ["option-lists"] });
  const create = useMutation({
    mutationFn: () =>
      api.optionLists
        .create({ optionListName: createName.trim(), optionListDescription: null, items: createItems.split("\n").map((line) => line.trim()).filter(Boolean).map((line) => ({ optionKey: toKey(line), optionDisplayText: line })) })
        .then((r) => r.result),
    onSuccess: async (list) => {
      setCreating(false);
      setCreateName("");
      setCreateItems("");
      await refresh();
      setSelectedId(list.optionListId);
    },
  });
  const save = useMutation({
    mutationFn: () =>
      api.optionLists
        .update(selected!.optionListId, {
          expectedOptionListRevision: Number(selected!.optionListRevision),
          optionListName: name.trim(),
          optionListDescription: description.trim(),
          changes: rows.map((row, index) => ({ optionListItemId: row.optionListItemId, optionKey: row.optionKey, optionDisplayText: row.label.trim(), optionSortOrder: index, isActive: row.isActive })),
        })
        .then((r) => r.result),
    onSuccess: refresh,
  });

  const dirty = selected ? name !== selected.optionListName || description !== selected.optionListDescription || JSON.stringify(rows) !== JSON.stringify(rowsOf(selected)) : false;
  const move = (index: number, delta: number) =>
    setRows((current) => {
      const target = index + delta;
      if (target < 0 || target >= current.length) {
        return current;
      }
      const next = [...current];
      [next[index], next[target]] = [next[target]!, next[index]!];
      return next;
    });
  const addItem = () => {
    const base = toKey(newLabel);
    if (!newLabel.trim() || !base) {
      return;
    }
    let key = base;
    for (let suffix = 2; rows.some((row) => row.optionKey === key); suffix++) {
      key = `${base.slice(0, 60)}_${suffix}`;
    }
    setRows((current) => [...current, { optionListItemId: null, optionKey: key, label: newLabel.trim(), isActive: true }]);
    setNewLabel("");
  };

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title="Shared option lists"
      description="Reusable dropdown choices. A list is copied into a field when you create the field from it; later list edits reach a field only when you sync it, and published forms and stored records are never changed."
      footer={
        <Button variant="secondary" onClick={() => onOpenChange(false)}>
          Close
        </Button>
      }
    >
      {lists.isPending ? <LoadingBlock label="Loading option lists" /> : null}
      {lists.isError ? <ErrorPanel error={lists.error} onRetry={() => void lists.refetch()} /> : null}
      {lists.data ? (
        <div className="flex flex-col gap-4">
          <div>
            <div>
              <Select actions={canDesign ? <Button variant="secondary" icon={ActionIcons.add} onClick={() => { setCreating(true); setSelectedId(""); }}>New list…</Button> : undefined} label="Option list" value={selectedId || undefined} placeholder={lists.data.length === 0 ? "No lists yet" : "Choose a list"} options={lists.data.map((list) => ({ value: list.optionListId, label: `${list.optionListName} (${list.items.filter((item) => item.isActive).length} active${list.boundFieldCount ? `, used by ${list.boundFieldCount} field${list.boundFieldCount === 1 ? "" : "s"}` : ""})` }))} onValueChange={(value) => { setCreating(false); setSelectedId(value); }} />
            </div>
          </div>

          {creating ? (
            <section aria-label="New option list" className="flex flex-col gap-2 rounded-control border border-line bg-page p-3">
              <TextInput label="List name" value={createName} required maxLength={200} onChange={(event) => setCreateName(event.target.value.replace(/[<>]/g, ""))} />
              <TextArea label="Options (one per line)" value={createItems} rows={5} helpText="Keys are derived from the labels and then never change." onChange={(event) => setCreateItems(event.target.value.replace(/[<>]/g, ""))} />
              <div className="flex gap-2">
                <Button busy={create.isPending} disabled={createName.trim() === "" || createItems.trim() === ""} onClick={() => create.mutate()}>
                  Create list
                </Button>
                <Button variant="secondary" onClick={() => setCreating(false)}>
                  Cancel
                </Button>
              </div>
              {create.isError ? <ErrorPanel error={create.error} /> : null}
            </section>
          ) : null}

          {selected ? (
            <section aria-label={`Edit ${selected.optionListName}`} className="flex flex-col gap-3 rounded-control border border-line bg-page p-3">
              <div className="flex flex-wrap items-center gap-2">
                <Badge tone="neutral">Revision {String(selected.optionListRevision)}</Badge>
                <span className="text-xs text-muted">Fields that were created from this list keep their own copy until you sync them.</span>
              </div>
              <TextInput label="List name" value={name} maxLength={200} disabled={!canDesign} onChange={(event) => setName(event.target.value.replace(/[<>]/g, ""))} />
              <TextArea label="Description" value={description} rows={2} maxLength={1000} disabled={!canDesign} onChange={(event) => setDescription(event.target.value.replace(/[<>]/g, ""))} />
              <ol className="flex flex-col gap-2" aria-label="List items">
                {rows.map((row, index) => (
                  <li key={row.optionKey} className="flex flex-col gap-1 rounded-control border border-line bg-surface p-2">
                    <TextInput label={`Label of item ${index + 1}`} value={row.label} maxLength={200} disabled={!canDesign} onChange={(event) => setRows((current) => current.map((candidate, at) => (at === index ? { ...candidate, label: event.target.value.replace(/[<>]/g, "") } : candidate)))} />
                    <p className="text-xs text-muted">
                      Key <code>{row.optionKey}</code>
                      {row.optionListItemId === null ? " · not saved yet" : ""}
                    </p>
                    <div className="flex flex-wrap items-center gap-3">
                      <Checkbox label="Active" checked={row.isActive} disabled={!canDesign} onCheckedChange={(checked) => setRows((current) => current.map((candidate, at) => (at === index ? { ...candidate, isActive: checked } : candidate)))} />
                      {canDesign ? (
                        <>
                          <Button size="sm" variant="secondary" disabled={index === 0} aria-label={`Move ${row.label} earlier`} onClick={() => move(index, -1)}>
                            Move earlier
                          </Button>
                          <Button size="sm" variant="secondary" disabled={index === rows.length - 1} aria-label={`Move ${row.label} later`} onClick={() => move(index, 1)}>
                            Move later
                          </Button>
                        </>
                      ) : null}
                    </div>
                  </li>
                ))}
              </ol>
              {canDesign ? (
                <>
                  <TextInput label="New item" value={newLabel} maxLength={200} actions={<Button variant="secondary" icon={ActionIcons.add} disabled={newLabel.trim() === ""} onClick={addItem}>Add item</Button>} onChange={(event) => setNewLabel(event.target.value.replace(/[<>]/g, ""))} onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); addItem(); } }} />
                  <div className="flex flex-wrap gap-2">
                    <Button size="sm" busy={save.isPending} disabled={!dirty || name.trim() === "" || rows.some((row) => row.label.trim() === "")} onClick={() => save.mutate()}>
                      Save list
                    </Button>
                  </div>
                </>
              ) : null}
              {save.isError ? <ErrorPanel error={save.error} /> : null}
            </section>
          ) : null}
        </div>
      ) : null}
    </Dialog>
  );
}
