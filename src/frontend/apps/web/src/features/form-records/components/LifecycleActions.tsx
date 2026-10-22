import { useApi, presentError } from "@enterpriseforms/app-runtime";
import type { RecordLifecycleTransition, RecordProjectionResponse } from "@enterpriseforms/api-client";
import { Button, Dialog, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";

/**
 * Submit, Reopen, Archive and Unarchive (Phase C6 M1). Which actions appear comes from the server's verified lifecycle
 * state (owner only); hiding a button is a convenience, never the control — every transition is decided again by the
 * server from the Authority-verified signed header. Each action confirms first; Reopen needs a reason.
 */

interface TransitionCopy {
  readonly transition: RecordLifecycleTransition;
  /** The server's transition name (what AllowedTransitions lists). */
  readonly name: string;
  readonly button: string;
  readonly title: string;
  readonly body: string;
  readonly confirm: string;
  readonly done: string;
  readonly variant: "primary" | "secondary";
  /** "required" asks for a reason before confirming; "optional" offers one. */
  readonly reason?: "required" | "optional";
}

export const TRANSITIONS: readonly TransitionCopy[] = [
  {
    transition: "submit",
    name: "Submit",
    button: "Submit",
    title: "Submit this record?",
    body: "The record is checked against the form's rules. Once submitted it is locked: its values, documents and related records can't be changed unless the owner reopens it.",
    confirm: "Submit record",
    done: "Record submitted. It is now locked.",
    variant: "primary",
  },
  {
    transition: "reopen",
    name: "Reopen",
    button: "Reopen as draft",
    title: "Reopen this record as a draft?",
    body: "The record becomes editable again. Reopening is recorded in the activity history together with your reason.",
    confirm: "Reopen as draft",
    done: "Record reopened as a draft.",
    variant: "secondary",
    reason: "required",
  },
  {
    transition: "archive",
    name: "Archive",
    button: "Archive",
    title: "Archive this record?",
    body: "Archived records are read-only and hidden from My Records unless you choose to include them. Nothing is deleted: you can unarchive the record later.",
    confirm: "Archive record",
    done: "Record archived.",
    variant: "secondary",
    reason: "optional",
  },
  {
    transition: "unarchive",
    name: "Unarchive",
    button: "Unarchive",
    title: "Unarchive this record?",
    body: "The record returns to the state it had before it was archived (a draft, or submitted if it was submitted).",
    confirm: "Unarchive record",
    done: "Record unarchived.",
    variant: "primary",
  },
];

export function LifecycleActions({ formRecordId, data, onDone }: { formRecordId: string; data: RecordProjectionResponse; onDone: (message: string) => void }) {
  const allowed = data.lifecycle?.allowedTransitions ?? [];
  const [active, setActive] = useState<TransitionCopy | null>(null);
  if (allowed.length === 0) {
    return null;
  }
  return (
    <>
      {TRANSITIONS.filter((copy) => allowed.includes(copy.name)).map((copy) => (
        <Button key={copy.name} variant={copy.variant} onClick={() => setActive(copy)}>
          {copy.button}
        </Button>
      ))}
      {active ? <LifecycleDialog key={active.name} copy={active} formRecordId={formRecordId} data={data} onClose={() => setActive(null)} onDone={onDone} /> : null}
    </>
  );
}

function LifecycleDialog({ copy, formRecordId, data, onClose, onDone }: { copy: TransitionCopy; formRecordId: string; data: RecordProjectionResponse; onClose: () => void; onDone: (message: string) => void }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState("");
  const labelsByKey = new Map(data.fields.map((field) => [field.fieldKey, field.fieldDisplayName]));
  const mutation = useMutation({
    mutationFn: () =>
      api.records
        .lifecycle(copy.transition, formRecordId, {
          expectedContentRevision: Number(data.record.contentRevision),
          expectedStatus: data.lifecycle?.status ?? null,
          reason: reason.trim() === "" ? null : reason.trim(),
        })
        .then((r) => r.result),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["records"] });
      onClose();
      onDone(copy.done);
    },
  });
  const presented = mutation.isError ? presentError(mutation.error) : null;
  const reasonMissing = copy.reason === "required" && reason.trim().length === 0;
  return (
    <Dialog
      open
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={copy.title}
      description={copy.body}
      locked={mutation.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button busy={mutation.isPending} disabled={reasonMissing} onClick={() => mutation.mutate()}>
            {copy.confirm}
          </Button>
        </>
      }
    >
      {copy.reason ? <TextInput label={copy.reason === "required" ? "Reason" : "Reason (optional)"} value={reason} required={copy.reason === "required"} maxLength={500} helpText="Recorded in the activity history. Don't include confidential details." onChange={(event) => setReason(event.target.value)} /> : null}
      {mutation.isError ? (
        <div className="flex flex-col gap-2">
          <ErrorPanel error={mutation.error} />
          {presented && presented.fieldKeys.length > 0 ? (
            <div>
              <p className="text-sm font-medium">Fix these fields in the editor, then submit again:</p>
              <ul className="list-disc pl-5 text-sm">
                {presented.fieldKeys.map((key) => (
                  <li key={key}>{labelsByKey.get(key) ?? key}</li>
                ))}
              </ul>
            </div>
          ) : null}
        </div>
      ) : null}
    </Dialog>
  );
}
