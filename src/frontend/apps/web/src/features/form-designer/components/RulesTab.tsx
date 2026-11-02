import type { FieldDefinitionResponse } from "@enterpriseforms/api-client";
import { appendCondition, asGroup, candidateFilterParents, candidateSources, conditionProblem, fieldScopes, findOptionFilterCycle, findRuleCycle, newComparison, removeNode, replaceNode, type ConditionPath } from "@enterpriseforms/form-designer";
import {
  OPERATOR_LABEL,
  allowedOperators,
  comparisonsOf,
  describeCondition,
  evaluateRuleStates,
  splitSelection,
  RULE_MAX_DEPTH,
  RULE_MAX_NODES,
  type ComparisonOperator,
  type FieldElement,
  type FormLayout,
  type RuleCondition,
  type RuleField,
} from "@enterpriseforms/form-renderer";
import { Badge, Button, Checkbox, errorTextClass, legendClass, Select, StatusMessage, TextArea, TextInput } from "@enterpriseforms/ui";
import { useState } from "react";

type Source = RuleField & { fieldDisplayName: string };

const toSource = (field: FieldDefinitionResponse): Source => ({ fieldDefinitionId: field.fieldDefinitionId, fieldKey: field.fieldKey, fieldDataType: field.fieldDataType, optionSelectionMode: field.optionSelectionMode, fieldDisplayName: field.fieldDisplayName });

const countNodes = (node: RuleCondition): number => ("conditions" in node ? 1 + node.conditions.reduce((sum, child) => sum + countNodes(child), 0) : 1);

/**
 * Conditional rules tab of Field Properties (Phase C2; Phase C3 designer UX refinement relocated dependent-dropdown
 * choices to their own "Dropdown choices and dependent choices" tab — see `DependentChoices` below): Show in form,
 * Read-only, a visibility rule, a required-if rule and the opt-in clear-when-hidden behaviour. Everything edits the
 * draft element (undoable, saved with Save draft); the server validates the same rules on save and publish and
 * enforces them on every record write.
 */
