import type { FieldSearchCapabilitiesResponse, FieldSearchCapabilityField } from "@enterpriseforms/api-client";
import { ActionIcons, Badge, Button, helpTextClass, LoadingBlock, Select, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useEffect, useId, useState, type FormEvent, type ReactNode } from "react";
import { addCondition, arity, fieldFor, maximumConditions, newCondition, operatorLabel, removeCondition, setField, setOperator, setValue, validateConditions, type ConditionDraft, type ConditionErrors } from "./conditionModel";

export interface AdvancedFieldSearchProps {
  /** A form is selected in the main filters; advanced field search always runs over one form. */
  formSelected: boolean;
  /** Server capability response for the selected form (the only source of searchable fields and operators). */
  capabilities: FieldSearchCapabilitiesResponse | undefined;
  loading?: boolean | undefined;
  /** The capability request failed: nothing is offered (never a guessed field list). */
  loadFailed?: boolean | undefined;
  onRetryLoad?: (() => void) | undefined;
  /** Runs the search. Pass it only when the server reports field-value search available; otherwise Search records stays disabled. */
  onSearch?: ((conditions: ConditionDraft[]) => void) | undefined;
  searching?: boolean | undefined;
  defaultExpanded?: boolean | undefined;
  /**
   * false: the panel is always visible under a plain heading (the My Records side-by-side layout) and its controls stack in
   * one column. true (default): a disclosure, as in the gallery preview.
   */
  collapsible?: boolean | undefined;
  /** Shown above the conditions, e.g. the gallery's "synthetic preview" notice. */
  notice?: ReactNode;
}

/**
 * Advanced Search (PG-015, Phase C7; owner-approved layout): one or more conditions, each Field and Condition side by side
 * with the Value beneath, then Add condition and Search records. Conditions combine with AND. Fields, operators and option
 * choices come only from the server capability response; when it reports field-value search unavailable (Phase C7 M0:
 * trusted execution not enabled) every control is disabled and the reason is stated — nothing is searched.
 */
