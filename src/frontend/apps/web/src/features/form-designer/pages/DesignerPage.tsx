import type { FieldDefinitionResponse, StrengthenFieldProtectionRequest } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import { DesignerCanvas, columnSlotFor, duplicateSection, elementKey, findElement, findOptionFilterCycle, findRuleCycle, insertElement, insertSection, locateField, moveSection, newSectionKey, placedIds, removeElement, setSectionRepeat, updateElement, type DesignerSelection } from "@enterpriseforms/form-designer";
import { FormRenderer, describeCondition, emptyLayout, initialValues, serializeLayout, tryParseLayout, type Breakpoint, type FieldElement, type FormLayout, type RendererField } from "@enterpriseforms/form-renderer";
import { ActionIcons, Badge, Button, Card, DataTable, Dialog, FieldLayoutProvider, LoadingBlock, menuContentClass, menuItemClass, RowActions, Select, SideNavTabs, StatusMessage, TextInput } from "@enterpriseforms/ui";
import * as DropdownMenu from "@radix-ui/react-dropdown-menu";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useBlocker, useParams } from "@tanstack/react-router";
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { ErrorPanel } from "../../support";
import { AddFieldDialog, toKey } from "../components/AddFieldDialog";
import { DesignerInspector, newElementFor, type DesignerInspectorProps, type PlacementTarget } from "../components/DesignerInspector";
import { EditFieldDialog, type FieldEditSubmission } from "../components/EditFieldDialog";
import { FieldConfigModal } from "../components/FieldConfigModal";
import { OptionListsDialog } from "../components/OptionListsDialog";
import { FormAccessPanel } from "../components/FormAccessPanel";
import { RemoveSectionDialog } from "../components/RemoveSectionDialog";

type View = "design" | "fields" | "rules" | "preview" | "versions" | "settings" | "access" | "relationships";

