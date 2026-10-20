import { useApi } from "@enterpriseforms/app-runtime";
import { FormRenderer, initialValues, tryParseLayout, validateValues, type ValueMap } from "@enterpriseforms/form-renderer";
import { Button, LoadingBlock, PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { layoutValues, toRendererFields } from "../../form-records";
import { ErrorPanel } from "../../support";

/** Creates a child record (e.g. AccusedAddress) under a parent through the relationship; ownership follows the parent in the authority. */
export function NewChildRecordPage() {
  const { formRecordId, formRelationshipDefinitionId } = useParams({ from: "/app/records/$formRecordId/related/$formRelationshipDefinitionId/new" });
  const api = useApi();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const parent = useQuery({ queryKey: ["records", "projection", formRecordId], queryFn: () => api.records.projection(formRecordId).then((r) => r.result) });
  const relationship = parent.data?.relationships.find((candidate) => candidate.formRelationshipDefinitionId === formRelationshipDefinitionId);
  const childForm = useQuery({ queryKey: ["forms", relationship?.childFormDefinitionId], queryFn: () => api.forms.get(relationship!.childFormDefinitionId).then((r) => r.result), enabled: Boolean(relationship) });
  const [values, setValues] = useState<ValueMap | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const create = useMutation({
    mutationFn: (payload: Record<string, string>) => api.related.create(formRecordId, { formRelationshipDefinitionId, values: payload }).then((r) => r.result),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["related", formRecordId] });
      await queryClient.invalidateQueries({ queryKey: ["records"] });
      await navigate({ to: "/app/records/$formRecordId", params: { formRecordId } });
    },
  });
  if (parent.isPending || (relationship && childForm.isPending)) {
    return <LoadingBlock label="Loading" />;
  }
  if (parent.isError) {
    return <ErrorPanel error={parent.error} onRetry={() => void parent.refetch()} />;
  }
  if (!relationship) {
    return <StatusMessage tone="danger" title="Unknown relationship" />;
  }
  if (childForm.isError) {
    return <ErrorPanel error={childForm.error} onRetry={() => void childForm.refetch()} />;
  }
  const child = childForm.data!;
  if (!child.currentVersion) {
    return <StatusMessage tone="warning" title="The child form has no published version" />;
  }
  const parsed = tryParseLayout(child.currentVersion.layoutJson);
  if (!parsed.ok) {
    return <StatusMessage tone="danger" title="Child layout could not be parsed">{parsed.error}</StatusMessage>;
  }
  const fields = toRendererFields(child.fields);
  // A new child starts with each dropdown's default selection; the server re-validates whatever is sent.
  const starting = initialValues(parsed.layout, fields);
  const current = values ?? starting;
  const submit = () => {
    const validation = validateValues(parsed.layout, fields, current, "create");
    setErrors(validation);
    if (Object.keys(validation).length === 0) {
      create.mutate(layoutValues(parsed.layout, fields, current, starting));
    }
  };
  return (
    <>
      <PageHeader title={`Add ${relationship.childFormDefinitionName}`} lead={`${relationship.relationshipDisplayName} on ${parent.data.formDefinitionName}`} />
      <form
        noValidate
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <FormRenderer layout={parsed.layout} fields={fields} values={current} errors={errors} onChange={(key, value) => setValues((previous) => ({ ...(previous ?? starting), [key]: value }))} />
        {create.isError ? <ErrorPanel error={create.error} /> : null}
        <div className="flex gap-2">
          <Button type="submit" busy={create.isPending}>
            Add {relationship.childFormDefinitionName}
          </Button>
          <Link to="/app/records/$formRecordId" params={{ formRecordId }}>
            <Button variant="secondary">Cancel</Button>
          </Link>
        </div>
      </form>
    </>
  );
}
