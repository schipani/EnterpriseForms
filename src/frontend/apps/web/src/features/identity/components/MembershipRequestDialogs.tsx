import type { MembershipRequestResponse } from "@enterpriseforms/api-client";
import { presentError, useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, Checkbox, DescriptionList, Dialog, Select, StatusMessage, TextInput, errorTextClass } from "@enterpriseforms/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { providerName } from "./MemberTable";

const STALE_CODES = new Set(["MEMBERSHIP_REQUEST_CHANGED", "STALE_AUTHORIZATION_REVISION", "MEMBERSHIP_REQUEST_NOT_PENDING"]);

const date = (value: string | null | undefined) => (value ? new Date(value).toLocaleString() : "—");

export const REQUEST_STATUS_LABELS: Record<string, string> = {
  Pending: "Awaiting operators",
  Approved: "Approved",
  Rejected: "Rejected",
  Expired: "Expired",
  Cancelled: "Cancelled",
  Replaced: "Replaced",
};

export const requestStatusTone = (state: string) => (state === "Pending" ? "warning" : state === "Approved" ? "success" : state === "Rejected" ? "danger" : "neutral");

/** Names the problem when the request moved on since the list was read; the person reloads instead of overwriting. */
function StaleNotice({ error }: { error: unknown }) {
  return (
    <StatusMessage tone="warning" title="This request changed">
      {presentError(error).code === "MEMBERSHIP_REQUEST_NOT_PENDING"
        ? "The request is no longer pending, so it cannot be changed. Nothing was saved."
        : "Someone else changed, cancelled or replaced this request after you opened it. Nothing was saved; close this dialog and review the updated list."}
    </StatusMessage>
  );
}

function StepUp({ operational, stepUp, setStepUp, touched }: { operational: boolean; stepUp: boolean; setStepUp: (value: boolean) => void; touched: boolean }) {
  return operational ? (
    <>
      <Checkbox
        label="Confirm with the development-only step-up"
        checked={stepUp}
        onCheckedChange={(checked) => setStepUp(checked === true)}
        helpText="A synthetic stand-in for a passkey confirmation that exists only in local development and test."
      />
      {touched && !stepUp ? <p className={errorTextClass}>Confirm with the step-up to continue.</p> : null}
    </>
  ) : (
    <StatusMessage tone="info" title="Not available in this environment">
      Changing requests needs a fresh passkey confirmation (step-up), which is not available yet (PB-08).
    </StatusMessage>
  );
}

/**
 * Edit request (AC-7.5). Display name, initial role and the request reason can change; the verified identity (object ID, issuer) and the
 * organization are shown read-only and cannot be edited: a wrong identity is cancelled and requested again. Saving replaces the request
 * with a new one (new ID, fresh seven-day expiry, no operator approval carried over); nothing is granted.
 */
export function EditRequestDialog({
  request,
  operational,
  onClose,
  onDone,
}: {
  request: MembershipRequestResponse | null;
  operational: boolean;
  onClose: () => void;
  onDone: (message: string) => void;
}) {
  return request ? <EditRequestForm key={request.requestId + request.requestDigest} request={request} operational={operational} onClose={onClose} onDone={onDone} /> : null;
}

function EditRequestForm({ request, operational, onClose, onDone }: { request: MembershipRequestResponse; operational: boolean; onClose: () => void; onDone: (message: string) => void }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const [name, setName] = useState(request.displayNameHint);
  const [role, setRole] = useState(request.initialRoleName === "Designer" ? "Designer" : "none");
  const [reason, setReason] = useState(request.reason);
  const [stepUp, setStepUp] = useState(false);
  const [touched, setTouched] = useState(false);
  const mutation = useMutation({
    mutationFn: () =>
      api.organization
        .replaceMembershipRequest(request.requestId, {
          expectedRequestDigest: request.requestDigest,
          displayNameHint: name.trim(),
          initialRoleName: role === "Designer" ? "Designer" : null,
          reason: reason.trim(),
          useDevelopmentStepUp: stepUp,
        })
        .then((r) => r.result),
    onSuccess: async () => {
      onDone(`Request for ${name.trim()} updated. The edited request awaits two operator approvals and has a new seven-day expiry.`);
      onClose();
      await queryClient.invalidateQueries({ queryKey: ["organization"] });
    },
  });
  const nameProblem = name.trim().length === 0 ? "Enter the person's name." : null;
  const reasonProblem = reason.trim().length < 3 ? "Give a reason (3 to 500 characters)." : null;
  const changed = name.trim() !== request.displayNameHint || (role === "Designer" ? "Designer" : null) !== (request.initialRoleName ?? null) || reason.trim() !== request.reason;
  const stale = mutation.isError && STALE_CODES.has(presentError(mutation.error).code);
  return (
    <Dialog
      open
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={`Edit request: ${request.displayNameHint || request.subjectIdentifier}`}
      description="Change the display name, initial role or reason. Saving replaces this request with a new one that needs two operator approvals again."
      locked={mutation.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={mutation.isPending}>
            Close
          </Button>
          <Button
            busy={mutation.isPending}
            disabled={!operational || stale}
            onClick={() => {
              setTouched(true);
              if (!nameProblem && !reasonProblem && changed && stepUp) {
                mutation.mutate();
              }
            }}
          >
            Save changes
          </Button>
        </>
      }
    >
      <TextInput label="Identity provider" value={providerName(request.issuerUri)} readOnly helpText="Cannot be changed." />
      <TextInput
        label="Object ID"
        value={request.subjectIdentifier}
        readOnly
        spellCheck={false}
        helpText="The verified identity cannot be changed. If it is wrong, cancel this request and create a new one."
      />
      <TextInput
        label="Name"
        value={name}
        required
        maxLength={200}
        error={touched && nameProblem ? nameProblem : undefined}
        helpText="Shown in People until the person signs in. Display only."
        onChange={(event) => setName(event.target.value)}
      />
      <Select
        label="Initial role"
        value={role}
        options={[
          { value: "none", label: "No role" },
          { value: "Designer", label: "Designer" },
        ]}
        onValueChange={setRole}
      />
      <TextInput
        label="Reason"
        value={reason}
        required
        maxLength={500}
        error={touched && reasonProblem ? reasonProblem : undefined}
        helpText="Recorded with the request (3 to 500 characters)."
        onChange={(event) => setReason(event.target.value)}
      />
      {touched && !changed ? <p className={errorTextClass}>Change the name, role or reason, or close this dialog.</p> : null}
      <StepUp operational={operational} stepUp={stepUp} setStepUp={setStepUp} touched={touched} />
      {stale ? <StaleNotice error={mutation.error} /> : mutation.isError ? <ErrorPanel error={mutation.error} /> : null}
    </Dialog>
  );
}

