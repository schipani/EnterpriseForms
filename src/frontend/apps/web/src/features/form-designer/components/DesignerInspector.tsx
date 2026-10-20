import type { FieldDefinitionResponse, RelationshipDefinitionResponse, StrengthenFieldProtectionRequest } from "@enterpriseforms/api-client";
import { applyEditValues, elementKey, findElement, moveElementTo, placedIds, toEditValues, updateElementByKey, updateSection, validateEditValues, type DesignerSelection, type FieldEditValues } from "@enterpriseforms/form-designer";
import { CONTROLS_BY_TYPE, type DocumentElement, type FieldDataType, type FieldElement, type FormLayout, type LayoutElement, type RelatedElement } from "@enterpriseforms/form-renderer";
import { Badge, Button, Checkbox, Select, StatusMessage, Tabs, TextArea, TextInput } from "@enterpriseforms/ui";
import { useState, type ReactNode } from "react";
import { CurrentProtectionSection } from "./FieldSecuritySection";
import { OptionsEditor } from "./OptionsEditor";
import { RulesTab } from "./RulesTab";

export interface PlacementTarget {
  sectionKey: string;
  insertIndex: number;
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
  /** Tab to open when a field is selected from elsewhere (e.g. the Rules overview). */
  requestedTab?: string | undefined;
}

const WIDTH_OPTIONS = [
  { value: "1", label: "1 column" },
  { value: "2", label: "2 columns" },
  { value: "3", label: "3 columns" },
  { value: "4", label: "4 columns (full row)" },
];

const TYPE_LABEL: Record<string, string> = { String: "Text (String)", Integer: "Whole number (Integer)", Decimal: "Decimal", Date: "Date", DateTime: "Date and time", Guid: "Identifier (Guid)", Boolean: "Yes/No (Boolean)", Option: "Dropdown", File: "File (document collection)" };

