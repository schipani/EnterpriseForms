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
  const [dataType, setDataType] = useState<string>(documents ? "File" : "String");
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
  // Dropdown choices: single or multiple selection (permanent), and either the field's own options or a copy of a shared list.
  const [selectionMode, setSelectionMode] = useState<string>("Single");
  const [optionListId, setOptionListId] = useState<string>("");
  const listsQuery = useQuery({ queryKey: ["option-lists"], queryFn: () => api.optionLists.list().then((r) => r.result), enabled: open && dataType === "Option" });
  const lists = (listsQuery.data ?? []).filter((list) => list.lifecycleStatus === "Active" && list.items.some((item) => item.isActive));
  const effectiveKey = keyTouched ? key : toKey(displayName);
  const options = optionsText
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => ({ optionKey: toKey(line), optionDisplayText: line }));
  const valid = KEY.test(effectiveKey) && displayName.trim().length > 0 && (dataType !== "Option" || optionListId !== "" || options.length > 0) && security.allowed;
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
          <Button busy={pending} disabled={!valid} onClick={() => onSubmit({ fieldKey: effectiveKey, fieldDisplayName: displayName.trim(), fieldDescription: description.trim(), fieldDataType: documents ? "File" : dataType, options: dataType === "Option" && !optionListId ? options : null, fieldClassification: classification, fieldProtectionMode: security.protectionMode, ...(dataType === "Option" ? { optionSelectionMode: selectionMode, ...(optionListId ? { optionListId } : {}) } : {}) })}>
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
      {dataType === "Option" ? (
        <>
          <Select
            label="Selection"
            value={selectionMode}
            options={[
              { value: "Single", label: "Dropdown — choose one" },
              { value: "Multiple", label: "Multi-select — choose several" },
            ]}
            onValueChange={setSelectionMode}
            helpText="Permanent for this field: stored selections and published layouts depend on it."
          />
          <Select
            label="Option source"
            value={optionListId || "own"}
            options={[{ value: "own", label: "Options I enter for this field" }, ...lists.map((list) => ({ value: list.optionListId, label: `Shared list: ${list.optionListName} (${list.items.filter((item) => item.isActive).length} options)` }))]}
            onValueChange={(value) => setOptionListId(value === "own" ? "" : value)}
            helpText="A shared list is copied into the field; later list edits reach this field only when you sync it, and published versions never change."
          />
          {optionListId ? null : <TextArea label="Options (one per line)" value={optionsText} required helpText="Display text per line; keys are derived and stay stable. You can add, relabel, reorder and deactivate options later in the field properties." onChange={(event) => setOptionsText(event.target.value)} />}
        </>
      ) : null}
      <FieldSecuritySection dataType={documents ? "File" : dataType} classification={classification} encryptChoice={encryptChoice} policy={policy} policyPending={policyQuery.isPending} onClassificationChange={setClassification} onEncryptChoiceChange={setEncryptChoice} />
      {error ? <ErrorPanel error={error} /> : null}
    </Dialog>
  );
}
