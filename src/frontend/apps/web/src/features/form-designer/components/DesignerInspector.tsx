import type { FieldDefinitionResponse, GroupDefinitionResponse, RelationshipDefinitionResponse, StrengthenFieldProtectionRequest } from "@enterpriseforms/api-client";
import { applyEditValues, elementKey, findElement, moveElementTo, placedIds, sameScope, setSectionRepeat, toEditValues, updateElementByKey, updateSection, validateEditValues, type DesignerSelection, type FieldEditValues } from "@enterpriseforms/form-designer";
import { CONTROLS_BY_TYPE, REPEAT_CEILING, type DocumentElement, type FieldDataType, type FieldElement, type FormLayout, type LayoutElement, type LayoutRepeat, type RelatedElement } from "@enterpriseforms/form-renderer";
import { Badge, Button, Checkbox, Select, StatusMessage, Tabs, TextArea, TextInput } from "@enterpriseforms/ui";
import { useState, type ReactNode } from "react";
import { CurrentProtectionSection } from "./FieldSecuritySection";
import { OptionsEditor } from "./OptionsEditor";
import { DependentChoices, RulesTab } from "./RulesTab";
import { ErrorPanel } from "../../support";

export interface PlacementTarget {
  sectionKey: string;
  insertIndex: number;
  /** The specific empty cell this is, when it is one of several at the same `insertIndex` (e.g. a "+ Add Row" column); used to consume exactly that slot and to carry its `startOnNewRow` into the placed field. */
  slotId?: string | undefined;
  startOnNewRow?: boolean | undefined;
}

export interface DesignerInspectorProps {
  layout: FormLayout;
  onChange: (layout: FormLayout) => void;
  selection: DesignerSelection;
  onSelect: (selection: DesignerSelection) => void;
  fields: readonly FieldDefinitionResponse[];
  relationships: readonly RelationshipDefinitionResponse[];
  formDefinitionId: string;
  canDesign: boolean;
  collapsed: ReadonlySet<string>;
  onToggleCollapse: (sectionKey: string) => void;
  onRequestRemoveSection: (sectionIndex: number) => void;
  onMoveSection: (sectionIndex: number, delta: number) => void;
  onDuplicateSection: (sectionIndex: number) => void;
  /** Opens the catalog editor dialog (display name, description; all five tabs). */
  onOpenFieldEditor: (fieldDefinitionId: string) => void;
  /** Starts creating a new field/document collection that is placed at `target` once the server confirms it. */
  onCreateAt: (kind: "field" | "documents", target: PlacementTarget) => void;
  /** An empty cell was filled; lets the page retire an extra "+ Add Column" cell. */
  onPlacedAt?: ((target: PlacementTarget) => void) | undefined;
  onOpenRelatedForms: () => void;
  onStrengthen: (fieldDefinitionId: string, request: StrengthenFieldProtectionRequest) => void;
  strengthenPending: boolean;
  strengthenError: unknown;
  announce: (message: string) => void;
  /** Opens the full Configure Field modal (Validation / Conditional Rules / Dropdown Choices / Placement & Alignment / Security & Encryption / Advanced) on `key`, optionally landing on a given tab. */
  onOpenConfig: (key: string, tab?: string) => void;
  /** Repeating groups of the form (Phase C3). */
  groups?: readonly GroupDefinitionResponse[] | undefined;
  /** "Allow multiple entries" on an empty section: creates a group (or reuses an unplaced one) and places it in the section. */
  onEnableRepeat?: ((sectionIndex: number, existingGroupDefinitionId: string | null) => void) | undefined;
  enableRepeatPending?: boolean | undefined;
  enableRepeatError?: unknown;
}

const WIDTH_OPTIONS = [
  { value: "1", label: "1 column" },
  { value: "2", label: "2 columns" },
  { value: "3", label: "3 columns" },
  { value: "4", label: "4 columns (full row)" },
];

const TYPE_LABEL: Record<string, string> = { String: "Text (String)", Integer: "Whole number (Integer)", Decimal: "Decimal", Date: "Date", DateTime: "Date and time", Guid: "Identifier (Guid)", Boolean: "Yes/No (Boolean)", Option: "Dropdown", File: "File (document collection)" };

/**
 * Right-hand inspector (Phase C3 designer UX refinement): the single place a selection starts. A placed field or
 * document collection shows a short **Field Properties** summary (label, type, help text, placeholder) with a
 * **Configure Field** button that opens the full Validation / Conditional Rules / Dropdown Choices (Option fields
 * only) / Placement & Alignment / Security & Encryption / Advanced workspace in a large modal (`FieldConfigModal`);
 * there is no separate General tab — nothing it would show is not already in this quick panel. A section shows
 * **Section Properties** (unchanged, still inline —
 * it is short); an empty cell shows the Add Field options for that exact position. Presentation edits change the
 * draft layout (undoable; saved with Save draft). Security changes reuse the existing Authority-backed workflow —
 * the inspector never offers its own encryption switch.
 */
