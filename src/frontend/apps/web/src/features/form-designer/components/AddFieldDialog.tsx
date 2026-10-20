import type { AddFieldDefinitionRequest, OrganizationSecurityPolicyResponse } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import { useQuery } from "@tanstack/react-query";
import { useEffect } from "react";
import { FieldSecuritySection, defaultClassification, resolveEncryption } from "./FieldSecuritySection";
import { FIELD_DATA_TYPES } from "@enterpriseforms/form-renderer";
import { Button, Dialog, Select, TextArea, TextInput } from "@enterpriseforms/ui";
import { useState } from "react";
import { ErrorPanel } from "../../support";

const KEY = /^[a-z][a-z0-9_]{0,63}$/;

/** User-facing field types. A dropdown is stored as the permanent "Option" storage type; the user picks single or multi select here. */
const DROPDOWN_SINGLE = "DropdownSingle";
const DROPDOWN_MULTI = "DropdownMulti";
const TYPE_CHOICES: Array<{ value: string; label: string }> = [
  ...FIELD_DATA_TYPES.filter((type) => type !== "Option").map((type) => ({ value: type, label: type === "File" ? "File (document collection)" : type })),
  { value: DROPDOWN_SINGLE, label: "Dropdown (Single Select)" },
  { value: DROPDOWN_MULTI, label: "Dropdown (Multi Select)" },
];

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
 * `documentCollection` element on the Design canvas (immutable per published version).
 */
export function AddFieldDialog({ open, onOpenChange, onSubmit, pending, error, kind = "field" }: { open: boolean; onOpenChange: (open: boolean) => void; onSubmit: (request: AddFieldDefinitionRequest) => void; pending: boolean; error: unknown; kind?: "field" | "documents" }) {
  const documents = kind === "documents";
  const [displayName, setDisplayName] = useState("");
  const [key, setKey] = useState("");
  const [keyTouched, setKeyTouched] = useState(false);
  const [typeChoice, setTypeChoice] = useState<string>(documents ? "File" : "String");
  const isDropdown = typeChoice === DROPDOWN_SINGLE || typeChoice === DROPDOWN_MULTI;
  const dataType = isDropdown ? "Option" : typeChoice;
  const setDataType = setTypeChoice;
  const [description, setDescription] = useState("");
  const api = useApi();
  const policyQuery = useQuery({ queryKey: ["organization", "security-policy"], queryFn: () => api.organization.securityPolicy().then((r) => r.result), enabled: open });
  const policy: OrganizationSecurityPolicyResponse | undefined = policyQuery.data;
  const [classification, setClassification] = useState<string>(defaultClassification(documents ? "File" : "String"));
  const [encryptChoice, setEncryptChoice] = useState<boolean>(true);
  // Changing the storage type re-applies the secure defaults (free text and documents are Confidential + encrypted).
  useEffect(() => {
    setClassification(defaultClassification(dataType));
    setEncryptChoice(dataType === "String" || dataType === "File");
  }, [dataType]);
  const security = resolveEncryption(dataType, classification, encryptChoice, policy);
  const [optionsText, setOptionsText] = useState("");
  // Dropdown choices: either entered here or copied from an existing shared list.
  const selectionMode = typeChoice === DROPDOWN_MULTI ? "Multiple" : "Single";
  const [useList, setUseList] = useState(false);
  const [optionListId, setOptionListId] = useState<string>("");
  const listsQuery = useQuery({ queryKey: ["option-lists"], queryFn: () => api.optionLists.list().then((r) => r.result), enabled: open && dataType === "Option" });
  const lists = (listsQuery.data ?? []).filter((list) => list.lifecycleStatus === "Active" && list.items.some((item) => item.isActive));
  const effectiveKey = keyTouched ? key : toKey(displayName);
  const options = optionsText
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => ({ optionKey: toKey(line), optionDisplayText: line }));
  const valid = KEY.test(effectiveKey) && displayName.trim().length > 0 && (dataType !== "Option" || (useList ? optionListId !== "" : options.length > 0)) && security.allowed;
  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={documents ? "Add document collection" : "Add field"}
      description={documents ? "A document collection is a File field: a stable identity whose uploads are scanned, encrypted and bound to the record. Allowed types, maximum files and size are set in the designer inspector after you place it on the Design canvas." : "A field is a stable identity with an immutable storage type. Its label, width and validation are set per layout version."}
      locked={pending}
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={pending}>
            Cancel
          </Button>
          <Button busy={pending} disabled={!valid} onClick={() => onSubmit({ fieldKey: effectiveKey, fieldDisplayName: displayName.trim(), fieldDescription: description.trim(), fieldDataType: documents ? "File" : dataType, options: dataType === "Option" && !useList ? options : null, fieldClassification: classification, fieldProtectionMode: security.protectionMode, ...(dataType === "Option" ? { optionSelectionMode: selectionMode, ...(useList && optionListId ? { optionListId } : {}) } : {}) })}>
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
        <Select label="Storage type" value={typeChoice} required options={TYPE_CHOICES} onValueChange={setDataType} />
      )}
      <TextArea label="Description" value={description} maxLength={1000} rows={2} onChange={(event) => setDescription(event.target.value)} />
      {isDropdown ? (
        <>
          <p className="text-sm text-muted">
            {selectionMode === "Multiple" ? "People can choose several of the choices." : "People choose one of the choices."} This cannot be changed later: stored selections and published layouts depend on it.
          </p>
          <Select
            label="Choices"
            value={useList ? "list" : "own"}
            options={[
              { value: "own", label: "Enter choices for this field" },
              { value: "list", label: "Use Existing List" },
            ]}
            onValueChange={(value) => setUseList(value === "list")}
          />
          {useList ? (
            <Select
              label="Existing list"
              value={optionListId || undefined}
              required
              placeholder={lists.length === 0 ? "No lists yet — create one under Fields → Option lists" : "Choose a list"}
              disabled={lists.length === 0}
              options={lists.map((list) => ({ value: list.optionListId, label: `${list.optionListName} (${list.items.filter((item) => item.isActive).length} choices)` }))}
              onValueChange={setOptionListId}
              helpText="The list is copied into the field. Later list edits reach this field only when you sync it, and published versions never change."
            />
          ) : (
            <TextArea label="Options (one per line)" value={optionsText} required helpText="One choice per line; keys are derived and stay stable. You can add, relabel, reorder and deactivate choices later in the field properties." onChange={(event) => setOptionsText(event.target.value)} />
          )}
        </>
      ) : null}
      <FieldSecuritySection dataType={documents ? "File" : dataType} classification={classification} encryptChoice={encryptChoice} policy={policy} policyPending={policyQuery.isPending} onClassificationChange={setClassification} onEncryptChoiceChange={setEncryptChoice} />
      {error ? <ErrorPanel error={error} /> : null}
    </Dialog>
  );
}
