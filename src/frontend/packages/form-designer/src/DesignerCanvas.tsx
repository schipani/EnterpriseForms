import { CONTROLS_BY_TYPE, FormRenderer, packElements, type Breakpoint, type FieldDataType, type FormLayout, type LayoutElement, type RendererField } from "@enterpriseforms/form-renderer";
import { Button, Checkbox, Select, TextInput } from "@enterpriseforms/ui";
import { useEffect, useRef, useState } from "react";
import { appendElement, moveElement, placedIds, removeElement, updateElement } from "./layoutEdits";

export interface DesignerRelationship {
  formRelationshipDefinitionId: string;
  relationshipDisplayName: string;
  childFormDefinitionName: string;
}

export interface DesignerCanvasProps {
  layout: FormLayout;
  onChange: (layout: FormLayout) => void;
  fields: readonly RendererField[];
  relationships: readonly DesignerRelationship[];
  readOnly?: boolean | undefined;
  /** Opens the full tabbed Edit Field dialog (General/Validation/Rules/Security & Encryption/Advanced) for a field or document collection. */
  onOpenFullEditor?: ((fieldDefinitionId: string) => void) | undefined;
}

const spanOptions = [
  { value: "1", label: "1 of 4 columns (25%)" },
  { value: "2", label: "2 of 4 columns (50%)" },
  { value: "3", label: "3 of 4 columns (75%)" },
  { value: "4", label: "Full row (100%)" },
];

/**
 * Designer canvas (PG-033): each section is a real four-column CSS grid — elements occupy the exact row/column their
 * span and "start on a new row" setting produce (reusing the renderer's own deterministic packer, so the canvas and
 * the published form always agree). Click a card to select it (outline + inspector); double-click its title to
 * rename the display label inline; drag a card onto another to reorder, or use the keyboard-accessible Move
 * up/down/narrower/wider buttons — dragging is never the only way. Empty cells at the end of a row offer "+ Add
 * field". Every element keeps its stable field identity; nothing here can change a FieldDefinitionId, field key or
 * storage type, and attachments/values are never touched by a presentation edit.
 */
