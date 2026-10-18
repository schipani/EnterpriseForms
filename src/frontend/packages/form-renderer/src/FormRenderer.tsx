import { Checkbox, Select, TextArea, TextInput } from "@enterpriseforms/ui";
import { useEffect, useRef, useState, type ReactNode } from "react";
import type { DocumentElement, FieldElement, FormLayout, LayoutElement, RelatedElement } from "./layout";
import { breakpointForWidth, packElements, visibleColumns, type Breakpoint } from "./packer";
import type { ErrorMap, RendererField, ValueMap } from "./validation";

export interface FormRendererProps {
  layout: FormLayout;
  fields: readonly RendererField[];
  values: ValueMap;
  errors?: ErrorMap | undefined;
  onChange?: ((fieldKey: string, value: string) => void) | undefined;
  readOnly?: boolean | undefined;
  /** Force a breakpoint (designer preview); otherwise measured from the container width. */
  breakpoint?: Breakpoint | undefined;
  /** Slots for elements the renderer does not own: child-record lists and document collections. */
  renderRelatedList?: ((element: RelatedElement) => ReactNode) | undefined;
  renderDocumentCollection?: ((element: DocumentElement, field: RendererField | undefined) => ReactNode) | undefined;
  /** Fields present in the layout but not authorized/known are omitted silently (projection rules). */
  idPrefix?: string | undefined;
}

const gapClass = { small: "gap-2", medium: "gap-4", large: "gap-6" } as const;

export function useContainerBreakpoint<T extends HTMLElement>(forced: Breakpoint | undefined): [React.RefObject<T | null>, Breakpoint] {
  const ref = useRef<T | null>(null);
  const [measured, setMeasured] = useState<Breakpoint>("desktop");
  useEffect(() => {
    if (forced || !ref.current || typeof ResizeObserver === "undefined") {
      return;
    }
    const observer = new ResizeObserver((entries) => {
      const width = entries[0]?.contentRect.width ?? 1024;
      setMeasured(breakpointForWidth(width));
    });
    observer.observe(ref.current);
    return () => observer.disconnect();
  }, [forced]);
  return [ref, forced ?? measured];
}

