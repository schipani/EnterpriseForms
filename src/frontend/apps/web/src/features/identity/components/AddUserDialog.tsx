import { useApi } from "@enterpriseforms/app-runtime";
import { Button, Checkbox, Dialog, Select, StatusMessage, TextInput, errorTextClass } from "@enterpriseforms/ui";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";

const ENTRA_ISSUER = /^https:\/\/(login\.microsoftonline\.com|sts\.windows\.net)\//i;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const SUBJECT = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;

/** Same rule as the Authority: the identity provider's immutable subject (a GUID object id for Microsoft Entra), never an email. */
export function subjectProblem(issuer: string, subject: string): string | null {
  const value = subject.trim();
  if (value.length === 0) {
    return "Enter the person's identity.";
  }
  if (value.includes("@")) {
    return "Use the person's object ID, not an email address.";
  }
  if (ENTRA_ISSUER.test(issuer)) {
    return GUID.test(value) ? null : "Enter the Microsoft Entra object ID (a GUID such as 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0).";
  }
  return SUBJECT.test(value) ? null : "Use letters, digits and . _ : - only.";
}

/**
 * Add user (C8 M2, AC-7.4). Records an owner's request for a person of the organization's identity provider tenant; it
 * grants nothing. Two operator approvals (development and test environments only, until production privileged access
 * exists) create the membership. Membership is not sign-in: a real Microsoft Entra account can sign in only once the Entra
 * integration (PB-08) exists. No password is ever created.
 */
export function AddUserDialog({
  open,
  issuer,
  operational,
  operatorApprovalOperational,
  initial,
  onClose,
  onDone,
}: {
  open: boolean;
  issuer: string | null | undefined;
  operational: boolean;
  operatorApprovalOperational: boolean;
  /** Pre-filled values for "Create new request" after a rejected, expired or cancelled request (still editable; a new request, nothing is carried over). */
  initial?: { subject: string; name: string; role: string | null } | undefined;
  onClose: () => void;
  onDone: (message: string) => void;
}) {
  const api = useApi();
  const queryClient = useQueryClient();
  const [subject, setSubject] = useState(initial?.subject ?? "");
  const [name, setName] = useState(initial?.name ?? "");
  const [role, setRole] = useState(initial?.role === "Designer" ? "Designer" : "none");
  const [reason, setReason] = useState("");
  const [stepUp, setStepUp] = useState(false);
  const [touched, setTouched] = useState(false);
  const mutation = useMutation({
    mutationFn: () =>
      api.organization
        .requestMembership({
          subjectIdentifier: subject.trim(),
          displayNameHint: name.trim(),
          initialRoleName: role === "Designer" ? "Designer" : null,
          reason: reason.trim(),
          useDevelopmentStepUp: stepUp,
        })
        .then((r) => r.result),
    onSuccess: async (outcome) => {
      onDone(`Request recorded for ${name.trim()}. It needs two operator approvals before ${new Date(outcome.expiresOn ?? Date.now()).toLocaleDateString()}; until then the person is not a member.`);
      close();
      await queryClient.invalidateQueries({ queryKey: ["organization"] });
    },
  });
  const close = () => {
    setSubject(initial?.subject ?? "");
    setName(initial?.name ?? "");
    setRole(initial?.role === "Designer" ? "Designer" : "none");
    setReason("");
    setStepUp(false);
    setTouched(false);
    mutation.reset();
    onClose();
  };
  const problem = subjectProblem(issuer ?? "", subject);
  const nameProblem = name.trim().length === 0 ? "Enter the person's name." : null;
  const ready = !problem && !nameProblem && reason.trim().length >= 3 && operational && stepUp && !!issuer;
  return (
    <Dialog
      open={open}
      onOpenChange={(next) => (next ? undefined : close())}
      title="Add user"
      description="Request membership for a person of your organization's identity provider. Nothing is granted until two operators approve the request."
      locked={mutation.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={close} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button
            busy={mutation.isPending}
            disabled={!operational || !issuer}
            onClick={() => {
              setTouched(true);
              if (ready) {
                mutation.mutate();
              }
            }}
          >
            Request membership
          </Button>
        </>
      }
    >
      <TextInput label="Identity provider" value={issuer ?? "Not configured"} readOnly helpText="The organization's configured Microsoft Entra tenant. People from other tenants cannot be added." />
      <TextInput
        label="Object ID"
        value={subject}
        required
        autoComplete="off"
        spellCheck={false}
        helpText="The person's immutable Microsoft Entra object ID. Email addresses are not identifiers."
        error={touched && problem ? problem : undefined}
        onChange={(event) => setSubject(event.target.value)}
      />
      <TextInput
        label="Name"
        value={name}
        required
        maxLength={200}
        helpText="Shown in People until the person signs in. Display only."
        error={touched && nameProblem ? nameProblem : undefined}
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
        helpText="Recorded with the request (3 to 500 characters)."
        error={touched && reason.trim().length < 3 ? "Give a reason." : undefined}
        onChange={(event) => setReason(event.target.value)}
      />
      <StatusMessage tone="info" title="Membership is not sign-in">
        Approval makes the person a member of this organization. They can sign in only through Microsoft Entra once that integration (PB-08) is available; no password is ever created.
        {operatorApprovalOperational ? " Operator approval is available in this environment." : " Operator approval is not available in this environment, so the request will wait."}
      </StatusMessage>
      {operational ? (
        <Checkbox
          label="Confirm with the development-only step-up"
          checked={stepUp}
          onCheckedChange={(checked) => setStepUp(checked === true)}
          helpText="A synthetic stand-in for a passkey confirmation that exists only in local development and test."
        />
      ) : (
        <StatusMessage tone="info" title="Not available in this environment">
          Adding people needs a fresh passkey confirmation (step-up), which is not available yet (PB-08).
        </StatusMessage>
      )}
      {touched && operational && !stepUp ? <p className={errorTextClass}>Confirm with the step-up to continue.</p> : null}
      {mutation.isError ? <ErrorPanel error={mutation.error} /> : null}
    </Dialog>
  );
}
