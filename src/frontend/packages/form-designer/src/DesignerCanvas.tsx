import { CONTROLS_BY_TYPE, FormRenderer, type Breakpoint, type FieldDataType, type FormLayout, type LayoutElement, type RendererField } from "@enterpriseforms/form-renderer";
import { Button, Checkbox, Select, TextInput } from "@enterpriseforms/ui";
import { useState } from "react";
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
}

const spanOptions = [
  { value: "1", label: "1 of 4 columns (25%)" },
  { value: "2", label: "2 of 4 columns (50%)" },
  { value: "3", label: "3 of 4 columns (75%)" },
  { value: "4", label: "Full row (100%)" },
];

/**
 * Designer canvas (PG-033): an ordered element list per section with span/row-break/required/label controls, keyboard
 * reordering (Move up/down buttons) and a live renderer preview at phone/tablet/desktop widths. Field identities come
 * from the catalog; the canvas never invents ids or changes storage types.
 */
export function DesignerCanvas({ layout, onChange, fields, relationships, readOnly = false }: DesignerCanvasProps) {
  const [preview, setPreview] = useState<Breakpoint>("desktop");
  const [previewValues, setPreviewValues] = useState<Record<string, string>>({});
  const placed = placedIds(layout);
  const fieldById = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  const unplacedFields = fields.filter((field) => !placed.fieldIds.has(field.fieldDefinitionId) && field.fieldDataType !== "File");
  const unplacedDocumentFields = fields.filter((field) => !placed.fieldIds.has(field.fieldDefinitionId) && field.fieldDataType === "File");
  const unplacedRelationships = relationships.filter((relationship) => !placed.relationshipIds.has(relationship.formRelationshipDefinitionId));

  const add = (sectionIndex: number, element: LayoutElement) => onChange(appendElement(layout, sectionIndex, element));
  const previewWidth = preview === "phone" ? "max-w-[320px]" : preview === "tablet" ? "max-w-[768px]" : "max-w-full";

  return (
    <div className="grid grid-cols-1 gap-6 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <div className="flex min-w-0 flex-col gap-4">
        {layout.sections.map((section, sectionIndex) => (
          <section key={section.sectionKey} aria-labelledby={`section-${section.sectionKey}`} className="rounded-lg border border-line bg-surface p-4">
            <h3 id={`section-${section.sectionKey}`} className="text-lg font-semibold">
              {section.title ?? section.sectionKey}
            </h3>
            <ol className="mt-3 flex flex-col gap-3" aria-label={`Elements in ${section.title ?? section.sectionKey}`}>
              {section.elements.length === 0 ? <li className="text-sm text-muted">No elements yet. Add a field, a related list or a document collection below.</li> : null}
              {section.elements.map((element, index) => (
                <li key={elementKey(element)} className="rounded border border-input p-3">
                  <ElementEditor
                    element={element}
                    field={element.kind === "relatedFormList" ? undefined : fieldById.get(element.fieldDefinitionId)}
                    relationship={element.kind === "relatedFormList" ? relationships.find((r) => r.formRelationshipDefinitionId === element.formRelationshipDefinitionId) : undefined}
                    position={index + 1}
                    count={section.elements.length}
                    readOnly={readOnly}
                    onPatch={(patch) => onChange(updateElement(layout, sectionIndex, index, patch))}
                    onMove={(delta) => onChange(moveElement(layout, sectionIndex, index, index + delta))}
                    onRemove={() => onChange(removeElement(layout, sectionIndex, index))}
                  />
                </li>
              ))}
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
        ))}
      </div>

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
  );
}

function elementKey(element: LayoutElement): string {
  return element.kind === "relatedFormList" ? `rel:${element.formRelationshipDefinitionId}` : `${element.kind}:${element.fieldDefinitionId}`;
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

function ElementEditor({ element, field, relationship, position, count, readOnly, onPatch, onMove, onRemove }: { element: LayoutElement; field: RendererField | undefined; relationship: DesignerRelationship | undefined; position: number; count: number; readOnly: boolean; onPatch: (patch: (element: LayoutElement) => LayoutElement) => void; onMove: (delta: number) => void; onRemove: () => void }) {
  const title = element.kind === "relatedFormList" ? `Related list: ${relationship?.relationshipDisplayName ?? "unknown relationship"}` : element.kind === "documentCollection" ? `Documents: ${field?.fieldDisplayName ?? "unknown field"}` : `Field: ${field?.fieldDisplayName ?? "unknown field"} (${field?.fieldDataType ?? "?"})`;
  const controls = element.kind === "field" && field ? (CONTROLS_BY_TYPE[field.fieldDataType as FieldDataType] ?? ["text"]) : [];
  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="font-medium">
          <span className="sr-only">Element {position} of {count}: </span>
          {title}
        </p>
        {readOnly ? null : (
          <div className="flex gap-1">
            <Button size="sm" variant="secondary" disabled={position === 1} onClick={() => onMove(-1)} aria-label={`Move ${title} up`}>
              ↑ Up
            </Button>
            <Button size="sm" variant="secondary" disabled={position === count} onClick={() => onMove(1)} aria-label={`Move ${title} down`}>
              ↓ Down
            </Button>
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
