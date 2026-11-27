import type { MemberCommandRequest, OrganizationMemberResponse, OwnerProposalResponse } from "@enterpriseforms/api-client";
import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Button, Checkbox, Dialog, FieldShell, StatusMessage, TextArea, TextInput, getActiveFormatter, inputClass, useFormatter } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
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
          "They are signed out everywhere at once and cannot use the organization. Their roles, teams and the shares they hold are kept but unusable, and come back if you re-enable them. Shares they delegated to other people end permanently and do not come back. People who still own records must have those records reassigned first.",
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
    case "AppointSecurityAuditor":
      return {
        title: `Make ${name} a Security Auditor`,
        description: "Security Auditors can review the organization's security audit trail. The role gives no access to records, no Form Designer rights and no administration rights.",
        confirm: "Appoint",
        danger: false,
      };
    case "RemoveSecurityAuditor":
      return {
        title: `Remove ${name} as Security Auditor`,
        description: "They lose access to the security audit trail on their next request. Nothing else about their account changes.",
        confirm: "Remove auditor",
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

const NOTE_LIMIT = 500;
const AUDITOR_NOTE_HELP = "Encrypted before it is sent and kept only as ciphertext with the command. It cannot be read back in this application, is never shown to Security Auditors and is not stored in this application's audit log.";

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
  useFormatter();
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState("");
  const [stepUp, setStepUp] = useState(false);
  const [reasonCode, setReasonCode] = useState("");
  const [reasonTouched, setReasonTouched] = useState(false);
  const [note, setNote] = useState("");
  const auditorAction = change?.kind === "member" && (change.action === "AppointSecurityAuditor" || change.action === "RemoveSecurityAuditor") ? change.action : null;
  const reasonCodes = useQuery({
    queryKey: ["organization", "security-auditors", "reason-codes"],
    queryFn: async ({ signal }) => (await api.organization.securityAuditorReasonCodes(signal)).result,
    enabled: auditorAction !== null,
  });
  const reasonOptions = (auditorAction === "RemoveSecurityAuditor" ? reasonCodes.data?.removal : reasonCodes.data?.appointment) ?? [];
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
        case "AppointSecurityAuditor":
          await api.organization.appointSecurityAuditor({ userId: member.userId, reasonCode, reasonNote: note.trim() || null, useDevelopmentStepUp: stepUp });
          return `${name} is now a Security Auditor.`;
        case "RemoveSecurityAuditor":
          await api.organization.removeSecurityAuditor(member.userId, { reasonCode, reasonNote: note.trim() || null, useDevelopmentStepUp: stepUp });
          return `${name} is no longer a Security Auditor.`;
        default: {
          const proposal = (await api.organization.proposeOwnerChange(member.userId, { change: PROPOSALS[action]!, ...request })).result;
          return `Proposal recorded for ${name}. Another organization owner must approve it before ${getActiveFormatter().instant(proposal.expiresOn ?? Date.now())}.`;
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
    setReasonCode("");
    setReasonTouched(false);
    setNote("");
    mutation.reset();
    onClose();
  };
  const text = change ? describe(change) : null;
  const reasonValid = auditorAction ? reasonCode !== "" : reason.trim().length >= 3;
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
      {auditorAction ? (
        <>
          <FieldShell
            label="Reason"
            required
            helpText="Choose the reason recorded with this change."
            error={reasonCodes.isError ? "The reasons could not be loaded. Close this dialog and try again." : reasonTouched && reasonCode === "" ? "Choose a reason to continue." : undefined}
          >
            {({ inputId, describedBy, invalid }) => (
              <select
                id={inputId}
                aria-describedby={describedBy}
                aria-invalid={invalid || undefined}
                aria-required="true"
                required
                className={inputClass}
                value={reasonCode}
                onChange={(event) => setReasonCode(event.target.value)}
                onBlur={() => setReasonTouched(true)}
              >
                <option value="">Select a reason</option>
                {reasonOptions.map((option) => (
                  <option key={option.reasonCode} value={option.reasonCode}>
                    {option.reasonLabel}
                  </option>
                ))}
              </select>
            )}
          </FieldShell>
          <TextArea
            label="Note (optional)"
            value={note}
            maxLength={NOTE_LIMIT}
            rows={3}
            helpText={AUDITOR_NOTE_HELP}
            note={`${note.length} / ${NOTE_LIMIT}`}
            onChange={(event) => setNote(event.target.value.slice(0, NOTE_LIMIT))}
          />
        </>
      ) : (
        <TextInput
          label="Reason"
          value={reason}
          required
          maxLength={500}
          helpText="Recorded with the change by the Authorization Authority (3 to 500 characters)."
          onChange={(event) => setReason(event.target.value)}
        />
      )}
      {change?.kind === "member" && change.action === "DisableMembership" ? (
        <p className="text-sm">
          <Link to="/app/members/$memberUserId/reassign" params={{ memberUserId: change.member.userId }}>
            Reassign {memberName(change.member)}'s records
          </Link>
        </p>
      ) : null}
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
