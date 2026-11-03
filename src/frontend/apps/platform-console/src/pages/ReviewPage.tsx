import { Badge, Button, Card, Checkbox, DescriptionList, Dialog, LoadingBlock, StatusMessage, TextArea } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { consoleApi, ConsoleApiError, type DecisionKind, type DecisionResponse, type RequestDetail } from "../api/client";
import { describeError } from "../app/errors";
import { approvalChip, formatDateTime, identitySummary, roleLabel } from "../app/format";
import { navigate } from "../app/router";
import { useConsoleSession } from "../app/session";

const MIN_JUSTIFICATION = 10;
const MAX_JUSTIFICATION = 500;

const verbs: Record<DecisionKind, { action: string; title: string; confirm: string; variant: "primary" | "danger" | "secondary" }> = {
  approve: { action: "Approve", title: "Approve this membership request", confirm: "Record my approval", variant: "primary" },
  reject: { action: "Reject", title: "Reject this membership request", confirm: "Reject request", variant: "danger" },
  withdraw: { action: "Withdraw my approval", title: "Withdraw your approval", confirm: "Withdraw my approval", variant: "secondary" },
};

/** What the Authority reported, in the words of the screen. Only a confirmed response produces a success message. */
export function successMessage(kind: DecisionKind, response: DecisionResponse): string {
  const recorded = Number(response.approvalsRecorded);
  const required = Number(response.approvalsRequired);
  if (kind === "approve") {
    return response.outcome === "Approved" ? `Membership approved — ${required} of ${required}` : `Approval recorded — ${recorded} of ${required}`;
  }
  if (kind === "withdraw") {
    return "Approval withdrawn";
  }
  return "Request rejected";
}

/** Review details: the operator reads the Authority's current request and statement fingerprint, then decides through the BFF. No evidence is built here. */
export function ReviewPage({ organizationId, requestId }: { organizationId: string; requestId: string }) {
  const { touch, handleError } = useConsoleSession();
  // Every view of the details is audited by the Authority, so the page fetches once per visit and again only on an explicit reload or after a decision.
  const detail = useQuery({ queryKey: ["detail", organizationId, requestId], queryFn: () => consoleApi.detail(organizationId, requestId), staleTime: Infinity, gcTime: 0 });
  const [justification, setJustification] = useState("");
  const [dialog, setDialog] = useState<DecisionKind | null>(null);
  const [reviewed, setReviewed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ReturnType<typeof describeError> | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    if (detail.isSuccess) {
      touch();
    }
  }, [detail.isSuccess, detail.dataUpdatedAt, touch]);
  useEffect(() => {
    if (detail.isError) {
      handleError(detail.error);
    }
  }, [detail.isError, detail.error, handleError]);

  const data = detail.data;
  const trimmed = justification.trim();
  const justificationError = justification.length > 0 && (trimmed.length < MIN_JUSTIFICATION || trimmed.length > MAX_JUSTIFICATION) ? `Enter ${MIN_JUSTIFICATION} to ${MAX_JUSTIFICATION} characters.` : undefined;
  const justificationOk = trimmed.length >= MIN_JUSTIFICATION && trimmed.length <= MAX_JUSTIFICATION;

  const reload = async () => {
    setFailure(null);
    await detail.refetch();
  };

  const open = (kind: DecisionKind) => {
    setFailure(null);
    setSuccess(null);
    setReviewed(false);
    setDialog(kind);
  };

  const submit = async () => {
    if (!dialog || !data) {
      return;
    }
    const kind = dialog;
    setBusy(true);
    setFailure(null);
    try {
      const response = await consoleApi.decide(kind, organizationId, requestId, { expectedStatementSha256: data.statementSha256, justification: trimmed });
      touch();
      setDialog(null);
      setJustification("");
      setSuccess(successMessage(kind, response));
      await detail.refetch();
    } catch (error) {
      setDialog(null);
      if (!handleError(error)) {
        setFailure(describeError(error));
        if (error instanceof ConsoleApiError && error.status === 409) {
          // Changed, expired, not pending: show the current request, never the stale one.
          void detail.refetch();
        }
      }
    } finally {
      setBusy(false);
    }
  };

  const loadFailure = detail.isError ? describeError(detail.error) : null;
  return (
    <div className="flex flex-col gap-4">
      <div>
        <Button variant="ghost" size="sm" onClick={() => navigate("/platform/approvals")}>
          ← Back to operator approvals
        </Button>
        <h1 ref={headingRef} tabIndex={-1} className="mt-1 text-2xl font-semibold">
          Review request
        </h1>
      </div>
      {detail.isLoading ? <LoadingBlock label="Loading request" /> : null}
      {loadFailure ? (
        <StatusMessage
          tone={loadFailure.tone}
          title={loadFailure.title}
          actions={
            loadFailure.retryable || loadFailure.reload ? (
              <Button variant="secondary" size="sm" onClick={() => void reload()}>
                Reload request
              </Button>
            ) : undefined
          }
        >
          {loadFailure.message}
          {loadFailure.reference ? ` Reference ${loadFailure.reference}.` : ""}
        </StatusMessage>
      ) : null}
      {success ? (
        <div role="status" aria-live="polite" className="rounded-control border-l-4 border-[#166534] bg-[#f0fdf4] px-4 py-3 font-semibold text-[#14532d]">
          {success}
        </div>
      ) : null}
      {failure ? (
        <StatusMessage
          tone={failure.tone}
          title={failure.title}
          actions={
            failure.reload || failure.retryable ? (
              <Button variant="secondary" size="sm" onClick={() => void reload()}>
                Reload request
              </Button>
            ) : undefined
          }
        >
          {failure.message}
          {failure.reference ? ` Reference ${failure.reference}.` : ""}
        </StatusMessage>
      ) : null}
      {data ? <ReviewBody data={data} justification={justification} onJustification={setJustification} justificationError={justificationError} justificationOk={justificationOk} onDecide={open} onReload={() => void reload()} reloading={detail.isFetching} /> : null}
      {data && dialog ? (
        <Dialog
          open
          onOpenChange={(next) => !next && setDialog(null)}
          locked={busy}
          title={verbs[dialog].title}
          description="Check the details below. The Authority verifies that this is the exact request you reviewed."
          footer={
            <>
              <Button variant="secondary" onClick={() => setDialog(null)} disabled={busy}>
                Cancel
              </Button>
              <Button variant={verbs[dialog].variant} busy={busy} disabled={!reviewed} onClick={() => void submit()}>
                {verbs[dialog].confirm}
              </Button>
            </>
          }
        >
          <DescriptionList
            items={[
              { term: "Organization", detail: data.request.organizationDisplayName },
              { term: "Requested member", detail: `${data.request.displayNameHint} (${identitySummary(data.request.target).subject})` },
              { term: "Requested role", detail: roleLabel(data.request.initialRoleName) },
              { term: "Request fingerprint", detail: <code className="break-all text-help">{data.statementSha256.slice(0, 16)}…</code> },
              { term: "Your justification", detail: <span className="whitespace-pre-wrap">{trimmed}</span> },
            ]}
          />
          <p className="text-help text-muted">Development operators: this decision is signed with synthetic evidence and is not independent human approval.</p>
          <Checkbox label="I have reviewed the current request details and fingerprint" checked={reviewed} onCheckedChange={setReviewed} />
        </Dialog>
      ) : null}
    </div>
  );
}

