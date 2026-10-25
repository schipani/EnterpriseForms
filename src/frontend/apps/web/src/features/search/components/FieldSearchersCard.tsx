import type { DirectoryTeamResponse, DirectoryUserResponse } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import { Button, Card, DataTable, LoadingBlock, Select, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { fieldSearchKey } from "./FieldSearchSection";

/**
 * Phase C7 M2 (AC-6): who may run Advanced Search on this form's approved fields (the Authority's field_searcher relation).
 * The list comes back only for security administrators, who alone can change it; everyone else sees why it is hidden.
 * Holding this permission never reveals a record: search results stay limited to records the person can already discover.
 */
export function FieldSearchersCard({ formDefinitionId, people, teams }: { formDefinitionId: string; people: readonly DirectoryUserResponse[]; teams: readonly DirectoryTeamResponse[] }) {
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