export function DesignerCanvas({ layout, onChange, fields, relationships, readOnly = false, onOpenFullEditor }: DesignerCanvasProps) {
  const [preview, setPreview] = useState<Breakpoint>("desktop");
  const [previewValues, setPreviewValues] = useState<Record<string, string>>({});
  const [selected, setSelected] = useState<{ sectionIndex: number; index: number } | null>(null);
  const [renaming, setRenaming] = useState<{ sectionIndex: number; index: number } | null>(null);
  const [addingAt, setAddingAt] = useState<number | null>(null);
  const dragSource = useRef<{ sectionIndex: number; index: number } | null>(null);
  const placed = placedIds(layout);
  const fieldById = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  const unplacedFields = fields.filter((field) => !placed.fieldIds.has(field.fieldDefinitionId) && field.fieldDataType !== "File");
  const unplacedDocumentFields = fields.filter((field) => !placed.fieldIds.has(field.fieldDefinitionId) && field.fieldDataType === "File");
  const unplacedRelationships = relationships.filter((relationship) => !placed.relationshipIds.has(relationship.formRelationshipDefinitionId));
  const unplacedAny = unplacedFields.length + unplacedDocumentFields.length + unplacedRelationships.length > 0;

  const add = (sectionIndex: number, element: LayoutElement) => onChange(appendElement(layout, sectionIndex, element));
  const previewWidth = preview === "phone" ? "max-w-[320px]" : preview === "tablet" ? "max-w-[768px]" : "max-w-full";
  const selectedElement = selected ? layout.sections[selected.sectionIndex]?.elements[selected.index] : undefined;
  const selectedField = selectedElement && selectedElement.kind !== "relatedFormList" ? fieldById.get(selectedElement.fieldDefinitionId) : undefined;
  const selectedRelationship = selectedElement && selectedElement.kind === "relatedFormList" ? relationships.find((r) => r.formRelationshipDefinitionId === selectedElement.formRelationshipDefinitionId) : undefined;

  return (
    <div className="grid grid-cols-1 gap-6 xl:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
      <div className="flex min-w-0 flex-col gap-4">
        {layout.sections.map((section, sectionIndex) => {
          const packed = packElements(section.elements, "desktop");
          const lastRow = packed.length > 0 ? Math.max(...packed.map((cell) => cell.row)) : 1;
          const lastRowCells = packed.filter((cell) => cell.row === lastRow);
          const occupied = lastRowCells.reduce((sum, cell) => sum + cell.span, 0);
          const trailingCapacity = packed.length === 0 ? 4 : Math.max(0, 4 - occupied);
          const nextColumn = packed.length === 0 ? 1 : occupied + 1;
          return (
            <section key={section.sectionKey} aria-labelledby={`section-${section.sectionKey}`} className="rounded-lg border border-line bg-surface p-4">
              <h3 id={`section-${section.sectionKey}`} className="text-lg font-semibold">
                {section.title ?? section.sectionKey}
              </h3>
              {section.elements.length === 0 ? <p className="mt-3 text-sm text-muted">No elements yet. Add a field, a related list or a document collection below.</p> : null}
              <ol aria-label={`Elements in ${section.title ?? section.sectionKey}`} className="mt-3 grid grid-cols-4 gap-3" style={{ gridAutoRows: "auto" }}>
                {section.elements.map((element, index) => {
                  const cell = packed.find((candidate) => candidate.element === element);
                  const field = element.kind === "relatedFormList" ? undefined : fieldById.get(element.fieldDefinitionId);
                  const relationship = element.kind === "relatedFormList" ? relationships.find((r) => r.formRelationshipDefinitionId === element.formRelationshipDefinitionId) : undefined;
                  const isSelected = selected?.sectionIndex === sectionIndex && selected.index === index;
                  const title = elementTitle(element, field, relationship);
                  return (
                    <li
                      key={elementKey(element)}
                      style={cell ? { gridColumn: `${cell.column} / span ${cell.span}`, gridRowStart: cell.row } : undefined}
                      draggable={!readOnly}
                      // `aria-selected` is only a valid ARIA attribute on roles such as option/tab/row/gridcell, not on
                      // the implicit `listitem` role these cards carry (axe aria-allowed-attr, PB-14). `aria-current`
                      // is a global attribute valid on any role and still announces the selected card.
                      aria-current={isSelected ? "true" : undefined}
                      className={`rounded border p-3 transition-colors ${isSelected ? "border-focus ring-2 ring-focus" : "border-input"}`}
                      onClick={(event) => {
                        if ((event.target as HTMLElement).closest("input,select,button,textarea,label")) {
                          return;
                        }
                        setSelected({ sectionIndex, index });
                      }}
                      onDragStart={() => {
                        dragSource.current = { sectionIndex, index };
                      }}
                      onDragOver={(event) => {
                        if (dragSource.current && dragSource.current.sectionIndex === sectionIndex) {
                          event.preventDefault();
                        }
                      }}
                      onDrop={(event) => {
                        event.preventDefault();
                        const from = dragSource.current;
                        dragSource.current = null;
                        if (from && from.sectionIndex === sectionIndex && from.index !== index) {
                          onChange(moveElement(layout, sectionIndex, from.index, index));
                          setSelected({ sectionIndex, index });
                        }
                      }}
                    >
                      {/* Always-present identity anchor: the visible title inside ElementEditor is replaced by the rename
                          input while renaming, which would otherwise make this card briefly unidentifiable (by element
                          text content) to both screen readers and any `hasText` locator. This span never disappears. */}
                      <span className="sr-only">
                        Element {index + 1} of {section.elements.length}: {title}
                      </span>
                      <ElementEditor
                        element={element}
                        field={element.kind === "relatedFormList" ? undefined : field}
                        relationship={relationship}
                        position={index + 1}
                        count={section.elements.length}
                        readOnly={readOnly}
                        renaming={renaming?.sectionIndex === sectionIndex && renaming.index === index}
                        onStartRename={() => setRenaming({ sectionIndex, index })}
                        onCommitRename={(label) => {
                          onChange(updateElement(layout, sectionIndex, index, (current) => ({ ...current, label })));
                          setRenaming(null);
                        }}
                        onCancelRename={() => setRenaming(null)}
                        onPatch={(patch) => onChange(updateElement(layout, sectionIndex, index, patch))}
                        onMove={(delta) => onChange(moveElement(layout, sectionIndex, index, index + delta))}
                        onRemove={() => onChange(removeElement(layout, sectionIndex, index))}
                        onOpenFullEditor={element.kind !== "relatedFormList" && onOpenFullEditor ? () => onOpenFullEditor(element.fieldDefinitionId) : undefined}
                      />
                    </li>
                  );
                })}
                {!readOnly && unplacedAny
                  ? Array.from({ length: Math.max(trailingCapacity, section.elements.length === 0 ? 4 : 0) }, (_, offset) => {
                      const column = nextColumn + offset;
                      const cellIndex = sectionIndex * 100 + column;
                      return (
                        <li key={`empty-${sectionIndex}-${column}`} style={{ gridColumn: `${column} / span 1`, gridRowStart: section.elements.length === 0 ? 1 : lastRow }}>
                          {addingAt === cellIndex ? (
                            <EmptyCellPicker
                              capacity={trailingCapacity === 0 ? 4 : trailingCapacity - offset}
                              fields={unplacedFields}
                              documentFields={unplacedDocumentFields}
                              relationships={unplacedRelationships}
                              onCancel={() => setAddingAt(null)}
                              onAddField={(field, span) => {
                                const control = CONTROLS_BY_TYPE[field.fieldDataType as FieldDataType]?.[0] ?? "text";
                                add(sectionIndex, { kind: "field", fieldDefinitionId: field.fieldDefinitionId, control, label: field.fieldDisplayName, required: false, layout: { columnSpan: span, startOnNewRow: false } });
                                setAddingAt(null);
                                setSelected({ sectionIndex, index: section.elements.length });
                              }}
                              onAddDocument={(field, span) => {
                                add(sectionIndex, { kind: "documentCollection", fieldDefinitionId: field.fieldDefinitionId, label: field.fieldDisplayName, required: false, layout: { columnSpan: span, startOnNewRow: false }, upload: { allowMultiple: true, maxFiles: 20, maxFileSizeBytes: 50 * 1024 * 1024, allowedContentTypes: ["application/pdf", "image/png", "image/jpeg"] } });
                                setAddingAt(null);
                              }}
                              onAddRelationship={(relationship, span) => {
                                add(sectionIndex, { kind: "relatedFormList", formRelationshipDefinitionId: relationship.formRelationshipDefinitionId, label: relationship.relationshipDisplayName, layout: { columnSpan: span, startOnNewRow: false }, summaryFieldKeys: [] });
                                setAddingAt(null);
                              }}
                            />
                          ) : (
                            <button
                              type="button"
                              className="flex h-full min-h-20 w-full items-center justify-center rounded border-2 border-dashed border-input text-sm text-muted hover:border-primary hover:text-body"
                              onClick={() => setAddingAt(cellIndex)}
                              aria-label={`Add field in ${section.title ?? section.sectionKey}, column ${column}`}
                            >
                              + Add field
                            </button>
                          )}
                        </li>
                      );
                    })
                  : null}
              </ol>
              {readOnly ? null : (
                <div className="mt-4 grid grid-cols-1 gap-3 md:grid-cols-3">
                  <AddPicker
                    label="Add field"
                    options={unplacedFields.map((field) => ({ value: field.fieldDefinitionId, label: `${field.fieldDisplayName} (${field.fieldDataType})` }))}
                    onAdd={(id) => {
                      const field = fieldById.get(id)!;
                      const control = CONTROLS_BY_TYPE[field.fieldDataType as FieldDataType]?.[0] ?? "text";
                      add(sectionIndex, { kind: "field", fieldDefinitionId: id, control, label: field.fieldDisplayName, required: false, layout: { columnSpan: 4, startOnNewRow: false } });
                    }}
                  />
                  <AddPicker
                    label="Add related list"
                    options={unplacedRelationships.map((relationship) => ({ value: relationship.formRelationshipDefinitionId, label: `${relationship.relationshipDisplayName} → ${relationship.childFormDefinitionName}` }))}
                    onAdd={(id) => {
                      const relationship = relationships.find((r) => r.formRelationshipDefinitionId === id)!;
                      add(sectionIndex, { kind: "relatedFormList", formRelationshipDefinitionId: id, label: relationship.relationshipDisplayName, layout: { columnSpan: 4, startOnNewRow: true }, summaryFieldKeys: [] });
                    }}
                  />
                  <AddPicker
                    label="Add document collection"
                    options={unplacedDocumentFields.map((field) => ({ value: field.fieldDefinitionId, label: field.fieldDisplayName }))}
                    onAdd={(id) => {
                      const field = fieldById.get(id)!;
                      add(sectionIndex, { kind: "documentCollection", fieldDefinitionId: id, label: field.fieldDisplayName, required: false, layout: { columnSpan: 4, startOnNewRow: true }, upload: { allowMultiple: true, maxFiles: 20, maxFileSizeBytes: 50 * 1024 * 1024, allowedContentTypes: ["application/pdf", "image/png", "image/jpeg"] } });
                    }}
                  />
                </div>
              )}
            </section>
          );
        })}
      </div>

      <div className="flex min-w-0 flex-col gap-4">
        <aside aria-labelledby="inspector-heading" className="min-w-0 rounded-lg border border-line bg-surface p-4">
          <h3 id="inspector-heading" className="text-lg font-semibold">
            Properties
          </h3>
          {selectedElement ? (
            <div className="mt-2 flex flex-col gap-2 text-sm">
              <p className="font-medium">{elementTitle(selectedElement, selectedField, selectedRelationship)}</p>
              <p className="text-muted">Selected from the canvas. Edit here, in the card, or open the full editor for validation, rules and security.</p>
              <TextInput
                label="Label"
                value={selectedElement.label}
                disabled={readOnly}
                maxLength={200}
                onChange={(event) => selected && onChange(updateElement(layout, selected.sectionIndex, selected.index, (current) => ({ ...current, label: event.target.value })))}
              />
              <Select
                label="Width"
                value={String(selectedElement.layout.columnSpan)}
                disabled={readOnly}
                options={spanOptions}
                onValueChange={(value) => selected && onChange(updateElement(layout, selected.sectionIndex, selected.index, (current) => ({ ...current, layout: { ...current.layout, columnSpan: Number(value) as 1 | 2 | 3 | 4 } })))}
              />
              {selectedElement.kind !== "relatedFormList" && onOpenFullEditor ? (
                <div>
                  <Button size="sm" variant="secondary" onClick={() => onOpenFullEditor(selectedElement.fieldDefinitionId)}>
                    Open full editor…
                  </Button>
                </div>
              ) : null}
            </div>
          ) : (
            <p className="mt-2 text-sm text-muted">Click a field on the canvas to inspect and quick-edit it here. Click an empty cell to add one.</p>
          )}
        </aside>

        <aside aria-labelledby="preview-heading" className="min-w-0 rounded-lg border border-line bg-page p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h3 id="preview-heading" className="text-lg font-semibold">
              Preview
            </h3>
            <div role="group" aria-label="Preview width" className="flex flex-wrap gap-1">
              {(["phone", "tablet", "desktop"] as const).map((option) => (
                <Button key={option} size="sm" variant={preview === option ? "primary" : "secondary"} aria-pressed={preview === option} onClick={() => setPreview(option)}>
                  {option === "phone" ? "Phone 320" : option === "tablet" ? "Tablet 768" : "Desktop"}
                </Button>
              ))}
            </div>
          </div>
          <p className="mt-1 text-sm text-muted">Synthetic preview values only; no record data is loaded here.</p>
          <div className={`mt-3 ${previewWidth}`}>
            <FormRenderer
              layout={layout}
              fields={fields}
              values={previewValues}
              breakpoint={preview}
              onChange={(key, value) => setPreviewValues((current) => ({ ...current, [key]: value }))}
              renderRelatedList={(element) => <PreviewSlot label={element.label || "Related records"} detail="Child records list (rendered on the record page)" />}
              renderDocumentCollection={(element) => <PreviewSlot label={element.label || "Documents"} detail={`Up to ${element.upload?.maxFiles ?? 20} files`} />}
            />
          </div>
        </aside>
      </div>
    </div>
  );
}