export function DesignerInspector(props: DesignerInspectorProps) {
  const { selection, layout } = props;
  let title = "Properties";
  let body: ReactNode = <NothingSelected {...props} />;
  if (selection?.kind === "element") {
    const position = findElement(layout, selection.key);
    if (position) {
      const element = position.element;
      title = element.kind === "relatedFormList" ? "Related List Properties" : "Field Properties";
      body = <QuickElementProperties key={selection.key} {...props} element={element} />;
    }
  } else if (selection?.kind === "section") {
    const sectionIndex = layout.sections.findIndex((section) => section.sectionKey === selection.sectionKey);
    if (sectionIndex >= 0) {
      title = "Section Properties";
      body = <SectionProperties key={selection.sectionKey} {...props} sectionIndex={sectionIndex} />;
    }
  } else if (selection?.kind === "slot") {
    const sectionIndex = layout.sections.findIndex((section) => section.sectionKey === selection.sectionKey);
    if (sectionIndex >= 0) {
      title = "Add Field";
      body = <SlotOptions key={`${selection.sectionKey}:${selection.slotId}`} {...props} sectionIndex={sectionIndex} target={{ sectionKey: selection.sectionKey, insertIndex: selection.insertIndex, slotId: selection.slotId, startOnNewRow: selection.startOnNewRow }} />;
    }
  }
  return (
    <aside aria-labelledby="designer-inspector-heading" className="flex min-w-0 flex-col rounded-lg border border-line bg-surface">
      <div className="flex min-h-12 items-center gap-2 border-b border-line px-4 py-2">
        <h2 id="designer-inspector-heading" tabIndex={-1} className="min-w-0 flex-1 text-lg font-semibold focus-visible:outline-none">
          {title}
        </h2>
        {selection ? (
          <button type="button" aria-label="Close properties" title="Close properties" onClick={() => props.onSelect(null)} className="flex h-9 w-9 items-center justify-center rounded text-body hover:bg-page">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
              <path d="M18 6L6 18M6 6l12 12" />
            </svg>
          </button>
        ) : null}
      </div>
      <div className="flex min-w-0 flex-col gap-3 p-4">{body}</div>
    </aside>
  );
}

function NothingSelected({ layout, fields, relationships }: DesignerInspectorProps) {
  const placed = placedIds(layout);
  const unplaced = fields.filter((field) => !placed.fieldIds.has(field.fieldDefinitionId)).length + relationships.filter((relationship) => !placed.relationshipIds.has(relationship.formRelationshipDefinitionId)).length;
  return (
    <div role="status">
      <p className="text-sm text-muted">Select a field to see its properties, a section title to edit the section, or an empty “+ Add Field” cell to place a field exactly there.</p>
      <p className="mt-2 text-sm">
        {layout.sections.length} {layout.sections.length === 1 ? "section" : "sections"} · {unplaced === 0 ? "every field is placed" : `${unplaced} not yet placed`}
      </p>
    </div>
  );
}

/**
 * `layout`/`onChange` are shared between every placement edit a field or related-list element makes — field width,
 * section, move earlier/later. `FieldConfigModal` reuses this unchanged with a local draft layout/setter so the
 * Advanced tab's Placement section behaves identically while deferring the actual commit to Apply Changes.
 */
export function placementControls({ layout, onChange, onSelect, announce, canDesign }: DesignerInspectorProps, element: LayoutElement, sectionIndex: number, index: number) {
  const key = elementKey(element);
  const section = layout.sections[sectionIndex]!;
  const moveTo = (targetSectionIndex: number, insertIndex: number, message: string) => {
    const next = moveElementTo(layout, key, targetSectionIndex, insertIndex);
    if (next !== layout) {
      onChange(next);
      onSelect({ kind: "element", key });
      announce(message);
    }
  };
  return (
    <div className="flex flex-col gap-3">
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <Select
          label="Field width"
          value={String(element.layout.columnSpan)}
          disabled={!canDesign}
          options={WIDTH_OPTIONS}
          onValueChange={(value) => onChange(updateElementByKey(layout, key, (current) => ({ ...current, layout: { ...current.layout, columnSpan: Number(value) as 1 | 2 | 3 | 4 } })))}
        />
        <Checkbox label="Start on a new row" checked={element.layout.startOnNewRow} disabled={!canDesign} onCheckedChange={(checked) => onChange(updateElementByKey(layout, key, (current) => ({ ...current, layout: { ...current.layout, startOnNewRow: checked } })))} />
      </div>
      <Select
        label="Section"
        value={section.sectionKey}
        disabled={!canDesign}
        options={layout.sections.filter((candidate) => sameScope(candidate, section)).map((candidate) => ({ value: candidate.sectionKey, label: candidate.title?.trim() || candidate.sectionKey }))}
        helpText={section.repeat ? "Fields of a section that allows multiple entries stay in that section (one value per entry)." : undefined}
        onValueChange={(sectionKey) => {
          const target = layout.sections.findIndex((candidate) => candidate.sectionKey === sectionKey);
          if (target >= 0 && target !== sectionIndex) {
            moveTo(target, layout.sections[target]!.elements.length, `Moved to ${layout.sections[target]!.title ?? sectionKey}`);
          }
        }}
      />
      <div className="flex flex-wrap gap-2" role="group" aria-label="Position in section">
        <Button size="sm" variant="secondary" disabled={!canDesign || index === 0} onClick={() => moveTo(sectionIndex, index - 1, "Moved earlier")}>
          Move earlier
        </Button>
        <Button size="sm" variant="secondary" disabled={!canDesign || index === section.elements.length - 1} onClick={() => moveTo(sectionIndex, index + 2, "Moved later")}>
          Move later
        </Button>
      </div>
    </div>
  );
}

