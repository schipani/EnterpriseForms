import type { FieldDefinitionResponse, StrengthenFieldProtectionRequest } from "@enterpriseforms/api-client";
import { CurrentProtectionSection } from "./FieldSecuritySection";
import { applyEditValues, toEditValues, validateEditValues, type FieldEditValues, type FieldEditErrors, type FieldLocation } from "@enterpriseforms/form-designer";
import type { LayoutElement } from "@enterpriseforms/form-renderer";
import { Button, Checkbox, Dialog, StatusMessage, Tabs, TextArea, TextInput, type TabItem } from "@enterpriseforms/ui";
import { useState } from "react";
import { ErrorPanel } from "../../support";

export interface FieldEditSubmission {
  displayName: string;
  description: string;
  /** Present when the field is placed on the draft layout: the element with the edited presentation. */
  element: LayoutElement | null;
  catalogChanged: boolean;
}

const GENERAL_KEYS: (keyof FieldEditValues)[] = ["label", "helpText"];
const VALIDATION_KEYS: (keyof FieldEditValues)[] = ["minLength", "maxLength", "minimum", "maximum", "minDate", "maxDate", "maxFiles", "maxFileSizeMb"];

function tabLabel(text: string, hasErrors: boolean) {
  return hasErrors ? (
    <span className="inline-flex items-center gap-1.5">
      {text}
      <span aria-hidden="true" className="inline-block h-2 w-2 rounded-full bg-[#991b1b]" />
      <span className="sr-only">(has errors)</span>
    </span>
  ) : (
    text
  );
}

/**
 * Edit Field / Edit document collection (C1-UX §C): a stable-tab dialog — General, Validation, Rules, Security &amp;
 * Encryption, Advanced — backed by one lifted form-state object, so switching tabs never loses unsaved input.
 * Validation errors mark their own tab with a dot. The key and storage type are permanent and shown read-only;
 * presentation edits go to the draft layout (published versions stay immutable). Security & Encryption reuses the
 * existing Authority-backed strengthen/declassify/downgrade workflow unchanged — this dialog never invents its own
 * encryption toggle.
 */
