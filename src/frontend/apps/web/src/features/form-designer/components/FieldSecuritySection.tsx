import type { FieldDefinitionResponse, FieldProtectionMigrationResponse, OrganizationSecurityPolicyResponse, StrengthenFieldProtectionRequest } from "@enterpriseforms/api-client";
import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Badge, Button, Checkbox, Select, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { ErrorPanel } from "../../support";

export const CLASSIFICATIONS = ["Public", "Internal", "Confidential", "Restricted"] as const;
const RANK: Record<string, number> = { Public: 0, Internal: 1, Confidential: 2, Restricted: 3 };

export const SEARCH_IMPACT_ENCRYPTED = "Encrypted for privacy — not searchable. The value is stored as authenticated ciphertext; only authorized users see it on the record, and no plaintext copy exists in the business database. Confidential and Restricted values cannot be searched in V1 (ADR-PB-004).";
export const SEARCH_IMPACT_PLAINTEXT = "Plaintext: searchable within authorization, and readable by a database administrator. Only appropriate for Public or Internal data that the organization has approved for plaintext storage.";

/** Mirrors the server defaults (FieldProtectionPolicy): free text and documents are Confidential, structured values Internal. */
export function defaultClassification(dataType: string): string {
  return dataType === "String" || dataType === "File" ? "Confidential" : "Internal";
}

export function requiresEncryption(classification: string): boolean {
  return (RANK[classification] ?? 0) >= RANK["Confidential"]!;
}

export interface ResolvedEncryption {
  protectionMode: "Standard" | "ApplicationEncrypted";
  mandatory: boolean;
  allowed: boolean;
  reason: string | null;
}

/** Client-side mirror of the server policy so the dialog explains before the server refuses; the server remains authoritative. */
export function resolveEncryption(dataType: string, classification: string, encryptChoice: boolean, policy: OrganizationSecurityPolicyResponse | undefined): ResolvedEncryption {
  if (dataType === "File") {
    return { protectionMode: "ApplicationEncrypted", mandatory: true, allowed: true, reason: null };
  }
  const mandatory = requiresEncryption(classification);
  const encrypted = mandatory || encryptChoice;
  if (policy && (RANK[classification] ?? 0) < (RANK[policy.minimumFieldClassification] ?? 0)) {
    return { protectionMode: encrypted ? "ApplicationEncrypted" : "Standard", mandatory, allowed: false, reason: `The organization's minimum classification is ${policy.minimumFieldClassification}.` };
  }
  if (!encrypted && policy && !policy.plaintextInternalFieldsPermitted) {
    return { protectionMode: "Standard", mandatory, allowed: false, reason: "The organization's data-protection policy does not permit plaintext storage. Keep encryption on, or ask the organization owner to review the policy." };
  }
  return { protectionMode: encrypted ? "ApplicationEncrypted" : "Standard", mandatory, allowed: true, reason: null };
}

