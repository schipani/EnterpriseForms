import { Button, Checkbox, Select, TextArea, TextInput } from "@enterpriseforms/ui";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import type { DocumentElement, FieldElement, FormLayout, LayoutElement, RelatedElement } from "./layout";
import { choicesFor, describeSelection } from "./options";
import { breakpointForWidth, packElements, visibleColumns, type Breakpoint } from "./packer";
import { splitSelection } from "./rules";
import { ruleStatesFor, type ErrorMap, type RendererField, type ValueMap } from "./validation";

export interface FormRendererProps {
  layout: FormLayout;
  fields: readonly RendererField[];
  values: ValueMap;
  errors?: ErrorMap | undefined;
  onChange?: ((fieldKey: string, value: string) => void) | undefined;
  /** View mode: values only, no inputs. Shows every authorized field, including those the data-entry form leaves out (Show in form). */
  readOnly?: boolean | undefined;
  /**
   * Conditional visibility / required-if apply to a complete projection. A partial (shared) view must pass false: it lacks
   * the fields a rule may read, so evaluating rules would guess — and could hint at hidden values.
   */
  applyRules?: boolean | undefined;
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
export function FormRenderer({ layout, fields, values, errors = {}, onChange, readOnly = false, applyRules = true, breakpoint, renderRelatedList, renderDocumentCollection }: FormRendererProps) {
  const [ref, active] = useContainerBreakpoint<HTMLDivElement>(breakpoint);
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  const columns = visibleColumns(active);
  const states = ruleStatesFor(layout, fields, values, applyRules);
  return (
    <div ref={ref} className="flex flex-col gap-6" data-breakpoint={active}>
      {layout.sections.map((section) => {
        // Omitted from the grid (so nothing reflows around an empty slot): unauthorized/unknown fields, fields a rule hides,
        // and — in the data-entry form only — fields the designer chose not to show. "Show in form" is presentation, never security.
        const visible = section.elements.filter((element) => {
          if (element.kind !== "field") {
            return true;
          }
          if (!byId.has(element.fieldDefinitionId) || states.get(element.fieldDefinitionId)?.visible === false) {
            return false;
          }
          return readOnly || element.showInForm !== false;
        });
        if (visible.length === 0 && section.elements.length > 0) {
          return null;
        }
        const cells = packElements(visible, active);
        return (
          <fieldset key={section.sectionKey} className="min-w-0 rounded-lg border border-line bg-surface p-4">
            <legend className="px-1 text-lg font-semibold">{section.title ?? section.sectionKey}</legend>
            {section.description ? <p className="mb-3 text-sm text-muted">{section.description}</p> : null}
            <div className={`grid ${gapClass[section.layout?.columnGap ?? "medium"]}`} style={{ gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))` }}>
              {cells.map((cell) => (
                <div key={elementKey(cell.element)} className="min-w-0" style={{ gridColumn: `${cell.column} / span ${cell.span}`, gridRow: cell.row }}>
                  {renderElement(cell.element, byId, values, errors, onChange, readOnly, states, renderRelatedList, renderDocumentCollection)}
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
  states: ReturnType<typeof ruleStatesFor>,
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
  return <FieldControl element={element} field={field} value={values[field.fieldKey] ?? ""} error={errors[field.fieldKey]} onChange={(value) => onChange?.(field.fieldKey, value)} readOnly={readOnly} required={states.get(element.fieldDefinitionId)?.required ?? element.required} />;
}

const ENCRYPTED_NOTE = "Encrypted for privacy — not searchable.";

export function FieldControl({ element, field, value, error, onChange, readOnly, required }: { element: FieldElement; field: RendererField; value: string; error: string | undefined; onChange: (value: string) => void; readOnly: boolean; required?: boolean | undefined }) {
  const label = element.label || field.fieldDisplayName;
  const encrypted = field.applicationEncrypted === true;
  const isRequired = required ?? element.required;
  // The encryption notice sits below the control (a status of the stored value), separate from the designer's help text above it.
  const note = encrypted ? ENCRYPTED_NOTE : undefined;
  const common = { label, helpText: element.helpText, note, error, required: isRequired, disabled: readOnly } as const;
  const placeholder = element.placeholder || undefined;
  if (readOnly) {
    return <ReadOnlyValue label={label} value={displayValue(field, value, element)} helpText={element.helpText} encrypted={encrypted} />;
  }
  if (element.readOnly) {
    // The designer made this field read-only: the value is shown but cannot be edited, and the server refuses any change.
    return <ReadOnlyValue label={label} value={displayValue(field, value, element)} helpText={element.helpText} encrypted={encrypted} locked />;
  }
  switch (field.fieldDataType) {
    case "String":
      return element.control === "textarea" ? (
        <TextArea {...common} value={value} placeholder={placeholder} maxLength={element.validation?.maxLength ?? 4000} onChange={(event) => onChange(event.target.value)} />
      ) : (
        <TextInput {...common} type="text" value={value} placeholder={placeholder} maxLength={element.validation?.maxLength ?? 4000} autoComplete="off" onChange={(event) => onChange(event.target.value)} />
      );
    case "Integer":
      return <TextInput {...common} type="text" inputMode="numeric" pattern="-?[0-9]*" placeholder={placeholder} value={value} onChange={(event) => onChange(event.target.value)} />;
    case "Decimal":
      return <TextInput {...common} type="text" inputMode="decimal" placeholder={placeholder} value={value} onChange={(event) => onChange(event.target.value)} />;
    case "Date":
      return <TextInput {...common} type="date" value={value} min={element.validation?.minDate} max={element.validation?.maxDate} onChange={(event) => onChange(event.target.value)} />;
    case "DateTime":
      // The control edits local wall-clock time; the value sent and stored is the UTC instant (second precision).
      return <TextInput {...common} type="datetime-local" step={60} value={toLocalDateTimeInput(value)} onChange={(event) => onChange(fromLocalDateTimeInput(event.target.value))} />;
    case "Guid":
      return <TextInput {...common} type="text" inputMode="text" autoComplete="off" spellCheck={false} placeholder="0199a2b3-c4d5-7e6f-8123-456789abcdef" value={value} maxLength={36} onChange={(event) => onChange(event.target.value.trim().toLowerCase())} />;
    case "Boolean":
      return <Checkbox label={label} helpText={[element.helpText, note].filter(Boolean).join(" ") || undefined} error={error} required={isRequired} disabled={readOnly} checked={value === "true"} onCheckedChange={(checked) => onChange(checked ? "true" : "false")} />;
    case "Option":
      return field.optionSelectionMode === "Multiple" ? (
        <MultiSelectControl element={element} field={field} value={value} label={label} helpText={element.helpText} note={note} error={error} required={isRequired} onChange={onChange} />
      ) : (
        <SingleSelectControl element={element} field={field} value={value} common={common} onChange={onChange} />
      );
    default:
      return null;
  }
}

function SingleSelectControl({ element, field, value, common, onChange }: { element: FieldElement; field: RendererField; value: string; common: Omit<React.ComponentProps<typeof Select>, "value" | "onValueChange" | "options">; onChange: (value: string) => void }) {
  const choices = choicesFor(element, field, value ? [value] : []);
  return (
    <div className="flex min-w-0 flex-col gap-1">
      <Select {...common} value={value || undefined} options={choices.map((choice) => ({ value: choice.optionKey, label: choice.isActive ? choice.label : `${choice.label} (no longer available)`, disabled: !choice.isActive }))} onValueChange={onChange} />
      {value && !common.required ? (
        <div>
          <Button size="sm" variant="ghost" onClick={() => onChange("")}>
            Clear {common.label} selection
          </Button>
        </div>
      ) : null}
    </div>
  );
}

/** Multi-select dropdown as an accessible group of checkboxes: every option is a real, labelled control with an obvious checked state. */
function MultiSelectControl({ element, field, value, label, helpText, note, error, required, onChange }: { element: FieldElement; field: RendererField; value: string; label: string; helpText: string | undefined; note: string | undefined; error: string | undefined; required: boolean | undefined; onChange: (value: string) => void }) {
  const id = useId();
  const selected = splitSelection(value);
  const choices = choicesFor(element, field, selected);
  const order = new Map(choices.map((choice, index) => [choice.optionKey, index]));
  const toggle = (key: string, checked: boolean) => {
    const next = checked ? [...new Set([...selected, key])] : selected.filter((candidate) => candidate !== key);
    onChange(next.sort((a, b) => (order.get(a) ?? 0) - (order.get(b) ?? 0)).join(","));
  };
  const describedBy = [helpText ? `${id}-help` : null, note ? `${id}-note` : null, error ? `${id}-error` : null].filter(Boolean).join(" ") || undefined;
  return (
    <fieldset aria-describedby={describedBy} aria-invalid={error ? true : undefined} className="flex min-w-0 flex-col gap-1 border-0 p-0">
      <legend className="text-sm font-medium text-body">
        {label}
        {required ? <span className="ml-1 text-[#991b1b]" aria-hidden="true">*</span> : null}
        {required ? <span className="sr-only"> (required)</span> : null}
      </legend>
      {helpText ? (
        <p id={`${id}-help`} className="text-sm text-muted">
          {helpText}
        </p>
      ) : null}
      {choices.length === 0 ? <p className="text-sm text-muted">No options are available.</p> : null}
      {choices.map((choice) => (
        <Checkbox key={choice.optionKey} label={choice.isActive ? choice.label : `${choice.label} (no longer available)`} checked={selected.includes(choice.optionKey)} onCheckedChange={(checked) => toggle(choice.optionKey, checked)} />
      ))}
      {note ? (
        <p id={`${id}-note`} className="text-sm text-muted">
          {note}
        </p>
      ) : null}
      {error ? (
        <p id={`${id}-error`} role="alert" className="text-sm font-medium text-[#991b1b]">
          {error}
        </p>
      ) : null}
    </fieldset>
  );
}

export function displayValue(field: RendererField, value: string | undefined, element?: Pick<FieldElement, "options"> | undefined): string {
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
    return describeSelection(element, field, value);
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

export function ReadOnlyValue({ label, value, helpText, encrypted, locked }: { label: string; value: string; helpText?: string | undefined; encrypted?: boolean | undefined; locked?: boolean | undefined }) {
  return (
    <div className="flex min-w-0 flex-col gap-1" data-readonly-field={locked ? "true" : undefined}>
      <span className="flex flex-wrap items-center gap-2 text-sm font-medium text-muted">
        <span>{label}</span>
        {locked ? <span className="rounded border border-line px-1.5 py-0.5 text-xs font-normal" title="The designer made this field read-only; its value cannot be changed here.">Read-only</span> : null}
        {encrypted ? <span className="rounded border border-line px-1.5 py-0.5 text-xs font-normal" title="Stored application-encrypted; decrypted for you after authorization. Not searchable.">Encrypted</span> : null}
      </span>
      <span className="min-h-6 break-words text-base text-body">{value === "" ? <span className="text-muted">Not provided</span> : value}</span>
      {helpText ? <span className="text-xs text-muted">{helpText}</span> : null}
    </div>
  );
}