function elementKey(element: LayoutElement): string {
  return element.kind === "relatedFormList" ? `rel:${element.formRelationshipDefinitionId}` : `${element.kind}:${element.fieldDefinitionId}`;
}

/**
 * The card's identifying title shows this element's own layout-version label (`element.label`) — the same value the
 * card's "Label" input, the Properties inspector and the inline rename control all read and write — falling back to
 * the field/relationship catalog name only when the layout label is blank (e.g. a legacy/unlabelled element). This
 * keeps the headline in sync with what double-click rename actually changes; the catalog name alone would never
 * visibly change after a rename, since the catalog's display name is independent of this version's label.
 */
function elementTitle(element: LayoutElement, field: RendererField | undefined, relationship: DesignerRelationship | undefined): string {
  const catalogName = element.kind === "relatedFormList" ? relationship?.relationshipDisplayName ?? "unknown relationship" : field?.fieldDisplayName ?? "unknown field";
  const displayLabel = element.label.trim() || catalogName;
  return element.kind === "relatedFormList" ? `Related list: ${displayLabel}` : element.kind === "documentCollection" ? `Documents: ${displayLabel}` : `Field: ${displayLabel} (${field?.fieldDataType ?? "?"})`;
}

function PreviewSlot({ label, detail }: { label: string; detail: string }) {
  return (
    <div className="rounded border border-dashed border-input bg-surface p-3">
      <p className="font-medium">{label}</p>
      <p className="text-sm text-muted">{detail}</p>
    </div>
  );
}

