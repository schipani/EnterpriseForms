import { Badge, Button, Card, Checkbox, DescriptionList, Dialog, LoadingBlock, StatusMessage, TextArea } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { consoleApi, ConsoleApiError, type RecoveryDecisionKind, type RecoveryDecisionResponse, type RecoveryDetail } from "../api/client";
import { describeError } from "../app/errors";
import { formatDateTime, identitySummary } from "../app/format";
import { navigate, organizationPath } from "../app/router";
import { useConsoleSession } from "../app/session";
import { recoveryStateLabel, recoveryStateTone } from "./RecoveryQueuePage";
import { unavailabilityReasons } from "./RecoveryNewPage";
import { verificationMethods } from "./OnboardingNewPage";

const MIN_JUSTIFICATION = 10;
const MAX_JUSTIFICATION = 500;

const verbs: Record<RecoveryDecisionKind, { action: string; title: string; confirm: string; variant: "primary" | "danger" | "secondary" }> = {
  approve: { action: "Approve", title: "Approve this recovery case", confirm: "Record my approval", variant: "primary" },
  withdraw: { action: "Withdraw my approval", title: "Withdraw your approval", confirm: "Withdraw my approval", variant: "secondary" },
  cancel: { action: "Cancel case", title: "Cancel this recovery case", confirm: "Cancel case", variant: "danger" },
  execute: { action: "Execute recovery", title: "Execute this recovery", confirm: "Execute recovery", variant: "primary" },
};

const labelOf = (list: Array<{ value: string; label: string }>, value: string | null): string => list.find((o) => o.value === value)?.label ?? value ?? "";

/** What the Authority reported, in the words of the screen. Only a confirmed response produces a success message. */
export function recoverySuccessMessage(kind: RecoveryDecisionKind, response: RecoveryDecisionResponse): string {
  const recorded = Number(response.approvalsRecorded);
  const required = Number(response.approvalsRequired);
  switch (kind) {
    case "approve":
      return recorded >= required ? `Approval recorded — ${required} of ${required}. Next: the waiting period.` : `Approval recorded — ${recorded} of ${required}`;
    case "withdraw":
      return "Approval withdrawn";
    case "cancel":
      return "Case cancelled";
    default:
      return response.displayState === "Recovered" ? "Recovery executed — the owners were restored" : "Recovery started — refresh to see when it completes";
  }
}

/** Whole seconds left, never negative. The server stays authoritative; this only paces the screen. */
export const secondsUntil = (iso: string, now: number): number => Math.max(0, Math.ceil((Date.parse(iso) - now) / 1000));

export function formatCountdown(totalSeconds: number): string {
  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (days > 0) return `${days} d ${hours} h ${minutes} min`;
  if (hours > 0) return `${hours} h ${minutes} min ${seconds} s`;
  if (minutes > 0) return `${minutes} min ${seconds} s`;
  return `${seconds} s`;
}