/** "Security & Encryption" section of the Add field / Add document collection dialog. */
export function FieldSecuritySection({ dataType, classification, encryptChoice, policy, policyPending, onClassificationChange, onEncryptChoiceChange }: { dataType: string; classification: string; encryptChoice: boolean; policy: OrganizationSecurityPolicyResponse | undefined; policyPending: boolean; onClassificationChange: (value: string) => void; onEncryptChoiceChange: (value: boolean) => void }) {
  const security = resolveEncryption(dataType, classification, encryptChoice, policy);
  const encrypted = security.protectionMode === "ApplicationEncrypted";
  return (
    <fieldset className="flex flex-col gap-3 rounded border border-line p-3">
      <legend className="px-1 text-sm font-medium">Security &amp; Encryption</legend>
      <Select label="Classification" value={classification} required options={CLASSIFICATIONS.map((value) => ({ value, label: value }))} onValueChange={onClassificationChange} helpText={dataType === "File" ? "Documents are always application-encrypted." : "Confidential and Restricted require application-level encryption. New free text defaults to Confidential."} />
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-sm font-medium">Application-level encryption</span>
        <Badge tone={encrypted ? "success" : "warning"}>{encrypted ? (security.mandatory ? "Enabled (required)" : "Enabled") : "Off — plaintext"}</Badge>
      </div>
      {dataType !== "File" && !security.mandatory ? <Checkbox label="Encrypt this field (recommended for anything a person entered)" checked={encryptChoice} onCheckedChange={(checked) => onEncryptChoiceChange(checked === true)} /> : null}
      <p className="text-xs text-muted">{encrypted ? SEARCH_IMPACT_ENCRYPTED : SEARCH_IMPACT_PLAINTEXT}</p>
      {policyPending ? <p className="text-xs text-muted">Reading the organization's data-protection policy…</p> : null}
      {policy ? (
        <p className="text-xs text-muted">
          Organization policy: minimum classification {policy.minimumFieldClassification}; plaintext Internal fields {policy.plaintextInternalFieldsPermitted ? "permitted" : "not permitted"}
          {policy.isUnreviewedDefault ? " (development default, not yet reviewed by the organization owner)" : ""}.
        </p>
      ) : null}
      {!security.allowed && security.reason ? <StatusMessage tone="warning" title="Not allowed by the organization policy">{security.reason}</StatusMessage> : null}
      <p className="text-xs text-muted">Protection can be strengthened later from the Edit field dialog; it can never be weakened here. Published versions keep the protection they were published with.</p>
    </fieldset>
  );
}

const MIGRATION_LABELS: Record<string, { tone: "info" | "warning" | "danger" | "success"; label: string }> = {
  Queued: { tone: "info", label: "migration queued" },
  Running: { tone: "info", label: "migration running" },
  Blocked: { tone: "warning", label: "migration waiting for a dependency" },
  Failed: { tone: "danger", label: "migration failed" },
  Completed: { tone: "success", label: "migration completed" },
  Cancelled: { tone: "warning", label: "migration cancelled" },
};

/**
 * What the "all listed records" badges do and do not establish (security finding G19, SECURITY_GAP_ASSESSMENT.md):
 * the migration worker checked the Authority-accepted version of every record the business database lists for this
 * form. A hostile business-database administrator can hide records from that list, so this is not an independently
 * verified completeness guarantee; Authority-backed completeness verification is an open requirement.
 */
const COMPLETENESS_NOTE =
  "Checked by the migration worker against the Authorization Authority's accepted version of every record the business database lists for this form. This is not an independently verified completeness guarantee: a record hidden from the business database would not be covered (security finding G19).";

/** Progress of the background re-encryption (B-28) or decryption (Amendment A1) of existing values. Shown until the server reports every listed record as converted (see COMPLETENESS_NOTE: not an independent completeness proof). */
function MigrationProgress({ migration, legacyRecords, canRetry, onRetry, retrying, retryError }: { migration: FieldProtectionMigrationResponse; legacyRecords: number; canRetry: boolean; onRetry: () => void; retrying: boolean; retryError: unknown }) {
  const decrypting = migration.migrationDirection === "Decrypt";
  const noun = decrypting ? "Decryption" : "Encryption";
  const verb = decrypting ? "decrypted" : "re-encrypted";
  const label = MIGRATION_LABELS[migration.migrationState] ?? { tone: "warning" as const, label: migration.migrationState };
  const totalRecords = Number(migration.totalRecordCount);
  const completedRecords = Number(migration.completedRecordCount);
  const percent = Math.round((completedRecords / Math.max(totalRecords, 1)) * 100);
  return (
    <div className="flex flex-col gap-2 rounded border border-line bg-page p-3" role="group" aria-label={`${noun} migration progress`}>
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={label.tone}>{`${noun} ${label.label}`}</Badge>
        <span className="text-xs text-muted">
          {migration.completedRecordCount} of {migration.totalRecordCount} record(s) {verb} · {migration.pendingRecordCount} pending · {migration.failedRecordCount} failed
        </span>
      </div>
      <div className="h-2 w-full overflow-hidden rounded bg-line" role="progressbar" aria-valuemin={0} aria-valuemax={totalRecords} aria-valuenow={completedRecords} aria-label={`Records ${verb}`}>
        <div className="h-2 bg-primary" style={{ width: `${percent}%` }} />
      </div>
      <p className="text-xs text-muted">
        {decrypting
          ? `Existing ciphertext is rewritten as typed plaintext one record at a time through the Authorization Authority; every step is audited. Until this finishes and the worker's check passes, ${legacyRecords} record(s) still hold an encrypted copy.`
          : `Existing values are re-sealed one record at a time through the Authorization Authority; every step is audited. Until this finishes and the worker's check passes, ${legacyRecords} record(s) still hold a plaintext copy.`}
        {migration.lastErrorCode ? ` Last error: ${migration.lastErrorCode}.` : ""}
      </p>
      {migration.migrationState === "Failed" || migration.migrationState === "Blocked" ? (
        <div className="flex flex-col gap-2">
          <StatusMessage tone="warning" title={migration.migrationState === "Failed" ? `Some records could not be ${verb}` : "The migration is waiting for a dependency"}>
            {migration.migrationState === "Failed" ? "Records that failed keep their current form and are listed in the audit trail. Investigate them, then retry; nothing is re-signed silently." : "The key provider, signer or Authorization Authority is unavailable. The migration resumes automatically; nothing was lost."}
          </StatusMessage>
          {canRetry ? (
            <div>
              <Button size="sm" variant="secondary" busy={retrying} onClick={onRetry}>
                Retry failed records
              </Button>
            </div>
          ) : null}
          {retryError ? <ErrorPanel error={retryError} /> : null}
        </div>
      ) : null}
    </div>
  );
}

const EXPOSURE_WARNING = "Removing encryption is a governed exception (ADR-PB-004 Amendment A1). New saves are stored as plaintext immediately; existing values are decrypted by an audited background migration. A plaintext value is readable by a database administrator, and plaintext written to historical revisions, logs, write-ahead logs or backups cannot be taken back by re-encrypting later.";

/** Read-only protection state of an existing field plus the audited "Strengthen protection" step and, for organization administrators, the governed removal of encryption (Edit field dialog). */
export function CurrentProtectionSection({ formDefinitionId, field, canEdit, onStrengthen, pending, error }: { formDefinitionId: string; field: FieldDefinitionResponse; canEdit: boolean; onStrengthen: (request: StrengthenFieldProtectionRequest) => void; pending: boolean; error: unknown }) {
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const statusKey = ["forms", formDefinitionId, "protection", field.fieldDefinitionId];
  const status = useQuery({
    queryKey: statusKey,
    queryFn: () => api.forms.protection(formDefinitionId, field.fieldDefinitionId).then((r) => r.result),
    // A running migration is polled; the "all listed records" claims only ever come from the server status (worker check against the Authority heads of the records the business database lists — not an independent completeness proof, G19).
    refetchInterval: (query) => (query.state.data?.migration && ["Queued", "Running", "Blocked"].includes(query.state.data.migration.migrationState) ? 2000 : false),
  });
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: statusKey });
    await queryClient.invalidateQueries({ queryKey: ["forms", formDefinitionId], exact: true });
  };
  const retry = useMutation({
    mutationFn: () => api.forms.retryProtectionMigration(formDefinitionId, field.fieldDefinitionId, { retryReason: "Retried from the Edit field dialog after reviewing the failed records" }).then((r) => r.result),
    onSuccess: refresh,
  });
  const reclassify = useMutation({
    mutationFn: (request: { targetClassification: string; approvalReference: string; changeReason: string }) => api.forms.reclassifyField(formDefinitionId, field.fieldDefinitionId, request).then((r) => r.result),
    onSuccess: refresh,
  });
  const downgrade = useMutation({
    mutationFn: (request: { changeReason: string; expectedProtectionRevision: number }) => api.forms.downgradeProtection(formDefinitionId, field.fieldDefinitionId, request).then((r) => r.result),
    onSuccess: refresh,
  });
  const cancelDowngrade = useMutation({
    mutationFn: (request: { changeReason: string }) => api.forms.cancelProtectionDowngrade(formDefinitionId, field.fieldDefinitionId, request).then((r) => r.result),
    onSuccess: refresh,
  });
  const [open, setOpen] = useState(false);
  const [target, setTarget] = useState<string>(field.fieldClassification);
  const [encrypt, setEncrypt] = useState(true);
  const [reason, setReason] = useState("");
  const [governanceOpen, setGovernanceOpen] = useState(false);
  const [declassifyTarget, setDeclassifyTarget] = useState("Internal");
  const [approval, setApproval] = useState("");
  const [governanceReason, setGovernanceReason] = useState("");
  // A successful change arrives as a new protection revision on the field: close the panels and reset their inputs.
  useEffect(() => {
    setOpen(false);
    setGovernanceOpen(false);
    setTarget(field.fieldClassification);
    setReason("");
    setGovernanceReason("");
    setApproval("");
  }, [field.protectionPolicyRevision, field.fieldClassification, field.fieldProtectionMode]);
  const encrypted = field.fieldProtectionMode === "ApplicationEncrypted";
  const isDocuments = field.fieldDataType === "File";
  const higher = CLASSIFICATIONS.filter((value) => (RANK[value] ?? 0) >= (RANK[field.fieldClassification] ?? 0));
  const changes = target !== field.fieldClassification || (!encrypted && encrypt);
  const mandatory = requiresEncryption(target);
  const migration = status.data?.migration ?? null;
  const migrationActive = migration !== null && ["Queued", "Running", "Blocked", "Failed"].includes(migration.migrationState);
  const decryptInProgress = migration !== null && migration.migrationDirection === "Decrypt" && !status.data?.fullyPlaintext && migration.migrationState !== "Cancelled";
  const administrator = session.hasCapability("organization.manage");
  const policyPermitsPlaintext = status.data?.organizationPermitsPlaintext === true;
  const sensitive = requiresEncryption(field.fieldClassification);
  const governanceAvailable = administrator && encrypted && !isDocuments && !migrationActive;
  return (
    <fieldset className="flex flex-col gap-3 rounded border border-line p-3">
      <legend className="px-1 text-sm font-medium">Security &amp; Encryption</legend>
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <span>Classification</span>
        <Badge tone="neutral">{field.fieldClassification}</Badge>
        <span>Application-level encryption</span>
        <Badge tone={encrypted ? "success" : "warning"}>{encrypted ? "Enabled" : "Off — plaintext"}</Badge>
        {status.data?.fullyEncrypted ? <Badge tone="success">All listed records encrypted</Badge> : null}
        {status.data?.fullyPlaintext ? <Badge tone="warning">All listed records migrated to plaintext</Badge> : null}
        {status.data?.migrationState === "EncryptionPendingRewrite" ? <Badge tone="warning">{status.data.legacyPlaintextRecordCount} record(s) still hold a plaintext copy until their next save</Badge> : null}
        {status.data?.migrationState === "DecryptionPendingRewrite" ? <Badge tone="warning">{status.data.legacyEncryptedRecordCount} record(s) still hold an encrypted copy</Badge> : null}
      </div>
      {status.data?.fullyEncrypted || status.data?.fullyPlaintext ? <p className="text-xs text-muted">{COMPLETENESS_NOTE}</p> : null}
      {migration && !status.data?.fullyEncrypted && !status.data?.fullyPlaintext && migration.migrationState !== "Cancelled" ? (
        <MigrationProgress migration={migration} legacyRecords={Number(migration.migrationDirection === "Decrypt" ? status.data?.legacyEncryptedRecordCount : status.data?.legacyPlaintextRecordCount)} canRetry={migration.migrationDirection === "Decrypt" ? administrator : canEdit} onRetry={() => retry.mutate()} retrying={retry.isPending} retryError={retry.error} />
      ) : null}
      {decryptInProgress && administrator ? (
        <div className="flex flex-col gap-2 rounded border border-line bg-page p-3" role="group" aria-label="Cancel removal of encryption">
          <p className="text-xs text-muted">Cancelling re-requires encryption: values already decrypted are re-encrypted by a rollback migration. Plaintext held in historical revisions, logs, write-ahead logs or backups during the exposure window is not reversible.</p>
          <TextInput label="Reason for cancelling" value={governanceReason} required maxLength={500} onChange={(event) => setGovernanceReason(event.target.value)} />
          <div>
            <Button size="sm" variant="secondary" busy={cancelDowngrade.isPending} disabled={governanceReason.trim().length < 3} onClick={() => cancelDowngrade.mutate({ changeReason: governanceReason.trim() })}>
              Cancel removal and re-encrypt
            </Button>
          </div>
          {cancelDowngrade.error ? <ErrorPanel error={cancelDowngrade.error} /> : null}
        </div>
      ) : null}
      <p className="text-xs text-muted">{status.data?.searchImpact ?? (encrypted ? SEARCH_IMPACT_ENCRYPTED : SEARCH_IMPACT_PLAINTEXT)}</p>
      {status.isError ? <ErrorPanel error={status.error} onRetry={() => void status.refetch()} /> : null}
      {(encrypted && field.fieldClassification === "Restricted") || migrationActive ? null : canEdit && !open ? (
        <div>
          <Button size="sm" variant="secondary" onClick={() => setOpen(true)}>
            Strengthen protection…
          </Button>
          <p className="mt-1 text-xs text-muted">Protection can be strengthened by any designer. Removing encryption is not a designer action: it needs the organization's data-protection policy to permit it and an organization administrator (two of them for Confidential or Restricted data).</p>
        </div>
      ) : null}
      {open ? (
        <div className="flex flex-col gap-3 rounded border border-line bg-page p-3">
          <Select label="New classification" value={target} options={higher.map((value) => ({ value, label: value }))} onValueChange={setTarget} />
          {!encrypted && !mandatory ? <Checkbox label="Switch to application-level encryption" checked={encrypt} onCheckedChange={(checked) => setEncrypt(checked === true)} /> : null}
          {!encrypted ? <p className="text-xs text-muted">Future saves are encrypted immediately. Records that already hold a plaintext copy are rewritten as ciphertext when each is next saved; until then the Edit field dialog reports how many remain. This is recorded as an audited migration.</p> : null}
          <TextInput label="Reason" value={reason} required maxLength={500} helpText="Recorded with the audit event (3 to 500 characters)." onChange={(event) => setReason(event.target.value)} />
          <div className="flex gap-2">
            <Button size="sm" busy={pending} disabled={!changes || reason.trim().length < 3} onClick={() => onStrengthen({ targetClassification: target, targetProtectionMode: mandatory || encrypt || encrypted ? "ApplicationEncrypted" : "Standard", changeReason: reason.trim(), expectedProtectionRevision: Number(field.protectionPolicyRevision) })}>
              Apply strengthening
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setOpen(false)} disabled={pending}>
              Cancel
            </Button>
          </div>
          {error ? <ErrorPanel error={error} /> : null}
        </div>
      ) : null}
      {governanceAvailable && !governanceOpen ? (
        <div>
          <Button size="sm" variant="ghost" onClick={() => setGovernanceOpen(true)}>
            {sensitive ? "Declassify for removal of encryption…" : "Remove encryption…"}
          </Button>
          <p className="mt-1 text-xs text-muted">Organization administration only. {policyPermitsPlaintext ? "The organization's data-protection policy must also permit the removal of encryption (Organization page)." : "The organization's data-protection policy currently permits no plaintext; enable it on the Organization page first."}</p>
        </div>
      ) : null}
      {governanceOpen ? (
        <div className="flex flex-col gap-3 rounded border border-line bg-page p-3" role="group" aria-label={sensitive ? "Declassify field" : "Remove encryption"}>
          <StatusMessage tone="warning" title={sensitive ? "Step 1 of 2 — declassification" : "Governed removal of encryption"}>
            {sensitive ? `This field is ${field.fieldClassification}. One administrator declassifies it with a governance approval reference; a different administrator then removes the encryption within 24 hours. The field stays encrypted until then.` : EXPOSURE_WARNING}
          </StatusMessage>
          {sensitive ? (
            <>
              <Select label="Declassify to" value={declassifyTarget} options={[{ value: "Public", label: "Public" }, { value: "Internal", label: "Internal" }]} onValueChange={setDeclassifyTarget} />
              <TextInput label="Governance approval reference" value={approval} required maxLength={200} helpText="Ticket, minute or decision reference (1 to 200 characters); journaled by the Authorization Authority." onChange={(event) => setApproval(event.target.value)} />
            </>
          ) : null}
          <TextInput label="Reason" value={governanceReason} required maxLength={500} helpText="Recorded with the audit event (3 to 500 characters)." onChange={(event) => setGovernanceReason(event.target.value)} />
          <div className="flex gap-2">
            {sensitive ? (
              <Button size="sm" busy={reclassify.isPending} disabled={governanceReason.trim().length < 3 || approval.trim().length < 1} onClick={() => reclassify.mutate({ targetClassification: declassifyTarget, approvalReference: approval.trim(), changeReason: governanceReason.trim() })}>
                Declassify
              </Button>
            ) : (
              <Button size="sm" variant="danger" busy={downgrade.isPending} disabled={governanceReason.trim().length < 3} onClick={() => downgrade.mutate({ changeReason: governanceReason.trim(), expectedProtectionRevision: Number(field.protectionPolicyRevision) })}>
                Remove encryption
              </Button>
            )}
            <Button size="sm" variant="ghost" onClick={() => setGovernanceOpen(false)} disabled={reclassify.isPending || downgrade.isPending}>
              Cancel
            </Button>
          </div>
          {reclassify.error ? <ErrorPanel error={reclassify.error} /> : null}
          {downgrade.error ? <ErrorPanel error={downgrade.error} /> : null}
        </div>
      ) : null}
    </fieldset>
  );
}
