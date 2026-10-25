import type { FieldSearchAdministrationFormResponse } from "@enterpriseforms/api-client";
import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, DataTable, LoadingBlock, PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { FieldSearchersCard } from "../components/FieldSearchersCard";
import { FieldSearchSection, fieldSearchKey } from "../components/FieldSearchSection";

export const searchApprovalsKey = ["organization", "search-approvals"];

/**
 * Phase C7 M3: search-approval administration for security administrators (administer_security). Everything listed comes
 * from the Authorization Authority — the forms with a registered published version, the field facts recorded at
 * registration, the approvals — so a security administrator can approve or withdraw field search and grant search access
 * without Designer permission and without any access to records. Ordinary designers inspect a field's status in
 * Configure Field → Search instead.
 */
export function SearchApprovalsPage() {
  const api = useApi();
  const session = useSession();
  const allowed = session.hasCapability("security.administer");
  const administration = useQuery({ queryKey: searchApprovalsKey, queryFn: ({ signal }) => api.organization.searchApprovals(signal).then((r) => r.result), enabled: allowed });
  const [selected, setSelected] = useState<FieldSearchAdministrationFormResponse | null>(null);

  if (!allowed) {
    return (
      <>
        <PageHeader title="Search approvals" />
        <StatusMessage tone="info" title="Security administrators only">
          Approving fields for Advanced Search is done by security administrators.
        </StatusMessage>
      </>
    );
  }

  return (
    <>
      <PageHeader
        title="Search approvals"
        lead="Choose which plaintext Public or Internal fields of each published form can be used in Advanced Search, and who may search them. Approving a field never gives access to records: searches only ever cover records a person can already find."
      />
      {administration.isPending ? <LoadingBlock label="Reading search approvals from the Authorization Authority" /> : null}
      {administration.isError ? <ErrorPanel error={administration.error} onRetry={() => void administration.refetch()} /> : null}
      {administration.data ? (
        <div className="flex flex-col gap-4">
          {!administration.data.plaintextSearchPermitted ? (
            <StatusMessage tone="warning" title="Search approvals are not permitted by the organization policy">
              The organization owner can permit search approvals for plaintext Public/Internal fields under Organization settings. Until then no field can be approved.
            </StatusMessage>
          ) : null}
          {!administration.data.approvalOperational ? (
            <StatusMessage tone="info" title="Approval is not available in this environment">
              Approving a field needs a fresh passkey confirmation (step-up), which is not available yet (PB-08). You can review the forms and their fields, but every approval is refused.
            </StatusMessage>
          ) : null}
          <Card title={`Published forms (${administration.data.forms.length})`}>
            <DataTable
              caption="Published forms registered with the Authorization Authority"
              rowKey={(row) => row.formDefinitionId}
              rows={administration.data.forms}
              emptyMessage="No published form is registered with the Authorization Authority yet. Forms are registered when they are published."
              stickyActions
              columns={[
                { key: "form", header: "Form", isRowHeader: true, render: (row) => (row.formTitleRegistered ? row.formTitle : <span className="text-muted">{row.formTitle}</span>) },
                { key: "version", header: "Published version", render: (row) => String(row.registeredFormVersionNumber) },
                { key: "eligible", header: "Can be approved", render: (row) => `${row.eligibleFieldCount} of ${row.fieldCount}` },
                { key: "approved", header: "Approved", render: (row) => (Number(row.activeApprovalCount) > 0 ? <Badge tone="success">{String(row.activeApprovalCount)}</Badge> : "0") },
                { key: "invalidated", header: "No longer valid", render: (row) => (Number(row.invalidatedApprovalCount) > 0 ? <Badge tone="warning">{String(row.invalidatedApprovalCount)}</Badge> : "0") },
                {
                  key: "actions",
                  header: "Actions",
                  isActions: true,
                  render: (row) => (
                    <Button size="sm" variant="secondary" aria-label={`Review fields of ${row.formTitle}`} onClick={() => setSelected(row)}>
                      Review fields
                    </Button>
                  ),
                },
              ]}
            />
          </Card>
          {selected ? <FormApprovals key={selected.formDefinitionId} form={selected} onClose={() => setSelected(null)} /> : null}
        </div>
      ) : null}
    </>
  );
}

function FormApprovals({ form, onClose }: { form: FieldSearchAdministrationFormResponse; onClose: () => void }) {
  const api = useApi();
  const configuration = useQuery({ queryKey: fieldSearchKey(form.formDefinitionId), queryFn: ({ signal }) => api.forms.fieldSearch(form.formDefinitionId, signal).then((r) => r.result) });
  const users = useQuery({ queryKey: ["users"], queryFn: () => api.organization.users().then((r) => r.result) });
  const access = useQuery({ queryKey: ["forms", form.formDefinitionId, "access"], queryFn: () => api.forms.access(form.formDefinitionId).then((r) => r.result), retry: false });
  return (
    <section aria-labelledby="search-approvals-form" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="search-approvals-form" className="m-0 text-lg font-semibold">
          {`${form.formTitle} — published version ${form.registeredFormVersionNumber}`}
        </h2>
        <Button variant="secondary" size="sm" onClick={onClose}>
          Close
        </Button>
      </div>
      {configuration.isPending ? <LoadingBlock label="Reading the form's fields from the Authorization Authority" /> : null}
      {configuration.isError ? <ErrorPanel error={configuration.error} onRetry={() => void configuration.refetch()} /> : null}
      {configuration.data
        ? configuration.data.fields.map((field) => (
            <Card key={field.fieldDefinitionId} title={field.fieldLabel}>
              <FieldSearchSection formDefinitionId={form.formDefinitionId} fieldDefinitionId={field.fieldDefinitionId} />
            </Card>
          ))
        : null}
      <FieldSearchersCard formDefinitionId={form.formDefinitionId} people={users.data ?? []} teams={access.data?.availableTeams ?? []} />
    </section>
  );
}
