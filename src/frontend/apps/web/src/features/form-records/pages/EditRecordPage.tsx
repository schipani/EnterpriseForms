import { presentError, useApi } from "@enterpriseforms/app-runtime";
import { FormRenderer, validateValues, type ValueMap } from "@enterpriseforms/form-renderer";
import { Button, LoadingBlock, PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { ErrorPanel } from "../../support";
import { layoutValues, parseProjectionLayout, toRendererFields, toValueMap } from "../components/recordModel";

/** PG-018 edit: optimistic concurrency with the projection's content revision (If-Match); conflicts offer a reload. */
export function EditRecordPage() {
  const { formRecordId } = useParams({ from: "/app/records/$formRecordId/edit" });
  const api = useApi();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const projection = useQuery({ queryKey: ["records", "projection", formRecordId], queryFn: () => api.records.projection(formRecordId).then((r) => r.result) });
  const [values, setValues] = useState<ValueMap | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  useEffect(() => {
    if (projection.data && values === null) {
      setValues(toValueMap(projection.data.record));
    }
  }, [projection.data, values]);
  const update = useMutation({
    mutationFn: (payload: Record<string, string>) => api.records.update(formRecordId, { expectedContentRevision: Number(projection.data!.record.contentRevision), values: payload }).then((r) => r.result),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["records"] });
      await navigate({ to: "/app/records/$formRecordId", params: { formRecordId } });
    },
  });
  if (projection.isPending || values === null) {
    return projection.isError ? <ErrorPanel error={projection.error} onRetry={() => void projection.refetch()} /> : <LoadingBlock label="Loading record" />;
  }
  if (projection.isError) {
    return <ErrorPanel error={projection.error} onRetry={() => void projection.refetch()} />;
  }
  const data = projection.data;
  if (!data.permissions.canEdit) {
    return <StatusMessage tone="danger" title="You cannot edit this record" />;
  }
  const { layout, error } = parseProjectionLayout(data);
  if (!layout) {
    return <StatusMessage tone="danger" title="Layout could not be parsed">{error}</StatusMessage>;
  }
  const fields = toRendererFields(data.fields);
  const conflict = update.isError && presentError(update.error).kind === "conflict";
  const applyRules = data.permissions.isCompleteProjection;
  const initial = toValueMap(data.record);
  const submit = () => {
    const validation = validateValues(layout, fields, values, "edit", { applyRules });
    setErrors(validation);
    if (Object.keys(validation).length === 0) {
      update.mutate(layoutValues(layout, fields, values, initial, applyRules));
    }
  };
  return (
    <>
      <PageHeader title={`Edit ${data.formDefinitionName}`} lead={`Editing content revision ${String(data.record.contentRevision)} with layout v${data.layoutVersion.versionNumber}.`} />
      <form
        noValidate
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        {conflict ? (
          <StatusMessage
            tone="warning"
            title="Someone else changed this record"
            actions={
              <Button
                size="sm"
                variant="secondary"
                onClick={() => {
                  setValues(null);
                  update.reset();
                  void projection.refetch();
                }}
              >
                Reload the latest version (your edits are discarded)
              </Button>
            }
          >
            Your changes were not saved because the record's content revision moved on.
          </StatusMessage>
        ) : update.isError ? (
          <ErrorPanel error={update.error} />
        ) : null}
        <FormRenderer layout={layout} fields={fields} values={values} errors={errors} applyRules={applyRules} onChange={(key, value) => setValues((current) => ({ ...(current ?? {}), [key]: value }))} renderRelatedList={(element) => <p className="text-sm text-muted">{element.label || "Related records"} are managed from the record page.</p>} renderDocumentCollection={(element) => <p className="text-sm text-muted">{element.label || "Documents"} are managed from the record page.</p>} />
        <div className="flex gap-2">
          <Button type="submit" busy={update.isPending}>
            Save changes
          </Button>
          <Link to="/app/records/$formRecordId" params={{ formRecordId }}>
            <Button variant="secondary">Cancel</Button>
          </Link>
        </div>
      </form>
    </>
  );
}