function AddPicker({ label, options, onAdd }: { label: string; options: Array<{ value: string; label: string }>; onAdd: (id: string) => void }) {
  const [selected, setSelected] = useState<string | undefined>(undefined);
  return (
    <div className="flex flex-col gap-2">
      <Select label={label} value={selected} onValueChange={setSelected} options={options} placeholder={options.length === 0 ? "Nothing left to place" : "Choose…"} disabled={options.length === 0} />
      <Button
        variant="secondary"
        size="sm"
        disabled={!selected}
        onClick={() => {
          if (selected) {
            onAdd(selected);
            setSelected(undefined);
          }
        }}
      >
        {label}
      </Button>
    </div>
  );
}

/** Inline chooser shown when an empty grid cell's "+ Add field" affordance is activated (C1-UX §B). */
function EmptyCellPicker({
  capacity,
  fields,
  documentFields,
  relationships,
  onCancel,
  onAddField,
  onAddDocument,
  onAddRelationship,
}: {
  capacity: number;
  fields: readonly RendererField[];
  documentFields: readonly RendererField[];
  relationships: readonly DesignerRelationship[];
  onCancel: () => void;
  onAddField: (field: RendererField, span: 1 | 2 | 3 | 4) => void;
  onAddDocument: (field: RendererField, span: 1 | 2 | 3 | 4) => void;
  onAddRelationship: (relationship: DesignerRelationship, span: 1 | 2 | 3 | 4) => void;
}) {
  const span = Math.min(4, Math.max(1, capacity)) as 1 | 2 | 3 | 4;
  const options = [
    ...fields.map((field) => ({ value: `field:${field.fieldDefinitionId}`, label: `${field.fieldDisplayName} (${field.fieldDataType})` })),
    ...documentFields.map((field) => ({ value: `document:${field.fieldDefinitionId}`, label: `${field.fieldDisplayName} (documents)` })),
    ...relationships.map((relationship) => ({ value: `relationship:${relationship.formRelationshipDefinitionId}`, label: `${relationship.relationshipDisplayName} (related list)` })),
  ];
  const [choice, setChoice] = useState<string | undefined>(undefined);
  return (
    <div role="group" aria-label="Add field to this cell" className="flex h-full flex-col gap-2 rounded border-2 border-primary bg-page p-2">
      <Select label="Field to place here" value={choice} onValueChange={setChoice} options={options} placeholder="Choose a field…" />
      <div className="flex gap-2">
        <Button
          size="sm"
          disabled={!choice}
          onClick={() => {
            if (!choice) {
              return;
            }
            const [kind, id] = choice.split(":", 2) as [string, string];
            if (kind === "field") {
              onAddField(fields.find((field) => field.fieldDefinitionId === id)!, span);
            } else if (kind === "document") {
              onAddDocument(documentFields.find((field) => field.fieldDefinitionId === id)!, span);
            } else {
              onAddRelationship(relationships.find((relationship) => relationship.formRelationshipDefinitionId === id)!, span);
            }
          }}
        >
          Place
        </Button>
        <Button size="sm" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  );
}

