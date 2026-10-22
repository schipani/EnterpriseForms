import { Button, Checkbox, Select, TextArea, TextInput } from "@enterpriseforms/ui";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { changeInOccurrence, changeTopLevel, groupKeyFor, initialGroups, moveOccurrence, newOccurrence, occurrenceErrorKey, occurrenceRuleStates, occurrenceScopeValues, type GroupInfo, type GroupValueMap, type RepeatingSection } from "./groups";
import type { DocumentElement, FieldElement, FormLayout, LayoutElement, LayoutSection, RelatedElement } from "./layout";
import { dependentChoicesFor, describeSelection } from "./options";
import { breakpointForWidth, packElements, visibleColumns, type Breakpoint } from "./packer";
import type { FieldRuleState } from "./rules";
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
  /**
   * Repeating sections (Phase C3): the entries per group key. When omitted the renderer keeps its own entries (designer
   * preview), starting with each section's minimum. Errors of entry fields use the server's path keys (people[2].name).
   */
  groups?: GroupValueMap | undefined;
  /** Group keys of the layout's repeating groups (from the form definition or record). Without it the group id is used as key. */
  groupInfos?: readonly GroupInfo[] | undefined;
  onGroupsChange?: ((groups: GroupValueMap) => void) | undefined;
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
export function FormRenderer({ layout, fields, values, errors = {}, onChange, readOnly = false, applyRules = true, breakpoint, renderRelatedList, renderDocumentCollection, groups, groupInfos, onGroupsChange }: FormRendererProps) {
  const [ref, active] = useContainerBreakpoint<HTMLDivElement>(breakpoint);
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  const columns = visibleColumns(active);
  const states = ruleStatesFor(layout, fields, values, applyRules);
  const infos: GroupInfo[] = groupInfos ? [...groupInfos] : layout.sections.flatMap((section) => (section.repeat ? [{ groupDefinitionId: section.repeat.groupDefinitionId, groupKey: section.repeat.groupDefinitionId }] : []));
  const [localGroups, setLocalGroups] = useState<GroupValueMap>(() => initialGroups(layout, fields, infos));
  const currentGroups = groups ?? localGroups;
  const setGroups = (next: GroupValueMap) => (onGroupsChange ? onGroupsChange(next) : setLocalGroups(next));
  const [announcement, setAnnouncement] = useState("");
  const labelOf = (key: string) => {
    const field = fields.find((candidate) => candidate.fieldKey === key.replace(/^.*\]\./, ""));
    const entry = /^.*\[(\d+)\]\./.exec(key);
    return field ? `${field.fieldDisplayName}${entry ? ` (entry ${entry[1]})` : ""}` : key;
  };
  const announceCleared = (cleared: readonly string[]) => {
    if (cleared.length > 0) {
      setAnnouncement(`${cleared.map(labelOf).join(", ")} cleared: the choice is not available for the new selection.`);
    }
  };

  const changeTop = (fieldKey: string, value: string) => {
    const result = changeTopLevel(layout, fields, values, currentGroups, infos, fieldKey, value);
    onChange?.(fieldKey, value);
    for (const cleared of result.clearedFieldKeys.filter((key) => !key.includes("["))) {
      onChange?.(cleared, "");
    }
    if (result.groups !== currentGroups && result.clearedFieldKeys.some((key) => key.includes("["))) {
      setGroups(result.groups);
    }
    announceCleared(result.clearedFieldKeys);
  };

  return (
    <div ref={ref} className="flex flex-col gap-6" data-breakpoint={active}>
      <p className="sr-only" aria-live="polite" role="status">
        {announcement}
      </p>
      {layout.sections.map((section) => {
        if (section.repeat) {
          const groupKey = groupKeyFor(infos, section.repeat.groupDefinitionId) ?? section.repeat.groupDefinitionId;
          return (
            <RepeatingSectionView
              key={section.sectionKey}
              layout={layout}
              section={section as RepeatingSection}
              groupKey={groupKey}
              fields={fields}
              byId={byId}
              values={values}
              entries={currentGroups[groupKey] ?? []}
              errors={errors}
              readOnly={readOnly}
              applyRules={applyRules}
              active={active}
              columns={columns}
              onEntriesChange={(entries, message) => {
                setGroups({ ...currentGroups, [groupKey]: entries });
                if (message) {
                  setAnnouncement(message);
                }
              }}
              onFieldChange={(index, fieldKey, value) => {
                const result = changeInOccurrence(layout, section, fields, values, currentGroups, groupKey, index, fieldKey, value);
                setGroups(result.groups);
                announceCleared(result.clearedFieldKeys);
              }}
              renderRelatedList={renderRelatedList}
              renderDocumentCollection={renderDocumentCollection}
            />
          );
        }
        // Omitted from the grid (so nothing reflows around an empty slot): unauthorized/unknown fields, fields a rule hides,
        // and — in the data-entry form only — fields the designer chose not to show. "Show in form" is presentation, never security.
        const visible = visibleElements(section, byId, states, readOnly);
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
                <div key={elementKey(cell.element)} className="min-w-0" style={{ gridColumn: `${cell.column} / span ${cell.span}`, gridRow: cell.row }} data-field-key={fieldKeyOfCell(cell.element, byId)}>
                  {renderElement(cell.element, byId, values, values, (key) => errors[key], changeTop, readOnly, states, renderRelatedList, renderDocumentCollection)}
                </div>
              ))}
            </div>
          </fieldset>
        );
      })}
    </div>
  );
}

