import type { DirectoryTeamResponse, DirectoryUserResponse, FormAccessSubjectResponse } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, DataTable, Dialog, LoadingBlock, Select, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { fieldSearchKey } from "./FieldSearchSection";

/**
 * Form permissions: who may create records for this form. Everything shown is read from the independent Authorization
 * Authority (never a business-table copy) and every change is an authorized, journaled authority command that takes
 * effect immediately, needs no new form version, and grants no access to any existing record.
 */
export function FormAccessPanel({ formDefinitionId, canManage }: { formDefinitionId: string; canManage: boolean }) {
  const api = useApi();
  const queryClient = useQueryClient();
  // Read for everyone who opens the form: the Authority answers organization administrators too (who may not design this
  // form), and the search-access card below shows itself only to security administrators.
  const access = useQuery({ queryKey: ["forms", formDefinitionId, "access"], queryFn: () => api.forms.access(formDefinitionId).then((r) => r.result), retry: false });
  const users = useQuery({ queryKey: ["users"], queryFn: () => api.organization.users().then((r) => r.result) });
  const [grantOpen, setGrantOpen] = useState(false);
  const [revokeTarget, setRevokeTarget] = useState<FormAccessSubjectResponse | null>(null);
  const [personId, setPersonId] = useState<string | undefined>(undefined);
  const [teamId, setTeamId] = useState<string | undefined>(undefined);
  const [reason, setReason] = useState("");
  const close = () => {
    setGrantOpen(false);
    setRevokeTarget(null);
    setPersonId(undefined);
    setTeamId(undefined);
    setReason("");
  };
  const update = useMutation({
    mutationFn: (change: { grantUserIds: string[]; grantTeamIds: string[]; revokeUserIds: string[]; revokeTeamIds: string[] }) => api.forms.updateAccess(formDefinitionId, { ...change, changeReason: reason.trim() }).then((r) => r.result),
    onSuccess: async (state) => {
      queryClient.setQueryData(["forms", formDefinitionId, "access"], state);
      await queryClient.invalidateQueries({ queryKey: ["forms"] });
      close();
    },
  });

  if (!canManage) {
    return (
      <div className="flex flex-col gap-4">
        <StatusMessage tone="info" title="Form permissions">Only designers of this form and organization administrators can see or change who may create records.</StatusMessage>
        <FieldSearchersCard formDefinitionId={formDefinitionId} people={users.data ?? []} teams={access.data?.availableTeams ?? []} />
      </div>
    );
  }
  if (access.isPending) {
    return <LoadingBlock label="Reading permissions from the Authorization Authority" />;
  }
  if (access.isError) {
    return <ErrorPanel error={access.error} onRetry={() => void access.refetch()} />;
  }
  const data = access.data;
  const permittedUsers = new Set(data.formUsers.filter((s) => s.subjectKind === "User").map((s) => s.subjectId));
  const permittedTeams = new Set(data.formUsers.filter((s) => s.subjectKind === "Team").map((s) => s.subjectId));
  const candidatePeople = (users.data ?? []).filter((user) => !permittedUsers.has(user.userId));
  const candidateTeams = data.availableTeams.filter((team) => !permittedTeams.has(team.teamId));
  const grantReady = (personId !== undefined || teamId !== undefined) && reason.trim().length >= 3;

  return (
    <div className="flex flex-col gap-4">
      <Card
        title={`Who can create records (${data.formUsers.length})`}
        actions={
          <>
            <Button size="sm" variant="secondary" busy={access.isFetching} onClick={() => void access.refetch()}>
              Refresh
            </Button>
            <Button size="sm" onClick={() => setGrantOpen(true)}>
              Grant permission
            </Button>
          </>
        }
      >
        <div className="flex flex-wrap gap-2">
          <Badge tone="info">Source: {data.source}</Badge>
          <Badge tone="neutral">Authorization revision {String(data.authorizationRevision)}</Badge>
        </div>
        <DataTable
          caption="People and teams who can create records"
          rowKey={(row) => `${row.subjectKind}:${row.subjectId}`}
          rows={data.formUsers}
          emptyMessage="Nobody can create records for this form yet."
          columns={[
            { key: "name", header: "Name", isRowHeader: true, render: (row) => row.displayName },
            { key: "kind", header: "Type", render: (row) => row.subjectKind },
            { key: "effective", header: "Effective permission", render: () => "Create records" },
            { key: "actions", header: "Actions", isActions: true, render: (row) => <Button size="sm" variant="ghost" aria-label={`Revoke ${row.displayName}`} onClick={() => setRevokeTarget(row)}>Revoke</Button> },
          ]}
        />
        <StatusMessage tone="info" title="What this changes">
          Creating records only. It does not publish a new form version and does not give anyone access to existing records. Someone you remove keeps the records they own or that were shared with them; access to a record is managed on that record.
        </StatusMessage>
      </Card>
      <Card title={`Designers (${data.designers.length})`}>
        <DataTable
          caption="People and teams who can design this form"
          rowKey={(row) => `${row.subjectKind}:${row.subjectId}`}
          rows={data.designers}
          emptyMessage="No designers."
          columns={[
            { key: "name", header: "Name", isRowHeader: true, render: (row) => row.displayName },
            { key: "kind", header: "Type", render: (row) => row.subjectKind },
            { key: "effective", header: "Effective permission", render: () => "Design and publish (records are not readable by design alone)" },
          ]}
        />
      </Card>
      <FieldSearchersCard formDefinitionId={formDefinitionId} people={users.data ?? []} teams={data.availableTeams} />

      <Dialog
        open={grantOpen}
        onOpenChange={(open) => (open ? undefined : close())}
        title="Grant permission to create records"
        description="Choose a person, a team, or both. The change is a recorded authority command and takes effect immediately."
        locked={update.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={close} disabled={update.isPending}>
              Cancel
            </Button>
            <Button busy={update.isPending} disabled={!grantReady} onClick={() => update.mutate({ grantUserIds: personId ? [personId] : [], grantTeamIds: teamId ? [teamId] : [], revokeUserIds: [], revokeTeamIds: [] })}>
              Grant
            </Button>
          </>
        }
      >
        <Select label="Person" value={personId} placeholder={candidatePeople.length === 0 ? "Everyone already has permission" : "Choose a person"} options={candidatePeople.map((user) => ({ value: user.userId, label: user.displayName }))} onValueChange={setPersonId} />
        <Select label="Team" value={teamId} placeholder={candidateTeams.length === 0 ? "No teams available" : "Choose a team (optional)"} options={candidateTeams.map((team) => ({ value: team.teamId, label: team.teamName }))} onValueChange={setTeamId} />
        <TextInput label="Reason" value={reason} required maxLength={500} helpText="Recorded with the change (3 to 500 characters)." onChange={(event) => setReason(event.target.value)} />
        {update.isError ? <ErrorPanel error={update.error} /> : null}
      </Dialog>

      <Dialog
        open={revokeTarget !== null}
        onOpenChange={(open) => (open ? undefined : close())}
        title={`Revoke ${revokeTarget?.displayName ?? ""}`}
        description="They can no longer create records for this form from their next request. Records they already own stay theirs."
        locked={update.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={close} disabled={update.isPending}>
              Keep permission
            </Button>
            <Button
              variant="danger"
              busy={update.isPending}
              disabled={reason.trim().length < 3}
              onClick={() => revokeTarget && update.mutate({ grantUserIds: [], grantTeamIds: [], revokeUserIds: revokeTarget.subjectKind === "User" ? [revokeTarget.subjectId] : [], revokeTeamIds: revokeTarget.subjectKind === "Team" ? [revokeTarget.subjectId] : [] })}
            >
              Revoke permission
            </Button>
          </>
        }
      >
        <TextInput label="Reason" value={reason} required maxLength={500} helpText="Recorded with the change (3 to 500 characters)." onChange={(event) => setReason(event.target.value)} />
        {update.isError ? <ErrorPanel error={update.error} /> : null}
      </Dialog>
    </div>
  );
}

