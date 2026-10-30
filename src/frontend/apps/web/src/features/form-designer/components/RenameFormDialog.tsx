import { presentError, useApi } from "@enterpriseforms/app-runtime";
import { Button, Dialog, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { ErrorPanel } from "../../support";

/** Same rule as the server (FormDefinitionNameRules): trimmed, 1–200 characters, no control characters. */
export const FORM_NAME_MAXIMUM_LENGTH = 200;

export function formNameProblem(name: string): string | null {
  const trimmed = name.trim();
  if (trimmed.length === 0) {
    return "Enter a form name.";
  }
  if (trimmed.length > FORM_NAME_MAXIMUM_LENGTH) {
    return `Use at most ${FORM_NAME_MAXIMUM_LENGTH} characters.`;
  }
  if (/[\u0000-\u001f\u007f-\u009f]/.test(trimmed)) {
    return "Remove line breaks, tabs and other control characters.";
  }
  return null;
}

export interface RenameFormTarget {
  formDefinitionId: string;
  formDefinitionName: string;
  formDefinitionRevision: number | string;
}

/**
 * Rename a form (display name only). Used by the Forms list and the Form Designer. Published versions, records,
 * permissions, sharing and history are unchanged and no new version is created; the server checks the Authority's
 * design permission, the revision the dialog was opened with (a stale one is refused, never overwritten) and audits
 * the change. On success every cached list or page that shows form names is refreshed.
 */
export function RenameFormDialog({ target, onClose, onRenamed }: { target: RenameFormTarget | null; onClose: () => void; onRenamed: (name: string) => void }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [touched, setTouched] = useState(false);
  useEffect(() => {
    if (target) {
      setName(target.formDefinitionName);
      setTouched(false);
    }
  }, [target]);
  const rename = useMutation({
    mutationFn: (request: { formDefinitionId: string; formDefinitionName: string; expectedFormDefinitionRevision: number | string }) =>
      api.forms.rename(request.formDefinitionId, { formDefinitionName: request.formDefinitionName, expectedFormDefinitionRevision: request.expectedFormDefinitionRevision }).then((r) => r.result),
    onSuccess: async (detail) => {
      onRenamed(detail.summary.formDefinitionName);
      onClose();
      await Promise.all(["forms", "records", "dashboard", "search", "related"].map((root) => queryClient.invalidateQueries({ queryKey: [root] })));
    },
    onError: async (error) => {
      // A conflict means someone else renamed it: refresh so a retry starts from the current name and revision.
      if (presentError(error).kind === "conflict") {
        await queryClient.invalidateQueries({ queryKey: ["forms"] });
      }
    },
  });
  const close = () => {
    rename.reset();
    onClose();
  };
  const problem = formNameProblem(name);
  const unchanged = target !== null && name.trim() === target.formDefinitionName;
  const submit = () => {
    setTouched(true);
    if (!target || problem || unchanged) {
      return;
    }
    rename.mutate({ formDefinitionId: target.formDefinitionId, formDefinitionName: name.trim(), expectedFormDefinitionRevision: target.formDefinitionRevision });
  };
  return (
    <Dialog
      open={target !== null}
      onOpenChange={(open) => (open ? undefined : close())}
      title="Rename form"
      description="Changes the name shown everywhere in the application. Published versions, records, permissions, sharing and history are unchanged, and no new version is created. The change is audited."
      locked={rename.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={close} disabled={rename.isPending}>
            Cancel
          </Button>
          <Button busy={rename.isPending} disabled={unchanged} onClick={submit}>
            Save name
          </Button>
        </>
      }
    >
      <p className="m-0 text-sm">
        <span className="font-medium">Current name:</span> <span className="break-words">{target?.formDefinitionName}</span>
      </p>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <TextInput
          label="New name"
          value={name}
          required
          maxLength={FORM_NAME_MAXIMUM_LENGTH}
          helpText={`Up to ${FORM_NAME_MAXIMUM_LENGTH} characters. Names do not need to be unique.`}
          error={touched && problem ? problem : undefined}
          onChange={(event) => {
            setName(event.target.value);
            setTouched(true);
          }}
        />
      </form>
      {rename.isError ? <ErrorPanel error={rename.error} /> : null}
    </Dialog>
  );
}