function ReviewBody({
  data,
  justification,
  onJustification,
  justificationError,
  justificationOk,
  onDecide,
  onReload,
  reloading,
}: {
  data: RequestDetail;
  justification: string;
  onJustification: (value: string) => void;
  justificationError: string | undefined;
  justificationOk: boolean;
  onDecide: (kind: DecisionKind) => void;
  onReload: () => void;
  reloading: boolean;
}) {
  const { request } = data;
  const target = identitySummary(request.target);
  const requester = identitySummary(request.requestedBy);
  const chip = approvalChip(request);
  const expired = Date.parse(request.expiresOn) <= Date.now();
  const pending = request.requestState === "Pending" && !expired;
  const eligibility = data.myEligibility;
  const refusal = eligibility.refusalCode ? describeError(new ConsoleApiError(409, eligibility.refusalCode, eligibility.refusalDetail ?? "", false)).message : null;
  const reason = (allowed: boolean, fallback: string): string | null => (allowed ? null : !pending ? (expired ? "This request has expired." : `This request is ${request.requestState.toLowerCase()}.`) : (refusal ?? fallback));
  const approveWhy = reason(eligibility.canApprove, "You cannot approve this request.");
  const rejectWhy = reason(eligibility.canReject, "You cannot reject this request.");
  const withdrawWhy = reason(eligibility.canWithdraw, "You have no pending approval to withdraw.");
  const canApprove = pending && eligibility.canApprove && justificationOk;
  const canReject = pending && eligibility.canReject && justificationOk;
  const canWithdraw = pending && eligibility.canWithdraw && justificationOk;

  return (
    <>
      <div className="grid gap-4 lg:grid-cols-2">
        <Card title="Request">
          <DescriptionList
            items={[
              { term: "Organization", detail: request.organizationDisplayName },
              { term: "Requested member", detail: <><span className="font-medium">{request.displayNameHint}</span><br /><span className="text-help text-muted">Verified identity {target.subject}{target.issuer ? ` · ${target.issuer}` : ""}</span></> },
              { term: "Requested role", detail: roleLabel(request.initialRoleName) },
              { term: "Requested by (owner)", detail: <span>{requester.subject}{requester.issuer ? <span className="text-help text-muted"> · {requester.issuer}</span> : null}</span> },
              { term: "Owner’s justification", detail: <span className="whitespace-pre-wrap">{data.requestReason}</span> },
              { term: "Submitted", detail: formatDateTime(request.requestedOn) },
              { term: "Expires", detail: formatDateTime(request.expiresOn) },
              { term: "Status", detail: <Badge tone={chip.tone}>{request.requestState === "Pending" ? `Pending · ${chip.text}` : chip.text}</Badge> },
              { term: "Request ID", detail: <code className="break-all text-help">{request.requestId}</code> },
            ]}
          />
          {data.replacesRequestId ? <p className="text-help text-muted">This request replaces an earlier one ({data.replacesRequestId}).</p> : null}
        </Card>
        <div className="flex flex-col gap-4">
          <Card title="What approval does">
            <ul className="list-disc space-y-1 pl-5 text-sm">
              {data.consequences.map((c) => (
                <li key={c}>{c}</li>
              ))}
            </ul>
          </Card>
          <Card title="Approvals">
            {data.approvals.length === 0 ? (
              <p className="text-sm text-muted">No operator has approved yet. Approval progress: {Number(request.approvalStatus.recorded)} of {Number(request.approvalStatus.required)}.</p>
            ) : (
              <ul className="flex flex-col gap-3">
                {data.approvals.map((a) => (
                  <li key={a.approvalId} className="rounded-control border border-line p-3">
                    <p className="font-medium">
                      {a.operatorDisplayName} {a.isMine ? <Badge tone="info">You</Badge> : null} <Badge tone={a.state === "Pending" ? "warning" : "neutral"}>{a.state}</Badge>
                    </p>
                    <p className="text-help text-muted">
                      {identitySummary(a.operatorIdentity).subject} · {formatDateTime(a.recordedOn)}
                    </p>
                    <p className="mt-1 whitespace-pre-wrap text-sm">{a.justification}</p>
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </div>
      </div>
      <Card
        title="Exact statement"
        actions={
          <Button variant="secondary" size="sm" onClick={onReload} busy={reloading}>
            Reload request
          </Button>
        }
      >
        <p className="text-sm">
          Fingerprint (SHA-256 of the statement the Authority built from its own records): <code className="break-all" data-testid="fingerprint">{data.statementSha256.slice(0, 16)}</code>
        </p>
        <details>
          <summary className="cursor-pointer text-sm font-medium">Show full fingerprint and statement</summary>
          <p className="mt-2 break-all text-help">
            <code>{data.statementSha256}</code>
          </p>
          <pre tabIndex={0} aria-label="Statement the Authority built" className="mt-2 max-h-72 overflow-auto rounded-control bg-page p-3 text-help">{prettyJson(data.statementJson)}</pre>
        </details>
      </Card>
      <Card title="Your decision">
        <p className="text-sm text-muted">{data.evidenceAssurance}</p>
        <TextArea
          label="Your justification"
          required
          rows={3}
          value={justification}
          maxLength={MAX_JUSTIFICATION + 100}
          onChange={(event) => onJustification(event.target.value)}
          error={justificationError}
          helpText={`${justification.trim().length} of ${MAX_JUSTIFICATION} characters. Required for every decision; it is recorded with your name.`}
          disabled={!pending}
        />
        <div className="flex flex-wrap gap-2">
          <Button variant="primary" disabled={!canApprove} onClick={() => onDecide("approve")}>
            Approve
          </Button>
          <Button variant="danger" disabled={!canReject} onClick={() => onDecide("reject")}>
            Reject
          </Button>
          {eligibility.canWithdraw ? (
            <Button variant="secondary" disabled={!canWithdraw} onClick={() => onDecide("withdraw")}>
              Withdraw my approval
            </Button>
          ) : null}
        </div>
        <ul className="space-y-1 text-help text-muted" aria-label="Why an action may be unavailable">
          {approveWhy ? <li>Approve is unavailable: {approveWhy}</li> : null}
          {rejectWhy ? <li>Reject is unavailable: {rejectWhy}</li> : null}
          {pending && !justificationOk ? <li>Enter a justification of {MIN_JUSTIFICATION} to {MAX_JUSTIFICATION} characters to enable the decision buttons.</li> : null}
          {eligibility.canWithdraw ? null : withdrawWhy && pending ? <li>Withdraw is unavailable: {withdrawWhy}</li> : null}
        </ul>
      </Card>
    </>
  );
}

function prettyJson(value: string): string {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}