/** Cancel request (AC-7.5): confirmation with a reason; a cancelled request can never be approved. Nothing is granted or removed. */
export function CancelRequestDialog({
  request,
  operational,
  onClose,
  onDone,
}: {
  request: MembershipRequestResponse | null;
  operational: boolean;
  onClose: () => void;
  onDone: (message: string) => void;
}) {
  return request ? <CancelRequestForm key={request.requestId + request.requestDigest} request={request} operational={operational} onClose={onClose} onDone={onDone} /> : null;
}

function CancelRequestForm({ request, operational, onClose, onDone }: { request: MembershipRequestResponse; operational: boolean; onClose: () => void; onDone: (message: string) => void }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState("");
  const [stepUp, setStepUp] = useState(false);
  const [touched, setTouched] = useState(false);
  const name = request.displayNameHint || request.subjectIdentifier;
  const mutation = useMutation({
    mutationFn: () =>
      api.organization.cancelMembershipRequest(request.requestId, { expectedRequestDigest: request.requestDigest, reason: reason.trim(), useDevelopmentStepUp: stepUp }).then((r) => r.result),
    onSuccess: async () => {
      onDone(`Request for ${name} cancelled. It can no longer be approved; you can create a new request for the same person.`);
      onClose();
      await queryClient.invalidateQueries({ queryKey: ["organization"] });
    },
  });
  const stale = mutation.isError && STALE_CODES.has(presentError(mutation.error).code);
  return (
    <Dialog
      open
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={`Cancel request: ${name}`}
      description="The request stays in the history as Cancelled and can never be approved. Nothing was granted, so nothing is removed."
      locked={mutation.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={mutation.isPending}>
            Keep request
          </Button>
          <Button
            variant="danger"
            busy={mutation.isPending}
            disabled={!operational || stale}
            onClick={() => {
              setTouched(true);
              if (reason.trim().length >= 3 && stepUp) {
                mutation.mutate();
              }
            }}
          >
            Cancel request
          </Button>
        </>
      }
    >
      <TextInput
        label="Reason"
        value={reason}
        required
        maxLength={500}
        error={touched && reason.trim().length < 3 ? "Give a reason (3 to 500 characters)." : undefined}
        helpText="Recorded with the cancellation (3 to 500 characters)."
        onChange={(event) => setReason(event.target.value)}
      />
      <StepUp operational={operational} stepUp={stepUp} setStepUp={setStepUp} touched={touched} />
      {stale ? <StaleNotice error={mutation.error} /> : mutation.isError ? <ErrorPanel error={mutation.error} /> : null}
    </Dialog>
  );
}

/** Read-only details of a request in any status, including its replacement lineage. */
export function RequestDetailsDialog({ request, onClose }: { request: MembershipRequestResponse | null; onClose: () => void }) {
  return (
    <Dialog
      open={request !== null}
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={request ? `Request details: ${request.displayNameHint || request.subjectIdentifier}` : ""}
      footer={
        <Button variant="secondary" onClick={onClose}>
          Close
        </Button>
      }
    >
      {request ? (
        <DescriptionList
          items={[
            { term: "Status", detail: <Badge tone={requestStatusTone(request.requestState)}>{REQUEST_STATUS_LABELS[request.requestState] ?? request.requestState}</Badge> },
            { term: "Name", detail: request.displayNameHint },
            { term: "Object ID", detail: <span className="break-all">{request.subjectIdentifier}</span> },
            { term: "Identity provider", detail: <span title={request.issuerUri}>{providerName(request.issuerUri)}</span> },
            { term: "Initial role", detail: request.initialRoleName ?? "None" },
            { term: "Requested by", detail: request.requestedByDisplayName },
            { term: "Reason", detail: request.reason },
            { term: "Requested", detail: date(request.requestedOn) },
            { term: "Expires", detail: date(request.expiresOn) },
            ...(request.cancelledOn ? [{ term: request.requestState === "Replaced" ? "Replaced" : "Cancelled", detail: date(request.cancelledOn) }] : []),
            ...(request.decidedOn && !request.cancelledOn ? [{ term: "Decided", detail: `${date(request.decidedOn)}${request.decisionReason ? ` — ${request.decisionReason}` : ""}` }] : []),
            ...(request.replacesRequestId ? [{ term: "Replaces", detail: <span className="break-all">An earlier request ({request.replacesRequestId})</span> }] : []),
            ...(request.replacedByRequestId ? [{ term: "Replaced by", detail: <span className="break-all">A newer request ({request.replacedByRequestId})</span> }] : []),
            { term: "Request ID", detail: <span className="break-all">{request.requestId}</span> },
          ]}
        />
      ) : null}
    </Dialog>
  );
}