/**
 * Phase C7 M2 (AC-6): who may run Advanced Search on this form's approved fields (the Authority's field_searcher relation).
 * The list comes back only for security administrators, who alone can change it; everyone else sees why it is hidden.
 * Holding this permission never reveals a record: search results stay limited to records the person can already discover.
 */
function FieldSearchersCard({ formDefinitionId, people, teams }: { formDefinitionId: string; people: readonly DirectoryUserResponse[]; teams: readonly DirectoryTeamResponse[] }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const configuration = useQuery({ queryKey: fieldSearchKey(formDefinitionId), queryFn: ({ signal }) => api.forms.fieldSearch(formDefinitionId, signal).then((r) => r.result) });
  const [personId, setPersonId] = useState<string | undefined>(undefined);
  const [teamId, setTeamId] = useState<string | undefined>(undefined);
  const update = useMutation({
    mutationFn: (change: { grantUserIds: string[]; grantTeamIds: string[]; revokeUserIds: string[]; revokeTeamIds: string[] }) => api.forms.updateFieldSearchers(formDefinitionId, change).then((r) => r.result),
    onSuccess: async () => {
      setPersonId(undefined);
      setTeamId(undefined);
      await queryClient.invalidateQueries({ queryKey: fieldSearchKey(formDefinitionId) });
      await queryClient.invalidateQueries({ queryKey: ["search", "capabilities", formDefinitionId] });
    },
  });

  if (configuration.isPending) {
    return <LoadingBlock label="Reading search access from the Authorization Authority" />;
  }
  const searchers = configuration.data?.fieldSearchers ?? null;
  if (searchers === null) {
    // Not a security administrator (or no configuration for this person): say nothing about who may search.
    return configuration.isError ? null : (
      <Card title="Who can search field values">
        <p className="text-sm text-muted">Only security administrators can see or change who may search this form's approved fields.</p>
      </Card>
    );
  }
  const userIds = new Set(searchers.filter((s) => s.subjectKind === "User").map((s) => s.subjectId));
  const teamIds = new Set(searchers.filter((s) => s.subjectKind === "Team").map((s) => s.subjectId));
  return (
    <Card title={`Who can search field values (${searchers.length})`}>
      <DataTable
        caption="People and teams who can use Advanced Search on this form"
        rowKey={(row) => `${row.subjectKind}:${row.subjectId}`}
        rows={searchers}
        emptyMessage="Nobody can search this form's fields yet."
        columns={[
          { key: "name", header: "Name", isRowHeader: true, render: (row) => row.displayName },
          { key: "kind", header: "Type", render: (row) => row.subjectKind },
          {
            key: "actions",
            header: "Actions",
            isActions: true,
            render: (row) => (
              <Button size="sm" variant="ghost" aria-label={`Remove search access for ${row.displayName}`} disabled={update.isPending} onClick={() => update.mutate({ grantUserIds: [], grantTeamIds: [], revokeUserIds: row.subjectKind === "User" ? [row.subjectId] : [], revokeTeamIds: row.subjectKind === "Team" ? [row.subjectId] : [] })}>
                Remove
              </Button>
            ),
          },
        ]}
      />
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <Select label="Person" value={personId} placeholder="Choose a person" options={people.filter((user) => !userIds.has(user.userId)).map((user) => ({ value: user.userId, label: user.displayName }))} onValueChange={setPersonId} />
        <Select label="Team" value={teamId} placeholder="Choose a team (optional)" options={teams.filter((team) => !teamIds.has(team.teamId)).map((team) => ({ value: team.teamId, label: team.teamName }))} onValueChange={setTeamId} />
      </div>
      <div>
        <Button size="sm" busy={update.isPending} disabled={personId === undefined && teamId === undefined} onClick={() => update.mutate({ grantUserIds: personId ? [personId] : [], grantTeamIds: teamId ? [teamId] : [], revokeUserIds: [], revokeTeamIds: [] })}>
          Grant search access
        </Button>
      </div>
      {update.isError ? <ErrorPanel error={update.error} /> : null}
      <StatusMessage tone="info" title="What this changes">
        Searching only, and only on fields a security administrator approved. It never shows a record the person could not already find. Searching itself is not turned on yet.
      </StatusMessage>
    </Card>
  );
}