/** PG-075 case details: the operator reads the Authority's current case and statement fingerprint, then decides through the BFF. */
export function RecoveryDetailPage({ caseId }: { caseId: string }) {
  const { touch, handleError } = useConsoleSession();
  // Every view is audited by the Authority: fetch once per visit, and again only on an explicit refresh, a decision or the end of the waiting period.
  const detail = useQuery({ queryKey: ["recovery-detail", caseId], queryFn: () => consoleApi.recoveryDetail(caseId), staleTime: Infinity, gcTime: 0 });
  const [justification, setJustification] = useState("");
  const [dialog, setDialog] = useState<RecoveryDecisionKind | null>(null);
  const [reviewed, setReviewed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ReturnType<typeof describeError> | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);

  useEffect(() => headingRef.current?.focus(), []);
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
  const justificationError =
    justification.length > 0 && (trimmed.length < MIN_JUSTIFICATION || trimmed.length > MAX_JUSTIFICATION) ? `Enter ${MIN_JUSTIFICATION} to ${MAX_JUSTIFICATION} characters.` : undefined;
  const justificationOk = trimmed.length >= MIN_JUSTIFICATION && trimmed.length <= MAX_JUSTIFICATION;

  const reload = async () => {
    setFailure(null);
    await detail.refetch();
  };

  // Countdown to the end of the waiting period; reloads the case once when it reaches zero so the Execute eligibility is the server's.
  const [now, setNow] = useState(() => Date.now());
  const waiting = data?.displayState === "WaitingPeriod";
  const endsOn = data?.waitingPeriodEndsOn;
  const reloadedAt = useRef<string | null>(null);
  useEffect(() => {
    if (!waiting) return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [waiting]);
  const remaining = endsOn ? secondsUntil(endsOn, now) : 0;
  useEffect(() => {
    if (waiting && endsOn && remaining === 0 && reloadedAt.current !== endsOn) {
      reloadedAt.current = endsOn;
      void detail.refetch();
    }
  }, [waiting, endsOn, remaining, detail]);

  const open = (kind: RecoveryDecisionKind) => {
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
      const response = await consoleApi.decideRecovery(kind, caseId, { expectedStatementSha256: data.statementSha256, justification: trimmed });
      touch();
      setDialog(null);
      setJustification("");
      setSuccess(recoverySuccessMessage(kind, response));
      await detail.refetch();
    } catch (error) {
      setDialog(null);
      if (!handleError(error)) {
        setFailure(describeError(error));
        if (error instanceof ConsoleApiError && error.status === 409) {
          // Changed, objected, expired, still waiting: show the current case, never the stale one.
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
        <Button variant="ghost" size="sm" onClick={() => navigate("/platform/recovery")}>
          ← Back to owner recovery
        </Button>
        <h1 ref={headingRef} tabIndex={-1} className="mt-1 text-2xl font-semibold">
          Recovery case
        </h1>
      </div>
      {detail.isLoading ? <LoadingBlock label="Loading case" /> : null}
      {loadFailure ? (
        <StatusMessage
          tone={loadFailure.tone}
          title={loadFailure.title}
          actions={
            loadFailure.retryable || loadFailure.reload ? (
              <Button variant="secondary" size="sm" onClick={() => void reload()}>
                Reload case
              </Button>
            ) : undefined
          }
        >
          {loadFailure.message}
          {loadFailure.reference ? ` Reference ${loadFailure.reference}.` : ""}
        </StatusMessage>
      ) : null}
      <div aria-live="polite">
        {success ? (
          <div role="status" className="rounded-control border-l-4 border-[#166534] bg-[#f0fdf4] px-4 py-3 font-semibold text-[#14532d]">
            {success}
          </div>
        ) : null}
      </div>
      {failure ? (
        <StatusMessage
          tone={failure.tone}
          title={failure.title}
          actions={
            failure.reload || failure.retryable ? (
              <Button variant="secondary" size="sm" onClick={() => void reload()}>
                Reload case
              </Button>
            ) : undefined
          }
        >
          {failure.message}
          {failure.reference ? ` Reference ${failure.reference}.` : ""}
        </StatusMessage>
      ) : null}
      {data ? (
        <DetailBody
          data={data}
          remaining={remaining}
          justification={justification}
          onJustification={setJustification}
          justificationError={justificationError}
          justificationOk={justificationOk}
          onDecide={open}
          onReload={() => void reload()}
          reloading={detail.isFetching}
        />
      ) : null}
      {data && dialog ? (
        <Dialog
          open
          onOpenChange={(next) => !next && setDialog(null)}
          locked={busy}
          title={verbs[dialog].title}
          description="Check the details below. The Authority verifies that this is the exact case you reviewed."
          footer={
            <>
              <Button variant="secondary" onClick={() => setDialog(null)} disabled={busy}>
                Go back
              </Button>
              <Button variant={verbs[dialog].variant} busy={busy} disabled={!reviewed} onClick={() => void submit()}>
                {verbs[dialog].confirm}
              </Button>
            </>
          }
        >
          <DescriptionList
            items={[
              { term: "Organization", detail: data.organizationDisplayName },
              { term: "Incident", detail: data.incidentReference },
              { term: "Replacement owners", detail: String(data.subjects.filter((s) => s.role === "Replacement").length) },
              { term: "Case fingerprint", detail: <code className="break-all text-help">{data.statementSha256.slice(0, 16)}…</code> },
              { term: "Your justification", detail: <span className="whitespace-pre-wrap">{trimmed}</span> },
            ]}
          />
          <p className="text-help text-muted">Development operators: this decision is signed with synthetic evidence and is not independent human approval.</p>
          <Checkbox label="I have reviewed the current case details and fingerprint" checked={reviewed} onCheckedChange={setReviewed} />
        </Dialog>
      ) : null}
    </div>
  );
}

function DetailBody({
  data,
  remaining,
  justification,
  onJustification,
  justificationError,
  justificationOk,
  onDecide,
  onReload,
  reloading,
}: {
  data: RecoveryDetail;
  remaining: number;
  justification: string;
  onJustification: (value: string) => void;
  justificationError: string | undefined;
  justificationOk: boolean;
  onDecide: (kind: RecoveryDecisionKind) => void;
  onReload: () => void;
  reloading: boolean;
}) {
  const e = data.myEligibility;
  const display = data.displayState;
  const open = display === "AwaitingApprovals" || display === "WaitingPeriod" || display === "ReadyForRecovery" || display === "BlockedByObjection";
  const anyAction = e.canApprove || e.canWithdraw || e.canCancel || e.canExecute;
  const refusalText = (code: string | null, detail: string | null): string | null => (code ? describeError(new ConsoleApiError(409, code, detail ?? "", false)).message : null);
  const approveWhy = !e.canApprove && open ? (refusalText(e.approveRefusalCode, e.approveRefusalDetail) ?? "You cannot approve this case.") : null;
  const executeWhy = !e.canExecute && open ? (refusalText(e.executeRefusalCode, e.executeRefusalDetail) ?? "The recovery cannot be executed yet.") : null;
  const replacements = data.subjects.filter((s) => s.role === "Replacement");
  const declaredSubjects = data.subjects.filter((s) => s.role === "DeclaredUnavailable");
  const recorded = Number(data.approvalsRecorded);
  const required = Number(data.approvalsRequired);
  return (
    <>
      <Card
        title="Status"
        actions={
          <Button variant="secondary" size="sm" onClick={onReload} busy={reloading}>
            Refresh
          </Button>
        }
      >
        <p className="text-sm">
          Status: <Badge tone={recoveryStateTone(display)}>{recoveryStateLabel[display] ?? display}</Badge>
          {data.closureReason ? <span> · {data.closureReason}</span> : null}
        </p>
        <p className="text-sm">
          Approvals recorded: <span data-testid="approval-progress">{recorded} of {required}</span>
        </p>
        {data.closureJustification ? <p className="whitespace-pre-wrap text-sm">{data.closureJustification}</p> : null}
        {display === "WaitingPeriod" ? (
          <p className="text-sm">
            Waiting period: <strong data-testid="waiting-remaining">{remaining > 0 ? `${formatCountdown(remaining)} remaining` : "ended — refreshing"}</strong> (ends {formatDateTime(data.waitingPeriodEndsOn)}).
            The organization's owners can object until then.
          </p>
        ) : null}
        {display === "BlockedByObjection" || data.objection ? (
          <StatusMessage tone="danger" title="An owner objected">
            {data.objection ? (
              <>
                Objection recorded {formatDateTime(data.objection.objectedOn)} by {data.objection.objectedBySubjectIdentifier}.
                <span className="mt-1 block whitespace-pre-wrap">{data.objection.reason}</span>
              </>
            ) : (
              "The recovery is blocked."
            )}
            <span className="mt-1 block">The case cannot be executed. Cancel it and investigate before opening a new one.</span>
          </StatusMessage>
        ) : (
          <p className="text-sm text-muted">No owner has objected.</p>
        )}
        {data.lastExecutionFailureCode ? (
          <StatusMessage tone="warning" title="The last execution attempt failed">
            Code {data.lastExecutionFailureCode}
            {data.lastExecutionFailureOn ? ` at ${formatDateTime(data.lastExecutionFailureOn)}` : ""}. The owners were not restored by that attempt; check the case and try again.
          </StatusMessage>
        ) : null}
        {display === "Recovered" ? (
          <p className="text-sm font-medium" role="status">
            Recovered {data.recoveredOn ? formatDateTime(data.recoveredOn) : ""}: {Number(data.eligibleOwnersBefore)} enabled {Number(data.eligibleOwnersBefore) === 1 ? "owner" : "owners"} before,{" "}
            {Number(data.eligibleOwnersAfter)} after.
          </p>
        ) : null}
      </Card>
      <div className="grid gap-4 lg:grid-cols-2">
        <Card title="Case">
          <DescriptionList
            items={[
              { term: "Organization", detail: data.organizationDisplayName },
              { term: "Incident", detail: <span className="break-words">{data.incidentReference}</span> },
              { term: "Opened by", detail: data.openedByOperatorDisplayName },
              { term: "Justification", detail: <span className="whitespace-pre-wrap">{data.justification}</span> },
              { term: "Opened", detail: formatDateTime(data.openedOn) },
              { term: "Waiting period ends", detail: formatDateTime(data.waitingPeriodEndsOn) },
              { term: "Expires", detail: formatDateTime(data.expiresOn) },
              ...(data.closedOn ? [{ term: "Closed", detail: formatDateTime(data.closedOn) }] : []),
              { term: "Case ID", detail: <code className="break-all text-help">{data.caseId}</code> },
            ]}
          />
          <Button variant="secondary" size="sm" className="self-start" onClick={() => navigate(organizationPath(data.organizationId))}>
            View organization
          </Button>
        </Card>
        <Card title="Approvals">
          <p className="text-sm">
            Approvals recorded: {recorded} of {required}
          </p>
          {data.approvals.length === 0 ? (
            <p className="text-sm text-muted">No operator has approved yet.</p>
          ) : (
            <ul aria-label="Approvals" className="flex list-none flex-col gap-3 p-0">
              {data.approvals.map((a) => {
                const inactive = a.state === "Withdrawn" || a.state === "Invalidated";
                return (
                  <li key={a.approvalId} className={`rounded-control border border-line p-3 ${inactive ? "bg-page text-muted" : ""}`}>
                    <p className="font-medium">
                      <span className={inactive ? "line-through" : ""}>{a.operatorDisplayName}</span> {a.isMine ? <Badge tone="info">You</Badge> : null}{" "}
                      <Badge tone={inactive ? "neutral" : "success"}>{a.state}</Badge>
                    </p>
                    <p className="text-help text-muted">{formatDateTime(a.recordedOn)}</p>
                    <p className={`mt-1 whitespace-pre-wrap text-sm ${inactive ? "line-through" : ""}`}>{a.justification}</p>
                    {inactive && a.closureReason ? <p className="mt-1 text-help">Reason: {a.closureReason}</p> : null}
                  </li>
                );
              })}
            </ul>
          )}
        </Card>
      </div>
      <Card title="Current owners">
        {data.currentOwners.length === 0 ? (
          <p className="text-sm text-muted">This organization has no owners.</p>
        ) : (
          <ul aria-label="Current owners" className="flex list-none flex-col gap-2 p-0">
            {data.currentOwners.map((o) => {
              const identity = identitySummary(o);
              return (
                <li key={o.authorityUserId} className="flex flex-wrap items-center justify-between gap-2 rounded-control border border-line p-3">
                  <div className="min-w-0">
                    <p className="break-words font-medium">{identity.subject}</p>
                    <p className="break-words text-help text-muted">{identity.issuer}</p>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <Badge tone={o.enabled ? "success" : "neutral"}>{o.enabled ? "Enabled" : "Disabled"}</Badge>
                    {o.declaredUnavailable ? <Badge tone="warning">Declared unavailable</Badge> : null}
                    {o.activeSinceCaseOpened ? <Badge tone="danger">Signed in since the case opened</Badge> : null}
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </Card>
      <Card title="Proposed owners">
        <ul aria-label="Proposed owners" className="flex list-none flex-col gap-2 p-0">
          {replacements.map((s) => {
            const identity = identitySummary(s);
            return (
              <li key={s.authorityUserId} className="rounded-control border border-line p-3">
                <p className="break-words font-medium">
                  {s.displayNameHint} {s.isNewIdentity ? <Badge tone="info">New identity</Badge> : <Badge tone="neutral">Existing member</Badge>}
                </p>
                <p className="break-words text-help text-muted">
                  {identity.subject} · {identity.issuer}
                </p>
                <p className="text-help">
                  Verified by {labelOf(verificationMethods, s.verificationMethod)} · reference {s.verificationReference}
                </p>
              </li>
            );
          })}
        </ul>
      </Card>
      {declaredSubjects.length > 0 ? (
        <Card title="Owners declared unavailable">
          <ul aria-label="Owners declared unavailable" className="flex list-none flex-col gap-2 p-0">
            {declaredSubjects.map((s) => {
              const identity = identitySummary(s);
              return (
                <li key={s.authorityUserId} className="rounded-control border border-line p-3">
                  <p className="break-words font-medium">{identity.subject}</p>
                  <p className="break-words text-help text-muted">{identity.issuer}</p>
                  <p className="text-help">
                    {labelOf(unavailabilityReasons, s.unavailabilityReason)} · evidence {s.evidenceReference}
                  </p>
                  {s.evidenceSummary ? <p className="mt-1 whitespace-pre-wrap text-sm">{s.evidenceSummary}</p> : null}
                </li>
              );
            })}
          </ul>
        </Card>
      ) : null}
      {data.activation ? (
        <Card title="Activation">
          <p className="text-sm" role="status">
            {data.activation.status === "Active" ? "The organization is active." : "The Authority is applying its authorization state; refresh to see when it completes."}
          </p>
          <DescriptionList
            items={[
              { term: "Status", detail: <Badge tone={data.activation.status === "Active" ? "success" : "info"}>{data.activation.status}</Badge> },
              { term: "Authorization revision", detail: String(data.activation.authorizationRevision) },
              { term: "Projection applied revision", detail: String(data.activation.projectionAppliedRevision) },
            ]}
          />
        </Card>
      ) : null}
      <Card title="Exact statement">
        <p className="text-sm">
          Fingerprint (SHA-256 of the statement the Authority built from its own records):{" "}
          <code className="break-all" data-testid="fingerprint">
            {data.statementSha256.slice(0, 16)}
          </code>
        </p>
        <details>
          <summary className="cursor-pointer text-sm font-medium">Show statement</summary>
          <p className="mt-2 break-all text-help">
            <code>{data.statementSha256}</code>
          </p>
          <pre tabIndex={0} aria-label="Statement the Authority built" className="mt-2 max-h-72 overflow-auto rounded-control bg-page p-3 text-help">
            {prettyJson(data.statementJson)}
          </pre>
        </details>
      </Card>
      <Card title="Your decision">
        <p className="text-sm text-muted">{data.approvalAssurance}</p>
        <TextArea
          label="Your justification"
          required
          rows={3}
          value={justification}
          maxLength={MAX_JUSTIFICATION + 100}
          onChange={(event) => onJustification(event.target.value)}
          error={justificationError}
          helpText={`${justification.trim().length} of ${MAX_JUSTIFICATION} characters. Required for every decision; it is recorded with your name.`}
          disabled={!anyAction}
        />
        <div className="flex flex-wrap gap-2">
          {open ? (
            <>
              <Button variant="primary" disabled={!e.canApprove || !justificationOk} onClick={() => onDecide("approve")}>
                Approve
              </Button>
              {e.canWithdraw ? (
                <Button variant="secondary" disabled={!justificationOk} onClick={() => onDecide("withdraw")}>
                  Withdraw my approval
                </Button>
              ) : null}
              <Button variant="primary" disabled={!e.canExecute || !justificationOk} onClick={() => onDecide("execute")}>
                Execute recovery
              </Button>
              <Button variant="danger" disabled={!e.canCancel || !justificationOk} onClick={() => onDecide("cancel")}>
                Cancel case
              </Button>
            </>
          ) : null}
        </div>
        <ul className="space-y-1 text-help text-muted" aria-label="Why an action may be unavailable">
          {approveWhy ? <li>Approve is unavailable: {approveWhy}</li> : null}
          {executeWhy ? <li>Execute recovery is unavailable: {executeWhy}</li> : null}
          {anyAction && !justificationOk ? (
            <li>
              Enter a justification of {MIN_JUSTIFICATION} to {MAX_JUSTIFICATION} characters to enable the decision buttons.
            </li>
          ) : null}
          {!anyAction ? <li>No decision is available for this case{open ? "" : ", which is closed"}.</li> : null}
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