/**
 * PG-033/037 Form Designer (C1 designer rebuild, docs/ui/c1-designer-rebuild): a left designer navigation, a centre
 * toolbar + canvas of titled sections with compact field previews, and a persistent right inspector. "+ Add Section"
 * creates a titled section (an existing layout-v2 `section`, not a line-break flag); "+ Add Row" adds an empty,
 * full-width cell at the end of the currently selected section so the next field placed there starts a fresh row
 * (usability corrections round: the old "+ Add Row" button actually created a section, which was wrong). Fields are
 * placed into exact cells, moved between sections and edited in the inspector. Save draft uses the draft revision
 * CAS, Publish the draft + current version CAS; the server validates every layout again and published versions stay
 * immutable.
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
  // Undo/redo over the draft layout: `setLayoutTracked` is the single mutation path for the canvas and inspector, so
  // every layout change — sections, placements, moves, spans, labels and validation — is undoable.
  const undoStack = useRef<FormLayout[]>([]);
  const redoStack = useRef<FormLayout[]>([]);
  const [, setHistoryTick] = useState(0);
  const setLayoutTracked = useCallback((next: FormLayout | ((current: FormLayout | null) => FormLayout | null)) => {
    setLayout((current) => {
      const resolved = typeof next === "function" ? (next as (c: FormLayout | null) => FormLayout | null)(current) : next;
      if (current && resolved && serializeLayout(resolved) !== serializeLayout(current)) {
        undoStack.current = [...undoStack.current, current].slice(-100);
        redoStack.current = [];
        setHistoryTick((tick) => tick + 1);
      }
      return resolved;
    });
  }, []);
  const undo = () => {
    const previous = undoStack.current.at(-1);
    if (!previous || !layout) {
      return;
    }
    undoStack.current = undoStack.current.slice(0, -1);
    redoStack.current = [...redoStack.current, layout];
    setLayout(previous);
    setHistoryTick((tick) => tick + 1);
    announce("Undone");
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
    announce("Redone");
  };

  const [view, setView] = useState<View>("design");
  const [selection, setSelection] = useState<DesignerSelection>(null);
  const [extraSlots, setExtraSlots] = useState<Record<string, Array<{ id: string; index: number; startOnNewRow?: boolean }>>>({});
  // Stable per-slot ids (Phase C3 usability corrections): several empty cells at the same insertIndex — most notably
  // the four independent columns "+ Add Row" creates — are otherwise indistinguishable, which was the root cause of
  // clicking one column selecting/highlighting all of them.
  const nextSlotIdRef = useRef(0);
  const nextSlotId = () => `slot-${nextSlotIdRef.current++}`;
  const [collapsed, setCollapsed] = useState<Set<string>>(() => readCollapsed(formDefinitionId));
  const [renamingSectionKey, setRenamingSectionKey] = useState<string | null>(null);
  const [pane, setPane] = useState<"canvas" | "properties">("canvas");
  const [removeSectionIndex, setRemoveSectionIndex] = useState<number | null>(null);
  const [createAt, setCreateAt] = useState<{ kind: "field" | "documents"; target: PlacementTarget | null } | null>(null);
  const [editFieldId, setEditFieldId] = useState<string | null>(null);
  const [optionListsOpen, setOptionListsOpen] = useState(false);
  // Phase C3 designer UX refinement: the quick inspector has no tabs of its own any more; "Configure Field" and the
  // Rules overview both open the full workspace in FieldConfigModal, landing on the tab named here.
  const [configTarget, setConfigTarget] = useState<{ key: string; tab: string } | null>(null);
  const [publishOpen, setPublishOpen] = useState(false);
  const [changeReason, setChangeReason] = useState("");
  const [relationshipOpen, setRelationshipOpen] = useState(false);
  const [childFormId, setChildFormId] = useState<string | undefined>(undefined);
  const [relationshipName, setRelationshipName] = useState("");
  const [maxChildren, setMaxChildren] = useState("50");
  const [announcement, setAnnouncement] = useState("");
  const [notice, setNotice] = useState<string | null>(null);
  const announce = useCallback((message: string) => setAnnouncement((current) => (current === message ? `${message} ` : message)), []);

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
      setLayout(emptyLayout(detail.data.summary.formDefinitionName));
      setBaseJson("");
      return;
    }
    // Layouts written before sections were editable already carry one section (the form-title section created with the
    // form); they are read unchanged — no migration rewrites a stored or published layout.
    const parsed = tryParseLayout(source);
    if (parsed.ok) {
      setLayout(parsed.layout);
      setBaseJson(serializeLayout(parsed.layout));
    } else {
      setParseError(parsed.error);
    }
  }, [detail.data, layout]);

  useEffect(() => {
    try {
      window.localStorage.setItem(`ef.designer.collapsed.${formDefinitionId}`, JSON.stringify([...collapsed]));
    } catch {
      // Per-viewer convenience only; the designer works without storage.
    }
  }, [collapsed, formDefinitionId]);

  const invalidate = () => Promise.all([queryClient.invalidateQueries({ queryKey: ["forms", formDefinitionId] }), queryClient.invalidateQueries({ queryKey: ["forms"] })]);
  const consumeSlot = (target: PlacementTarget) =>
    setExtraSlots((current) => {
      const slots = current[target.sectionKey];
      if (!slots) {
        return current;
      }
      // Remove exactly the slot that was filled (by its stable id, when the placement came from one — e.g. a
      // specific column of a "+ Add Row" row); fall back to the first slot at that position otherwise. Every other
      // pending slot at or after this position shifts by one, since the array of elements just grew.
      const at = target.slotId && target.slotId !== "baseline" ? slots.findIndex((slot) => slot.id === target.slotId) : slots.findIndex((slot) => slot.index === target.insertIndex);
      const remaining = (at >= 0 ? slots.filter((_, index) => index !== at) : slots).map((slot) => (slot.index >= target.insertIndex ? { ...slot, index: slot.index + 1 } : slot));
      return { ...current, [target.sectionKey]: remaining };
    });
  const addFieldMutation = useMutation({
    mutationFn: (request: Parameters<typeof api.forms.addField>[1]) => api.forms.addField(formDefinitionId, request).then((r) => r.result),
    onSuccess: async (field) => {
      const target = createAt?.target ?? null;
      setCreateAt(null);
      if (target) {
        const element = newElementFor(field, 1);
        setLayoutTracked((current) => {
          if (!current) {
            return current;
          }
          const sectionIndex = current.sections.findIndex((section) => section.sectionKey === target.sectionKey);
          return sectionIndex >= 0 ? insertElement(current, sectionIndex, target.insertIndex, element) : current;
        });
        consumeSlot(target);
        setSelection({ kind: "element", key: elementKey(element) });
        setView("design");
        announce(`${field.fieldDisplayName} created and placed`);
      }
      await invalidate();
    },
  });
  // "Allow multiple entries": the stable group is created on the server (or an unplaced one reused), then placed in the empty section.
  const enableRepeat = useMutation({
    mutationFn: async ({ sectionIndex, existingGroupDefinitionId }: { sectionIndex: number; existingGroupDefinitionId: string | null }) => {
      if (existingGroupDefinitionId) {
        return { sectionIndex, groupDefinitionId: existingGroupDefinitionId };
      }
      const section = layout!.sections[sectionIndex]!;
      const title = section.title?.trim() || "Entries";
      const existingKeys = new Set((detail.data?.groups ?? []).map((group) => group.groupKey));
      let groupKey = toKey(title) || "entries";
      for (let n = 2; existingKeys.has(groupKey); n += 1) {
        groupKey = `${toKey(title) || "entries"}_${n}`.slice(0, 64);
      }
      const group = await api.forms.createGroup(formDefinitionId, { groupKey, groupDisplayName: title.slice(0, 200) }).then((r) => r.result);
      return { sectionIndex, groupDefinitionId: group.groupDefinitionId };
    },
    onSuccess: async ({ sectionIndex, groupDefinitionId }) => {
      setLayoutTracked((current) => (current ? setSectionRepeat(current, sectionIndex, { groupDefinitionId, minOccurrences: 0, maxOccurrences: 10, allowAdd: true, allowRemove: true, allowReorder: true }) : current));
      announce("The section now allows multiple entries. Create its fields with the Add Field cell.");
      await invalidate();
    },
  });
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
      announce(`Draft saved (revision ${draft.draftRevision})`);
      await invalidate();
    },
  });
  const publish = useMutation({
    mutationFn: () => api.forms.publish(formDefinitionId, { expectedDraftRevision: Number(detail.data?.draft?.draftRevision ?? 0), expectedCurrentVersionNumber: Number(detail.data?.summary.currentVersionNumber ?? 0), changeReason: changeReason.trim() }).then((r) => r.result),
    onSuccess: async () => {
      setPublishOpen(false);
      setChangeReason("");
      setLayout(null);
      setSelection(null);
      await invalidate();
      await queryClient.invalidateQueries({ queryKey: ["forms", formDefinitionId, "versions"] });
    },
  });
  const restore = useMutation({
    mutationFn: (fromFormVersionId: string) => api.forms.restoreDraft(formDefinitionId, { fromFormVersionId }).then((r) => r.result),
    onSuccess: async () => {
      setLayout(null);
      setSelection(null);
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

  // Unsaved layout changes block accidental SPA navigation (and tab close) until the designer confirms.
  const dirtyRef = useRef(false);
  const blocker = useBlocker({ shouldBlockFn: () => dirtyRef.current, enableBeforeUnload: () => dirtyRef.current, withResolver: true });

  // Ctrl/Cmd+Z and Ctrl/Cmd+Shift+Z / Ctrl+Y on the Design view, outside text inputs (which keep native undo); Escape
  // moves selection to its immediate parent while focus is on the canvas, outside any dialog/menu/listbox (which
  // keep their own Escape-to-close behavior). `shortcuts.current` is refreshed every render (below, once
  // `parentOfSelection` exists) so the listener — created once per `view` — never reads a stale `selection`/`layout`.
  const shortcuts = useRef({ undo, redo, parentOfSelection: (): DesignerSelection => null });
  useEffect(() => {
    if (view !== "design") {
      return;
    }
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      if (event.key === "Escape" && !event.ctrlKey && !event.metaKey && target?.closest('[aria-label="Form canvas"]') && !target?.closest('[role="dialog"],[role="menu"],[role="listbox"],[data-radix-popper-content-wrapper]')) {
        const parent = shortcuts.current.parentOfSelection();
        if (parent) {
          event.preventDefault();
          setSelection(parent);
        }
        return;
      }
      if (!(event.ctrlKey || event.metaKey) || target?.closest("input,textarea,select,[contenteditable=true],[role=dialog]")) {
        return;
      }
      const key = event.key.toLowerCase();
      if (key === "z" && !event.shiftKey) {
        event.preventDefault();
        shortcuts.current.undo();
      } else if ((key === "z" && event.shiftKey) || key === "y") {
        event.preventDefault();
        shortcuts.current.redo();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [view]);

  // Phase C3 designer UX refinement: the header + toolbar pin to the top of the page (one scroll surface for the
  // whole designer, not a second one inside it); the nav list and quick inspector stick just below it. The offset is
  // measured, not guessed, so it stays correct however the header wraps (long form names, narrow widths, 400% zoom).
  // A callback ref (not a plain useRef read inside a `[]`-deps effect) because the pinned bar's DOM node does not
  // exist on first mount: the component still returns the loading/error state below until `detail` resolves, so a
  // one-time mount effect would see `pinnedRef.current === null`, skip creating the observer, and never run again —
  // leaving every consumer of `pinnedHeight` (the side nav, the quick inspector) stuck at the initial offset forever.
  const [pinnedNode, setPinnedNode] = useState<HTMLDivElement | null>(null);
  const [pinnedHeight, setPinnedHeight] = useState(0);
  useEffect(() => {
    if (!pinnedNode || typeof ResizeObserver === "undefined") {
      return;
    }
    const observer = new ResizeObserver((entries) => setPinnedHeight(entries[0]?.contentRect.height ?? 0));
    observer.observe(pinnedNode);
    return () => observer.disconnect();
  }, [pinnedNode]);

  if (detail.isPending) {
    return <LoadingBlock label="Loading form" />;
  }
  if (detail.isError) {
    return <ErrorPanel error={detail.error} onRetry={() => void detail.refetch()} />;
  }
  const form = detail.data;
  const canDesign = form.summary.canDesign;
  const rendererFields: RendererField[] = form.fields.map((field) => ({ fieldDefinitionId: field.fieldDefinitionId, fieldKey: field.fieldKey, fieldDisplayName: field.fieldDisplayName, fieldDataType: field.fieldDataType, optionSelectionMode: field.optionSelectionMode, options: field.options.map((option) => ({ ...option, optionSortOrder: Number(option.optionSortOrder) })), applicationEncrypted: field.fieldProtectionMode === "ApplicationEncrypted", groupDefinitionId: field.groupDefinitionId ?? null }));
  const relationships = form.relationships.map((relationship) => ({ formRelationshipDefinitionId: relationship.formRelationshipDefinitionId, relationshipDisplayName: relationship.relationshipDisplayName, childFormDefinitionName: relationship.childFormDefinitionName }));
  const dirty = layout !== null && serializeLayout(layout) !== baseJson;
  dirtyRef.current = dirty;
  const canPublish = form.draft !== null && !dirty;
  const childCandidates = (forms.data ?? []).filter((candidate) => candidate.formDefinitionId !== formDefinitionId && candidate.currentVersionNumber !== null);

  const sectionOfSelection = (): number | null => {
    if (!layout || !selection) {
      return null;
    }
    if (selection.kind === "element") {
      return findElement(layout, selection.key)?.sectionIndex ?? null;
    }
    const index = layout.sections.findIndex((section) => section.sectionKey === selection.sectionKey);
    return index >= 0 ? index : null;
  };
  // Escape moves selection to its immediate parent (a field or empty column to its containing section); a section
  // has no higher parent to move to (Phase C3 usability corrections).
  const parentOfSelection = (): DesignerSelection => {
    if (!layout || !selection || selection.kind === "section") {
      return null;
    }
    const index = sectionOfSelection();
    return index !== null ? { kind: "section", sectionKey: layout.sections[index]!.sectionKey } : null;
  };
  shortcuts.current = { undo, redo, parentOfSelection };
  // Add Section creates a new titled section (what "+ Add Row" used to do; the real "Add Row" below adds a row
  // inside the selected section instead).
  const addSection = () => {
    if (!layout) {
      return;
    }
    if (layout.sections.length >= 50) {
      setNotice("A form can have at most 50 sections.");
      return;
    }
    const key = newSectionKey(layout);
    setLayoutTracked(insertSection(layout, sectionOfSelection(), key, "New section"));
    setView("design");
    setPane("canvas");
    setSelection({ kind: "section", sectionKey: key });
    setRenamingSectionKey(key);
    setNotice(null);
    announce("New section added. Type its title and press Enter.");
  };
  // Add Row adds a new row containing four independent 1-column cells at the end of the currently selected section
  // (disabled with no section target) — the same single-column building block "+ Add Column" uses, not a separate
  // structure. Only the first of the four is marked startOnNewRow, so the row reliably starts fresh even when the
  // current row has room left; the packer then lays the other three immediately after it, filling exactly one row.
  // Each column gets its own stable id so it can be independently selected and placed into.
  const addRow = () => {
    if (!layout) {
      return;
    }
    const sectionIndex = sectionOfSelection();
    if (sectionIndex === null) {
      return;
    }
    const section = layout.sections[sectionIndex]!;
    const insertIndex = section.elements.length;
    const columns = [{ id: nextSlotId(), index: insertIndex, startOnNewRow: true }, { id: nextSlotId(), index: insertIndex, startOnNewRow: false }, { id: nextSlotId(), index: insertIndex, startOnNewRow: false }, { id: nextSlotId(), index: insertIndex, startOnNewRow: false }];
    setView("design");
    setExtraSlots((current) => ({ ...current, [section.sectionKey]: [...(current[section.sectionKey] ?? []), ...columns] }));
    setSelection({ kind: "slot", sectionKey: section.sectionKey, insertIndex, slotId: columns[0]!.id, startOnNewRow: true });
    setNotice(null);
    announce(`New row of 4 empty columns added in ${section.title ?? section.sectionKey}`);
  };
  const addColumn = () => {
    if (!layout) {
      return;
    }
    const target = columnSlotFor(layout, selection);
    if (!target) {
      return;
    }
    const section = layout.sections.find((candidate) => candidate.sectionKey === target.sectionKey)!;
    const existing = extraSlots[target.sectionKey] ?? [];
    setView("design");
    if (existing.length >= 3) {
      setNotice(`“${section.title ?? section.sectionKey}” already shows four empty cells. Sections use the standard four-column grid; place a field in an empty cell first.`);
      return;
    }
    const id = nextSlotId();
    setExtraSlots((current) => ({ ...current, [target.sectionKey]: [...(current[target.sectionKey] ?? []), { id, index: target.insertIndex }] }));
    setSelection({ kind: "slot", sectionKey: target.sectionKey, insertIndex: target.insertIndex, slotId: id });
    setNotice(`Empty cell added in “${section.title ?? section.sectionKey}”. Sections keep the four-column grid; a full row continues on the next row.`);
    announce("Empty cell added");
  };
  const placeExisting = () => {
    if (!layout) {
      return;
    }
    const sectionIndex = sectionOfSelection() ?? layout.sections.length - 1;
    const section = layout.sections[sectionIndex]!;
    setView("design");
    setSelection({ kind: "slot", sectionKey: section.sectionKey, insertIndex: section.elements.length, slotId: "baseline" });
    setPane("properties");
  };
  const toggleCollapse = (sectionKey: string) =>
    setCollapsed((current) => {
      const next = new Set(current);
      if (next.has(sectionKey)) {
        next.delete(sectionKey);
      } else {
        next.add(sectionKey);
      }
      return next;
    });

  const placement = layout ? placedIds(layout) : { fieldIds: new Set<string>(), relationshipIds: new Set<string>() };
  const sectionNameOf = (fieldId: string) => {
    const found = layout ? locateField(layout, fieldId) : null;
    return found ? (layout!.sections[found.sectionIndex]!.title ?? "") : null;
  };

  // Shared by the quick inspector and the Configure Field modal (Phase C3 designer UX refinement) so the two never
  // drift: the modal gets the exact same callbacks, just with layout/onChange later overridden to a local draft.
  const inspectorProps: DesignerInspectorProps | null = layout
    ? {
        layout,
        onChange: setLayoutTracked,
        selection,
        onSelect: (next) => {
          setSelection(next);
          setNotice(null);
        },
        fields: form.fields,
        relationships: form.relationships,
        formDefinitionId,
        canDesign,
        collapsed,
        onToggleCollapse: toggleCollapse,
        onRequestRemoveSection: setRemoveSectionIndex,
        onMoveSection: (sectionIndex, delta) => setLayoutTracked(moveSection(layout, sectionIndex, sectionIndex + delta)),
        onDuplicateSection: (sectionIndex) => {
          const key = newSectionKey(layout);
          setLayoutTracked(duplicateSection(layout, sectionIndex, key));
          setSelection({ kind: "section", sectionKey: key });
        },
        onOpenFieldEditor: (fieldId) => {
          editField.reset();
          setEditFieldId(fieldId);
        },
        onCreateAt: (kind, target) => setCreateAt({ kind, target }),
        onPlacedAt: consumeSlot,
        onOpenRelatedForms: () => setView("relationships"),
        onStrengthen: (fieldId, request) => strengthen.mutate({ fieldId, request }),
        strengthenPending: strengthen.isPending,
        strengthenError: strengthen.error,
        announce,
        // "validation" is the modal's first tab (Phase C3 designer UX refinement removed the General tab; label,
        // type, help text and placeholder live only in the quick inspector).
        onOpenConfig: (key, tab) => setConfigTarget({ key, tab: tab ?? "validation" }),
        groups: form.groups ?? [],
        onEnableRepeat: (sectionIndex, existingGroupDefinitionId) => enableRepeat.mutate({ sectionIndex, existingGroupDefinitionId }),
        enableRepeatPending: enableRepeat.isPending,
        enableRepeatError: enableRepeat.error,
      }
    : null;

  // "+ Add Row" needs an actual section target (a selected section, a field or a cell within one); unlike "+ Add
  // Column" it does not fall back to the last section when nothing is selected.
  const sectionTargetIndex = sectionOfSelection();

  const toolbar = (
    <div role="group" aria-label="Designer actions" className="flex flex-wrap items-center gap-2 rounded-panel border border-line bg-surface p-2">
      {canDesign ? (
        <>
          <DropdownMenu.Root>
            <DropdownMenu.Trigger asChild>
              <Button variant="secondary" icon={ActionIcons.add}>
                Add Field <Caret />
              </Button>
            </DropdownMenu.Trigger>
            <DropdownMenu.Portal>
              <DropdownMenu.Content align="start" sideOffset={6} className={`${menuContentClass} min-w-56`}>
                {[
                  { key: "new", label: "New field…", run: () => setCreateAt({ kind: "field", target: layout ? columnSlotFor(layout, selection) : null }) },
                  { key: "documents", label: "New document collection…", run: () => setCreateAt({ kind: "documents", target: layout ? columnSlotFor(layout, selection) : null }) },
                  { key: "existing", label: "Place an existing field…", run: placeExisting },
                ].map((item) => (
                  <DropdownMenu.Item key={item.key} onSelect={item.run} className={menuItemClass}>
                    {item.label}
                  </DropdownMenu.Item>
                ))}
              </DropdownMenu.Content>
            </DropdownMenu.Portal>
          </DropdownMenu.Root>
          <Button variant="secondary" icon={ActionIcons.add} onClick={addSection} disabled={!layout}>
            Add Section
          </Button>
          <Button variant="secondary" icon={ActionIcons.add} onClick={addRow} disabled={!layout || sectionTargetIndex === null} title={!layout || sectionTargetIndex !== null ? undefined : "Select a section, a field or a cell first"}>
            Add Row
          </Button>
          <Button variant="secondary" icon={ActionIcons.add} onClick={addColumn} disabled={!layout || !columnSlotFor(layout, selection)}>
            Add Column
          </Button>
          <span aria-hidden="true" className="mx-1 hidden h-6 w-px bg-line sm:inline-block" />
          <Button variant="ghost" disabled={undoStack.current.length === 0} onClick={undo} aria-label="Undo last layout change" title="Undo (Ctrl+Z)">
            <UndoIcon /> Undo
          </Button>
          <Button variant="ghost" disabled={redoStack.current.length === 0} onClick={redo} aria-label="Redo last undone layout change" title="Redo (Ctrl+Shift+Z)">
            <UndoIcon flip /> Redo
          </Button>
        </>
      ) : null}
      <div className="ml-auto flex flex-wrap items-center gap-2">
        <Button variant="secondary" aria-pressed={view === "preview"} onClick={() => setView(view === "preview" ? "design" : "preview")}>
          <EyeIcon /> Preview
        </Button>
        {canDesign ? (
          <>
            <Button busy={saveDraft.isPending} disabled={!layout || !dirty} onClick={() => layout && saveDraft.mutate(layout)}>
              <SaveIcon /> Save draft
            </Button>
            <Button variant="secondary" disabled={!canPublish} onClick={() => setPublishOpen(true)} title={dirty ? "Save the draft before publishing" : undefined}>
              Publish…
            </Button>
          </>
        ) : null}
      </div>
    </div>
  );

  const designView = layout ? (
    <div className="flex flex-col gap-3">
      <div role="group" aria-label="Designer panes" className="flex gap-2 lg:hidden">
        {(["canvas", "properties"] as const).map((option) => (
          <Button key={option} variant={pane === option ? "primary" : "secondary"} aria-pressed={pane === option} onClick={() => setPane(option)} className="flex-1">
            {option === "canvas" ? "Canvas" : "Properties"}
          </Button>
        ))}
      </div>
      {/* lg:items-start: without it, CSS Grid's default align-items:stretch makes the inspector column match the
          canvas's full (often very tall) height, and a position:sticky element stretched to span its entire
          scrollable range does not stick in Chromium — it just tracks normal flow. SideNavTabs' own grid already has
          this; this was the one grid missing it, which is why the inspector (and nothing else) failed to stay pinned
          on a tall canvas. */}
      <div className="grid min-w-0 grid-cols-1 gap-4 lg:items-start lg:grid-cols-[minmax(0,1fr)_20rem] xl:grid-cols-[minmax(0,1fr)_22rem]">
        <div className={`min-w-0 ${pane === "canvas" ? "" : "hidden lg:block"}`} aria-label="Form canvas" role="region">
          <DesignerCanvas
            layout={layout}
            onChange={setLayoutTracked}
            fields={rendererFields}
            relationships={relationships}
            readOnly={!canDesign}
            selection={selection}
            onSelect={(next) => {
              setSelection(next);
              setNotice(null);
            }}
            extraSlots={extraSlots}
            collapsed={collapsed}
            onToggleCollapse={toggleCollapse}
            renamingSectionKey={renamingSectionKey}
            onRenamingSectionChange={setRenamingSectionKey}
            onRequestRemoveSection={setRemoveSectionIndex}
            onOpenConfig={(key) => {
              setSelection({ kind: "element", key });
              setConfigTarget({ key, tab: "validation" });
            }}
            announce={announce}
          />
        </div>
        {/* Sticky, not scroll-capped: the quick panel is short by design (Phase C3 designer UX refinement), so it never
            needs a scrollbar of its own — it just stays in view, offset below the pinned header, while the canvas scrolls. */}
        <div className={`min-w-0 lg:sticky ${pane === "properties" ? "" : "hidden lg:block"}`} style={{ top: pinnedHeight + 16 }}>
          <DesignerInspector {...inspectorProps!} />
        </div>
      </div>
    </div>
  ) : (
    <LoadingBlock />
  );

  const fieldsView = (
    <Card
      title="Field catalog"
      actions={
        canDesign ? (
          <>
            <Button size="sm" variant="secondary" onClick={() => setOptionListsOpen(true)}>
              Option lists
            </Button>
            <Button size="sm" variant="secondary" onClick={() => setCreateAt({ kind: "documents", target: null })}>
              Add document collection
            </Button>
            <Button size="sm" onClick={() => setCreateAt({ kind: "field", target: null })}>
              Add field
            </Button>
          </>
        ) : null
      }
    >
      <p className="text-sm text-muted">Fields are permanent identities with an immutable storage type. Placing, moving or removing one on the canvas never changes its id or stored values.</p>
      <DataTable
        caption="Fields"
        rowKey={(row) => row.fieldDefinitionId}
        rows={form.fields}
        emptyMessage="No fields yet. Add the first field, then place it on the canvas."
        columns={[
          { key: "name", header: "Field", isRowHeader: true, render: (row) => row.fieldDisplayName },
          { key: "key", header: "Key", render: (row) => <code>{row.fieldKey}</code> },
          { key: "type", header: "Type", render: (row) => (row.fieldDataType === "Option" ? (row.optionSelectionMode === "Multiple" ? "Dropdown (Multi Select)" : "Dropdown (Single Select)") : row.fieldDataType) },
          { key: "placement", header: "Section", render: (row) => sectionNameOf(row.fieldDefinitionId) ?? <span className="text-muted">Not placed</span> },
          { key: "protection", header: "Protection", render: (row) => (row.fieldProtectionMode === "ApplicationEncrypted" ? <Badge tone="success">{row.fieldClassification} · Encrypted</Badge> : <Badge tone="warning">{row.fieldClassification} · Plaintext</Badge>) },
          { key: "options", header: "Options", render: (row) => (row.options.length === 0 ? "" : row.options.map((option) => `${option.optionDisplayText}${option.isActive ? "" : " (inactive)"}`).join(", ")) },
          { key: "status", header: "Status", render: (row) => <Badge tone="neutral">{row.lifecycleStatus}</Badge> },
          {
            key: "actions",
            header: "Actions",
            isActions: true,
            render: (row) => (
              <RowActions
                overflowLabel={`More actions for ${row.fieldDisplayName}`}
                primary={
                  <Button size="sm" variant="secondary" aria-label={`${canDesign ? "Edit" : "View"} ${row.fieldDisplayName}`} onClick={() => { editField.reset(); setEditFieldId(row.fieldDefinitionId); }}>
                    {canDesign ? "Edit" : "View"}
                  </Button>
                }
                overflow={fieldOverflow(row)}
              />
            ),
          },
        ]}
      />
    </Card>
  );

  function fieldOverflow(row: FieldDefinitionResponse) {
    if (!layout) {
      return [];
    }
    const placed = placement.fieldIds.has(row.fieldDefinitionId);
    const key = elementKey(newElementFor(row, 1));
    const items: Array<{ key: string; label: string; run: () => void }> = placed
      ? [{ key: "show", label: "Show on canvas", run: () => { setSelection({ kind: "element", key }); setView("design"); } }]
      : canDesign
        ? [{ key: "place", label: "Place on canvas…", run: placeExisting }]
        : [];
    if (placed && canDesign) {
      items.push({
        key: "unplace",
        label: "Remove from layout",
        run: () => {
          const found = findElement(layout, key);
          if (found) {
            setLayoutTracked(removeElement(layout, found.sectionIndex, found.index));
            announce(`${row.fieldDisplayName} removed from the layout; the field and its values are kept`);
          }
        },
      });
    }
    return items.map((item) => ({ key: item.key, content: <button type="button" onClick={item.run}>{item.label}</button> }));
  }

  const views: Array<{ value: View; label: ReactNode; icon: ReactNode; content: ReactNode }> = [
    { value: "design", label: "Design", icon: <NavIcon d="M4 20h4L18.5 9.5a2.8 2.8 0 0 0-4-4L4 16v4zM13.5 6.5l4 4" />, content: designView },
    { value: "fields", label: <>Fields <Count n={form.fields.length} /></>, icon: <NavIcon d="M3 5h18v14H3zM3 10h18M3 15h18M9 5v14" />, content: fieldsView },
    { value: "rules", label: "Rules", icon: <NavIcon d="M6 3v12M18 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM18 9a9 9 0 0 1-9 9" />, content: layout ? <RulesView layout={layout} fields={form.fields} onEdit={(key) => { setSelection({ kind: "element", key }); setPane("properties"); setView("design"); setConfigTarget({ key, tab: "rules" }); }} /> : <LoadingBlock /> },
    {
      value: "preview",
      label: "Preview",
      icon: <NavIcon d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z" />,
      content: layout ? <PreviewView layout={layout} fields={rendererFields} /> : <LoadingBlock />,
    },
    {
      value: "versions",
      label: "Versions",
      icon: <NavIcon d="M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5M12 7v5l3 3" />,
      content: (
        <Card title="Published versions">
          <p className="text-sm text-muted">Published versions are immutable. Restoring copies a version into the draft; publishing it creates a new version.</p>
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
                {
                  key: "actions",
                  header: "Actions",
                  isActions: true,
                  render: (row) => <RowActions overflowLabel={`More actions for version ${row.versionNumber}`} primary={canDesign ? <Button size="sm" variant="secondary" busy={restore.isPending} onClick={() => restore.mutate(row.formVersionId)}>Restore to draft</Button> : null} />,
                },
              ]}
            />
          ) : null}
        </Card>
      ),
    },
    {
      value: "settings",
      label: "Settings",
      icon: <NavIcon d="M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15l1.6 1-2 3.5-1.8-.7a7 7 0 0 1-2.2 1.3L14.7 22h-4l-.3-1.9a7 7 0 0 1-2.2-1.3l-1.8.7-2-3.5 1.6-1a7 7 0 0 1 0-2.6L4.4 11l2-3.5 1.8.7a7 7 0 0 1 2.2-1.3L10.7 3h4l.3 1.9a7 7 0 0 1 2.2 1.3l1.8-.7 2 3.5-1.6 1a7 7 0 0 1 0 2.6z" />,
      content: (
        <Card title="Form settings">
          <dl className="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-[12rem_1fr]">
            <dt className="font-medium">Form name</dt>
            <dd>{form.summary.formDefinitionName}</dd>
            <dt className="font-medium">Description</dt>
            <dd>{form.summary.formDefinitionDescription || <span className="text-muted">None</span>}</dd>
            <dt className="font-medium">Current version</dt>
            <dd>{form.summary.currentVersionNumber === null ? "Not published" : `v${form.summary.currentVersionNumber}`}</dd>
            <dt className="font-medium">Draft</dt>
            <dd>{form.draft ? `Revision ${form.draft.draftRevision}` : "No draft"}</dd>
            <dt className="font-medium">Form identity</dt>
            <dd>
              <code className="break-all">{formDefinitionId}</code>
            </dd>
          </dl>
          <div className="flex flex-wrap gap-2">
            <Button size="sm" variant="secondary" onClick={() => setView("access")}>
              Form permissions
            </Button>
            <Button size="sm" variant="secondary" onClick={() => setView("relationships")}>
              Related forms
            </Button>
          </div>
        </Card>
      ),
    },
    { value: "access", label: "Form permissions", icon: <NavIcon d="M5 11h14v10H5zM8 11V7a4 4 0 0 1 8 0v4" />, content: <FormAccessPanel formDefinitionId={formDefinitionId} canManage={canDesign} /> },
    {
      value: "relationships",
      label: <>Related forms <Count n={form.relationships.length} /></>,
      icon: <NavIcon d="M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7" />,
      content: (
        <Card title="Child record lists" actions={canDesign ? <Button size="sm" onClick={() => setRelationshipOpen(true)}>Add relationship</Button> : null}>
          <p className="text-sm text-muted">A relationship hosts a reusable child form (for example AccusedAddress) as a list of independent child records. Place it in any section from the canvas.</p>
          <DataTable
            caption="Relationships"
            rowKey={(row) => row.formRelationshipDefinitionId}
            rows={form.relationships}
            emptyMessage="No relationships yet."
            columns={[
              { key: "name", header: "List", isRowHeader: true, render: (row) => row.relationshipDisplayName },
              { key: "child", header: "Child form", render: (row) => row.childFormDefinitionName },
              { key: "bounds", header: "Bounds", render: (row) => `${row.minimumChildCount}–${row.maximumChildCount}` },
              { key: "placement", header: "Placed", render: (row) => (placement.relationshipIds.has(row.formRelationshipDefinitionId) ? "On canvas" : <span className="text-muted">Not placed</span>) },
              { key: "status", header: "Status", render: (row) => <Badge tone="neutral">{row.lifecycleStatus}</Badge> },
              {
                key: "actions",
                header: "Actions",
                isActions: true,
                render: (row) => {
                  const key = `rel:${row.formRelationshipDefinitionId}`;
                  const placed = placement.relationshipIds.has(row.formRelationshipDefinitionId);
                  return (
                    <RowActions
                      overflowLabel={`More actions for ${row.relationshipDisplayName}`}
                      primary={
                        placed ? (
                          <Button size="sm" variant="secondary" aria-label={`Show ${row.relationshipDisplayName} on canvas`} onClick={() => { setSelection({ kind: "element", key }); setView("design"); }}>
                            View
                          </Button>
                        ) : canDesign ? (
                          <Button size="sm" variant="secondary" aria-label={`Place ${row.relationshipDisplayName} on canvas`} onClick={placeExisting}>
                            Place
                          </Button>
                        ) : null
                      }
                    />
                  );
                },
              },
            ]}
          />
        </Card>
      ),
    },
  ];

  const header = (
    <div className="flex flex-col gap-2">
      <Link to="/app/forms" className="inline-flex min-h-9 items-center gap-1 self-start rounded text-sm font-medium text-muted underline-offset-4 hover:text-body hover:underline">
        <span aria-hidden="true">←</span> All forms
      </Link>
      <p className="text-xl font-bold leading-tight">Form Designer</p>
      <h1 className="break-words text-base font-semibold leading-snug">{form.summary.formDefinitionName}</h1>
      <div className="flex flex-wrap gap-1.5">
        <Badge tone={form.draft ? "success" : "neutral"}>{form.draft ? `Draft revision ${form.draft.draftRevision}` : "No draft"}</Badge>
        <Badge tone={form.summary.currentVersionNumber === null ? "neutral" : "info"}>{form.summary.currentVersionNumber === null ? "Not published" : `Published v${form.summary.currentVersionNumber}`}</Badge>
        {!canDesign ? <Badge tone="danger">Read-only</Badge> : null}
      </div>
      <div role="status" aria-live="polite" className="min-h-0">
        {dirty ? (
          <span className="inline-flex items-center gap-1.5 text-sm font-medium text-body">
            <span aria-hidden="true" className="h-2.5 w-2.5 rounded-full bg-[#b45309]" />
            Unsaved changes
          </span>
        ) : null}
      </div>
    </div>
  );

  return (
    // Form Designer usability corrections: label, control, then help text in every field here (and in the dialogs
    // it opens — Configure Field, Add/Edit Field, Option lists, Publish, Remove section — since Radix dialogs portal
    // their DOM elsewhere but stay inside this React tree). The record-entry form keeps its own tested order
    // (FieldLayoutProvider's default) unaffected.
    <FieldLayoutProvider helpAfterControl>
      <div className="sr-only" role="status" aria-live="polite">
        {announcement}
      </div>
      {parseError ? <StatusMessage tone="danger" title="The stored layout could not be parsed">{parseError}</StatusMessage> : null}
      {/* Phase C3 designer UX refinement: the form name, draft status and primary actions stay reachable while the
          canvas (or any tab's content) scrolls, without a second independently-scrolling box. Height is measured
          (pinnedRef) so the nav list and quick inspector below can offset by exactly this much. */}
      {/* max-h/overflow-y-auto is a safety net, not the normal case: at ordinary widths and zoom this content fits and
          never scrolls. Only when the header and toolbar together would be taller than most of a very short viewport
          (e.g. 400% zoom) does this bar get its own short scroll, so Save draft/Publish stay reachable instead of
          being permanently covered — never content "inaccessible" the way an un-clamped sticky block would leave it. */}
      <div ref={setPinnedNode} className="sticky top-0 z-20 -mx-4 flex max-h-[70vh] flex-col gap-2 overflow-y-auto bg-page px-4 pb-2 pt-2 sm:-mx-0 sm:px-0 sm:pt-0">
        {header}
        {toolbar}
      </div>
      <SideNavTabs
        label="Form designer"
        value={view}
        onValueChange={(next) => setView(next as View)}
        stickyTop={pinnedHeight + 16}
        items={views.map((item) => ({
          value: item.value,
          label: item.label,
          icon: item.icon,
          content: (
            <div className="flex min-w-0 flex-col gap-3 pt-3">
              {notice && item.value === "design" ? (
                <StatusMessage tone="info" title="Layout grid">
                  {notice}
                </StatusMessage>
              ) : null}
              {saveDraft.isError ? <ErrorPanel error={saveDraft.error} /> : null}
              {restore.isError ? <ErrorPanel error={restore.error} /> : null}
              {item.content}
            </div>
          ),
        }))}
      />
      {configTarget && inspectorProps ? <FieldConfigModal {...inspectorProps} open configKey={configTarget.key} initialTab={configTarget.tab} onClose={() => setConfigTarget(null)} /> : null}

      {removeSectionIndex !== null && layout ? (
        <RemoveSectionDialog
          layout={layout}
          sectionIndex={removeSectionIndex}
          onClose={() => setRemoveSectionIndex(null)}
          onConfirm={(next, message) => {
            setLayoutTracked(next);
            setRemoveSectionIndex(null);
            setSelection(null);
            announce(message);
          }}
        />
      ) : null}
      {editFieldId !== null && form.fields.some((candidate) => candidate.fieldDefinitionId === editFieldId) ? (
        <EditFieldDialog
          field={form.fields.find((candidate) => candidate.fieldDefinitionId === editFieldId)!}
          location={layout ? locateField(layout, editFieldId) : null}
          canEdit={canDesign}
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
      {optionListsOpen ? <OptionListsDialog open onOpenChange={setOptionListsOpen} canDesign={canDesign} /> : null}
      {createAt ? (
        <AddFieldDialog
          kind={createAt.kind}
          group={(() => {
            const section = createAt.target && layout ? layout.sections.find((candidate) => candidate.sectionKey === createAt.target!.sectionKey) : undefined;
            return section?.repeat ? { groupDefinitionId: section.repeat.groupDefinitionId, sectionTitle: section.title ?? section.sectionKey } : undefined;
          })()}
          open
          onOpenChange={(open) => (open ? undefined : setCreateAt(null))}
          pending={addFieldMutation.isPending}
          error={addFieldMutation.error}
          onSubmit={(request) => addFieldMutation.mutate(request)}
        />
      ) : null}

      <Dialog
        open={publishOpen}
        onOpenChange={setPublishOpen}
        title="Publish a new version"
        description={`This publishes draft revision ${form.draft?.draftRevision ?? 0} as version ${Number(form.summary.currentVersionNumber ?? 0) + 1}, with ${layout?.sections.length ?? 0} ${layout?.sections.length === 1 ? "section" : "sections"}. Published versions are immutable; existing records keep their values.`}
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
    </FieldLayoutProvider>
  );
}

function readCollapsed(formDefinitionId: string): Set<string> {
  try {
    const stored = window.localStorage.getItem(`ef.designer.collapsed.${formDefinitionId}`);
    const parsed: unknown = stored ? JSON.parse(stored) : [];
    return new Set(Array.isArray(parsed) ? parsed.filter((value): value is string => typeof value === "string") : []);
  } catch {
    return new Set();
  }
}

function PreviewView({ layout, fields }: { layout: FormLayout; fields: RendererField[] }) {
  const [breakpoint, setBreakpoint] = useState<Breakpoint>("desktop");
  // A new record starts with each dropdown's default selection; everything else is synthetic input.
  const [values, setValues] = useState<Record<string, string>>(() => initialValues(layout, fields));
  const width = breakpoint === "phone" ? "max-w-[320px]" : breakpoint === "tablet" ? "max-w-[768px]" : "max-w-full";
  return (
    <Card
      title="Preview"
      actions={
        <div role="group" aria-label="Preview width" className="flex flex-wrap gap-1">
          {(["phone", "tablet", "desktop"] as const).map((option) => (
            <Button key={option} size="sm" variant={breakpoint === option ? "primary" : "secondary"} aria-pressed={breakpoint === option} onClick={() => setBreakpoint(option)}>
              {option === "phone" ? "Phone 320" : option === "tablet" ? "Tablet 768" : "Desktop"}
            </Button>
          ))}
        </div>
      }
    >
      <p className="text-sm text-muted">The draft as people will see it in the data-entry form, with the same renderer the record page uses. Fields marked Not in form are left out. Type synthetic values to try the visibility and required-if rules; nothing typed here is saved.</p>
      <div className={`w-full ${width} rounded border border-dashed border-input bg-page p-3`}>
        <FormRenderer
          layout={layout}
          fields={fields}
          values={values}
          breakpoint={breakpoint}
          onChange={(key, value) => setValues((current) => ({ ...current, [key]: value }))}
          renderRelatedList={(element) => <PreviewSlot label={element.label || "Related records"} detail="Child records list (rendered on the record page)" />}
          renderDocumentCollection={(element) => <PreviewSlot label={element.label || "Documents"} detail={`Up to ${element.upload?.maxFiles ?? 20} files`} />}
        />
      </div>
    </Card>
  );
}

function PreviewSlot({ label, detail }: { label: string; detail: string }) {
  return (
    <div className="rounded border border-dashed border-input bg-surface p-3">
      <p className="font-medium">{label}</p>
      <p className="text-sm text-muted">{detail}</p>
    </div>
  );
}

function RulesView({ layout, fields, onEdit }: { layout: FormLayout; fields: readonly FieldDefinitionResponse[]; onEdit: (elementKey: string) => void }) {
  const sources = new Map(fields.map((field) => [field.fieldDefinitionId, { fieldDefinitionId: field.fieldDefinitionId, fieldKey: field.fieldKey, fieldDataType: field.fieldDataType, optionSelectionMode: field.optionSelectionMode, fieldDisplayName: field.fieldDisplayName }]));
  const optionLabel = (fieldId: string, key: string) => fields.find((field) => field.fieldDefinitionId === fieldId)?.options.find((option) => option.optionKey === key)?.optionDisplayText ?? key;
  const rows = layout.sections
    .flatMap((section) => section.elements.map((element) => ({ section, element })))
    .filter((entry): entry is { section: FormLayout["sections"][number]; element: FieldElement } => entry.element.kind === "field" && (entry.element.visibleWhen !== undefined || entry.element.requiredWhen !== undefined || entry.element.readOnly === true || entry.element.showInForm === false || entry.element.optionFilter !== undefined));
  const cycle = findRuleCycle(layout);
  const filterCycle = findOptionFilterCycle(layout);
  return (
    <Card title="Rules and field behavior">
      <p className="text-sm text-muted">Every field with a condition, Read-only or Not-in-form setting in the draft. Select a row to edit it in the Rules tab of the field properties; there is no separate rules editor.</p>
      {cycle ? (
        <StatusMessage tone="danger" title="Circular rule dependency">
          {cycle.map((id) => fields.find((field) => field.fieldDefinitionId === id)?.fieldDisplayName ?? id).join(" → ")}. The draft cannot be saved until the loop is removed.
        </StatusMessage>
      ) : null}
      {filterCycle ? (
        <StatusMessage tone="danger" title="Circular dropdown dependency">
          {filterCycle.map((id) => fields.find((field) => field.fieldDefinitionId === id)?.fieldDisplayName ?? id).join(" → ")}. Dropdowns cannot filter each other in a loop; the draft cannot be saved until it is removed.
        </StatusMessage>
      ) : null}
      <DataTable
        caption="Fields with rules or behavior settings"
        rowKey={(row) => row.element.fieldDefinitionId}
        rows={rows}
        emptyMessage="No field has a rule yet. Select a field on the Design canvas and open its Rules tab."
        columns={[
          { key: "field", header: "Field", isRowHeader: true, render: (row) => row.element.label || (fields.find((field) => field.fieldDefinitionId === row.element.fieldDefinitionId)?.fieldDisplayName ?? "Unknown field") },
          { key: "visible", header: "Shown when", render: (row) => (row.element.visibleWhen ? describeCondition(row.element.visibleWhen, sources, optionLabel) : <span className="text-muted">Always</span>) },
          { key: "required", header: "Required", render: (row) => (row.element.required ? "Always" : row.element.requiredWhen ? `When ${describeCondition(row.element.requiredWhen, sources, optionLabel)}` : <span className="text-muted">No</span>) },
          { key: "flags", header: "Behavior", render: (row) => [row.element.readOnly ? "Read-only" : null, row.element.showInForm === false ? "Not in form" : null, row.element.clearWhenHidden ? "Clears when hidden" : null, row.element.optionFilter ? `Choices depend on ${fields.find((field) => field.fieldDefinitionId === row.element.optionFilter!.parentFieldDefinitionId)?.fieldDisplayName ?? "another dropdown"}` : null, row.section.repeat ? "Per entry" : null].filter(Boolean).join(", ") || <span className="text-muted">Standard</span> },
          { key: "actions", header: "Actions", isActions: true, render: (row) => <Button size="sm" variant="secondary" aria-label={`Edit rules of ${row.element.label || "field"}`} onClick={() => onEdit(elementKey(row.element))}>Edit rules</Button> },
        ]}
      />
    </Card>
  );
}

function Count({ n }: { n: number }) {
  return <span className="ml-1 rounded-full bg-page px-1.5 text-xs font-medium text-muted">{n}</span>;
}

const iconProps = { width: 18, height: 18, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round", strokeLinejoin: "round", "aria-hidden": true } as const;

function NavIcon({ d }: { d: string }) {
  return (
    <svg {...iconProps}>
      <path d={d} />
    </svg>
  );
}

function Caret() {
  return (
    <svg {...iconProps} width={14} height={14}>
      <path d="M6 9l6 6 6-6" />
    </svg>
  );
}

function UndoIcon({ flip = false }: { flip?: boolean }) {
  return (
    <svg {...iconProps} width={16} height={16} className={flip ? "-scale-x-100" : undefined}>
      <path d="M9 14L4 9l5-5M4 9h11a5 5 0 0 1 0 10h-3" />
    </svg>
  );
}

function EyeIcon() {
  return (
    <svg {...iconProps} width={16} height={16}>
      <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  );
}

function SaveIcon() {
  return (
    <svg {...iconProps} width={16} height={16}>
      <path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2zM17 21v-8H7v8M7 3v5h8" />
    </svg>
  );
}