/**
 * Quick properties (Phase C3 designer UX refinement): the four fields the right-hand panel always shows for a placed
 * field or document collection — label, type, help text, placeholder — plus "Configure Field" for everything else.
 * This panel edits the draft layout live, the same way the full inspector always has; it never needs its own
 * scrollbar because there is nothing else here to crowd it.
 */
function QuickElementProperties(props: DesignerInspectorProps & { element: LayoutElement }) {
  const { element } = props;
  if (element.kind === "relatedFormList") {
    return <QuickRelatedListProperties {...props} element={element} />;
  }
  const field = props.fields.find((candidate) => candidate.fieldDefinitionId === element.fieldDefinitionId);
  if (!field) {
    return <StatusMessage tone="warning" title="Unknown field">This placement refers to a field that is not in this form's catalog. Remove it from the layout.</StatusMessage>;
  }
  return <QuickFieldProperties {...props} element={element} field={field} />;
}

function QuickFieldProperties(props: DesignerInspectorProps & { element: FieldElement | DocumentElement; field: FieldDefinitionResponse }) {
  const { element, field, layout, onChange, canDesign } = props;
  const key = elementKey(element);
  const dataType = field.fieldDataType;
  // Values derive from the draft element on every render (so Undo/Redo and the Configure Field modal's Apply are
  // reflected immediately); only input that does not yet validate is held locally until it does.
  const [pending, setPending] = useState<Partial<FieldEditValues>>({});
  const base = toEditValues(element, field.fieldDisplayName);
  const values: FieldEditValues = { ...base, ...pending };
  const errors = validateEditValues(values, dataType);
  const set = <K extends keyof FieldEditValues>(name: K, value: FieldEditValues[K]) => {
    const next = { ...values, [name]: value };
    const nextErrors = validateEditValues(next, dataType);
    if (Object.keys(nextErrors).length === 0) {
      setPending({});
      onChange(updateElementByKey(layout, key, (current) => applyEditValues(current, next, dataType)));
    } else {
      setPending((current) => ({ ...current, [name]: value }));
    }
  };
  const supportsPlaceholder = element.kind === "field" && (dataType === "String" || dataType === "Integer" || dataType === "Decimal");

  return (
    <div className="flex flex-col gap-3">
      <TextInput label="Field label" value={values.label} required maxLength={200} disabled={!canDesign} error={errors.label} onChange={(event) => set("label", event.target.value)} />
      <TextInput label="Field type" value={dataType === "Option" ? (field.optionSelectionMode === "Multiple" ? "Dropdown (Multi Select)" : "Dropdown (Single Select)") : (TYPE_LABEL[dataType] ?? dataType)} readOnly helpText="Permanent: changing it would reinterpret stored values." />
      <TextArea label="Help text" value={values.helpText} rows={2} maxLength={1000} disabled={!canDesign} error={errors.helpText} onChange={(event) => set("helpText", event.target.value)} />
      {supportsPlaceholder ? (
        <TextInput
          label="Placeholder"
          value={element.kind === "field" ? (element.placeholder ?? "") : ""}
          maxLength={200}
          disabled={!canDesign}
          onChange={(event) => {
            const text = event.target.value.replace(/[<>]/g, "");
            onChange(
              updateElementByKey(layout, key, (current) => {
                if (current.kind !== "field") {
                  return current;
                }
                const next: FieldElement = { ...current };
                if (text) {
                  next.placeholder = text;
                } else {
                  delete next.placeholder;
                }
                return next;
              }),
            );
          }}
        />
      ) : null}
      <ConfigureFieldButton onClick={() => props.onOpenConfig(key)} />
    </div>
  );
}

