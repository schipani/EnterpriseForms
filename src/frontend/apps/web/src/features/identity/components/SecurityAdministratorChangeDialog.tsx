import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Button, Checkbox, Dialog, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { memberName, type MemberAction } from "./MemberTable";

/**
 * Appoint or remove a Security Administrator from the People page. Uses the existing Authority commands (C-70) and their
 * rules unchanged: only a Security Administrator may request it, a reason is recorded, the step-up confirmation is the
 * development-only stand-in (PB-08 is not available), and the Authority refuses anything its rules do not allow.
 */
export function SecurityAdministratorChangeDialog({
  change,
  operational,
  unavailableReason,
  onClose,
  onDone,
}: {
  change: { member: OrganizationMemberResponse; action: MemberAction } | null;
  operational: boolean;
  unavailableReason: string | null | undefined;
  onClose: () => void;
  onDone: (message: string) => void;
}) {
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState("");
  const [stepUp, setStepUp] = useState(false);
  const appointing = change?.action === "AppointSecurityAdministrator";
  const name = change ? memberName(change.member) : "";
  const mutation = useMutation({
    mutationFn: async () => {
      if (!change) {
        return;
      }
      if (appointing) {
        await api.organization.appointSecurityAdministrator({
          userId: change.member.userId,
          reason: reason.trim(),
          useDevelopmentStepUp: stepUp,
        });
      } else {
        await api.organization.removeSecurityAdministrator(change.member.userId, { reason: reason.trim(), useDevelopmentStepUp: stepUp });
      }
    },
    onSuccess: async () => {
      onDone(appointing ? `${name} is now a Security Administrator.` : `${name} is no longer a Security Administrator.`);
      close();
      await queryClient.invalidateQueries({ queryKey: ["organization"] });
      await session.refresh();
    },
  });
  const close = () => {
    setReason("");
    setStepUp(false);
    mutation.reset();
    onClose();
  };
  const reasonValid = reason.trim().length >= 3;
  return (
    <Dialog
      open={change !== null}
      onOpenChange={(open) => (open ? undefined : close())}
      title={appointing ? `Make ${name} a Security Administrator` : `Remove ${name} as Security Administrator`}
      description={
        appointing
          ? "Security Administrators approve which plaintext fields can be searched and who may search them. The role gives no access to records, no Form Designer rights and no control over the encryption policy. The appointment is a recorded Authorization Authority command."
          : "They lose search-approval administration on their next request. Nothing else about their account changes. The removal is a recorded Authorization Authority command."
      }
      locked={mutation.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={close} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button variant={appointing ? "primary" : "danger"} busy={mutation.isPending} disabled={!reasonValid || !operational || !stepUp} onClick={() => mutation.mutate()}>
            {appointing ? "Appoint" : "Remove administrator"}
          </Button>
        </>
      }
    >
      <TextInput label="Reason" value={reason} required maxLength={500} helpText="Recorded with the change (3 to 500 characters)." onChange={(event) => setReason(event.target.value)} />
      {operational ? (
        <Checkbox
          label="Confirm with the development-only step-up"
          checked={stepUp}
          onCheckedChange={(checked) => setStepUp(checked === true)}
          helpText="A synthetic stand-in for a passkey confirmation that exists only in local development and test. It proves nothing about the person and can never be enabled in staging or production."
        />
      ) : (
        <StatusMessage tone="info" title="Not available in this environment">
          {unavailableReason ||
            "Changing Security Administrators needs a fresh passkey confirmation (step-up), which is not available yet (PB-08). The Authorization Authority refuses every change here."}
        </StatusMessage>
      )}
      {mutation.isError ? <ErrorPanel error={mutation.error} /> : null}
    </Dialog>
  );
}