export function AdvancedFieldSearch({ formSelected, capabilities, loading, loadFailed, onRetryLoad, onSearch, searching, defaultExpanded = false, collapsible = true, notice }: AdvancedFieldSearchProps) {
  const headingId = useId();
  const panelId = useId();
  const unavailableId = useId();
  const [expandedState, setExpanded] = useState(defaultExpanded);
  const expanded = collapsible ? expandedState : true;
  const stacked = !collapsible;
  const [conditions, setConditions] = useState<ConditionDraft[]>(() => [newCondition()]);
  const [errors, setErrors] = useState<Map<string, ConditionErrors>>(new Map());
  const [attempted, setAttempted] = useState(false);
  const formId = capabilities?.formDefinitionId;
  // A different form has different fields: start again rather than keep conditions that refer to the previous form.
  useEffect(() => {
    setConditions([newCondition()]);
    setErrors(new Map());
    setAttempted(false);
  }, [formId]);

  const available = formSelected && capabilities?.fieldValueSearchAvailable === true;
  const executable = available && onSearch !== undefined && (capabilities?.fields.length ?? 0) > 0;
  const maximum = maximumConditions(capabilities);
  // Phase C7 M2: the fields are the Authority-approved ones this person may search. They can be chosen and conditions
  // composed even while execution is off (M3), so the controls reflect real permissions; Search records stays disabled.
  const fields = formSelected && !loadFailed ? (capabilities?.fields ?? []) : [];
  const composable = fields.length > 0;
  const update = (next: ConditionDraft[]) => {
    setConditions(next);
    if (attempted) {
      setErrors(validateConditions(next, capabilities));
    }
  };
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!executable) {
      return;
    }
    const found = validateConditions(conditions, capabilities);
    setAttempted(true);
    setErrors(found);
    if (found.size === 0) {
      onSearch(conditions);
    }
  };
  const unavailableReason = !formSelected ? null : loading ? null : loadFailed ? "failed" : capabilities && !capabilities.fieldValueSearchAvailable ? (capabilities.unavailableReasonCode ?? "UNAVAILABLE") : null;
  const notSearchable = capabilities?.notSearchableFields ?? [];

  return (
    <section aria-labelledby={headingId} className={stacked ? "flex flex-col gap-4" : "flex flex-col gap-3 border-t border-input pt-4"}>
      <h3 id={headingId} className="m-0 text-base font-semibold">
        {collapsible ? (
          <button type="button" aria-expanded={expanded} aria-controls={panelId} onClick={() => setExpanded((open) => !open)} className="flex min-h-control w-full items-center justify-between gap-3 rounded-control px-1 text-left">
            <span>Advanced Search</span>
            <span className="flex items-center gap-2">
              {unavailableReason ? <Badge tone="neutral">Not available yet</Badge> : null}
              <svg aria-hidden="true" viewBox="0 0 16 16" className={`h-4 w-4 transition-transform ${expanded ? "rotate-180" : ""}`} fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M4 6l4 4 4-4" />
              </svg>
            </span>
          </button>
        ) : (
          <span className="flex items-center gap-2">
            <ActionIcons.filters aria-hidden="true" focusable="false" strokeWidth={2} className="h-[1.125rem] w-[1.125rem] shrink-0" />
            <span>Advanced Search</span>
            {unavailableReason ? <Badge tone="neutral">Not available yet</Badge> : null}
          </span>
        )}
      </h3>
      <div id={panelId} hidden={!expanded} className="flex flex-col gap-4">
        <p className={helpTextClass}>Find records of the selected form by the values of approved fields. Every condition must match.</p>
        {notice}
        {!formSelected ? (
          <StatusMessage tone="info" title="Choose a form first">
            Advanced Search looks inside the records of one form. Choose it in the Form filter above.
          </StatusMessage>
        ) : null}
        {formSelected && loading ? <LoadingBlock label="Loading searchable fields" /> : null}
        {unavailableReason === "failed" ? (
          <StatusMessage tone="danger" title="Searchable fields could not be loaded" actions={onRetryLoad ? <Button variant="secondary" size="sm" onClick={onRetryLoad}>Try again</Button> : undefined}>
            No fields are offered until the server says which ones may be searched.
          </StatusMessage>
        ) : null}
        {unavailableReason && unavailableReason !== "failed" ? (
          <div id={unavailableId}>
            <StatusMessage tone="info" title="Advanced field search is not available yet">
              {unavailableReason === "TRUSTED_SEARCH_NOT_ENABLED"
                ? "Searching inside records is turned off until the Authorization Authority can verify that results are complete and that only approved fields are searched. Nothing is searched here. To find one record, use Search records above with its reference or ID."
                : "Searching inside the records of this form is not available. To find one record, use Search records above with its reference or ID."}
            </StatusMessage>
          </div>
        ) : null}
        {composable && !available ? (
          <p className={helpTextClass}>You can choose approved fields and conditions to see what can be searched, but nothing is searched until trusted search is turned on.</p>
        ) : null}
        {available && fields.length === 0 ? (
          <StatusMessage tone="info" title="No fields can be searched in this form">
            None of this form's fields are approved for searching, or you cannot search them.
          </StatusMessage>
        ) : null}
        {notSearchable.length > 0 ? (
          <p className={helpTextClass}>
            {notSearchable.some((field) => field.reasonCode === "ENCRYPTED") ? <>Encrypted for privacy — not searchable: {notSearchable.filter((field) => field.reasonCode === "ENCRYPTED").map((field) => field.displayLabel).join(", ")}. </> : null}
            {notSearchable.some((field) => field.reasonCode !== "ENCRYPTED") ? <>Not approved for searching: {notSearchable.filter((field) => field.reasonCode !== "ENCRYPTED").map((field) => field.displayLabel).join(", ")}.</> : null}
          </p>
        ) : null}
        <form onSubmit={submit} noValidate aria-label="Advanced Search conditions" className="flex flex-col gap-4">
          {conditions.map((condition, index) => (
            <ConditionRow
              key={condition.key}
              number={index + 1}
              condition={condition}
              field={fieldFor(capabilities, condition.fieldDefinitionId)}
              fields={fields}
              enabled={composable}
              errors={errors.get(condition.key) ?? {}}
              removable={conditions.length > 1}
              stacked={stacked}
              onFieldChange={(fieldId) => update(setField(conditions, condition.key, fieldId, capabilities))}
              onOperatorChange={(operator) => update(setOperator(conditions, condition.key, operator))}
              onValueChange={(part, text) => update(setValue(conditions, condition.key, part, text))}
              onRemove={() => update(removeCondition(conditions, condition.key))}
            />
          ))}
          <div className="flex flex-col gap-1.5">
            <div className={stacked ? "flex flex-col gap-3 [&>button]:w-full" : "flex flex-wrap items-start gap-2"}>
              <Button type="button" variant="secondary" icon={ActionIcons.add} disabled={!composable || conditions.length >= maximum} onClick={() => update(addCondition(conditions, maximum))} aria-describedby={unavailableReason ? unavailableId : undefined}>
                Add condition
              </Button>
              <Button type="submit" icon={ActionIcons.search} disabled={!executable} busy={Boolean(searching)} aria-describedby={unavailableReason ? unavailableId : undefined}>
                Search records
              </Button>
            </div>
            {composable ? <p className={helpTextClass}>{conditions.length >= maximum ? `You can use up to ${maximum} conditions.` : `Up to ${maximum} conditions; all of them must match.`}</p> : null}
          </div>
        </form>
      </div>
    </section>
  );
}