function QuickRelatedListProperties(props: DesignerInspectorProps & { element: RelatedElement }) {
  const { element, relationships, layout, onChange, canDesign } = props;
  const key = elementKey(element);
  const relationship = relationships.find((candidate) => candidate.formRelationshipDefinitionId === element.formRelationshipDefinitionId);
  return (
    <div className="flex flex-col gap-3">
      <TextInput
        label="Field label"
        value={element.label}
        required
        maxLength={200}
        disabled={!canDesign}
        onChange={(event) => onChange(updateElementByKey(layout, key, (current) => (current.kind === "relatedFormList" ? { ...current, label: event.target.value.replace(/[<>]/g, "") } : current)))}
      />
      <TextInput label="Field type" value="Related form list" readOnly />
      <TextInput label="Child form" value={relationship?.childFormDefinitionName ?? "Unknown"} readOnly />
      <ConfigureFieldButton onClick={() => props.onOpenConfig(key)} />
    </div>
  );
}

/** Prominent entry point to the full configuration workspace (Phase C3 designer UX refinement). */
export function ConfigureFieldButton({ onClick }: { onClick: () => void }) {
  return (
    <Button onClick={onClick} className="justify-center">
      <GearIcon /> Configure Field
    </Button>
  );
}

function GearIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68 1.65 1.65 0 0 0 10 3.17V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
    </svg>
  );
}

/**
 * Full Field Properties content (Validation / Conditional Rules / Dropdown Choices for Option fields / Placement &
 * Alignment / Security & Encryption / Advanced), used only inside `FieldConfigModal` (Phase C3 designer UX
 * refinement — this used to render inline in the right-hand panel, with a General tab that duplicated the quick
 * inspector's label/type/help text/placeholder; General is gone, Required moved to Validation, Control stayed on
 * Advanced, and Placement moved to its own tab so nothing here repeats the quick inspector). The vertical tab
 * navigation suits the modal's width; `Tabs` keeps the same `role="tab"`/`role="tablist"` semantics either way.
 */
