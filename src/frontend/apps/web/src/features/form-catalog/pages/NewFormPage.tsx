import { useApi } from "@enterpriseforms/app-runtime";
import { Button, Card, Checkbox, LoadingBlock, PageHeader, TextArea, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { ErrorPanel } from "../../support";

/** Creates a form definition (authority command) and opens the designer. Form users are explicit grants, never implied. */
export function NewFormPage() {
  const api = useApi();
  const navigate = useNavigate();
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [users, setUsers] = useState<Set<string>>(new Set());
  const directory = useQuery({ queryKey: ["users"], queryFn: () => api.organization.users().then((r) => r.result) });
  const create = useMutation({
    mutationFn: () => api.forms.create({ formDefinitionName: name.trim(), formDefinitionDescription: description.trim(), formUserUserIds: [...users], formUserTeamIds: [] }).then((r) => r.result),
    onSuccess: (detail) => void navigate({ to: "/app/forms/$formDefinitionId/design", params: { formDefinitionId: detail.summary.formDefinitionId } }),
  });
  return (
    <div className="mx-auto max-w-2xl">
      <PageHeader title="New form" lead="Create the template, then add fields and publish a version before records can be created." />
      <Card>
        <TextInput label="Form name" value={name} required maxLength={200} onChange={(event) => setName(event.target.value)} />
        <TextArea label="Description" value={description} maxLength={1000} onChange={(event) => setDescription(event.target.value)} />
        <fieldset className="flex flex-col gap-1">
          <legend className="text-sm font-medium">Who may create records with this form</legend>
          <p className="text-sm text-muted">Designing a form never grants access to its records; record access follows ownership and explicit shares.</p>
          {directory.isPending ? <LoadingBlock label="Loading users" /> : null}
          {directory.isError ? <ErrorPanel error={directory.error} onRetry={() => void directory.refetch()} /> : null}
          {(directory.data ?? []).map((user) => (
            <Checkbox
              key={user.userId}
              label={user.displayName}
              checked={users.has(user.userId)}
              onCheckedChange={(checked) =>
                setUsers((current) => {
                  const next = new Set(current);
                  if (checked) {
                    next.add(user.userId);
                  } else {
                    next.delete(user.userId);
                  }
                  return next;
                })
              }
            />
          ))}
        </fieldset>
        {create.isError ? <ErrorPanel error={create.error} /> : null}
        <div className="flex gap-2">
          <Button busy={create.isPending} disabled={name.trim().length === 0} onClick={() => create.mutate()}>
            Create form
          </Button>
          <Button variant="secondary" onClick={() => void navigate({ to: "/app/forms" })}>
            Cancel
          </Button>
        </div>
      </Card>
    </div>
  );
}
