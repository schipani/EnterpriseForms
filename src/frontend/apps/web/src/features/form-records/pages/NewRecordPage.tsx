import { useApi } from "@enterpriseforms/app-runtime";
import { FormRenderer, groupPayload, initialGroups, initialValues, tryParseLayout, validateGroups, validateValues, type GroupValueMap, type RecordGroupPayload, type ValueMap } from "@enterpriseforms/form-renderer";
import { Button, LoadingBlock, PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate, useParams } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { ErrorPanel } from "../../support";
import { layoutValues, toGroupInfos, toRendererFields } from "../components/recordModel";

/** PG-016 create record from the current published version; the creator becomes the owner in the authority. */
export function NewRecordPage() {
  const { formDefinitionId } = useParams({ from: "/app/forms/$formDefinitionId/records/new" });
  const api = useApi();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const form = useQuery({ queryKey: ["forms", formDefinitionId], queryFn: () => api.forms.get(formDefinitionId).then((r) => r.result) });
  const [values, setValues] = useState<ValueMap | null>(null);
  const [groups, setGroups] = useState<GroupValueMap | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  // Repeating sections start with their minimum number of entries (created once, so entries keep their identity while editing).
  useEffect(() => {
    const parsedLayout = form.data?.currentVersion ? tryParseLayout(form.data.currentVersion.layoutJson) : null;
    if (groups === null && form.data && parsedLayout?.ok) {
      setGroups(initialGroups(parsedLayout.layout, toRendererFields(form.data.fields), toGroupInfos(form.data.groups)));
    }
  }, [form.data, groups]);
  const create = useMutation({
    mutationFn: (payload: { values: Record<string, string>; groups: RecordGroupPayload[] }) => api.records.create(formDefinitionId, { values: payload.values, ownerTeamId: null, groups: payload.groups }).then((r) => r.result),
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
  const infos = toGroupInfos(form.data.groups);
  const currentGroups = groups ?? {};
  const submit = () => {
    const validation = { ...validateValues(parsed.layout, fields, current, "create"), ...validateGroups(parsed.layout, fields, current, currentGroups, infos, "create") };
    setErrors(validation);
    if (Object.keys(validation).length === 0) {
      create.mutate({ values: layoutValues(parsed.layout, fields, current, starting), groups: groupPayload(parsed.layout, fields, current, currentGroups, infos) });
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
        <FormRenderer layout={parsed.layout} fields={fields} values={current} errors={errors} groups={currentGroups} groupInfos={infos} onGroupsChange={setGroups} onChange={(key, value) => setValues((previous) => ({ ...(previous ?? starting), [key]: value }))} renderRelatedList={(element) => <p className="text-sm text-muted">{element.label || "Related records"}: available after the record is created.</p>} renderDocumentCollection={(element) => <p className="text-sm text-muted">{element.label || "Documents"}: upload after the record is created.</p>} />
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