export function FieldProperties(props: DesignerInspectorProps & { element: FieldElement | DocumentElement; field: FieldDefinitionResponse; sectionIndex: number; index: number; tab: string; setTab: (tab: string) => void }) {
  const { element, field, layout, onChange, canDesign } = props;
  const key = elementKey(element);
  const dataType = field.fieldDataType;
  const [pending, setPending] = useState<Partial<FieldEditValues>>({});
  const base = toEditValues(element, field.fieldDisplayName);
  const values: FieldEditValues = { ...base, ...pending };
  const errors = validateEditValues(values, dataType);
  const set = <K extends keyof FieldEditValues>(name: K, value: FieldEditValues[K]) => {
    const next = { ...values, [name]: value };
    const nextErrors = validateEditValues(next, dataType);
    if (Object.keys(nextErrors).length === 0) {
      setPending({});
      onChange(updateElementByKey(layout, key, (current) => applyEditValues(current, next, dataType)));
    } else {
      setPending((current) => ({ ...current, [name]: value }));
    }
  };
  const controls = element.kind === "field" ? (CONTROLS_BY_TYPE[dataType as FieldDataType] ?? []) : [];
  const encrypted = field.fieldProtectionMode === "ApplicationEncrypted";

  // Label, type, help text and placeholder live only in the quick inspector (Phase C3 designer UX refinement); this
  // modal never repeats them. Required moves here (it is a validation constraint); Control and Placement move to
  // Advanced; dropdown choices and dependent choices get their own tab below.
  const validation = (
    <div className="flex flex-col gap-3">
      <Checkbox label="Required" checked={values.required} disabled={!canDesign} onCheckedChange={(checked) => set("required", checked)} />
      {dataType === "String" ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <TextInput label="Minimum length" inputMode="numeric" value={values.minLength} disabled={!canDesign} error={errors.minLength} onChange={(event) => set("minLength", event.target.value)} />
          <TextInput label="Maximum length" inputMode="numeric" value={values.maxLength} disabled={!canDesign} error={errors.maxLength} onChange={(event) => set("maxLength", event.target.value)} />
        </div>
      ) : null}
      {dataType === "Integer" || dataType === "Decimal" ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <TextInput label="Minimum value" inputMode="decimal" value={values.minimum} disabled={!canDesign} error={errors.minimum} onChange={(event) => set("minimum", event.target.value)} />
          <TextInput label="Maximum value" inputMode="decimal" value={values.maximum} disabled={!canDesign} error={errors.maximum} onChange={(event) => set("maximum", event.target.value)} />
        </div>
      ) : null}
      {dataType === "Date" || dataType === "DateTime" ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <TextInput label="Earliest date" type="date" value={values.minDate} disabled={!canDesign} error={errors.minDate} onChange={(event) => set("minDate", event.target.value)} />
          <TextInput label="Latest date" type="date" value={values.maxDate} disabled={!canDesign} error={errors.maxDate} onChange={(event) => set("maxDate", event.target.value)} />
        </div>
      ) : null}
      {dataType === "File" ? (
        <>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <TextInput label="Maximum files" inputMode="numeric" value={values.maxFiles} disabled={!canDesign} error={errors.maxFiles} onChange={(event) => set("maxFiles", event.target.value)} />
            <TextInput label="Maximum file size (MB)" inputMode="numeric" value={values.maxFileSizeMb} disabled={!canDesign} error={errors.maxFileSizeMb} onChange={(event) => set("maxFileSizeMb", event.target.value)} />
          </div>
          <TextInput label="Allowed content types" value={values.allowedContentTypes} disabled={!canDesign} helpText="Comma-separated MIME types, e.g. application/pdf, text/plain. Documents already stored are never removed." onChange={(event) => set("allowedContentTypes", event.target.value)} />
        </>
      ) : null}
      {dataType === "Boolean" || dataType === "Option" || dataType === "Guid" ? <p className="text-sm text-muted">{dataType === "Option" ? "A dropdown accepts only its active options (the server enforces this on every save). Use Required above to demand a choice." : "This storage type has no extra constraints beyond Required above and its type rules, which the server enforces."}</p> : null}
      <p className="text-xs text-muted">Rules apply to new saves after publishing; stored values and documents are never rewritten.</p>
    </div>
  );

  const items = [
    { value: "validation", label: "Validation", content: validation },
    {
      value: "rules",
      label: "Conditional Rules",
      content: element.kind === "field" ? <RulesTab layout={layout} element={element} field={field} fields={props.fields} canDesign={canDesign} onElementChange={(next) => onChange(updateElementByKey(layout, key, () => next))} /> : <RulesNotice />,
    },
    dataType === "Option" && element.kind === "field"
      ? {
          value: "dropdown",
          label: "Dropdown Choices",
          content: (
            <div className="flex flex-col gap-4">
              <OptionsEditor formDefinitionId={props.formDefinitionId} field={field} element={element} canDesign={canDesign} onElementChange={(next) => onChange(updateElementByKey(layout, key, () => next))} />
              <div className="border-t border-line pt-4">
                <DependentChoices layout={layout} element={element} field={field} fields={props.fields} canDesign={canDesign} onElementChange={(next) => onChange(updateElementByKey(layout, key, () => next))} />
              </div>
            </div>
          ),
        }
      : null,
    {
      value: "placement",
      label: "Placement & Alignment",
      content: <div className="flex flex-col gap-4">{placementControls(props, element, props.sectionIndex, props.index)}</div>,
    },
    {
      value: "security",
      label: "Security & Encryption",
      content: (
        <div className="flex flex-col gap-3">
          <p className="flex flex-wrap items-center gap-2 text-sm">
            <Badge tone={encrypted ? "success" : "warning"}>
              {field.fieldClassification} · {encrypted ? "Encrypted" : "Plaintext"}
            </Badge>
          </p>
          <CurrentProtectionSection formDefinitionId={props.formDefinitionId} field={field} canEdit={canDesign} onStrengthen={(request) => props.onStrengthen(field.fieldDefinitionId, request)} pending={props.strengthenPending} error={props.strengthenError} />
        </div>
      ),
    },
    {
      value: "advanced",
      label: "Advanced",
      content: (
        <div className="flex flex-col gap-4">
          {controls.length > 1 && element.kind === "field" ? (
            <Select label="Control" value={element.control} disabled={!canDesign} options={controls.map((control) => ({ value: control, label: control === "textarea" ? "Multi-line text" : "Single-line text" }))} onValueChange={(value) => onChange(updateElementByKey(layout, key, (current) => (current.kind === "field" ? { ...current, control: value as FieldElement["control"] } : current)))} />
          ) : null}
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <TextInput label="Field ID" value={field.fieldDefinitionId} readOnly helpText="This ID stays the same even if you rename or move the field." />
            <TextInput label="Field key" value={field.fieldKey} readOnly />
          </div>
          <TextInput label="Catalog name" value={field.fieldDisplayName} readOnly helpText="Name in the field catalog; the label above is this form version's display label." />
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <TextInput label="Field definition revision" value={String(field.fieldDefinitionRevision)} readOnly />
            <TextInput label="Lifecycle status" value={field.lifecycleStatus} readOnly />
          </div>
          <Button size="sm" variant="secondary" onClick={() => props.onOpenFieldEditor(field.fieldDefinitionId)}>
            {canDesign ? "Edit catalog name and description…" : "View field details…"}
          </Button>
        </div>
      ),
    },
  ].filter((item): item is NonNullable<typeof item> => item !== null);
  return <Tabs label={`Properties of ${values.label || field.fieldDisplayName}`} orientation="vertical" value={props.tab} onValueChange={props.setTab} items={items} />;
}

