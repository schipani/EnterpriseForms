import type { FieldDefinitionResponse, FieldSearchConfigurationResponse, FieldSearchStatusResponse } from "@enterpriseforms/api-client";
import { presentError, useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, Checkbox, LoadingBlock, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { operatorLabel } from "../../search";
import { ErrorPanel } from "../../support";

/**
 * Phase C7 M2 (AC-6): the "Search" section of Configure Field. Everything shown is the Authorization Authority's view of
 * the form's latest registered published version: the field's classification and protection as the Authority holds them,
 * whether it can be approved, which conditions are allowed and the approval state. Viewing is separate from granting:
 * only a security administrator (administer_security) sees the approval controls, and they stay nonfunctional wherever
 * the Authority cannot accept approvals (production and staging until PB-08 step-up exists). There is no control here
 * that changes a field's encryption; an encrypted field is simply not searchable.
 */
export const ENCRYPTED_NOT_SEARCHABLE = "Encrypted for privacy — not searchable.";

const ELIGIBILITY: Record<string, { tone: "success" | "neutral" | "warning"; label: string; detail: string }> = {
  Eligible: { tone: "success", label: "Can be approved", detail: "A plaintext Public or Internal field. A security administrator can approve it for Advanced Search." },
  Encrypted: { tone: "neutral", label: "Not searchable", detail: `${ENCRYPTED_NOT_SEARCHABLE} Its values are stored as authenticated ciphertext and cannot be searched (ADR-PB-004).` },
  ClassificationNotPermitted: { tone: "neutral", label: "Not searchable", detail: "Confidential and Restricted values cannot be searched in this release, and this field's classification is below what the organization allows for searching." },
  OrganizationSearchDisabled: { tone: "warning", label: "Not permitted by policy", detail: "The organization's data-protection policy does not permit search approvals. The organization owner can change this under Organization settings." },
  OrganizationPolicyMissing: { tone: "warning", label: "No organization policy", detail: "The organization has not published a data-protection policy to the Authorization Authority, so no field can be approved." },
  ProtectionPending: { tone: "warning", label: "Protection change in progress", detail: "This field's protection is changing. It cannot be approved until the change has completed and the form is published again." },
  TypeNotSearchable: { tone: "neutral", label: "Not searchable", detail: "Fields of this type cannot be searched." },
};

const APPROVAL: Record<string, { tone: "success" | "neutral" | "warning"; label: string }> = {
  None: { tone: "neutral", label: "Not approved" },
  Active: { tone: "success", label: "Approved" },
  Withdrawn: { tone: "neutral", label: "Approval withdrawn" },
  Invalidated: { tone: "warning", label: "Approval no longer valid" },
};

const INVALIDATION: Record<string, string> = {
  FIELD_STRENGTHENED: "The field's protection was strengthened after it was approved.",
  FORM_VERSION_CHANGED: "A later published version changed this field (type, classification, protection, label or choices).",
  ORGANIZATION_POLICY_CHANGED: "The organization's data-protection policy changed after it was approved.",
};

function isNotFound(error: unknown): boolean {
  return presentError(error).kind === "not-found";
}

export function fieldSearchKey(formDefinitionId: string) {
  return ["forms", formDefinitionId, "field-search"];
}

export function FieldSearchSection({ formDefinitionId, field }: { formDefinitionId: string; field: FieldDefinitionResponse }) {
  const api = useApi();
  const configuration = useQuery({ queryKey: fieldSearchKey(formDefinitionId), queryFn: ({ signal }) => api.forms.fieldSearch(formDefinitionId, signal).then((r) => r.result), retry: (count, error) => !isNotFound(error) && count < 2 });

  if (configuration.isPending) {
    return <LoadingBlock label="Reading search settings from the Authorization Authority" />;
  }
  if (configuration.isError) {
    return isNotFound(configuration.error) ? (
      <StatusMessage tone="info" title="Search settings are not available to you">
        Search settings are shown to designers of this form and to security administrators.
      </StatusMessage>
    ) : (
      <ErrorPanel error={configuration.error} onRetry={() => void configuration.refetch()} />
    );
  }

  const data = configuration.data;
  const status = data.fields.find((candidate) => candidate.fieldDefinitionId === field.fieldDefinitionId);
  return (
    <div className="flex flex-col gap-4">
      <p className="text-sm text-muted">Which fields can be used in Advanced Search is decided by the Authorization Authority, field by field, for the latest published version of this form. Searching itself is not turned on yet.</p>
      {data.registeredFormVersionNumber === null ? (
        <StatusMessage tone="info" title="Publish this form first">
          Only fields of a published version registered with the Authorization Authority can be approved for search.
        </StatusMessage>
      ) : status === undefined ? (
        <StatusMessage tone="info" title="Not in the published version">
          {`This field is not part of published version ${data.registeredFormVersionNumber}. Publish the form to include it.`}
        </StatusMessage>
      ) : (
        <>
          <FieldSearchFacts status={status} versionNumber={Number(data.registeredFormVersionNumber)} />
          <ApprovalControls formDefinitionId={formDefinitionId} data={data} status={status} />
        </>
      )}
    </div>
  );
}

function FieldSearchFacts({ status, versionNumber }: { status: FieldSearchStatusResponse; versionNumber: number }) {
  const encrypted = status.protectionMode === "ApplicationEncrypted";
  const eligibility = ELIGIBILITY[status.eligibility] ?? { tone: "neutral" as const, label: status.eligibility, detail: "This field cannot be approved for search." };
  const approval = APPROVAL[status.approvalState] ?? { tone: "neutral" as const, label: status.approvalState };
  return (
    <dl className="grid grid-cols-1 gap-x-4 gap-y-3 text-sm sm:grid-cols-[max-content_1fr]">
      <dt className="font-medium">Classification</dt>
      <dd className="m-0">{status.fieldClassification}</dd>
      <dt className="font-medium">Protection</dt>
      <dd className="m-0">
        <Badge tone={encrypted ? "success" : "warning"}>{encrypted ? "Encrypted" : "Plaintext"}</Badge>
        {encrypted ? <p className="m-0 mt-1 font-medium">{ENCRYPTED_NOT_SEARCHABLE}</p> : null}
      </dd>
      <dt className="font-medium">Search eligibility</dt>
      <dd className="m-0">
        <Badge tone={eligibility.tone}>{eligibility.label}</Badge>
        <p className="m-0 mt-1 text-muted">{eligibility.detail}</p>
      </dd>
      <dt className="font-medium">Conditions</dt>
      <dd className="m-0">{status.permittedOperators.length > 0 ? status.permittedOperators.map((operator) => operatorLabel(operator, status.fieldDataType)).join(", ") : "None"}</dd>
      <dt className="font-medium">Approval</dt>
      <dd className="m-0">
        <Badge tone={approval.tone}>{approval.label}</Badge>
        {status.approvalState === "Active" ? <p className="m-0 mt-1 text-muted">{`Approved for version ${versionNumber}: ${status.approvedOperators.map((operator) => operatorLabel(operator, status.fieldDataType)).join(", ")}.`}</p> : null}
        {status.approvalState === "Invalidated" ? <p className="m-0 mt-1 text-muted">{INVALIDATION[status.invalidationReason ?? ""] ?? "The approval no longer matches the field."} A security administrator can review and approve it again.</p> : null}
      </dd>
    </dl>
  );
}

function ApprovalControls({ formDefinitionId, data, status }: { formDefinitionId: string; data: FieldSearchConfigurationResponse; status: FieldSearchStatusResponse }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const [operators, setOperators] = useState<string[]>(() => [...status.permittedOperators]);
  const [reference, setReference] = useState("");
  const [stepUp, setStepUp] = useState(false);
  const [withdrawReason, setWithdrawReason] = useState("");
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: fieldSearchKey(formDefinitionId) });
    await queryClient.invalidateQueries({ queryKey: ["search", "capabilities", formDefinitionId] });
  };
  const approve = useMutation({
    mutationFn: () => api.forms.approveFieldSearch(formDefinitionId, status.fieldDefinitionId, { operators, approvalReference: reference.trim(), useDevelopmentStepUp: stepUp }).then((r) => r.result),
    onSuccess: async () => {
      setReference("");
      setStepUp(false);
      await refresh();
    },
  });
  const withdraw = useMutation({
    mutationFn: () => api.forms.withdrawFieldSearch(formDefinitionId, status.fieldDefinitionId, { reason: withdrawReason.trim() }).then((r) => r.result),
    onSuccess: async () => {
      setWithdrawReason("");
      await refresh();
    },
  });

  if (!data.canApprove) {
    return <p className="text-sm text-muted">Approving a field for search is done by a security administrator. You can see the field's search status here but cannot change it.</p>;
  }

  const active = status.approvalState === "Active";
  const eligible = status.eligibility === "Eligible";
  return (
    <div className="flex flex-col gap-4 border-t border-line pt-4">
      {active ? (
        <fieldset className="flex flex-col gap-3 rounded border border-line p-3">
          <legend className="px-1 text-sm font-medium">Withdraw approval</legend>
          <p className="m-0 text-sm text-muted">Withdrawing takes effect immediately for every searcher. It does not change the field or its values.</p>
          <TextInput label="Reason" value={withdrawReason} required maxLength={500} onChange={(event) => setWithdrawReason(event.target.value)} helpText="Recorded with the change (1 to 500 characters)." />
          {withdraw.error ? <ErrorPanel error={withdraw.error} /> : null}
          <div>
            <Button variant="secondary" size="sm" disabled={withdrawReason.trim() === ""} busy={withdraw.isPending} onClick={() => withdraw.mutate()}>
              Withdraw search approval
            </Button>
          </div>
        </fieldset>
      ) : null}
      {!active && eligible ? (
        <fieldset className="flex flex-col gap-3 rounded border border-line p-3" aria-describedby={data.approvalOperational ? undefined : "field-search-approval-unavailable"}>
          <legend className="px-1 text-sm font-medium">Approve for search</legend>
          {!data.approvalOperational ? (
            <div id="field-search-approval-unavailable">
              <StatusMessage tone="info" title="Approval is not available in this environment">
                Approving a field for search needs a fresh passkey confirmation (step-up), which is not available yet (PB-08). Until then the Authorization Authority refuses every approval here.
              </StatusMessage>
            </div>
          ) : null}
          <fieldset className="flex flex-col gap-2" disabled={!data.approvalOperational}>
            <legend className="text-sm font-medium">Allowed conditions</legend>
            {status.permittedOperators.map((operator) => (
              <Checkbox
                key={operator}
                label={operatorLabel(operator, status.fieldDataType)}
                checked={operators.includes(operator)}
                disabled={!data.approvalOperational}
                onCheckedChange={(checked) => setOperators((current) => (checked === true ? [...current.filter((value) => value !== operator), operator] : current.filter((value) => value !== operator)))}
              />
            ))}
          </fieldset>
          <TextInput label="Approval reference" value={reference} required maxLength={200} disabled={!data.approvalOperational} onChange={(event) => setReference(event.target.value)} helpText="Identifies the governance decision that approved searching this field (1 to 200 characters)." />
          {data.approvalOperational ? (
            <Checkbox
              label="Confirm with the development-only step-up"
              checked={stepUp}
              onCheckedChange={(checked) => setStepUp(checked === true)}
              helpText="This is a synthetic stand-in for a passkey confirmation that exists only in local development and test. It proves nothing about the person and can never be enabled in staging or production."
            />
          ) : null}
          {approve.error ? <ErrorPanel error={approve.error} /> : null}
          <div>
            <Button size="sm" disabled={!data.approvalOperational || operators.length === 0 || reference.trim() === "" || !stepUp} busy={approve.isPending} onClick={() => approve.mutate()}>
              Approve for search
            </Button>
          </div>
        </fieldset>
      ) : null}
    </div>
  );
}