function visibleElements(section: LayoutSection, byId: Map<string, RendererField>, states: ReadonlyMap<string, FieldRuleState>, readOnly: boolean): LayoutElement[] {
  return section.elements.filter((element) => {
    if (element.kind !== "field") {
      return true;
    }
    if (!byId.has(element.fieldDefinitionId) || states.get(element.fieldDefinitionId)?.visible === false) {
      return false;
    }
    return readOnly || element.showInForm !== false;
  });
}

/**
 * A section that allows multiple entries: an ordered list of entries, each a labelled group with its own fields, rules and
 * errors. Add / move / remove follow the section's settings and bounds; the server enforces the same rules.
 */
function RepeatingSectionView({
  layout,
  section,
  groupKey,
  fields,
  byId,
  values,
  entries,
  errors,
  readOnly,
  applyRules,
  active,
  columns,
  onEntriesChange,
  onFieldChange,
  renderRelatedList,
  renderDocumentCollection,
}: {
  layout: FormLayout;
  section: RepeatingSection;
  groupKey: string;
  fields: readonly RendererField[];
  byId: Map<string, RendererField>;
  values: ValueMap;
  entries: GroupValueMap[string];
  errors: ErrorMap;
  readOnly: boolean;
  applyRules: boolean;
  active: Breakpoint;
  columns: number;
  onEntriesChange: (entries: GroupValueMap[string], announcement?: string) => void;
  onFieldChange: (index: number, fieldKey: string, value: string) => void;
  renderRelatedList: FormRendererProps["renderRelatedList"];
  renderDocumentCollection: FormRendererProps["renderDocumentCollection"];
}) {
  const { repeat } = section;
  const title = section.title ?? section.sectionKey;
  const headingId = useId();
  const focusKey = useRef<string | null>(null);
  const entryRefs = useRef(new Map<string, HTMLElement>());
  useEffect(() => {
    if (focusKey.current) {
      entryRefs.current.get(focusKey.current)?.focus();
      focusKey.current = null;
    }
  });
  const canAdd = !readOnly && repeat.allowAdd && entries.length < repeat.maxOccurrences;
  const canRemove = !readOnly && repeat.allowRemove && entries.length > repeat.minOccurrences;
  const canReorder = !readOnly && repeat.allowReorder && entries.length > 1;
  const sectionError = errors[groupKey];
  return (
    <fieldset className="min-w-0 rounded-lg border border-line bg-surface p-4" data-repeating-section={groupKey} aria-describedby={sectionError ? `${headingId}-error` : undefined}>
      <legend className="px-1 text-lg font-semibold">{title}</legend>
      {section.description ? <p className="mb-3 text-sm text-muted">{section.description}</p> : null}
      {!readOnly ? (
        <p className="mb-3 text-sm text-muted">
          {entries.length} of up to {repeat.maxOccurrences} {repeat.maxOccurrences === 1 ? "entry" : "entries"}
          {repeat.minOccurrences > 0 ? ` (at least ${repeat.minOccurrences})` : ""}.
        </p>
      ) : null}
      {sectionError ? (
        <p id={`${headingId}-error`} role="alert" className="mb-3 text-sm font-medium text-[#991b1b]">
          {sectionError}
        </p>
      ) : null}
      {entries.length === 0 ? <p className="mb-3 text-sm text-muted">{readOnly ? "No entries." : "No entries yet."}</p> : null}
      <ol className="m-0 flex list-none flex-col gap-3 p-0" aria-label={`${title} entries`}>
        {entries.map((entry, index) => {
          const scopeValues = occurrenceScopeValues(values, entry);
          const states = occurrenceRuleStates(layout, section, fields, values, entry, applyRules);
          const cells = packElements(visibleElements(section, byId, states, readOnly), active);
          const position = `entry ${index + 1} of ${entries.length}`;
          return (
            <li key={entry.clientKey}>
              <section
                ref={(node) => {
                  if (node) {
                    entryRefs.current.set(entry.clientKey, node);
                  } else {
                    entryRefs.current.delete(entry.clientKey);
                  }
                }}
                tabIndex={-1}
                aria-label={`${title}, ${position}`}
                data-entry-index={index + 1}
                className="rounded-md border border-line p-3 focus:outline focus:outline-2 focus:outline-offset-2"
              >
                <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                  <h3 className="m-0 text-base font-semibold">
                    {title} {index + 1}
                  </h3>
                  {!readOnly ? (
                    <div className="flex flex-wrap gap-2">
                      {canReorder ? (
                        <>
                          <Button size="sm" variant="ghost" disabled={index === 0} aria-label={`Move up, ${title} ${index + 1}`} onClick={() => onEntriesChange(moveOccurrence(entries, index, -1), `${title} ${index + 1} moved to position ${index}.`)}>
                            Move up
                          </Button>
                          <Button size="sm" variant="ghost" disabled={index === entries.length - 1} aria-label={`Move down, ${title} ${index + 1}`} onClick={() => onEntriesChange(moveOccurrence(entries, index, 1), `${title} ${index + 1} moved to position ${index + 2}.`)}>
                            Move down
                          </Button>
                        </>
                      ) : null}
                      {canRemove ? (
                        <Button size="sm" variant="secondary" aria-label={`Remove ${title} ${index + 1}`} onClick={() => onEntriesChange(entries.filter((_, i) => i !== index), `${title} ${index + 1} removed. ${entries.length - 1} ${entries.length - 1 === 1 ? "entry" : "entries"} remain.`)}>
                          Remove
                        </Button>
                      ) : null}
                    </div>
                  ) : null}
                </div>
                <div className={`grid ${gapClass[section.layout?.columnGap ?? "medium"]}`} style={{ gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))` }}>
                  {cells.map((cell) => (
                    <div key={elementKey(cell.element)} className="min-w-0" style={{ gridColumn: `${cell.column} / span ${cell.span}`, gridRow: cell.row }} data-field-key={entryFieldKeyOfCell(cell.element, byId, groupKey, index)}>
                      {renderElement(cell.element, byId, scopeValues, entry.values, (key) => errors[occurrenceErrorKey(groupKey, index, key)], (key, value) => onFieldChange(index, key, value), readOnly, states, renderRelatedList, renderDocumentCollection)}
                    </div>
                  ))}
                </div>
              </section>
            </li>
          );
        })}
      </ol>
      {canAdd ? (
        <div className="mt-3">
          <Button
            variant="secondary"
            onClick={() => {
              const entry = newOccurrence(section, fields);
              focusKey.current = entry.clientKey;
              onEntriesChange([...entries, entry], `${title} ${entries.length + 1} added.`);
            }}
          >
            Add {title}
          </Button>
        </div>
      ) : null}
    </fieldset>
  );
}

function elementKey(element: LayoutElement): string {
  return element.kind === "relatedFormList" ? `rel:${element.formRelationshipDefinitionId}` : `${element.kind}:${element.fieldDefinitionId}`;
}

function renderElement(
  element: LayoutElement,
  byId: Map<string, RendererField>,
  scopeValues: ValueMap,
  ownValues: ValueMap,
  errorOf: (fieldKey: string) => string | undefined,
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
  const parent = element.optionFilter ? byId.get(element.optionFilter.parentFieldDefinitionId) : undefined;
  return (
    <FieldControl
      element={element}
      field={field}
      value={ownValues[field.fieldKey] ?? ""}
      error={errorOf(field.fieldKey)}
      onChange={(value) => onChange?.(field.fieldKey, value)}
      readOnly={readOnly}
      required={states.get(element.fieldDefinitionId)?.required ?? element.required}
      parentValue={parent ? (scopeValues[parent.fieldKey] ?? "") : undefined}
      parentLabel={parent?.fieldDisplayName}
    />
  );
}

const ENCRYPTED_NOTE = "Encrypted for privacy — not searchable.";

export function FieldControl({ element, field, value, error, onChange, readOnly, required, parentValue, parentLabel }: { element: FieldElement; field: RendererField; value: string; error: string | undefined; onChange: (value: string) => void; readOnly: boolean; required?: boolean | undefined; parentValue?: string | undefined; parentLabel?: string | undefined }) {
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
        <MultiSelectControl element={element} field={field} value={value} label={label} helpText={element.helpText} note={note} error={error} required={isRequired} onChange={onChange} parentValue={parentValue} parentLabel={parentLabel} />
      ) : (
        <SingleSelectControl element={element} field={field} value={value} common={common} onChange={onChange} parentValue={parentValue} parentLabel={parentLabel} />
      );
    default:
      return null;
  }
}

/** Label of a choice: marked when deactivated, or when the parent dropdown's current selection does not allow it. */
function choiceLabel(choice: { label: string; isActive: boolean; allowedByParent?: boolean | undefined }, parentLabel: string | undefined): string {
  if (!choice.isActive) {
    return `${choice.label} (no longer available)`;
  }
  return choice.allowedByParent === false ? `${choice.label} (not available for the selected ${parentLabel ?? "value"})` : choice.label;
}

function SingleSelectControl({ element, field, value, common, onChange, parentValue, parentLabel }: { element: FieldElement; field: RendererField; value: string; common: Omit<React.ComponentProps<typeof Select>, "value" | "onValueChange" | "options">; onChange: (value: string) => void; parentValue: string | undefined; parentLabel: string | undefined }) {
  const choices = dependentChoicesFor(element, field, value ? [value] : [], parentValue);
  const hint = parentValue !== undefined && parentValue === "" && element.optionFilter ? `Choices depend on ${parentLabel ?? "another field"}.` : undefined;
  return (
    <div className="flex min-w-0 flex-col gap-1">
      <Select {...common} note={[common.note, hint].filter(Boolean).join(" ") || undefined} value={value || undefined} options={choices.map((choice) => ({ value: choice.optionKey, label: choiceLabel(choice, parentLabel), disabled: !choice.isActive || choice.allowedByParent === false }))} onValueChange={onChange} />
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
function MultiSelectControl({ element, field, value, label, helpText, note, error, required, onChange, parentValue, parentLabel }: { element: FieldElement; field: RendererField; value: string; label: string; helpText: string | undefined; note: string | undefined; error: string | undefined; required: boolean | undefined; onChange: (value: string) => void; parentValue: string | undefined; parentLabel: string | undefined }) {
  const id = useId();
  const selected = splitSelection(value);
  const choices = dependentChoicesFor(element, field, selected, parentValue);
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
        <Checkbox key={choice.optionKey} label={choiceLabel(choice, parentLabel)} checked={selected.includes(choice.optionKey)} onCheckedChange={(checked) => toggle(choice.optionKey, checked)} />
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

/**
 * The error-map key of a field cell (`fieldKey`, or `groupKey[n].fieldKey` inside entry n), exposed as `data-field-key`
 * so a form-level validation summary can move focus to the field. Presentation only; never used for authorization.
 */
function fieldKeyOfCell(element: LayoutElement, byId: ReadonlyMap<string, RendererField>): string | undefined {
  return element.kind === "field" ? byId.get(element.fieldDefinitionId)?.fieldKey : undefined;
}

function entryFieldKeyOfCell(element: LayoutElement, byId: ReadonlyMap<string, RendererField>, groupKey: string, index: number): string | undefined {
  const key = fieldKeyOfCell(element, byId);
  return key ? occurrenceErrorKey(groupKey, index, key) : undefined;
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