export function RelatedListProperties(props: DesignerInspectorProps & { element: RelatedElement; sectionIndex: number; index: number; tab: string; setTab: (tab: string) => void }) {
  const { element } = props;
  const relationship = props.relationships.find((candidate) => candidate.formRelationshipDefinitionId === element.formRelationshipDefinitionId);
  // The list label lives in the quick inspector now (Phase C3 designer UX refinement), matching Field Properties.
  const items = [
    { value: "validation", label: "Validation", content: <p className="text-sm text-muted">Allowed number of child records: {relationship ? `${relationship.minimumChildCount}–${relationship.maximumChildCount}` : "unknown"}. Bounds belong to the relationship definition and are enforced by the server.</p> },
    { value: "rules", label: "Conditional Rules", content: <RulesNotice /> },
    {
      value: "placement",
      label: "Placement & Alignment",
      content: <div className="flex flex-col gap-4">{placementControls(props, element, props.sectionIndex, props.index)}</div>,
    },
    { value: "security", label: "Security & Encryption", content: <p className="text-sm text-muted">Each child is an independent record. The Authorization Authority decides access per child record; a share of the parent never covers its children, and each child field keeps its own encryption policy.</p> },
    {
      value: "advanced",
      label: "Advanced",
      content: (
        <div className="flex flex-col gap-4">
          <TextInput label="Relationship ID" value={element.formRelationshipDefinitionId} readOnly helpText="This ID stays the same even if you move the list to another section." />
          <TextInput label="Child form" value={relationship?.childFormDefinitionName ?? "Unknown"} readOnly />
          <Button size="sm" variant="secondary" onClick={props.onOpenRelatedForms}>
            Manage related forms…
          </Button>
        </div>
      ),
    },
  ];
  return <Tabs label={`Properties of ${element.label || "related list"}`} orientation="vertical" value={props.tab} onValueChange={props.setTab} items={items} />;
}

function RulesNotice() {
  return (
    <StatusMessage tone="info" title="Rules apply to fields">
      Conditional visibility, required-if, Show in form and Read-only are available on data fields. Document collections and related lists are always shown; their access is decided by authorization.
    </StatusMessage>
  );
}

function SectionProperties(props: DesignerInspectorProps & { sectionIndex: number }) {
  const { layout, onChange, sectionIndex, canDesign } = props;
  const section = layout.sections[sectionIndex]!;
  const collapsed = props.collapsed.has(section.sectionKey);
  return (
    <div className="flex flex-col gap-3">
      <TextInput label="Section title" value={section.title ?? ""} required maxLength={200} disabled={!canDesign} error={(section.title ?? "").trim() ? undefined : "Enter a section title"} onChange={(event) => onChange(updateSection(layout, sectionIndex, { title: event.target.value.replace(/[<>]/g, "") }))} />
      <TextArea label="Description" value={section.description ?? ""} rows={2} maxLength={1000} disabled={!canDesign} helpText="Optional; shown under the title on the form." onChange={(event) => onChange(updateSection(layout, sectionIndex, { description: event.target.value.replace(/[<>]/g, "") }))} />
      <div className="rounded border border-line bg-page p-3 text-sm">
        <p className="font-medium">Layout: 4-column grid</p>
        <p className="text-muted">
          Every section uses the standard four-column grid (one column on phones, two on tablets). {section.elements.length === 1 ? "1 item" : `${section.elements.length} items`} placed. Use “+ Add Column” to add another empty placement cell.
        </p>
      </div>
      <RepeatSettings {...props} sectionIndex={sectionIndex} />
      <Checkbox label="Collapsed in the designer" checked={collapsed} onCheckedChange={() => props.onToggleCollapse(section.sectionKey)} helpText="Designer view only; the form always shows the section." />
      {canDesign ? (
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="secondary" disabled={sectionIndex === 0} onClick={() => props.onMoveSection(sectionIndex, -1)}>
            Move section up
          </Button>
          <Button size="sm" variant="secondary" disabled={sectionIndex === layout.sections.length - 1} onClick={() => props.onMoveSection(sectionIndex, 1)}>
            Move section down
          </Button>
          <Button size="sm" variant="secondary" onClick={() => props.onDuplicateSection(sectionIndex)}>
            {section.repeat ? "Duplicate as a plain section" : "Duplicate section"}
          </Button>
          <Button size="sm" variant="danger" disabled={layout.sections.length <= 1} onClick={() => props.onRequestRemoveSection(sectionIndex)}>
            Remove section…
          </Button>
        </div>
      ) : null}
    </div>
  );
}

/**
 * "Allow multiple entries" (Phase C3). A section that allows multiple entries holds the fields of one repeating group: each
 * entry has its own values, and those fields stay in the section for life. It can be switched on or off only while the
 * section is empty; the entry settings can change at any time and take effect when the version is published.
 */
