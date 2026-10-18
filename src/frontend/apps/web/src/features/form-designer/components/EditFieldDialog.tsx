import type { FieldDefinitionResponse } from "@enterpriseforms/api-client";
import { applyEditValues, toEditValues, validateEditValues, type FieldEditValues, type FieldLocation } from "@enterpriseforms/form-designer";
import type { LayoutElement } from "@enterpriseforms/form-renderer";
import { Button, Checkbox, Dialog, StatusMessage, TextArea, TextInput } from "@enterpriseforms/ui";
import { useState } from "react";
import { ErrorPanel } from "../../support";

export interface FieldEditSubmission {
  displayName: string;
  description: string;
  /** Present when the field is placed on the draft layout: the element with the edited presentation. */
  element: LayoutElement | null;
  catalogChanged: boolean;
}

/**
 * Edit Field: label, description, help text, requiredness, validation and (for a document collection) upload limits.
 * The key and storage type are shown read-only with the reason they are permanent. Presentation edits go to the layout
 * draft (published versions stay immutable until the draft is published); the catalog label and description are saved
 * on the field identity, so stored values and attachments, which reference the field id, are untouched.
 */
export function EditFieldDialog({ field, location, canEdit, pending, error, onSave, onClose }: { field: FieldDefinitionResponse; location: FieldLocation | null; canEdit: boolean; pending: boolean; error: unknown; onSave: (submission: FieldEditSubmission) => void; onClose: () => void }) {
  const [values, setValues] = useState<FieldEditValues>(() => toEditValues(location?.element ?? null, field.fieldDisplayName));
  const [description, setDescription] = useState(field.fieldDescription);
  const errors = validateEditValues(values, field.fieldDataType);
  const set = <K extends keyof FieldEditValues>(key: K, value: FieldEditValues[K]) => setValues((current) => ({ ...current, [key]: value }));
  const isDocuments = field.fieldDataType === "File";
  const catalogChanged = values.label.trim() !== field.fieldDisplayName || description.trim() !== field.fieldDescription;
  const invalid = Object.keys(errors).length > 0;
  const field_ = (name: keyof FieldEditValues) => errors[name];
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
      <TextInput label="Display label" value={values.label} required maxLength={200} disabled={!canEdit} error={field_("label")} helpText="Shown on forms, lists and in this catalog. Changing it never changes the field key." onChange={(event) => set("label", event.target.value)} />
      <TextInput label="Field key" value={field.fieldKey} readOnly helpText="Permanent identifier used by records, searches and integrations. It cannot be renamed: add a new field if you need a different key." />
      <TextInput label="Storage type" value={isDocuments ? "File (document collection)" : field.fieldDataType} readOnly helpText="Permanent: changing it would reinterpret stored values. Add a new field of the new type and migrate deliberately." />
      <TextArea label="Description" value={description} rows={2} maxLength={1000} disabled={!canEdit} onChange={(event) => setDescription(event.target.value)} />
      {location ? (
        <>
          <TextInput label="Help text" value={values.helpText} maxLength={1000} disabled={!canEdit} error={field_("helpText")} onChange={(event) => set("helpText", event.target.value)} />
          <Checkbox label="Required" checked={values.required} disabled={!canEdit} onCheckedChange={(checked) => set("required", checked === true)} />
          {field.fieldDataType === "String" ? (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <TextInput label="Minimum length" type="text" inputMode="numeric" value={values.minLength} disabled={!canEdit} error={field_("minLength")} onChange={(event) => set("minLength", event.target.value)} />
              <TextInput label="Maximum length" type="text" inputMode="numeric" value={values.maxLength} disabled={!canEdit} error={field_("maxLength")} onChange={(event) => set("maxLength", event.target.value)} />
            </div>
          ) : null}
          {field.fieldDataType === "Integer" || field.fieldDataType === "Decimal" ? (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <TextInput label="Minimum value" type="text" inputMode="decimal" value={values.minimum} disabled={!canEdit} error={field_("minimum")} onChange={(event) => set("minimum", event.target.value)} />
              <TextInput label="Maximum value" type="text" inputMode="decimal" value={values.maximum} disabled={!canEdit} error={field_("maximum")} onChange={(event) => set("maximum", event.target.value)} />
            </div>
          ) : null}
          {field.fieldDataType === "Date" || field.fieldDataType === "DateTime" ? (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <TextInput label="Earliest date" type="date" value={values.minDate} disabled={!canEdit} error={field_("minDate")} onChange={(event) => set("minDate", event.target.value)} />
              <TextInput label="Latest date" type="date" value={values.maxDate} disabled={!canEdit} error={field_("maxDate")} onChange={(event) => set("maxDate", event.target.value)} />
            </div>
          ) : null}
          {isDocuments ? (
            <>
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <TextInput label="Maximum files" type="text" inputMode="numeric" value={values.maxFiles} disabled={!canEdit} error={field_("maxFiles")} onChange={(event) => set("maxFiles", event.target.value)} />
                <TextInput label="Maximum file size (MB)" type="text" inputMode="numeric" value={values.maxFileSizeMb} disabled={!canEdit} error={field_("maxFileSizeMb")} onChange={(event) => set("maxFileSizeMb", event.target.value)} />
              </div>
              <TextInput label="Allowed content types" value={values.allowedContentTypes} disabled={!canEdit} helpText="Comma-separated MIME types, e.g. application/pdf, text/plain. Empty allows any type the server accepts. Documents already stored are never removed by a change here." onChange={(event) => set("allowedContentTypes", event.target.value)} />
            </>
          ) : null}
          <p className="text-xs text-muted">Validation applies to new saves; values and documents already stored are never rewritten.</p>
        </>
      ) : (
        <StatusMessage tone="info" title="Not on the draft layout">
          This field is not placed on the draft layout. Place it in the Layout tab to set its help text, requiredness and validation. The label and description above are still saved on the field.
        </StatusMessage>
      )}
      {error ? <ErrorPanel error={error} /> : null}
    </Dialog>
  );
}