export function RulesTab({ layout, element, field, fields, canDesign, onElementChange }: { layout: FormLayout; element: FieldElement; field: FieldDefinitionResponse; fields: readonly FieldDefinitionResponse[]; canDesign: boolean; onElementChange: (next: FieldElement) => void }) {
  const sources = fields.map(toSource);
  const candidates = candidateSources(layout, field.fieldDefinitionId, sources, (source) => allowedOperators(source).length > 0) as Source[];
  const cycle = findRuleCycle(layout);
  const named = (id: string) => sources.find((source) => source.fieldDefinitionId === id)?.fieldDisplayName ?? "unknown field";

  const set = (patch: Partial<FieldElement>, remove: Array<keyof FieldElement> = []) => {
    const next: FieldElement = { ...element, ...patch };
    for (const key of remove) {
      delete next[key];
    }
    onElementChange(next);
  };
  const required = element.required === true;
  const noSources = candidates.length === 0;
  const startRule = (): RuleCondition => {
    const first = candidates[0]!;
    return { op: "and", conditions: [newComparison(first.fieldDefinitionId, allowedOperators(first))] };
  };

  return (
    <div className="flex flex-col gap-4">
      <section aria-label="Field behavior" className="flex flex-col gap-1">
        <h3 className="text-sm font-semibold">Behavior</h3>
        <Checkbox
          label="Show in form"
          checked={element.showInForm !== false}
          disabled={!canDesign}
          helpText="Presentation only. Turning it off removes the field from the data-entry form; it does not delete the value or change who may read it — access is decided by authorization."
          onCheckedChange={(checked) => (checked ? set({}, ["showInForm"]) : set({ showInForm: false }))}
        />
        {element.showInForm === false && (required || element.requiredWhen) ? <p className={errorTextClass}>A field that is not shown cannot be required. Clear “Required” and any required-if rule before saving.</p> : null}
        <Checkbox
          label="Read-only"
          checked={element.readOnly === true}
          disabled={!canDesign}
          helpText="The value is displayed but users cannot change it; the server refuses any change. A read-only dropdown with a default selection receives that default when a record is created."
          onCheckedChange={(checked) => (checked ? set({ readOnly: true }) : set({}, ["readOnly"]))}
        />
      </section>

      <section aria-label="Visibility rule" className="flex flex-col gap-2">
        <h3 className="text-sm font-semibold">Visibility</h3>
        <Checkbox
          label="Show this field only when conditions are met"
          checked={element.visibleWhen !== undefined}
          disabled={!canDesign || (noSources && element.visibleWhen === undefined)}
          onCheckedChange={(checked) => (checked ? set({ visibleWhen: startRule() }) : set({}, ["visibleWhen", "clearWhenHidden"]))}
        />
        {noSources && element.visibleWhen === undefined ? <p className="text-xs text-muted">Place at least one other field in the layout to build a rule.</p> : null}
        {element.visibleWhen ? (
          <>
            <ConditionEditor label="Visibility" root={element.visibleWhen} candidates={candidates} fields={fields} canDesign={canDesign} onChange={(next) => (next ? set({ visibleWhen: next }) : set({}, ["visibleWhen", "clearWhenHidden"]))} />
            <Checkbox
              label="Clear the value when the field is hidden"
              checked={element.clearWhenHidden === true}
              disabled={!canDesign}
              helpText="Off by default: a hidden field keeps its stored value (it is not shown, not required, and cannot be changed). Turn this on only if a hidden value must be removed; the removal is audited."
              onCheckedChange={(checked) => (checked ? set({ clearWhenHidden: true }) : set({}, ["clearWhenHidden"]))}
            />
          </>
        ) : null}
      </section>

      <section aria-label="Required rule" className="flex flex-col gap-2">
        <h3 className="text-sm font-semibold">Required</h3>
        <p className="text-xs text-muted">{required ? "This field is always required (Validation tab)." : "Optional unless a condition below makes it required."}</p>
        <Checkbox
          label="Make this field required when conditions are met"
          checked={element.requiredWhen !== undefined}
          disabled={!canDesign || (noSources && element.requiredWhen === undefined)}
          onCheckedChange={(checked) => (checked ? set({ requiredWhen: startRule() }) : set({}, ["requiredWhen"]))}
        />
        {element.requiredWhen ? <ConditionEditor label="Required" root={element.requiredWhen} candidates={candidates} fields={fields} canDesign={canDesign} onChange={(next) => (next ? set({ requiredWhen: next }) : set({}, ["requiredWhen"]))} /> : null}
      </section>

      {cycle ? (
        <StatusMessage tone="danger" title="Circular rule dependency">
          {cycle.map(named).join(" → ")}. Rules cannot depend on each other in a loop; the draft cannot be saved until this is removed.
        </StatusMessage>
      ) : null}

      {element.visibleWhen || element.requiredWhen ? <TryIt layout={layout} element={element} sources={sources} fields={fields} /> : null}
    </div>
  );
}

const byOrder = (a: { optionSortOrder: number | string }, b: { optionSortOrder: number | string }) => Number(a.optionSortOrder) - Number(b.optionSortOrder);

/**
 * Dependent dropdown (Phase C3): this dropdown offers only the choices that fit another dropdown's selection, mapped by
 * stable option keys (labels can change freely). A choice left unmapped is always available. Saved with the draft and
 * validated by the server (parent, keys, scope and no loops); the server refuses records whose selections do not fit.
 * Rendered from the "Dropdown choices and dependent choices" tab (Phase C3 designer UX refinement), alongside the
 * dropdown's own option list — not from the Conditional rules tab, so the two kinds of choice configuration are not
 * split across two different tabs.
 */