function ElementEditor({
  element,
  field,
  relationship,
  position,
  count,
  readOnly,
  renaming,
  onStartRename,
  onCommitRename,
  onCancelRename,
  onPatch,
  onMove,
  onRemove,
  onOpenFullEditor,
}: {
  element: LayoutElement;
  field: RendererField | undefined;
  relationship: DesignerRelationship | undefined;
  position: number;
  count: number;
  readOnly: boolean;
  renaming: boolean;
  onStartRename: () => void;
  onCommitRename: (label: string) => void;
  onCancelRename: () => void;
  onPatch: (patch: (element: LayoutElement) => LayoutElement) => void;
  onMove: (delta: number) => void;
  onRemove: () => void;
  onOpenFullEditor?: (() => void) | undefined;
}) {
  const title = elementTitle(element, field, relationship);
  const controls = element.kind === "field" && field ? (CONTROLS_BY_TYPE[field.fieldDataType as FieldDataType] ?? ["text"]) : [];
  const [draftLabel, setDraftLabel] = useState(element.label);
  useEffect(() => {
    if (renaming) {
      setDraftLabel(element.label);
    }
  }, [renaming, element.label]);
  const span = element.layout.columnSpan;
  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        {renaming ? (
          <TextInput
            label="Display label"
            value={draftLabel}
            maxLength={200}
            autoFocus
            onChange={(event) => setDraftLabel(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter") {
                onCommitRename(draftLabel.trim() || title);
              } else if (event.key === "Escape") {
                setDraftLabel(element.label);
                onCancelRename();
              }
            }}
            onBlur={() => onCommitRename(draftLabel.trim() || title)}
          />
        ) : (
          <p className="font-medium" onDoubleClick={() => !readOnly && onStartRename()} title={readOnly ? undefined : "Double-click to rename the display label"}>
            {/* Position/identity for screen readers is announced by the always-present sr-only span in the parent <li>
                (DesignerCanvas), which survives rename mode; this visible text is not duplicated with that prefix. */}
            {title}
          </p>
        )}
        {readOnly ? null : (
          <div className="flex flex-wrap gap-1">
            <Button size="sm" variant="secondary" disabled={position === 1} onClick={() => onMove(-1)} aria-label={`Move ${title} up`}>
              ↑ Up
            </Button>
            <Button size="sm" variant="secondary" disabled={position === count} onClick={() => onMove(1)} aria-label={`Move ${title} down`}>
              ↓ Down
            </Button>
            <Button size="sm" variant="secondary" disabled={span <= 1} onClick={() => onPatch((current) => ({ ...current, layout: { ...current.layout, columnSpan: Math.max(1, span - 1) as 1 | 2 | 3 | 4 } }))} aria-label={`Narrower: decrease width of ${title}`}>
              ← Narrower
            </Button>
            <Button size="sm" variant="secondary" disabled={span >= 4} onClick={() => onPatch((current) => ({ ...current, layout: { ...current.layout, columnSpan: Math.min(4, span + 1) as 1 | 2 | 3 | 4 } }))} aria-label={`Wider: increase width of ${title}`}>
              Wider →
            </Button>
            {onOpenFullEditor ? (
              <Button size="sm" variant="ghost" onClick={onOpenFullEditor} aria-label={`Configure: open full editor for ${title}`}>
                Configure
              </Button>
            ) : null}
            <Button size="sm" variant="ghost" onClick={onRemove} aria-label={`Remove ${title} from layout`}>
              Remove
            </Button>
          </div>
        )}
      </div>
      <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
        <TextInput label="Label" value={element.label} disabled={readOnly} maxLength={200} onChange={(event) => onPatch((current) => ({ ...current, label: event.target.value }))} />
        <Select label="Width" value={String(element.layout.columnSpan)} disabled={readOnly} options={spanOptions} onValueChange={(value) => onPatch((current) => ({ ...current, layout: { ...current.layout, columnSpan: Number(value) as 1 | 2 | 3 | 4 } }))} />
        <Checkbox label="Start on a new row" checked={element.layout.startOnNewRow} disabled={readOnly} onCheckedChange={(checked) => onPatch((current) => ({ ...current, layout: { ...current.layout, startOnNewRow: checked } }))} />
        {element.kind !== "relatedFormList" ? <Checkbox label="Required" checked={element.required} disabled={readOnly} onCheckedChange={(checked) => onPatch((current) => (current.kind === "relatedFormList" ? current : { ...current, required: checked }))} /> : null}
        {element.kind === "field" && controls.length > 1 ? <Select label="Control" value={element.control} disabled={readOnly} options={controls.map((control) => ({ value: control, label: control }))} onValueChange={(value) => onPatch((current) => (current.kind === "field" ? { ...current, control: value as typeof current.control } : current))} /> : null}
        {element.kind === "field" && field?.fieldDataType === "String" ? <TextInput label="Maximum length" type="text" inputMode="numeric" value={String(element.validation?.maxLength ?? "")} disabled={readOnly} onChange={(event) => onPatch((current) => (current.kind === "field" ? { ...current, validation: { ...(current.validation ?? {}), ...(event.target.value ? { maxLength: Number(event.target.value) } : {}) } } : current))} /> : null}
        {element.kind === "documentCollection" ? <TextInput label="Maximum files" type="text" inputMode="numeric" value={String(element.upload?.maxFiles ?? 20)} disabled={readOnly} onChange={(event) => onPatch((current) => (current.kind === "documentCollection" ? { ...current, upload: { ...(current.upload ?? {}), maxFiles: Math.max(1, Math.min(100, Number(event.target.value) || 1)) } } : current))} /> : null}
        {element.kind === "documentCollection" ? <TextInput label="Maximum file size (MB)" type="text" inputMode="numeric" helpText="1–500 MB per document" value={String(Math.round((element.upload?.maxFileSizeBytes ?? 50 * 1024 * 1024) / (1024 * 1024)))} disabled={readOnly} onChange={(event) => onPatch((current) => (current.kind === "documentCollection" ? { ...current, upload: { ...(current.upload ?? {}), maxFileSizeBytes: Math.max(1, Math.min(500, Number(event.target.value) || 1)) * 1024 * 1024 } } : current))} /> : null}
        {element.kind === "documentCollection" ? (
          <TextInput
            label="Allowed content types"
            helpText="Comma-separated MIME types; empty allows any type the server accepts"
            value={(element.upload?.allowedContentTypes ?? []).join(", ")}
            disabled={readOnly}
            onChange={(event) =>
              onPatch((current) =>
                current.kind === "documentCollection"
                  ? { ...current, upload: { ...(current.upload ?? {}), allowedContentTypes: event.target.value.split(",").map((type) => type.trim()).filter(Boolean) } }
                  : current,
              )
            }
          />
        ) : null}
      </div>
    </div>
  );
}
