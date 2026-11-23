import { presentError, useApi } from "@enterpriseforms/app-runtime";
import type { VersionBehavior, VersionBehaviorImpactResponse } from "@enterpriseforms/api-client";
import { Button, Dialog, LoadingBlock, RadioGroup, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { ErrorPanel } from "../../support";

/** Same rule as the server (FormDesignerHandlers): trimmed, 3–500 characters, no control characters. */
export const VERSION_BEHAVIOR_REASON_MINIMUM = 3;
export const VERSION_BEHAVIOR_REASON_MAXIMUM = 500;

export const VERSION_BEHAVIORS: readonly VersionBehavior[] = ["Latest", "Historical", "Hybrid"];

export const VERSION_BEHAVIOR_COPY: Record<VersionBehavior, { label: string; summary: string }> = {
  Latest: { label: "Current published version", summary: "Records are shown and edited with the current published version of the form." },
  Historical: { label: "Version a record was created with", summary: "Each record is shown and edited with the version it was created with. Newer versions apply to new records only." },
  Hybrid: { label: "Show original, edit with current", summary: "Each record is shown with the version it was created with, and edited with the current published version." },
};

export function isVersionBehavior(value: string | null | undefined): value is VersionBehavior {
  return value === "Latest" || value === "Historical" || value === "Hybrid";
}

export function versionBehaviorReasonProblem(reason: string): string | null {
  const trimmed = reason.trim();
  if (trimmed.length < VERSION_BEHAVIOR_REASON_MINIMUM) {
    return `Enter a reason of at least ${VERSION_BEHAVIOR_REASON_MINIMUM} characters.`;
  }
  if (trimmed.length > VERSION_BEHAVIOR_REASON_MAXIMUM) {
    return `Use at most ${VERSION_BEHAVIOR_REASON_MAXIMUM} characters.`;
  }
  if (/[\u0000-\u001f\u007f-\u009f]/.test(trimmed)) {
    return "Remove line breaks, tabs and other control characters.";
  }
  return null;
}

export interface VersionBehaviorTarget {
  formDefinitionId: string;
  formDefinitionName: string;
  formDefinitionRevision: number | string;
  versionBehavior: VersionBehavior;
}

/**
 * Phase C9 (CLAUDE.md §3; D9-2, D9-4, D9-7): choose which published layout the form's records are shown and edited
 * with. Before confirming, the dialog shows an approximate, record-level impact counted only over records the designer
 * can discover; no field values are inspected. The server checks the Authority's design permission and the form revision
 * the dialog was opened with (a stale one is refused, never overwritten), requires a reason, and audits the change.
 * Nothing about records, values, published versions or permissions changes.
 */
export function VersionBehaviorDialog({ target, onClose, onChanged }: { target: VersionBehaviorTarget | null; onClose: () => void; onChanged: (behavior: VersionBehavior) => void }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const [selected, setSelected] = useState<VersionBehavior>("Latest");
  const [reason, setReason] = useState("");
  const [touched, setTouched] = useState(false);
  useEffect(() => {
    if (target) {
      setSelected(target.versionBehavior);
      setReason("");
      setTouched(false);
    }
  }, [target]);
  const changing = target !== null && selected !== target.versionBehavior;
  const impact = useQuery<VersionBehaviorImpactResponse>({
    queryKey: ["forms", "version-behavior-impact", target?.formDefinitionId, selected],
    queryFn: ({ signal }) => api.forms.versionBehaviorImpact(target!.formDefinitionId, selected, signal).then((r) => r.result),
    enabled: changing,
    retry: false,
    staleTime: 0,
  });
  const change = useMutation({
    mutationFn: (request: { formDefinitionId: string; versionBehavior: VersionBehavior; expectedFormDefinitionRevision: number | string; changeReason: string }) =>
      api.forms.changeVersionBehavior(request.formDefinitionId, { versionBehavior: request.versionBehavior, expectedFormDefinitionRevision: request.expectedFormDefinitionRevision, changeReason: request.changeReason }).then((r) => r.result),
    onSuccess: async (detail) => {
      onChanged(isVersionBehavior(detail.summary.versionBehavior) ? detail.summary.versionBehavior : selected);
      onClose();
      await Promise.all(["forms", "records"].map((root) => queryClient.invalidateQueries({ queryKey: [root] })));
    },
    onError: async (error) => {
      // Someone else changed the form's settings: refresh so a retry starts from the current behaviour and revision.
      if (presentError(error).kind === "conflict") {
        await queryClient.invalidateQueries({ queryKey: ["forms"] });
      }
    },
  });
  const close = () => {
    change.reset();
    onClose();
  };
  const problem = versionBehaviorReasonProblem(reason);
  const submit = () => {
    setTouched(true);
    if (!target || !changing || problem) {
      return;
    }
    change.mutate({ formDefinitionId: target.formDefinitionId, versionBehavior: selected, expectedFormDefinitionRevision: target.formDefinitionRevision, changeReason: reason.trim() });
  };
  return (
    <Dialog
      open={target !== null}
      onOpenChange={(open) => (open ? undefined : close())}
      title="Record layout"
      description="Choose which published version of this form its records are shown and edited with. Changing this never changes, moves or deletes record values, published versions or permissions, and values of fields that are not part of a layout are kept."
      locked={change.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={close} disabled={change.isPending}>
            Cancel
          </Button>
          <Button busy={change.isPending} disabled={!changing} onClick={submit}>
            Change record layout
          </Button>
        </>
      }
    >
      <form
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <RadioGroup
          legend="Records use"
          name="version-behavior"
          value={selected}
          onValueChange={(value) => {
            if (isVersionBehavior(value)) {
              setSelected(value);
            }
          }}
          options={VERSION_BEHAVIORS.map((behavior) => ({
            value: behavior,
            label: (
              <span>
                {VERSION_BEHAVIOR_COPY[behavior].label}
                {target?.versionBehavior === behavior ? " (current setting)" : ""}
                <span className="block text-sm font-normal text-muted">{VERSION_BEHAVIOR_COPY[behavior].summary}</span>
              </span>
            ),
          }))}
        />
        {changing ? <ImpactSummary impact={impact.data} pending={impact.isPending} error={impact.isError ? impact.error : null} /> : null}
        {changing ? (
          <TextInput
            label="Reason for the change"
            value={reason}
            required
            maxLength={VERSION_BEHAVIOR_REASON_MAXIMUM}
            helpText={`${VERSION_BEHAVIOR_REASON_MINIMUM}–${VERSION_BEHAVIOR_REASON_MAXIMUM} characters. Recorded in the activity history. Don't include confidential details.`}
            error={touched && problem ? problem : undefined}
            onChange={(event) => {
              setReason(event.target.value);
              setTouched(true);
            }}
          />
        ) : null}
      </form>
      {change.isError ? <ErrorPanel error={change.error} /> : null}
    </Dialog>
  );
}

function ImpactSummary({ impact, pending, error }: { impact: VersionBehaviorImpactResponse | undefined; pending: boolean; error: unknown }) {
  if (error) {
    return <ErrorPanel error={error} />;
  }
  if (pending || !impact) {
    return <LoadingBlock label="Checking which records would be affected" />;
  }
  const visible = Number(impact.visibleRecordCount);
  const viewChanges = Number(impact.viewLayoutChangeCount);
  const editChanges = Number(impact.editLayoutChangeCount);
  const unavailable = Number(impact.unavailableRecordCount);
  const records = (n: number) => (n === 1 ? "1 record" : `${n} records`);
  return (
    <StatusMessage tone="info" title="Impact on records you can see">
      <ul className="m-0 flex list-disc flex-col gap-1 pl-5" aria-label="Approximate impact">
        <li>{visible === 0 ? "You can't see any records of this form." : `${records(visible)} of this form that you can see.`}</li>
        {visible > 0 ? <li>{viewChanges === 0 ? "None would be shown with a different layout." : `${records(viewChanges)} would be shown with a different layout.`}</li> : null}
        {visible > 0 ? <li>{editChanges === 0 ? "None would be edited with a different layout." : `${records(editChanges)} would be edited with a different layout.`}</li> : null}
        {impact.recordsByCreatedVersion.map((group) => (
          <li key={group.versionNumber ?? "unknown"}>
            {group.versionNumber === null || group.versionNumber === undefined ? `Created with a version that no longer exists: ${records(Number(group.recordCount))}` : `Created with v${String(group.versionNumber)}: ${records(Number(group.recordCount))}`}
          </li>
        ))}
        {unavailable > 0 ? <li>{`${records(unavailable)} you can access couldn't be described and ${unavailable === 1 ? "isn't" : "aren't"} counted.`}</li> : null}
      </ul>
      <p className="mt-2 text-sm">{impact.scopeStatement}</p>
    </StatusMessage>
  );
}