export function DependentChoices({ layout, element, field, fields, canDesign, onElementChange }: { layout: FormLayout; element: FieldElement; field: FieldDefinitionResponse; fields: readonly FieldDefinitionResponse[]; canDesign: boolean; onElementChange: (next: FieldElement) => void }) {
  const candidates = candidateFilterParents(layout, field.fieldDefinitionId, fields.map(toSource)).map((candidate) => fields.find((f) => f.fieldDefinitionId === candidate.fieldDefinitionId)!);
  const filter = element.optionFilter;
  const parent = filter ? fields.find((candidate) => candidate.fieldDefinitionId === filter.parentFieldDefinitionId) : undefined;
  const parentChoices = parent ? (candidates.some((candidate) => candidate.fieldDefinitionId === parent.fieldDefinitionId) ? candidates : [parent, ...candidates]) : candidates;
  const childOptions = [...field.options].filter((option) => option.isActive || filter?.parentOptionKeysByOption[option.optionKey] !== undefined).sort(byOrder);
  const parentOptions = parent ? [...parent.options].filter((option) => option.isActive).sort(byOrder) : [];
  const cycle = findOptionFilterCycle(layout);
  const scopes = fieldScopes(layout);
  const named = (id: string) => fields.find((candidate) => candidate.fieldDefinitionId === id)?.fieldDisplayName ?? "unknown field";
  const write = (next: FieldElement["optionFilter"]) => {
    const updated: FieldElement = { ...element };
    if (next) {
      updated.optionFilter = next;
    } else {
      delete updated.optionFilter;
    }
    onElementChange(updated);
  };
  const setMapping = (childKey: string, parentKeys: string[]) => {
    if (!filter) {
      return;
    }
    const map = { ...filter.parentOptionKeysByOption };
    if (parentKeys.length > 0) {
      map[childKey] = parentKeys;
    } else {
      delete map[childKey];
    }
    write({ ...filter, parentOptionKeysByOption: map });
  };
  return (
    <section aria-label="Dependent choices" className="flex flex-col gap-2">
      <h3 className="text-sm font-semibold">Dependent choices</h3>
      <p className="text-xs text-muted">Offer only the choices that fit another dropdown's selection — for example the provinces of the selected country. When the other dropdown changes, a choice that no longer fits is cleared in the form (and announced); the server refuses to save a combination that does not fit.</p>
      <Checkbox
        label="Filter the choices by another dropdown"
        checked={filter !== undefined}
        disabled={!canDesign || (candidates.length === 0 && filter === undefined)}
        onCheckedChange={(checked) => (checked && candidates[0] ? write({ parentFieldDefinitionId: candidates[0].fieldDefinitionId, parentOptionKeysByOption: {} }) : write(undefined))}
      />
      {candidates.length === 0 && filter === undefined ? <p className="text-xs text-muted">Place another dropdown {scopes.get(field.fieldDefinitionId) ? "in this section or outside sections that allow multiple entries" : "outside sections that allow multiple entries"} to filter by it.</p> : null}
      {filter && parent ? (
        <>
          <Select label="Depends on" value={parent.fieldDefinitionId} disabled={!canDesign} options={parentChoices.map((candidate) => ({ value: candidate.fieldDefinitionId, label: candidate.fieldDisplayName }))} onValueChange={(id) => id !== parent.fieldDefinitionId && write({ parentFieldDefinitionId: id, parentOptionKeysByOption: {} })} />
          <p className="text-xs text-muted">For each choice, tick the {parent.fieldDisplayName} values it belongs to. A choice with nothing ticked is always available.</p>
          {childOptions.map((option) => {
            const mapped = filter.parentOptionKeysByOption[option.optionKey] ?? [];
            return (
              <fieldset key={option.optionKey} className="flex flex-col gap-1 rounded-control border border-line p-2" data-dependent-choice={option.optionKey}>
                <legend className={`px-1 ${legendClass}`}>
                  {option.optionDisplayText} — {mapped.length === 0 ? "always available" : `available when ${parent.fieldDisplayName} is ${mapped.map((key) => parent.options.find((candidate) => candidate.optionKey === key)?.optionDisplayText ?? key).join(" or ")}`}
                </legend>
                {parentOptions.map((parentOption) => (
                  <Checkbox
                    key={parentOption.optionKey}
                    label={`${option.optionDisplayText} when ${parentOption.optionDisplayText}`}
                    checked={mapped.includes(parentOption.optionKey)}
                    disabled={!canDesign}
                    onCheckedChange={(checked) => setMapping(option.optionKey, parentOptions.map((candidate) => candidate.optionKey).filter((key) => (key === parentOption.optionKey ? checked : mapped.includes(key))))}
                  />
                ))}
              </fieldset>
            );
          })}
        </>
      ) : null}
      {cycle ? (
        <StatusMessage tone="danger" title="Circular dropdown dependency">
          {cycle.map(named).join(" → ")}. Dropdowns cannot filter each other in a loop; the draft cannot be saved until this is removed.
        </StatusMessage>
      ) : null}
    </section>
  );
}