export function EditFieldDialog({ field, location, canEdit, pending, error, onSave, onClose, formDefinitionId, onStrengthen, strengthenPending, strengthenError }: { field: FieldDefinitionResponse; location: FieldLocation | null; canEdit: boolean; pending: boolean; error: unknown; onSave: (submission: FieldEditSubmission) => void; onClose: () => void; formDefinitionId: string; onStrengthen: (request: StrengthenFieldProtectionRequest) => void; strengthenPending: boolean; strengthenError: unknown }) {
  const [values, setValues] = useState<FieldEditValues>(() => toEditValues(location?.element ?? null, field.fieldDisplayName));
  const [description, setDescription] = useState(field.fieldDescription);
  const [tab, setTab] = useState("general");
  const errors: FieldEditErrors = validateEditValues(values, field.fieldDataType);
  const set = <K extends keyof FieldEditValues>(key: K, value: FieldEditValues[K]) => setValues((current) => ({ ...current, [key]: value }));
  const isDocuments = field.fieldDataType === "File";
  const catalogChanged = values.label.trim() !== field.fieldDisplayName || description.trim() !== field.fieldDescription;
  const invalid = Object.keys(errors).length > 0;
  const errorFor = (name: keyof FieldEditValues) => errors[name];
  const hasAny = (keys: (keyof FieldEditValues)[]) => keys.some((key) => Boolean(errors[key]));

  const generalContent = (
    <div className="flex flex-col gap-3">
      <TextInput label="Display label" value={values.label} required maxLength={200} disabled={!canEdit} error={errorFor("label")} helpText="Shown on forms, lists and in this catalog. Changing it never changes the field key." onChange={(event) => set("label", event.target.value)} />
      <TextInput label="Field key" value={field.fieldKey} readOnly helpText="Permanent identifier used by records, searches and integrations. It cannot be renamed: add a new field if you need a different key." />
      <TextInput label="Storage type" value={isDocuments ? "File (document collection)" : field.fieldDataType} readOnly helpText="Permanent: changing it would reinterpret stored values. Add a new field of the new type and migrate deliberately." />
      <TextArea label="Description" value={description} rows={2} maxLength={1000} disabled={!canEdit} onChange={(event) => setDescription(event.target.value)} />
      {location ? (
        <TextInput label="Help text" value={values.helpText} maxLength={1000} disabled={!canEdit} error={errorFor("helpText")} onChange={(event) => set("helpText", event.target.value)} />
      ) : (
        <StatusMessage tone="info" title="Not on the draft layout">
          This field is not placed on the draft layout. Place it on the Design canvas to set its help text, requiredness and validation. The label and description above are still saved on the field.
        </StatusMessage>
      )}
    </div>
  );

  const validationContent = location ? (
    <div className="flex flex-col gap-3">
      <Checkbox label="Required" checked={values.required} disabled={!canEdit} onCheckedChange={(checked) => set("required", checked === true)} />
      {field.fieldDataType === "String" ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <TextInput label="Minimum length" type="text" inputMode="numeric" value={values.minLength} disabled={!canEdit} error={errorFor("minLength")} onChange={(event) => set("minLength", event.target.value)} />
          <TextInput label="Maximum length" type="text" inputMode="numeric" value={values.maxLength} disabled={!canEdit} error={errorFor("maxLength")} onChange={(event) => set("maxLength", event.target.value)} />
        </div>
      ) : null}
      {field.fieldDataType === "Integer" || field.fieldDataType === "Decimal" ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <TextInput label="Minimum value" type="text" inputMode="decimal" value={values.minimum} disabled={!canEdit} error={errorFor("minimum")} onChange={(event) => set("minimum", event.target.value)} />
          <TextInput label="Maximum value" type="text" inputMode="decimal" value={values.maximum} disabled={!canEdit} error={errorFor("maximum")} onChange={(event) => set("maximum", event.target.value)} />
        </div>
      ) : null}
      {field.fieldDataType === "Date" || field.fieldDataType === "DateTime" ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <TextInput label="Earliest date" type="date" value={values.minDate} disabled={!canEdit} error={errorFor("minDate")} onChange={(event) => set("minDate", event.target.value)} />
          <TextInput label="Latest date" type="date" value={values.maxDate} disabled={!canEdit} error={errorFor("maxDate")} onChange={(event) => set("maxDate", event.target.value)} />
        </div>
      ) : null}
      {isDocuments ? (
        <>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <TextInput label="Maximum files" type="text" inputMode="numeric" value={values.maxFiles} disabled={!canEdit} error={errorFor("maxFiles")} onChange={(event) => set("maxFiles", event.target.value)} />
            <TextInput label="Maximum file size (MB)" type="text" inputMode="numeric" value={values.maxFileSizeMb} disabled={!canEdit} error={errorFor("maxFileSizeMb")} onChange={(event) => set("maxFileSizeMb", event.target.value)} />
          </div>
          <TextInput label="Allowed content types" value={values.allowedContentTypes} disabled={!canEdit} helpText="Comma-separated MIME types, e.g. application/pdf, text/plain. Empty allows any type the server accepts. Documents already stored are never removed by a change here." onChange={(event) => set("allowedContentTypes", event.target.value)} />
        </>
      ) : null}
      <p className="text-xs text-muted">Validation applies to new saves; values and documents already stored are never rewritten.</p>
    </div>
  ) : (
    <StatusMessage tone="info" title="Not on the draft layout">
      Place this field on the draft layout (Design canvas) to set its validation rules.
    </StatusMessage>
  );

  const rulesContent = (
    <StatusMessage tone="info" title="Rules are edited in the field properties">
      Conditional visibility, required-if, Show in form and Read-only live in the Rules tab of the Field Properties inspector. Close this dialog, select the field on the Design canvas and open its Rules tab.
    </StatusMessage>
  );

  const securityContent = <CurrentProtectionSection formDefinitionId={formDefinitionId} field={field} canEdit={canEdit} onStrengthen={onStrengthen} pending={strengthenPending} error={strengthenError} />;

  const advancedContent = (
    <div className="flex flex-col gap-3 text-sm">
      <TextInput label="Field UUID" value={field.fieldDefinitionId} readOnly helpText="Stable identity (FieldDefinitionId). Reusing an existing field — rather than creating a new one — keeps this id, its key and its historical values." />
      <TextInput label="Field definition revision" value={String(field.fieldDefinitionRevision)} readOnly />
      <TextInput label="Protection policy revision" value={String(field.protectionPolicyRevision)} readOnly />
      <TextInput label="Lifecycle status" value={field.lifecycleStatus} readOnly />
      <p className="text-xs text-muted">Diagnostic identifiers only; nothing here is editable and no secret or key material is shown.</p>
    </div>
  );

  const items: TabItem[] = [
    { value: "general", label: tabLabel("General", hasAny(GENERAL_KEYS)), content: generalContent },
    { value: "validation", label: tabLabel("Validation", hasAny(VALIDATION_KEYS)), content: validationContent },
    { value: "rules", label: "Rules", content: rulesContent },
    { value: "security", label: "Security & Encryption", content: securityContent },
    { value: "advanced", label: "Advanced", content: advancedContent },
  ];

  return (
    <Dialog
      open
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={`Edit ${isDocuments ? "document collection" : "field"}`}
      description="Changes to the draft layout take effect for records only after you publish a new version. The field identity and everything already stored are preserved."
      locked={pending}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={pending}>
            Cancel
          </Button>
          <Button
            busy={pending}
            disabled={!canEdit || invalid}
            onClick={() => onSave({ displayName: values.label.trim(), description: description.trim(), catalogChanged, element: location ? applyEditValues(location.element, values, field.fieldDataType) : null })}
          >
            Save changes
          </Button>
        </>
      }
    >
      <Tabs label={`Edit ${isDocuments ? "document collection" : "field"} sections`} value={tab} onValueChange={setTab} items={items} />
      {error ? <ErrorPanel error={error} /> : null}
    </Dialog>
  );
}
