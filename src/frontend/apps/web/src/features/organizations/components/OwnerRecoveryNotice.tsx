import type { OwnerRecoveryNoticeCaseResponse } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import { Button, Card, Checkbox, StatusMessage, TextArea, getActiveFormatter, useFormatter } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";

export const ownerRecoveryNoticeKey = ["organization", "owner-recovery-notice"];

const REASON_MIN = 10;
const REASON_MAX = 500;

const names = (c: OwnerRecoveryNoticeCaseResponse, role: string) => c.subjects.filter((s) => s.role === role).map((s) => s.displayName);

/**
 * Owner recovery notice (C-105). Platform Operators can open a recovery case when an organization has lost its owners; the
 * Authority tells the organization's remaining owners and Security Administrators here, and an owner can object, which blocks
 * the recovery. The page renders nothing unless the server returns a case: callers who may not see it learn nothing. Names only;
 * never evidence summaries or record content. A confirmation appears only after the server has recorded the objection.
 */
export function OwnerRecoveryNotice() {
  useFormatter();
  const api = useApi();
  const queryClient = useQueryClient();
  const notice = useQuery({ queryKey: ownerRecoveryNoticeKey, queryFn: ({ signal }) => api.organization.ownerRecoveryNotice(signal).then((r) => r.result) });
  const [objecting, setObjecting] = useState(false);
  const [reason, setReason] = useState("");
  const [stepUp, setStepUp] = useState(false);
  // The reference of the case this person objected to: the refreshed notice no longer has an open case, but the confirmation stays.
  const [confirmed, setConfirmed] = useState<string | null>(null);
  const objection = useMutation({
    mutationFn: (caseId: string) => api.organization.objectToOwnerRecovery(caseId, { reason: reason.trim(), useDevelopmentStepUp: stepUp }).then((r) => r.result),
    onSuccess: async (result, caseId) => {
      setConfirmed(notice.data?.case?.caseId === caseId ? notice.data.case.incidentReference : result.caseId);
      setObjecting(false);
      setReason("");
      setStepUp(false);
      await queryClient.invalidateQueries({ queryKey: ownerRecoveryNoticeKey });
    },
  });

  const data = notice.data;
  const recoveryCase = data?.case;
  if (confirmed !== null && !recoveryCase) {
    return (
      <Card title="Owner recovery case" aria-label="Owner recovery case">
        <StatusMessage tone="success" title="Objection recorded">
          Your objection was recorded. The recovery is blocked. (Case {confirmed}.)
        </StatusMessage>
      </Card>
    );
  }

  if (!recoveryCase || !data) {
    // Hidden entirely for everyone the server does not show a case to; a read failure must not reveal that a case exists either.
    return null;
  }

  const trimmed = reason.trim();
  const reasonError = reason.length > 0 && (trimmed.length < REASON_MIN || trimmed.length > REASON_MAX) ? `Enter ${REASON_MIN} to ${REASON_MAX} characters.` : undefined;
  const reasonValid = trimmed.length >= REASON_MIN && trimmed.length <= REASON_MAX;
  const replacements = names(recoveryCase, "Replacement");
  const unavailable = names(recoveryCase, "DeclaredUnavailable");

  return (
    <Card title="Owner recovery case in progress" aria-label="Owner recovery case in progress">
      <p className="m-0 text-sm">
        Platform Operators opened a case to restore owners to this organization. If you do not recognise it, object: an objection blocks the recovery, and operators must investigate before any new case.
      </p>
      <dl className="m-0 grid grid-cols-[max-content_minmax(0,1fr)] gap-x-4 gap-y-1 text-sm">
        <dt className="text-muted">Reference</dt>
        <dd className="m-0 break-words">{recoveryCase.incidentReference}</dd>
        <dt className="text-muted">Justification</dt>
        <dd className="m-0 whitespace-pre-wrap break-words">{recoveryCase.justification}</dd>
        <dt className="text-muted">Waiting period ends</dt>
        <dd className="m-0">{getActiveFormatter().instant(recoveryCase.waitingPeriodEndsOn)}</dd>
        <dt className="text-muted">Operator approvals</dt>
        <dd className="m-0">
          {Number(recoveryCase.approvalsRecorded)} of {Number(recoveryCase.approvalsRequired)}
        </dd>
        <dt className="text-muted">Proposed new owners</dt>
        <dd className="m-0 break-words">{replacements.length > 0 ? replacements.join(", ") : "None"}</dd>
        <dt className="text-muted">Owners declared unavailable</dt>
        <dd className="m-0 break-words">{unavailable.length > 0 ? unavailable.join(", ") : "None"}</dd>
      </dl>
      {confirmed !== null ? (
        <StatusMessage tone="success" title="Objection recorded">
          Your objection was recorded. The recovery is blocked.
        </StatusMessage>
      ) : null}
      {confirmed !== null ? null : data.canObject ? (
        objecting ? (
          <form
            className="flex flex-col gap-3"
            noValidate
            onSubmit={(event) => {
              event.preventDefault();
              if (reasonValid && stepUp) {
                objection.mutate(recoveryCase.caseId);
              }
            }}
          >
            <TextArea
              label="Why are you objecting?"
              required
              rows={3}
              value={reason}
              maxLength={REASON_MAX + 100}
              onChange={(event) => setReason(event.target.value)}
              error={reasonError}
              helpText={`${trimmed.length} of ${REASON_MAX} characters. Recorded with your name and shown to the Platform Operators.`}
            />
            <Checkbox
              label="Confirm with the development-only step-up"
              checked={stepUp}
              onCheckedChange={(checked) => setStepUp(checked === true)}
              helpText="A synthetic stand-in for a passkey confirmation that exists only in local development and test. It proves nothing about the person and can never be enabled in staging or production."
            />
            {objection.isError ? <ErrorPanel error={objection.error} /> : null}
            <div className="flex flex-wrap gap-2">
              <Button type="submit" variant="danger" busy={objection.isPending} disabled={!reasonValid || !stepUp}>
                Record objection
              </Button>
              <Button
                type="button"
                variant="secondary"
                disabled={objection.isPending}
                onClick={() => {
                  setObjecting(false);
                  objection.reset();
                }}
              >
                Cancel
              </Button>
            </div>
          </form>
        ) : (
          <Button variant="danger" className="self-start" onClick={() => setObjecting(true)}>
            Object to this recovery
          </Button>
        )
      ) : (
        <StatusMessage tone="info" title="Objections are not available here">
          An objection needs a fresh step-up confirmation, which this environment does not provide. Contact the Platform Operators through your
          support channel.
        </StatusMessage>
      )}
    </Card>
  );
}