function ConditionEditor({ label, root, candidates, fields, canDesign, onChange }: { label: string; root: RuleCondition; candidates: Source[]; fields: readonly FieldDefinitionResponse[]; canDesign: boolean; onChange: (next: RuleCondition | undefined) => void }) {
  const group = asGroup(root);
  const all = fields.map(toSource);
  const fieldsById = new Map(all.map((source) => [source.fieldDefinitionId, source]));
  const problem = conditionProblem(group, fieldsById);
  const nodes = countNodes(group);
  const commit = (next: RuleCondition | undefined) => onChange(next);
  const optionLabel = (fieldId: string, key: string) => fields.find((candidate) => candidate.fieldDefinitionId === fieldId)?.options.find((option) => option.optionKey === key)?.optionDisplayText ?? key;

  function renderNode(node: RuleCondition, path: ConditionPath, depth: number, position: string): React.ReactNode {
    if ("conditions" in node) {
      const canAdd = canDesign && nodes < RULE_MAX_NODES && candidates.length > 0;
      const first = candidates[0];
      return (
        <fieldset key={path.join(".")} className="flex flex-col gap-2 rounded-control border border-line bg-page p-2" aria-label={`${label} ${node.op === "not" ? "negated" : "condition"} group ${position}`}>
          <div className="flex flex-wrap items-center gap-2">
            {node.op === "not" ? (
              <Badge tone="neutral">Not</Badge>
            ) : (
              <Select
                label={`${label} group ${position} matches`}
                value={node.op}
                disabled={!canDesign}
                options={[
                  { value: "and", label: "All of the following (AND)" },
                  { value: "or", label: "Any of the following (OR)" },
                ]}
                onValueChange={(value) => commit(replaceNode(group, path, { ...node, op: value as "and" | "or" }))}
              />
            )}
          </div>
          {node.conditions.map((child, index) => renderNode(child, [...path, index], depth + 1, `${position}.${index + 1}`))}
          {node.op !== "not" && canDesign ? (
            <div className="flex flex-wrap gap-2">
              <Button size="sm" variant="secondary" disabled={!canAdd} onClick={() => first && commit(appendCondition(group, path, newComparison(first.fieldDefinitionId, allowedOperators(first))))}>
                Add condition
              </Button>
              <Button size="sm" variant="secondary" disabled={!canAdd || depth + 1 >= RULE_MAX_DEPTH} onClick={() => first && commit(appendCondition(group, path, { op: "and", conditions: [newComparison(first.fieldDefinitionId, allowedOperators(first))] }))}>
                Add group
              </Button>
              {path.length > 0 ? (
                <Button size="sm" variant="ghost" onClick={() => commit(removeNode(group, path))}>
                  Remove group
                </Button>
              ) : null}
            </div>
          ) : null}
        </fieldset>
      );
    }
    const source = fieldsById.get(node.fieldDefinitionId);
    const sourceCandidates = candidates.some((candidate) => candidate.fieldDefinitionId === node.fieldDefinitionId) || !source ? candidates : [source, ...candidates];
    const operators = source ? allowedOperators(source) : [];
    const rowProblem = conditionProblem(node, fieldsById);
    return (
      <div key={path.join(".")} className="flex flex-col gap-2 rounded-control border border-line bg-surface p-2" data-condition-row={position}>
        <Select
          label={`${label} condition ${position} field`}
          value={node.fieldDefinitionId}
          disabled={!canDesign}
          options={sourceCandidates.map((candidate) => ({ value: candidate.fieldDefinitionId, label: candidate.fieldDisplayName }))}
          onValueChange={(id) => {
            const next = fieldsById.get(id);
            if (next) {
              commit(replaceNode(group, path, newComparison(id, allowedOperators(next))));
            }
          }}
        />
        <Select
          label={`${label} condition ${position} operator`}
          value={node.op}
          disabled={!canDesign}
          options={operators.map((operator) => ({ value: operator, label: OPERATOR_LABEL[operator] }))}
          onValueChange={(operator) => commit(replaceNode(group, path, switchOperator(node, operator as ComparisonOperator)))}
        />
        {source ? <OperandEditor label={`${label} condition ${position}`} field={fields.find((candidate) => candidate.fieldDefinitionId === node.fieldDefinitionId)!} condition={node} disabled={!canDesign} onChange={(next) => commit(replaceNode(group, path, next))} /> : null}
        {rowProblem ? (
          <p role="alert" className={errorTextClass}>
            {rowProblem}
          </p>
        ) : null}
        {canDesign ? (
          <div>
            <Button size="sm" variant="ghost" onClick={() => commit(removeNode(group, path))}>
              Remove condition {position}
            </Button>
          </div>
        ) : null}
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2" data-rule-editor={label.toLowerCase()}>
      {renderNode(group, [], 0, "1")}
      <p className="text-xs text-muted" data-testid={`${label.toLowerCase()}-summary`}>
        {describeCondition(group, new Map(all.map((source) => [source.fieldDefinitionId, source])), optionLabel)}
      </p>
      {problem ? <p className="text-xs text-muted">Complete the highlighted condition before saving the draft.</p> : null}
    </div>
  );
}

/** Switching operators keeps the operand when its shape still fits, otherwise starts the new shape empty. */
function switchOperator(node: Extract<RuleCondition, { fieldDefinitionId: string }>, operator: ComparisonOperator): RuleCondition {
  const base = { fieldDefinitionId: node.fieldDefinitionId };
  if (operator === "isEmpty" || operator === "isNotEmpty") {
    return { op: operator, ...base };
  }
  if (operator === "in" || operator === "notIn") {
    return { op: operator, ...base, values: node.values ?? (node.value ? [node.value] : []) };
  }
  return { op: operator, ...base, value: node.value ?? node.values?.[0] ?? "" };
}

function OperandEditor({ label, field, condition, disabled, onChange }: { label: string; field: FieldDefinitionResponse; condition: Extract<RuleCondition, { fieldDefinitionId: string }>; disabled: boolean; onChange: (next: RuleCondition) => void }) {
  if (condition.op === "isEmpty" || condition.op === "isNotEmpty") {
    return null;
  }
  const activeOptions = [...field.options].filter((option) => option.isActive).sort((a, b) => Number(a.optionSortOrder) - Number(b.optionSortOrder));
  const isOption = field.fieldDataType === "Option";
  if (condition.op === "in" || condition.op === "notIn") {
    const values = condition.values ?? [];
    if (isOption) {
      return (
        <fieldset className="flex flex-col gap-1 border-0 p-0">
          <legend className={legendClass}>{label} values</legend>
          {activeOptions.map((option) => (
            <Checkbox key={option.optionKey} label={option.optionDisplayText} checked={values.includes(option.optionKey)} disabled={disabled} onCheckedChange={(checked) => onChange({ ...condition, values: checked ? [...values, option.optionKey] : values.filter((value) => value !== option.optionKey) })} />
          ))}
        </fieldset>
      );
    }
    return <TextArea label={`${label} values (one per line)`} rows={3} value={values.join("\n")} disabled={disabled} maxLength={2000} onChange={(event) => onChange({ ...condition, values: event.target.value.split("\n").map((line) => line.trim()).filter(Boolean) })} />;
  }
  const value = condition.value ?? "";
  if (isOption) {
    const multi = field.optionSelectionMode === "Multiple" && (condition.op === "equals" || condition.op === "notEquals");
    if (multi) {
      const selected = splitSelection(value);
      return (
        <fieldset className="flex flex-col gap-1 border-0 p-0">
          <legend className={legendClass}>{label} selection</legend>
          {activeOptions.map((option) => (
            <Checkbox key={option.optionKey} label={option.optionDisplayText} checked={selected.includes(option.optionKey)} disabled={disabled} onCheckedChange={(checked) => onChange({ ...condition, value: activeOptions.map((candidate) => candidate.optionKey).filter((key) => (key === option.optionKey ? checked : selected.includes(key))).join(",") })} />
          ))}
        </fieldset>
      );
    }
    return <Select label={`${label} value`} value={value || undefined} disabled={disabled} options={activeOptions.map((option) => ({ value: option.optionKey, label: option.optionDisplayText }))} placeholder="Choose an option" onValueChange={(next) => onChange({ ...condition, value: next })} />;
  }
  switch (field.fieldDataType) {
    case "Boolean":
      return <Select label={`${label} value`} value={value || undefined} disabled={disabled} options={[{ value: "true", label: "Yes (checked)" }, { value: "false", label: "No (unchecked)" }]} placeholder="Choose" onValueChange={(next) => onChange({ ...condition, value: next })} />;
    case "Date":
      return <TextInput label={`${label} value`} type="date" value={value} disabled={disabled} onChange={(event) => onChange({ ...condition, value: event.target.value })} />;
    case "DateTime":
      return <TextInput label={`${label} value`} type="datetime-local" value={toLocal(value)} disabled={disabled} onChange={(event) => onChange({ ...condition, value: fromLocal(event.target.value) })} />;
    case "Integer":
    case "Decimal":
      return <TextInput label={`${label} value`} inputMode="decimal" value={value} disabled={disabled} onChange={(event) => onChange({ ...condition, value: event.target.value.trim() })} />;
    default:
      return <TextInput label={`${label} value`} value={value} maxLength={200} disabled={disabled} onChange={(event) => onChange({ ...condition, value: event.target.value.replace(/[<>]/g, "") })} />;
  }
}

const pad = (n: number) => String(n).padStart(2, "0");
function toLocal(wire: string): string {
  const parsed = new Date(wire);
  return !wire || Number.isNaN(parsed.getTime()) ? "" : `${parsed.getFullYear()}-${pad(parsed.getMonth() + 1)}-${pad(parsed.getDate())}T${pad(parsed.getHours())}:${pad(parsed.getMinutes())}`;
}
function fromLocal(local: string): string {
  const parsed = new Date(local);
  return !local || Number.isNaN(parsed.getTime()) ? "" : parsed.toISOString().replace(/\.\d{3}Z$/, "Z");
}

/** Evaluates this field's rules for synthetic sample values of the fields they read; nothing here is saved or sent. */
function TryIt({ layout, element, sources, fields }: { layout: FormLayout; element: FieldElement; sources: Source[]; fields: readonly FieldDefinitionResponse[] }) {
  const [samples, setSamples] = useState<Record<string, string>>({});
  const referenced = [...new Set([...comparisonsOf(element.visibleWhen), ...comparisonsOf(element.requiredWhen)].map((comparison) => comparison.fieldDefinitionId))];
  const elements = layout.sections.flatMap((section) => section.elements).filter((candidate): candidate is FieldElement => candidate.kind === "field");
  const states = evaluateRuleStates(elements, new Map(sources.map((source) => [source.fieldDefinitionId, source])), new Map(Object.entries(samples)));
  const state = states.get(element.fieldDefinitionId);
  return (
    <section aria-label="Try the rules" className="flex flex-col gap-2 rounded-control border border-dashed border-input p-3">
      <h3 className="text-sm font-semibold">Try it with sample values</h3>
      <p className="text-xs text-muted">Synthetic values only — nothing typed here is saved or sent.</p>
      {referenced.map((id) => {
        const field = fields.find((candidate) => candidate.fieldDefinitionId === id);
        if (!field) {
          return null;
        }
        const options = [...field.options].filter((option) => option.isActive).sort((a, b) => Number(a.optionSortOrder) - Number(b.optionSortOrder));
        const value = samples[id] ?? "";
        const set = (next: string) => setSamples((current) => ({ ...current, [id]: next }));
        if (field.fieldDataType === "Option" && field.optionSelectionMode === "Multiple") {
          const selected = splitSelection(value);
          return (
            <fieldset key={id} className="flex flex-col gap-1 border-0 p-0">
              <legend className={legendClass}>Sample {field.fieldDisplayName}</legend>
              {options.map((option) => (
                <Checkbox key={option.optionKey} label={option.optionDisplayText} checked={selected.includes(option.optionKey)} onCheckedChange={(checked) => set(options.map((candidate) => candidate.optionKey).filter((key) => (key === option.optionKey ? checked : selected.includes(key))).join(","))} />
              ))}
            </fieldset>
          );
        }
        if (field.fieldDataType === "Option") {
          return <Select key={id} label={`Sample ${field.fieldDisplayName}`} value={value || undefined} options={options.map((option) => ({ value: option.optionKey, label: option.optionDisplayText }))} placeholder="(empty)" onValueChange={set} />;
        }
        if (field.fieldDataType === "Boolean") {
          return <Select key={id} label={`Sample ${field.fieldDisplayName}`} value={value || undefined} options={[{ value: "true", label: "Yes" }, { value: "false", label: "No" }]} placeholder="(empty)" onValueChange={set} />;
        }
        if (field.fieldDataType === "Date") {
          return <TextInput key={id} label={`Sample ${field.fieldDisplayName}`} type="date" value={value} onChange={(event) => set(event.target.value)} />;
        }
        if (field.fieldDataType === "DateTime") {
          return <TextInput key={id} label={`Sample ${field.fieldDisplayName}`} type="datetime-local" value={toLocal(value)} onChange={(event) => set(fromLocal(event.target.value))} />;
        }
        return <TextInput key={id} label={`Sample ${field.fieldDisplayName}`} value={value} onChange={(event) => set(event.target.value)} />;
      })}
      <p role="status" aria-live="polite" className="text-sm font-medium" data-testid="rule-preview-result">
        With these values this field is {state?.visible === false ? "hidden" : "shown"} and {state?.required ? "required" : "optional"}.
      </p>
    </section>
  );
}
