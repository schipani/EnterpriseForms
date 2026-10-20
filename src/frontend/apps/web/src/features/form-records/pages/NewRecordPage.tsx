import { useApi } from "@enterpriseforms/app-runtime";
import { FormRenderer, initialValues, tryParseLayout, validateValues, type ValueMap } from "@enterpriseforms/form-renderer";
import { Button, LoadingBlock, PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { layoutValues, toRendererFields } from "../components/recordModel";

/** PG-016 create record from the current published version; the creator becomes the owner in the authority. */
export function NewRecordPage() {
  const { formDefinitionId } = useParams({ from: "/app/forms/$formDefinitionId/records/new" });
  const api = useApi();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const form = useQuery({ queryKey: ["forms", formDefinitionId], queryFn: () => api.forms.get(formDefinitionId).then((r) => r.result) });
  const [values, setValues] = useState<ValueMap | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const create = useMutation({
    mutationFn: (payload: Record<string, string>) => api.records.create(formDefinitionId, { values: payload, ownerTeamId: null }).then((r) => r.result),
    onSuccess: async (record) => {
      await queryClient.invalidateQueries({ queryKey: ["records"] });
      await navigate({ to: "/app/records/$formRecordId", params: { formRecordId: record.formRecordId } });
    },
  });
  if (form.isPending) {
    return <LoadingBlock label="Loading form" />;
  }
  if (form.isError) {
    return <ErrorPanel error={form.error} onRetry={() => void form.refetch()} />;
  }
  if (!form.data.currentVersion) {
    return <StatusMessage tone="warning" title="This form has no published version">Publish the form before creating records.</StatusMessage>;
  }
  const parsed = tryParseLayout(form.data.currentVersion.layoutJson);
  if (!parsed.ok) {
    return <StatusMessage tone="danger" title="The published layout could not be parsed">{parsed.error}</StatusMessage>;
  }
  const fields = toRendererFields(form.data.fields);
  // A new record starts with each dropdown's default selection; the server re-validates whatever is sent.
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
      <PageHeader title={`New ${form.data.summary.formDefinitionName}`} lead={`Version ${form.data.currentVersion.versionNumber}. Child records and documents are added after the record exists.`} />
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
        className="flex flex-col gap-4"
      >
        {Object.keys(errors).length > 0 ? (
          <StatusMessage tone="warning" title="Fix the highlighted fields">
            {Object.keys(errors).length} field(s) need attention.
          </StatusMessage>
        ) : null}
        <FormRenderer layout={parsed.layout} fields={fields} values={current} errors={errors} onChange={(key, value) => setValues((previous) => ({ ...(previous ?? starting), [key]: value }))} renderRelatedList={(element) => <p className="text-sm text-muted">{element.label || "Related records"}: available after the record is created.</p>} renderDocumentCollection={(element) => <p className="text-sm text-muted">{element.label || "Documents"}: upload after the record is created.</p>} />
        {create.isError ? <ErrorPanel error={create.error} /> : null}
        <div className="flex gap-2">
          <Button type="submit" busy={create.isPending}>
            Create record
          </Button>
          <Button variant="secondary" onClick={() => void navigate({ to: "/app/records", search: {} })}>
            Cancel
          </Button>
        </div>
      </form>
    </>
  );
}
