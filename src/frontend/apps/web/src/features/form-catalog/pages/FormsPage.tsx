import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { ActionIcons, Badge, Button, Card, DataTable, LoadingBlock, PageHeader, RowActions, StatusMessage } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { RenameFormDialog, type RenameFormTarget } from "../../form-designer";
import { ErrorPanel } from "../../support";

/** PG-029 form catalog: what the authority lets this user design or use. */
export function FormsPage() {
  const api = useApi();
  const session = useSession();
  const forms = useQuery({ queryKey: ["forms"], queryFn: () => api.forms.list().then((r) => r.result) });
  const [renaming, setRenaming] = useState<RenameFormTarget | null>(null);
  const [renamed, setRenamed] = useState<string | null>(null);
  return (
    <>
      <PageHeader
        title="Forms"
        lead="Templates are designed and published here; records are created from a published version."
        actions={
          <>
            <Button variant="secondary" busy={forms.isFetching} onClick={() => void forms.refetch()}>
              Refresh
            </Button>
            {session.hasCapability("forms.design") ? (
              <Link to="/app/forms/new">
                <Button>New form</Button>
              </Link>
            ) : null}
          </>
        }
      />
      {renamed !== null ? (
        <StatusMessage
          tone="success"
          title="Form renamed"
          actions={
            <Button size="sm" variant="ghost" onClick={() => setRenamed(null)}>
              Dismiss
            </Button>
          }
        >
          The form is now called “{renamed}”. Its versions, records and permissions are unchanged.
        </StatusMessage>
      ) : null}
      <Card>
        {forms.isPending ? <LoadingBlock label="Loading forms" /> : null}
        {forms.isError ? <ErrorPanel error={forms.error} onRetry={() => void forms.refetch()} /> : null}
        {forms.data ? (
          <DataTable
            caption="Form definitions"
            rowKey={(row) => row.formDefinitionId}
            rows={forms.data}
            emptyMessage="No forms are visible to you."
            columns={[
              { key: "name", header: "Form", isRowHeader: true, render: (row) => (row.canDesign ? <Link to="/app/forms/$formDefinitionId/design" params={{ formDefinitionId: row.formDefinitionId }} className="underline-offset-4 hover:underline">{row.formDefinitionName}</Link> : row.formDefinitionName) },
              { key: "version", header: "Published", render: (row) => (row.currentVersionNumber === null ? <Badge tone="neutral">Not published</Badge> : <Badge tone="success">v{row.currentVersionNumber}</Badge>) },
              { key: "draft", header: "Draft", render: (row) => (row.hasDraft ? <Badge tone="warning">Draft pending</Badge> : <span className="text-muted">None</span>) },
              { key: "modified", header: "Modified", render: (row) => new Date(row.modifiedOn).toLocaleString() },
              {
                key: "actions",
                header: "Actions",
                isActions: true,
                render: (row) => (
                  <RowActions
                    overflowLabel={`More actions for ${row.formDefinitionName}`}
                    primary={
                      <>
                        {row.canDesign ? (
                          <Link to="/app/forms/$formDefinitionId/design" params={{ formDefinitionId: row.formDefinitionId }}>
                            <Button size="sm" variant="secondary">
                              Design
                            </Button>
                          </Link>
                        ) : null}
                        {row.canCreateRecord && row.currentVersionNumber !== null ? (
                          <Link to="/app/forms/$formDefinitionId/records/new" params={{ formDefinitionId: row.formDefinitionId }}>
                            <Button size="sm" icon={ActionIcons.add}>New record</Button>
                          </Link>
                        ) : null}
                      </>
                    }
                    overflow={[
                      ...(row.canDesign
                        ? [
                            {
                              key: "rename",
                              content: (
                                <button type="button" onClick={() => setRenaming({ formDefinitionId: row.formDefinitionId, formDefinitionName: row.formDefinitionName, formDefinitionRevision: row.formDefinitionRevision })}>
                                  Rename
                                </button>
                              ),
                            },
                          ]
                        : []),
                      ...(row.currentVersionNumber !== null
                        ? [
                            {
                              key: "records",
                              content: (
                                <Link to="/app/records" search={{ form: row.formDefinitionId }}>
                                  Records using this form
                                </Link>
                              ),
                            },
                          ]
                        : []),
                    ]}
                  />
                ),
              },
            ]}
          />
        ) : null}
      </Card>
      <RenameFormDialog
        target={renaming}
        onClose={() => setRenaming(null)}
        onRenamed={(name) => {
          setRenamed(name);
        }}
      />
    </>
  );
}
