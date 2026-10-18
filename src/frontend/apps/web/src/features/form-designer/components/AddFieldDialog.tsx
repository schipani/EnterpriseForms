import type { AddFieldDefinitionRequest } from "@enterpriseforms/api-client";
import { FIELD_DATA_TYPES } from "@enterpriseforms/form-renderer";
import { Button, Dialog, Select, TextArea, TextInput } from "@enterpriseforms/ui";
import { useState } from "react";
import { ErrorPanel } from "../../support";

const KEY = /^[a-z][a-z0-9_]{0,63}$/;

export function toKey(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "")
    .replace(/^[^a-z]+/, "")
    .slice(0, 64);
}

/**
 * Adds a stable field identity (storage type is immutable after creation). Option fields need at least one option.
 * `kind="documents"` adds a File field — the identity behind a document collection; its upload limits are set on the
 * `documentCollection` element in the Layout tab (immutable per published version).
 */
export function AddFieldDialog({ open, onOpenChange, onSubmit, pending, error, kind = "field" }: { open: boolean; onOpenChange: (open: boolean) => void; onSubmit: (request: AddFieldDefinitionRequest) => void; pending: boolean; error: unknown; kind?: "field" | "documents" }) {
  const documents = kind === "documents";
  const [displayName, setDisplayName] = useState("");
  const [key, setKey] = useState("");
  const [keyTouched, setKeyTouched] = useState(false);
  const [dataType, setDataType] = useState<string>(documents ? "File" : "String");
  const [description, setDescription] = useState("");
  const [optionsText, setOptionsText] = useState("");
  const effectiveKey = keyTouched ? key : toKey(displayName);
  const options = optionsText
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => ({ optionKey: toKey(line), optionDisplayText: line }));
  const valid = KEY.test(effectiveKey) && displayName.trim().length > 0 && (dataType !== "Option" || options.length > 0);
  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={documents ? "Add document collection" : "Add field"}
      description={documents ? "A document collection is a File field: a stable identity whose uploads are scanned, encrypted and bound to the record. Allowed types, maximum files and size are set when you place it in the Layout tab." : "A field is a stable identity with an immutable storage type. Its label, width and validation are set per layout version."}
      locked={pending}
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={pending}>
            Cancel
          </Button>
          <Button busy={pending} disabled={!valid} onClick={() => onSubmit({ fieldKey: effectiveKey, fieldDisplayName: displayName.trim(), fieldDescription: description.trim(), fieldDataType: documents ? "File" : dataType, options: dataType === "Option" ? options : null })}>
            {documents ? "Add document collection" : "Add field"}
          </Button>
        </>
      }
    >
      <TextInput label="Display name" value={displayName} required maxLength={200} onChange={(event) => setDisplayName(event.target.value)} />
      <TextInput label="Field key" value={effectiveKey} required helpText="snake_case, immutable, unique within the form" error={effectiveKey && !KEY.test(effectiveKey) ? "Use lowercase letters, digits and underscores, starting with a letter" : undefined} onChange={(event) => { setKeyTouched(true); setKey(event.target.value); }} />
      {documents ? (
        <TextInput label="Storage type" value="File (document collection)" readOnly helpText="Documents are attachment references, never file bytes in the record." />
      ) : (
        <Select label="Storage type" value={dataType} required options={FIELD_DATA_TYPES.map((type) => ({ value: type, label: type === "File" ? "File (document collection)" : type }))} onValueChange={setDataType} />
      )}
      <TextArea label="Description" value={description} maxLength={1000} rows={2} onChange={(event) => setDescription(event.target.value)} />
      {dataType === "Option" ? <TextArea label="Options (one per line)" value={optionsText} required helpText="Display text per line; keys are derived and stay stable." onChange={(event) => setOptionsText(event.target.value)} /> : null}
      {error ? <ErrorPanel error={error} /> : null}
    </Dialog>
  );
}