function RepeatSettings(props: DesignerInspectorProps & { sectionIndex: number }) {
  const { layout, onChange, sectionIndex, canDesign } = props;
  const section = layout.sections[sectionIndex]!;
  const repeat = section.repeat;
  const placedGroups = new Set(layout.sections.flatMap((candidate) => (candidate.repeat ? [candidate.repeat.groupDefinitionId] : [])));
  const reusable = (props.groups ?? []).filter((group) => !placedGroups.has(group.groupDefinitionId));
  const [reuse, setReuse] = useState<string>("new");
  const group = repeat ? props.groups?.find((candidate) => candidate.groupDefinitionId === repeat.groupDefinitionId) : undefined;
  const update = (patch: Partial<LayoutRepeat>) => repeat && onChange(setSectionRepeat(layout, sectionIndex, { ...repeat, ...patch }));
  const [minText, setMinText] = useState(String(repeat?.minOccurrences ?? 0));
  const [maxText, setMaxText] = useState(String(repeat?.maxOccurrences ?? 10));
  const min = Number(minText);
  const max = Number(maxText);
  const boundsError = !/^\d+$/.test(minText) || !/^\d+$/.test(maxText) ? "Enter whole numbers" : max < 1 || max > REPEAT_CEILING ? `The maximum must be between 1 and ${REPEAT_CEILING}` : min > max ? "The minimum cannot exceed the maximum" : repeat && !repeat.allowAdd && min < 1 ? "When entries cannot be added, the minimum must be at least 1" : undefined;
  const commitBounds = (nextMin: string, nextMax: string) => {
    setMinText(nextMin);
    setMaxText(nextMax);
    const a = Number(nextMin);
    const b = Number(nextMax);
    if (/^\d+$/.test(nextMin) && /^\d+$/.test(nextMax) && b >= 1 && b <= REPEAT_CEILING && a <= b && (repeat?.allowAdd !== false || a >= 1)) {
      update({ minOccurrences: a, maxOccurrences: b });
    }
  };
  return (
    <div className="flex flex-col gap-2 rounded border border-line p-3" data-repeat-settings>
      <Checkbox
        label="Allow multiple entries"
        checked={repeat !== undefined}
        disabled={!canDesign || props.enableRepeatPending === true || section.elements.length > 0}
        helpText={
          section.elements.length > 0
            ? repeat
              ? "Remove the fields from this section before turning this off: they have one value per entry and cannot become record-level fields."
              : "Only an empty section can allow multiple entries. Add a new section, turn this on, then create its fields."
            : "People can add several entries to this section (for example several people or vehicles); each entry has its own values."
        }
        onCheckedChange={(checked) => {
          if (checked) {
            props.onEnableRepeat?.(sectionIndex, reuse === "new" ? null : reuse);
          } else {
            onChange(setSectionRepeat(layout, sectionIndex, undefined));
          }
        }}
      />
      {!repeat && reusable.length > 0 && section.elements.length === 0 ? (
        <Select label="Entries group" value={reuse} disabled={!canDesign} options={[{ value: "new", label: "A new group" }, ...reusable.map((candidate) => ({ value: candidate.groupDefinitionId, label: `${candidate.groupDisplayName} (existing, not placed)` }))]} onValueChange={setReuse} helpText="Reusing a group brings back its fields and the entries already stored in records." />
      ) : null}
      {props.enableRepeatError ? <ErrorPanel error={props.enableRepeatError} /> : null}
      {repeat ? (
        <>
          {group ? <p className="text-xs text-muted">Group key <code>{group.groupKey}</code> (permanent).</p> : null}
          <div className="grid grid-cols-2 gap-2">
            <TextInput label="Minimum entries" inputMode="numeric" value={minText} disabled={!canDesign} onChange={(event) => commitBounds(event.target.value.trim(), maxText)} />
            <TextInput label="Maximum entries" inputMode="numeric" value={maxText} disabled={!canDesign} onChange={(event) => commitBounds(minText, event.target.value.trim())} />
          </div>
          {boundsError ? (
            <p role="alert" className="text-sm font-medium text-[#991b1b]">
              {boundsError}
            </p>
          ) : (
            <p className="text-xs text-muted">
              Between {min} and {max} entries. A new record starts with {min} {min === 1 ? "entry" : "entries"}. At most {REPEAT_CEILING}.
            </p>
          )}
          <Checkbox label="People can add entries" checked={repeat.allowAdd} disabled={!canDesign || (!repeat.allowAdd && repeat.minOccurrences < 1)} onCheckedChange={(checked) => (checked || repeat.minOccurrences >= 1 ? update({ allowAdd: checked }) : undefined)} helpText={repeat.minOccurrences < 1 ? "Set a minimum of at least 1 to turn this off (the record then has exactly that many entries)." : undefined} />
          <Checkbox label="People can remove entries" checked={repeat.allowRemove} disabled={!canDesign} onCheckedChange={(checked) => update({ allowRemove: checked })} />
          <Checkbox label="People can reorder entries" checked={repeat.allowReorder} disabled={!canDesign} onCheckedChange={(checked) => update({ allowReorder: checked })} />
          <p className="text-xs text-muted">Each entry keeps a permanent identity through edits and reordering. Entries are part of the record: they are not shared on their own and are not separate records (use a related form list for independent child records such as addresses).</p>
        </>
      ) : null}
    </div>
  );
}