/**
 * Right-hand inspector (C1 designer rebuild §2): the single place where a selection is edited. A placed field or
 * document collection shows **Field Properties** with the five stable tabs General / Validation / Rules / Security &
 * Encryption / Advanced; a section shows **Section Properties**; an empty cell shows the Add Field options for that
 * exact position. Presentation edits change the draft layout (undoable; saved with Save draft). Security changes
 * reuse the existing Authority-backed workflow — the inspector never offers its own encryption switch.
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
      body = <ElementProperties key={selection.key} {...props} element={element} sectionIndex={position.sectionIndex} index={position.index} />;
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
      body = <SlotOptions key={`${selection.sectionKey}:${selection.insertIndex}`} {...props} sectionIndex={sectionIndex} target={{ sectionKey: selection.sectionKey, insertIndex: selection.insertIndex }} />;
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
    <>
      <p className="text-sm text-muted">Select a field to edit its properties, a section title to edit the section, or an empty “+ Add Field” cell to place a field exactly there.</p>
      <p className="text-sm">
        {layout.sections.length} {layout.sections.length === 1 ? "section" : "sections"} · {unplaced === 0 ? "every field is placed" : `${unplaced} not yet placed`}
      </p>
    </>
  );
}

function placementControls({ layout, onChange, onSelect, announce, canDesign }: DesignerInspectorProps, element: LayoutElement, sectionIndex: number, index: number) {
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
    <>
      <Select
        label="Field width"
        value={String(element.layout.columnSpan)}
        disabled={!canDesign}
        options={WIDTH_OPTIONS}
        onValueChange={(value) => onChange(updateElementByKey(layout, key, (current) => ({ ...current, layout: { ...current.layout, columnSpan: Number(value) as 1 | 2 | 3 | 4 } })))}
      />
      <Checkbox label="Start on a new row" checked={element.layout.startOnNewRow} disabled={!canDesign} onCheckedChange={(checked) => onChange(updateElementByKey(layout, key, (current) => ({ ...current, layout: { ...current.layout, startOnNewRow: checked } })))} />
      <Select
        label="Section"
        value={section.sectionKey}
        disabled={!canDesign}
        options={layout.sections.map((candidate) => ({ value: candidate.sectionKey, label: candidate.title?.trim() || candidate.sectionKey }))}
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
    </>
  );
}

function ElementProperties(props: DesignerInspectorProps & { element: LayoutElement; sectionIndex: number; index: number }) {
  const { element } = props;
  const [tab, setTab] = useState(props.requestedTab ?? "general");
  if (element.kind === "relatedFormList") {
    return <RelatedListProperties {...props} element={element} tab={tab} setTab={setTab} />;
  }
  const field = props.fields.find((candidate) => candidate.fieldDefinitionId === element.fieldDefinitionId);
  if (!field) {
    return <StatusMessage tone="warning" title="Unknown field">This placement refers to a field that is not in this form's catalog. Remove it from the layout.</StatusMessage>;
  }
  return <FieldProperties {...props} element={element} field={field} tab={tab} setTab={setTab} />;
}

function FieldProperties(props: DesignerInspectorProps & { element: FieldElement | DocumentElement; field: FieldDefinitionResponse; sectionIndex: number; index: number; tab: string; setTab: (tab: string) => void }) {
  const { element, field, layout, onChange, canDesign } = props;
  const key = elementKey(element);
  const dataType = field.fieldDataType;
  // Values derive from the draft element on every render (so Undo/Redo are reflected); only input that does not yet
  // validate is held locally until it does — switching tabs never loses it, because this state lives above the tabs.
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
  const supportsPlaceholder = element.kind === "field" && (dataType === "String" || dataType === "Integer" || dataType === "Decimal");
  const encrypted = field.fieldProtectionMode === "ApplicationEncrypted";

  const general = (
    <div className="flex flex-col gap-3">
      <TextInput label="Field label" value={values.label} required maxLength={200} disabled={!canDesign} error={errors.label} onChange={(event) => set("label", event.target.value)} />
      <TextInput label="Field type" value={dataType === "Option" ? (field.optionSelectionMode === "Multiple" ? "Dropdown (Multi Select)" : "Dropdown (Single Select)") : (TYPE_LABEL[dataType] ?? dataType)} readOnly />
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
      {dataType === "Option" && element.kind === "field" ? (
        <OptionsEditor formDefinitionId={props.formDefinitionId} field={field} element={element} canDesign={canDesign} onElementChange={(next) => onChange(updateElementByKey(layout, key, () => next))} />
      ) : null}
      {controls.length > 1 && element.kind === "field" ? (
        <Select label="Control" value={element.control} disabled={!canDesign} options={controls.map((control) => ({ value: control, label: control === "textarea" ? "Multi-line text" : "Single-line text" }))} onValueChange={(value) => onChange(updateElementByKey(layout, key, (current) => (current.kind === "field" ? { ...current, control: value as FieldElement["control"] } : current)))} />
      ) : null}
      <Checkbox label="Required" checked={values.required} disabled={!canDesign} onCheckedChange={(checked) => set("required", checked)} />
      {placementControls(props, element, props.sectionIndex, props.index)}
    </div>
  );

  const validation = (
    <div className="flex flex-col gap-3">
      {dataType === "String" ? (
        <>
          <TextInput label="Minimum length" inputMode="numeric" value={values.minLength} disabled={!canDesign} error={errors.minLength} onChange={(event) => set("minLength", event.target.value)} />
          <TextInput label="Maximum length" inputMode="numeric" value={values.maxLength} disabled={!canDesign} error={errors.maxLength} onChange={(event) => set("maxLength", event.target.value)} />
        </>
      ) : null}
      {dataType === "Integer" || dataType === "Decimal" ? (
        <>
          <TextInput label="Minimum value" inputMode="decimal" value={values.minimum} disabled={!canDesign} error={errors.minimum} onChange={(event) => set("minimum", event.target.value)} />
          <TextInput label="Maximum value" inputMode="decimal" value={values.maximum} disabled={!canDesign} error={errors.maximum} onChange={(event) => set("maximum", event.target.value)} />
        </>
      ) : null}
      {dataType === "Date" || dataType === "DateTime" ? (
        <>
          <TextInput label="Earliest date" type="date" value={values.minDate} disabled={!canDesign} error={errors.minDate} onChange={(event) => set("minDate", event.target.value)} />
          <TextInput label="Latest date" type="date" value={values.maxDate} disabled={!canDesign} error={errors.maxDate} onChange={(event) => set("maxDate", event.target.value)} />
        </>
      ) : null}
      {dataType === "File" ? (
        <>
          <TextInput label="Maximum files" inputMode="numeric" value={values.maxFiles} disabled={!canDesign} error={errors.maxFiles} onChange={(event) => set("maxFiles", event.target.value)} />
          <TextInput label="Maximum file size (MB)" inputMode="numeric" value={values.maxFileSizeMb} disabled={!canDesign} error={errors.maxFileSizeMb} onChange={(event) => set("maxFileSizeMb", event.target.value)} />
          <TextInput label="Allowed content types" value={values.allowedContentTypes} disabled={!canDesign} helpText="Comma-separated MIME types, e.g. application/pdf, text/plain. Documents already stored are never removed." onChange={(event) => set("allowedContentTypes", event.target.value)} />
        </>
      ) : null}
      {dataType === "Boolean" || dataType === "Option" || dataType === "Guid" ? <p className="text-sm text-muted">{dataType === "Option" ? "A dropdown accepts only its active options (the server enforces this on every save). Use Required (General tab) to demand a choice." : "This storage type has no extra constraints beyond Required (General tab) and its type rules, which the server enforces."}</p> : null}
      <p className="text-xs text-muted">Rules apply to new saves after publishing; stored values and documents are never rewritten.</p>
    </div>
  );

  const items = [
    { value: "general", label: "General", content: general },
    { value: "validation", label: "Validation", content: validation },
    {
      value: "rules",
      label: "Rules",
      content: element.kind === "field" ? <RulesTab layout={layout} element={element} field={field} fields={props.fields} canDesign={canDesign} onElementChange={(next) => onChange(updateElementByKey(layout, key, () => next))} /> : <RulesNotice />,
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
        <div className="flex flex-col gap-3">
          <TextInput label="Field UUID" value={field.fieldDefinitionId} readOnly helpText="Stable identity (FieldDefinitionId); renaming or moving never changes it." />
          <TextInput label="Field key" value={field.fieldKey} readOnly />
          <TextInput label="Catalog name" value={field.fieldDisplayName} readOnly helpText="Name in the field catalog; the label above is this form version's display label." />
          <TextInput label="Field definition revision" value={String(field.fieldDefinitionRevision)} readOnly />
          <TextInput label="Lifecycle status" value={field.lifecycleStatus} readOnly />
          <Button size="sm" variant="secondary" onClick={() => props.onOpenFieldEditor(field.fieldDefinitionId)}>
            {canDesign ? "Edit catalog name and description…" : "View field details…"}
          </Button>
        </div>
      ),
    },
  ];
  return <Tabs label={`Properties of ${values.label || field.fieldDisplayName}`} density="compact" value={props.tab} onValueChange={props.setTab} items={items} />;
}

function RelatedListProperties(props: DesignerInspectorProps & { element: RelatedElement; sectionIndex: number; index: number; tab: string; setTab: (tab: string) => void }) {
  const { element, layout, onChange, canDesign } = props;
  const key = elementKey(element);
  const relationship = props.relationships.find((candidate) => candidate.formRelationshipDefinitionId === element.formRelationshipDefinitionId);
  const items = [
    {
      value: "general",
      label: "General",
      content: (
        <div className="flex flex-col gap-3">
          <TextInput label="List label" value={element.label} maxLength={200} disabled={!canDesign} onChange={(event) => onChange(updateElementByKey(layout, key, (current) => ({ ...current, label: event.target.value.replace(/[<>]/g, "") })))} />
          <TextInput label="Child form" value={relationship?.childFormDefinitionName ?? "Unknown"} readOnly />
          {placementControls(props, element, props.sectionIndex, props.index)}
        </div>
      ),
    },
    { value: "validation", label: "Validation", content: <p className="text-sm text-muted">Allowed number of child records: {relationship ? `${relationship.minimumChildCount}–${relationship.maximumChildCount}` : "unknown"}. Bounds belong to the relationship definition and are enforced by the server.</p> },
    { value: "rules", label: "Rules", content: <RulesNotice /> },
    { value: "security", label: "Security & Encryption", content: <p className="text-sm text-muted">Each child is an independent record. The Authorization Authority decides access per child record; a share of the parent never covers its children, and each child field keeps its own encryption policy.</p> },
    {
      value: "advanced",
      label: "Advanced",
      content: (
        <div className="flex flex-col gap-3">
          <TextInput label="Relationship UUID" value={element.formRelationshipDefinitionId} readOnly helpText="Stable identity; moving the list between sections never changes its child links." />
          <Button size="sm" variant="secondary" onClick={props.onOpenRelatedForms}>
            Manage related forms…
          </Button>
        </div>
      ),
    },
  ];
  return <Tabs label={`Properties of ${element.label || "related list"}`} density="compact" value={props.tab} onValueChange={props.setTab} items={items} />;
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
            Duplicate section
          </Button>
          <Button size="sm" variant="danger" disabled={layout.sections.length <= 1} onClick={() => props.onRequestRemoveSection(sectionIndex)}>
            Remove section…
          </Button>
        </div>
      ) : null}
    </div>
  );
}

function SlotOptions(props: DesignerInspectorProps & { sectionIndex: number; target: PlacementTarget }) {
  const { layout, onChange, sectionIndex, target, fields, relationships, canDesign } = props;
  const section = layout.sections[sectionIndex]!;
  const placed = placedIds(layout);
  const options = [
    ...fields.filter((field) => !placed.fieldIds.has(field.fieldDefinitionId)).map((field) => ({ value: `field:${field.fieldDefinitionId}`, label: field.fieldDataType === "File" ? `${field.fieldDisplayName} (documents)` : `${field.fieldDisplayName} (${field.fieldDataType})` })),
    ...relationships.filter((relationship) => !placed.relationshipIds.has(relationship.formRelationshipDefinitionId)).map((relationship) => ({ value: `relationship:${relationship.formRelationshipDefinitionId}`, label: `${relationship.relationshipDisplayName} (related list)` })),
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
    let element: LayoutElement;
    if (kind === "relationship") {
      const relationship = relationships.find((candidate) => candidate.formRelationshipDefinitionId === id)!;
      element = { kind: "relatedFormList", formRelationshipDefinitionId: id, label: relationship.relationshipDisplayName, layout: { columnSpan, startOnNewRow: false }, summaryFieldKeys: [] };
    } else {
      const field = fields.find((candidate) => candidate.fieldDefinitionId === id)!;
      element = newElementFor(field, columnSpan);
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
            New field…
          </Button>
          <Button size="sm" variant="secondary" onClick={() => props.onCreateAt("documents", target)}>
            New document collection…
          </Button>
          <Button size="sm" variant="secondary" onClick={props.onOpenRelatedForms}>
            Related form list…
          </Button>
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
