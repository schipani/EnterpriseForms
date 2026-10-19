import type { FieldDefinitionResponse, FieldProtectionMigrationResponse, OrganizationSecurityPolicyResponse, StrengthenFieldProtectionRequest } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
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
  Queued: { tone: "info", label: "Encryption migration queued" },
  Running: { tone: "info", label: "Encryption migration running" },
  Blocked: { tone: "warning", label: "Encryption migration waiting for a dependency" },
  Failed: { tone: "danger", label: "Encryption migration failed" },
  Completed: { tone: "success", label: "Encryption migration completed" },
};

/** Progress of the background re-encryption of existing values (B-28). Shown until the server reports the field as fully encrypted and verified. */
function MigrationProgress({ migration, legacyRecords, canEdit, onRetry, retrying, retryError }: { migration: FieldProtectionMigrationResponse; legacyRecords: number; canEdit: boolean; onRetry: () => void; retrying: boolean; retryError: unknown }) {
  const label = MIGRATION_LABELS[migration.migrationState] ?? { tone: "warning" as const, label: migration.migrationState };
  const totalRecords = Number(migration.totalRecordCount);
  const completedRecords = Number(migration.completedRecordCount);
  const percent = Math.round((completedRecords / Math.max(totalRecords, 1)) * 100);
  return (
    <div className="flex flex-col gap-2 rounded border border-line bg-page p-3" role="group" aria-label="Encryption migration progress">
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={label.tone}>{label.label}</Badge>
        <span className="text-xs text-muted">
          {migration.completedRecordCount} of {migration.totalRecordCount} record(s) re-encrypted · {migration.pendingRecordCount} pending · {migration.failedRecordCount} failed
        </span>
      </div>
      <div className="h-2 w-full overflow-hidden rounded bg-line" role="progressbar" aria-valuemin={0} aria-valuemax={totalRecords} aria-valuenow={completedRecords} aria-label="Records re-encrypted">
        <div className="h-2 bg-primary" style={{ width: `${percent}%` }} />
      </div>
      <p className="text-xs text-muted">
        Existing values are re-sealed one record at a time through the Authorization Authority; every step is audited. Until this finishes and is verified, {legacyRecords} record(s) still hold a plaintext copy and the field is not reported as fully encrypted.
        {migration.lastErrorCode ? ` Last error: ${migration.lastErrorCode}.` : ""}
      </p>
      {migration.migrationState === "Failed" || migration.migrationState === "Blocked" ? (
        <div className="flex flex-col gap-2">
          <StatusMessage tone="warning" title={migration.migrationState === "Failed" ? "Some records could not be re-encrypted" : "The migration is waiting for a dependency"}>
            {migration.migrationState === "Failed" ? "Records that failed keep their plaintext copy and are listed in the audit trail. Investigate them, then retry; nothing is re-signed silently." : "The key provider, signer or Authorization Authority is unavailable. The migration resumes automatically; nothing was lost."}
          </StatusMessage>
          {canEdit ? (
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

/** Read-only protection state of an existing field plus the audited "Strengthen protection" step (Edit field dialog). */
export function CurrentProtectionSection({ formDefinitionId, field, canEdit, onStrengthen, pending, error }: { formDefinitionId: string; field: FieldDefinitionResponse; canEdit: boolean; onStrengthen: (request: StrengthenFieldProtectionRequest) => void; pending: boolean; error: unknown }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const status = useQuery({
    queryKey: ["forms", formDefinitionId, "protection", field.fieldDefinitionId],
    queryFn: () => api.forms.protection(formDefinitionId, field.fieldDefinitionId).then((r) => r.result),
    // A running migration is polled; the claim "Fully encrypted" only ever comes from the server's verified status.
    refetchInterval: (query) => (query.state.data?.migration && ["Queued", "Running", "Blocked"].includes(query.state.data.migration.migrationState) ? 2000 : false),
  });
  const retry = useMutation({
    mutationFn: () => api.forms.retryProtectionMigration(formDefinitionId, field.fieldDefinitionId, { retryReason: "Retried from the Edit field dialog after reviewing the failed records" }).then((r) => r.result),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ["forms", formDefinitionId, "protection", field.fieldDefinitionId] }),
  });
  const [open, setOpen] = useState(false);
  const [target, setTarget] = useState<string>(field.fieldClassification);
  const [encrypt, setEncrypt] = useState(true);
  const [reason, setReason] = useState("");
  // A successful strengthening arrives as a new protection revision on the field: close the panel and reset its inputs.
  useEffect(() => {
    setOpen(false);
    setTarget(field.fieldClassification);
    setReason("");
  }, [field.protectionPolicyRevision, field.fieldClassification]);
  const encrypted = field.fieldProtectionMode === "ApplicationEncrypted";
  const higher = CLASSIFICATIONS.filter((value) => (RANK[value] ?? 0) >= (RANK[field.fieldClassification] ?? 0));
  const changes = target !== field.fieldClassification || (!encrypted && encrypt);
  const mandatory = requiresEncryption(target);
  return (
    <fieldset className="flex flex-col gap-3 rounded border border-line p-3">
      <legend className="px-1 text-sm font-medium">Security &amp; Encryption</legend>
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <span>Classification</span>
        <Badge tone="neutral">{field.fieldClassification}</Badge>
        <span>Application-level encryption</span>
        <Badge tone={encrypted ? "success" : "warning"}>{encrypted ? "Enabled" : "Off — plaintext"}</Badge>
        {status.data?.fullyEncrypted ? <Badge tone="success">Fully encrypted · verified</Badge> : null}
        {status.data?.migrationState === "EncryptionPendingRewrite" ? <Badge tone="warning">{status.data.legacyPlaintextRecordCount} record(s) still hold a plaintext copy until their next save</Badge> : null}
      </div>
      {status.data?.migration && !status.data.fullyEncrypted ? <MigrationProgress migration={status.data.migration} legacyRecords={Number(status.data.legacyPlaintextRecordCount)} canEdit={canEdit} onRetry={() => retry.mutate()} retrying={retry.isPending} retryError={retry.error} /> : null}
      <p className="text-xs text-muted">{status.data?.searchImpact ?? (encrypted ? SEARCH_IMPACT_ENCRYPTED : SEARCH_IMPACT_PLAINTEXT)}</p>
      {status.isError ? <ErrorPanel error={status.error} onRetry={() => void status.refetch()} /> : null}
      {encrypted && field.fieldClassification === "Restricted" ? null : canEdit && !open ? (
        <div>
          <Button size="sm" variant="secondary" onClick={() => setOpen(true)}>
            Strengthen protection…
          </Button>
          <p className="mt-1 text-xs text-muted">Protection can only be strengthened. Making an encrypted field plaintext is not possible here (it would need an approved, audited security migration).</p>
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
    </fieldset>
  );
}
