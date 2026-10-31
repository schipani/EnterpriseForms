import type { MemberCommandRequest, OrganizationMemberResponse, OwnerProposalResponse } from "@enterpriseforms/api-client";
import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Button, Checkbox, Dialog, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { MEMBER_ACTION_LABELS, memberName, type MemberAction } from "./MemberTable";

export type MemberChange = { kind: "member"; member: OrganizationMemberResponse; action: MemberAction } | { kind: "proposal"; proposal: OwnerProposalResponse };

const CHANGE_LABELS: Record<string, string> = {
  GrantOwner: "make them an Organization Owner",
  RevokeOwner: "remove their Organization Owner role",
  DisableOwner: "disable their membership",
  EnableOwner: "re-enable their membership",
};

const PROPOSALS: Partial<Record<MemberAction, string>> = {
  ProposeOwnerGrant: "GrantOwner",
  ProposeOwnerRevoke: "RevokeOwner",
  ProposeOwnerDisable: "DisableOwner",
  ProposeOwnerEnable: "EnableOwner",
};

function describe(change: MemberChange): { title: string; description: string; confirm: string; danger: boolean } {
  if (change.kind === "proposal") {
    const p = change.proposal;
    return {
      title: `Approve: ${CHANGE_LABELS[p.proposedChange] ?? p.proposedChange} (${p.targetDisplayName})`,
      description: `${p.proposedByDisplayName} proposed this; your approval as a second organization owner carries it out now. The Authorization Authority re-checks the proposal, the proposer and every rule at this moment.`,
      confirm: "Approve and apply",
      danger: p.proposedChange === "RevokeOwner" || p.proposedChange === "DisableOwner",
    };
  }
  const name = memberName(change.member);
  switch (change.action) {
    case "GrantDesigner":
      return { title: `Make ${name} a designer`, description: "Designers can create and design forms. The role gives no access to records.", confirm: "Make designer", danger: false };
    case "RevokeDesigner":
      return {
        title: `Remove ${name}'s designer role`,
        description: "They can no longer create forms. Forms they were given design rights to keep those rights.",
        confirm: "Remove designer role",
        danger: true,
      };
    case "DisableMembership":
      return {
        title: `Disable ${name}'s membership`,
        description:
          "They are signed out everywhere at once and cannot use the organization. Their roles, teams and shares are kept but unusable, and come back if you re-enable them. People who still own records must have those records transferred first.",
        confirm: "Disable membership",
        danger: true,
      };
    case "EnableMembership":
      return {
        title: `Re-enable ${name}'s membership`,
        description: "Their kept roles, teams and shares become usable again. Nothing new is granted; they sign in again.",
        confirm: "Re-enable membership",
        danger: false,
      };
    case "AppointSecurityAdministrator":
      return {
        title: `Make ${name} a Security Administrator`,
        description:
          "Security Administrators approve which plaintext fields can be searched and who may search them. The role gives no access to records, no Form Designer rights and no control over the encryption policy.",
        confirm: "Appoint",
        danger: false,
      };
    case "RemoveSecurityAdministrator":
      return {
        title: `Remove ${name} as Security Administrator`,
        description: "They lose search-approval administration on their next request. Nothing else about their account changes.",
        confirm: "Remove administrator",
        danger: true,
      };
    default:
      return {
        title: `Propose: ${CHANGE_LABELS[PROPOSALS[change.action] ?? ""] ?? MEMBER_ACTION_LABELS[change.action]} (${name})`,
        description: "Changes to an Organization Owner need two owners. This records your proposal only; nothing changes until a different owner approves it within 24 hours.",
        confirm: "Record proposal",
        danger: false,
      };
  }
}

/**
 * One dialog for every People change (C8 M2): reason (recorded with the Authority command), the development-only step-up,
 * and the server's answer. Pending is never shown as done; refusals show the Authority's reason (for example a record
 * count when the person still owns records).
 */
export function MemberChangeDialog({
  change,
  operational,
  unavailableReason,
  onClose,
  onDone,
}: {
  change: MemberChange | null;
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
  const mutation = useMutation({
    mutationFn: async (target: MemberChange): Promise<string> => {
      const request: MemberCommandRequest = { reason: reason.trim(), useDevelopmentStepUp: stepUp };
      if (target.kind === "proposal") {
        await api.organization.approveOwnerProposal(target.proposal.proposalId, request);
        return `Approved: ${CHANGE_LABELS[target.proposal.proposedChange] ?? target.proposal.proposedChange} for ${target.proposal.targetDisplayName}.`;
      }
      const { member, action } = target;
      const name = memberName(member);
      switch (action) {
        case "GrantDesigner":
          await api.organization.grantMemberRole(member.userId, { roleName: "Designer", ...request });
          return `${name} is now a designer.`;
        case "RevokeDesigner":
          await api.organization.revokeMemberRole(member.userId, "Designer", request);
          return `${name} is no longer a designer.`;
        case "DisableMembership":
          await api.organization.disableMember(member.userId, request);
          return `${name}'s membership is disabled and their sessions were ended.`;
        case "EnableMembership":
          await api.organization.enableMember(member.userId, request);
          return `${name}'s membership is active again.`;
        case "AppointSecurityAdministrator":
          await api.organization.appointSecurityAdministrator({ userId: member.userId, reason: request.reason, useDevelopmentStepUp: stepUp });
          return `${name} is now a Security Administrator.`;
        case "RemoveSecurityAdministrator":
          await api.organization.removeSecurityAdministrator(member.userId, { reason: request.reason, useDevelopmentStepUp: stepUp });
          return `${name} is no longer a Security Administrator.`;
        default: {
          const proposal = (await api.organization.proposeOwnerChange(member.userId, { change: PROPOSALS[action]!, ...request })).result;
          return `Proposal recorded for ${name}. Another organization owner must approve it before ${new Date(proposal.expiresOn ?? Date.now()).toLocaleString()}.`;
        }
      }
    },
    onSuccess: async (message) => {
      onDone(message);
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
  const text = change ? describe(change) : null;
  const reasonValid = reason.trim().length >= 3;
  return (
    <Dialog
      open={change !== null}
      onOpenChange={(open) => (open ? undefined : close())}
      title={text?.title ?? ""}
      description={text?.description}
      locked={mutation.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={close} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button variant={text?.danger ? "danger" : "primary"} busy={mutation.isPending} disabled={!reasonValid || !operational || !stepUp} onClick={() => change && mutation.mutate(change)}>
            {text?.confirm ?? "Confirm"}
          </Button>
        </>
      }
    >
      <TextInput
        label="Reason"
        value={reason}
        required
        maxLength={500}
        helpText="Recorded with the change by the Authorization Authority (3 to 500 characters)."
        onChange={(event) => setReason(event.target.value)}
      />
      {operational ? (
        <Checkbox
          label="Confirm with the development-only step-up"
          checked={stepUp}
          onCheckedChange={(checked) => setStepUp(checked === true)}
          helpText="A synthetic stand-in for a passkey confirmation that exists only in local development and test. It proves nothing about the person and can never be enabled in staging or production."
        />
      ) : (
        <StatusMessage tone="info" title="Not available in this environment">
          {unavailableReason === "PB08_STEP_UP_NOT_AVAILABLE" || !unavailableReason
            ? "Membership and role changes need a fresh passkey confirmation (step-up), which is not available yet (PB-08). The Authorization Authority refuses every change here."
            : unavailableReason}
        </StatusMessage>
      )}
      {mutation.isError ? <ErrorPanel error={mutation.error} /> : null}
    </Dialog>
  );
}