function SlotOptions(props: DesignerInspectorProps & { sectionIndex: number; target: PlacementTarget }) {
  const { layout, onChange, sectionIndex, target, fields, relationships, canDesign } = props;
  const section = layout.sections[sectionIndex]!;
  const placed = placedIds(layout);
  // A section that allows multiple entries offers only its group's fields; every other section only record-level fields.
  const groupId = section.repeat?.groupDefinitionId ?? null;
  const options = [
    ...fields.filter((field) => !placed.fieldIds.has(field.fieldDefinitionId) && (field.groupDefinitionId ?? null) === groupId).map((field) => ({ value: `field:${field.fieldDefinitionId}`, label: field.fieldDataType === "File" ? `${field.fieldDisplayName} (documents)` : `${field.fieldDisplayName} (${field.fieldDataType})` })),
    ...(groupId ? [] : relationships.filter((relationship) => !placed.relationshipIds.has(relationship.formRelationshipDefinitionId)).map((relationship) => ({ value: `relationship:${relationship.formRelationshipDefinitionId}`, label: `${relationship.relationshipDisplayName} (related list)` }))),
  ];
  const [choice, setChoice] = useState<string | undefined>(undefined);
  const [width, setWidth] = useState("1");
  if (!canDesign) {
    return <p className="text-sm text-muted">You can view this form's design but not change it.</p>;
  }
  const place = () => {
    if (!choice) {
      return;
    }
    const [kind, id] = choice.split(":", 2) as [string, string];
    const columnSpan = Number(width) as 1 | 2 | 3 | 4;
    const startOnNewRow = target.startOnNewRow === true;
    let element: LayoutElement;
    if (kind === "relationship") {
      const relationship = relationships.find((candidate) => candidate.formRelationshipDefinitionId === id)!;
      element = { kind: "relatedFormList", formRelationshipDefinitionId: id, label: relationship.relationshipDisplayName, layout: { columnSpan, startOnNewRow }, summaryFieldKeys: [] };
    } else {
      const field = fields.find((candidate) => candidate.fieldDefinitionId === id)!;
      element = { ...newElementFor(field, columnSpan), layout: { columnSpan, startOnNewRow } };
    }
    const sectionNow = layout.sections[sectionIndex]!;
    const elements = [...sectionNow.elements];
    elements.splice(Math.min(target.insertIndex, elements.length), 0, element);
    const sections = [...layout.sections];
    sections[sectionIndex] = { ...sectionNow, elements };
    onChange({ ...layout, sections });
    props.onPlacedAt?.(target);
    props.onSelect({ kind: "element", key: elementKey(element) });
    props.announce(`${element.label} placed in ${section.title ?? section.sectionKey}`);
  };
  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm text-muted">
        Position {target.insertIndex + 1} in <span className="font-medium text-body">{section.title ?? section.sectionKey}</span>.
      </p>
      <Select label="Place an existing field" value={choice} onValueChange={setChoice} options={options} placeholder={options.length === 0 ? "Every field is already placed" : "Choose a field…"} disabled={options.length === 0} />
      <Select label="Width" value={width} onValueChange={setWidth} options={WIDTH_OPTIONS} />
      <div>
        <Button disabled={!choice} onClick={place}>
          Place field here
        </Button>
      </div>
      <div className="flex flex-col gap-2 border-t border-line pt-3">
        <p className="text-sm font-medium">Or create something new here</p>
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="secondary" onClick={() => props.onCreateAt("field", target)}>
            {groupId ? "New field for each entry…" : "New field…"}
          </Button>
          {groupId ? null : (
            <>
              <Button size="sm" variant="secondary" onClick={() => props.onCreateAt("documents", target)}>
                New document collection…
              </Button>
              <Button size="sm" variant="secondary" onClick={props.onOpenRelatedForms}>
                Related form list…
              </Button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

/** The draft element for a newly placed catalog field (documents get a document-collection element with safe upload defaults). */
export function newElementFor(field: Pick<FieldDefinitionResponse, "fieldDefinitionId" | "fieldDisplayName" | "fieldDataType"> & { optionSelectionMode?: string | undefined }, columnSpan: 1 | 2 | 3 | 4): LayoutElement {
  if (field.fieldDataType === "File") {
    return { kind: "documentCollection", fieldDefinitionId: field.fieldDefinitionId, label: field.fieldDisplayName, required: false, layout: { columnSpan, startOnNewRow: false }, upload: { allowMultiple: true, maxFiles: 20, maxFileSizeBytes: 50 * 1024 * 1024, allowedContentTypes: ["application/pdf", "image/png", "image/jpeg"] } };
  }
  // The control of a dropdown follows the field's permanent selection mode: choose-one is a select, choose-several a multiselect.
  const control = field.fieldDataType === "Option" && field.optionSelectionMode === "Multiple" ? "multiselect" : (CONTROLS_BY_TYPE[field.fieldDataType as FieldDataType]?.[0] ?? "text");
  return { kind: "field", fieldDefinitionId: field.fieldDefinitionId, control, label: field.fieldDisplayName, required: false, layout: { columnSpan, startOnNewRow: false } };
}