interface ConditionRowProps {
  number: number;
  condition: ConditionDraft;
  field: FieldSearchCapabilityField | undefined;
  fields: readonly FieldSearchCapabilityField[];
  enabled: boolean;
  errors: ConditionErrors;
  removable: boolean;
  /** Single column (field, condition, value stacked) with no frame around a lone condition. */
  stacked: boolean;
  onFieldChange: (fieldDefinitionId: string) => void;
  onOperatorChange: (operator: string) => void;
  onValueChange: (part: "value" | "lowerBound" | "upperBound", text: string) => void;
  onRemove: () => void;
}

function ConditionRow({ number, condition, field, fields, enabled, errors, removable, stacked, onFieldChange, onOperatorChange, onValueChange, onRemove }: ConditionRowProps) {
  const kind = arity(condition.operator);
  const bare = stacked && !removable;
  const columns = stacked ? "grid-cols-1" : "sm:grid-cols-2";
  // Inside a <form>, Radix Select's hidden native select can report "" while its options change (for example when a new
  // field replaces the operator list); that is never a choice the person made, so it must not clear the condition.
  const chosen = (handler: (value: string) => void) => (value: string) => {
    if (value !== "") handler(value);
  };
  return (
    <fieldset className={`flex min-w-0 flex-col gap-3 ${bare ? "border-0 p-0" : "rounded border border-input p-3"}`}>
      <legend className={bare ? "sr-only" : "px-1 text-sm font-semibold text-body"}>Condition {number}</legend>
      <div className={`grid items-start gap-x-4 gap-y-3 ${columns}`}>
        <Select
          label="Field"
          value={condition.fieldDefinitionId}
          placeholder={enabled ? (fields.length > 0 ? "Choose a field" : "No searchable fields") : "No searchable fields"}
          options={fields.map((candidate) => ({ value: candidate.fieldDefinitionId, label: candidate.displayLabel }))}
          disabled={!enabled || fields.length === 0}
          error={errors.field}
          onValueChange={chosen(onFieldChange)}
        />
        <Select
          label="Condition"
          value={condition.operator}
          placeholder={field ? "Choose a condition" : "Choose a field first"}
          options={(field?.operators ?? []).map((operator) => ({ value: operator, label: operatorLabel(operator, field?.valueType) }))}
          disabled={!enabled || !field}
          error={errors.operator}
          onValueChange={chosen(onOperatorChange)}
        />
      </div>
      {kind === "range" && field ? (
        <div className={`grid items-start gap-x-4 gap-y-3 ${columns}`}>
          <ValueControl label="From" field={field} text={condition.lowerBound} error={errors.lowerBound} enabled={enabled} onChange={(text) => onValueChange("lowerBound", text)} />
          <ValueControl label="To" field={field} text={condition.upperBound} error={errors.upperBound} enabled={enabled} onChange={(text) => onValueChange("upperBound", text)} />
        </div>
      ) : kind === "none" && field ? (
        <p className={helpTextClass}>No value is needed for this condition.</p>
      ) : (
        <ValueControl label="Value" field={field} text={condition.value} error={errors.value} enabled={enabled && Boolean(field)} onChange={(text) => onValueChange("value", text)} />
      )}
      {removable ? (
        <div>
          <Button variant="ghost" size="sm" icon={ActionIcons.delete} aria-label={`Remove condition ${number}`} onClick={onRemove}>
            Remove
          </Button>
        </div>
      ) : null}
    </fieldset>
  );
}

function ValueControl({ label, field, text, error, enabled, onChange }: { label: string; field: FieldSearchCapabilityField | undefined; text: string; error: string | undefined; enabled: boolean; onChange: (text: string) => void }) {
  if (field?.valueType === "Option") {
    return <Select label={label} value={text || undefined} placeholder="Choose an option" options={field.options.map((option) => ({ value: option.fieldOptionId, label: option.displayText }))} disabled={!enabled} error={error} onValueChange={(value) => value !== "" && onChange(value)} />;
  }
  const type = field?.valueType === "Date" ? "date" : field?.valueType === "DateTime" ? "datetime-local" : "text";
  const inputMode = field?.valueType === "Integer" ? "numeric" : field?.valueType === "Decimal" ? "decimal" : undefined;
  const helpText = field?.valueType === "DateTime" ? "In your browser's time zone." : field?.valueType === "Decimal" ? "Use a dot for decimals, no thousands separators." : undefined;
  return (
    <TextInput
      label={label}
      type={type}
      value={text}
      disabled={!enabled}
      autoComplete="off"
      spellCheck={false}
      maxLength={field?.valueType === "String" ? Number(field.maximumTextLength ?? 200) : undefined}
      {...(inputMode ? { inputMode } : {})}
      helpText={helpText}
      error={error}
      onChange={(event) => onChange(event.target.value)}
    />
  );
}