/** Four-column logical grid rendered as 1/2/4 visible columns from the measured container width (CLAUDE.md §4I). */
export function FormRenderer({ layout, fields, values, errors = {}, onChange, readOnly = false, breakpoint, renderRelatedList, renderDocumentCollection }: FormRendererProps) {
  const [ref, active] = useContainerBreakpoint<HTMLDivElement>(breakpoint);
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  const columns = visibleColumns(active);
  return (
    <div ref={ref} className="flex flex-col gap-6" data-breakpoint={active}>
      {layout.sections.map((section) => {
        const visible = section.elements.filter((element) => element.kind !== "field" || byId.has(element.fieldDefinitionId));
        const cells = packElements(visible, active);
        return (
          <fieldset key={section.sectionKey} className="min-w-0 rounded-lg border border-line bg-surface p-4">
            <legend className="px-1 text-lg font-semibold">{section.title ?? section.sectionKey}</legend>
            <div className={`grid ${gapClass[section.layout?.columnGap ?? "medium"]}`} style={{ gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))` }}>
              {cells.map((cell) => (
                <div key={elementKey(cell.element)} className="min-w-0" style={{ gridColumn: `${cell.column} / span ${cell.span}`, gridRow: cell.row }}>
                  {renderElement(cell.element, byId, values, errors, onChange, readOnly, renderRelatedList, renderDocumentCollection)}
                </div>
              ))}
            </div>
          </fieldset>
        );
      })}
    </div>
  );
}

function elementKey(element: LayoutElement): string {
  return element.kind === "relatedFormList" ? `rel:${element.formRelationshipDefinitionId}` : `${element.kind}:${element.fieldDefinitionId}`;
}

function renderElement(
  element: LayoutElement,
  byId: Map<string, RendererField>,
  values: ValueMap,
  errors: ErrorMap,
  onChange: FormRendererProps["onChange"],
  readOnly: boolean,
  renderRelatedList: FormRendererProps["renderRelatedList"],
  renderDocumentCollection: FormRendererProps["renderDocumentCollection"],
): ReactNode {
  if (element.kind === "relatedFormList") {
    return renderRelatedList ? renderRelatedList(element) : <p className="text-sm text-muted">{element.label || "Related records"} (not available here)</p>;
  }
  if (element.kind === "documentCollection") {
    return renderDocumentCollection ? renderDocumentCollection(element, byId.get(element.fieldDefinitionId)) : <p className="text-sm text-muted">{element.label || "Documents"} (not available here)</p>;
  }
  const field = byId.get(element.fieldDefinitionId);
  if (!field) {
    return null;
  }
  return <FieldControl element={element} field={field} value={values[field.fieldKey] ?? ""} error={errors[field.fieldKey]} onChange={(value) => onChange?.(field.fieldKey, value)} readOnly={readOnly} />;
}

export function FieldControl({ element, field, value, error, onChange, readOnly }: { element: FieldElement; field: RendererField; value: string; error: string | undefined; onChange: (value: string) => void; readOnly: boolean }) {
  const label = element.label || field.fieldDisplayName;
  const common = { label, helpText: element.helpText, error, required: element.required, disabled: readOnly } as const;
  if (readOnly) {
    return <ReadOnlyValue label={label} value={displayValue(field, value)} helpText={element.helpText} />;
  }
  switch (field.fieldDataType) {
    case "String":
      return element.control === "textarea" ? (
        <TextArea {...common} value={value} maxLength={element.validation?.maxLength ?? 4000} onChange={(event) => onChange(event.target.value)} />
      ) : (
        <TextInput {...common} type="text" value={value} maxLength={element.validation?.maxLength ?? 4000} autoComplete="off" onChange={(event) => onChange(event.target.value)} />
      );
    case "Integer":
      return <TextInput {...common} type="text" inputMode="numeric" pattern="-?[0-9]*" value={value} onChange={(event) => onChange(event.target.value)} />;
    case "Decimal":
      return <TextInput {...common} type="text" inputMode="decimal" value={value} onChange={(event) => onChange(event.target.value)} />;
    case "Date":
      return <TextInput {...common} type="date" value={value} min={element.validation?.minDate} max={element.validation?.maxDate} onChange={(event) => onChange(event.target.value)} />;
    case "DateTime":
      // The control edits local wall-clock time; the value sent and stored is the UTC instant (second precision).
      return <TextInput {...common} type="datetime-local" step={60} value={toLocalDateTimeInput(value)} onChange={(event) => onChange(fromLocalDateTimeInput(event.target.value))} />;
    case "Guid":
      return <TextInput {...common} type="text" inputMode="text" autoComplete="off" spellCheck={false} placeholder="0199a2b3-c4d5-7e6f-8123-456789abcdef" value={value} maxLength={36} onChange={(event) => onChange(event.target.value.trim().toLowerCase())} />;
    case "Boolean":
      return <Checkbox {...common} checked={value === "true"} onCheckedChange={(checked) => onChange(checked ? "true" : "false")} />;
    case "Option": {
      const options = [...field.options].sort((a, b) => a.optionSortOrder - b.optionSortOrder).filter((option) => option.isActive || option.optionKey === value);
      return (
        <Select
          {...common}
          value={value || undefined}
          options={options.map((option) => ({ value: option.optionKey, label: option.isActive ? option.optionDisplayText : `${option.optionDisplayText} (no longer available)`, disabled: !option.isActive }))}
          onValueChange={onChange}
        />
      );
    }
    default:
      return null;
  }
}

export function displayValue(field: RendererField, value: string | undefined): string {
  if (value === undefined || value === "") {
    return "";
  }
  if (field.fieldDataType === "Boolean") {
    return value === "true" ? "Yes" : "No";
  }
  if (field.fieldDataType === "DateTime") {
    const parsed = Date.parse(value);
    return Number.isNaN(parsed) ? value : `${new Date(parsed).toLocaleString()} (${value})`;
  }
  if (field.fieldDataType === "Option") {
    const option = field.options.find((candidate) => candidate.optionKey === value);
    return option ? (option.isActive ? option.optionDisplayText : `${option.optionDisplayText} (no longer available)`) : value;
  }
  return value;
}

const pad = (n: number) => String(n).padStart(2, "0");

/** UTC instant (wire) → value of an <input type="datetime-local"> in the viewer's zone; empty/invalid → "". */
export function toLocalDateTimeInput(wire: string): string {
  if (!wire) {
    return "";
  }
  const parsed = new Date(wire);
  if (Number.isNaN(parsed.getTime())) {
    return "";
  }
  return `${parsed.getFullYear()}-${pad(parsed.getMonth() + 1)}-${pad(parsed.getDate())}T${pad(parsed.getHours())}:${pad(parsed.getMinutes())}`;
}

/** datetime-local value (viewer's zone) → UTC instant at second precision; empty → "". */
export function fromLocalDateTimeInput(local: string): string {
  if (!local) {
    return "";
  }
  const parsed = new Date(local);
  if (Number.isNaN(parsed.getTime())) {
    return local;
  }
  return parsed.toISOString().replace(/\.\d{3}Z$/, "Z");
}

export function ReadOnlyValue({ label, value, helpText }: { label: string; value: string; helpText?: string | undefined }) {
  return (
    <div className="flex min-w-0 flex-col gap-1">
      <span className="text-sm font-medium text-muted">{label}</span>
      <span className="min-h-6 break-words text-base text-body">{value === "" ? <span className="text-muted">Not provided</span> : value}</span>
      {helpText ? <span className="text-xs text-muted">{helpText}</span> : null}
    </div>
  );
}
