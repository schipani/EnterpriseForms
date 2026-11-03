import { Badge, Button, Card, Checkbox, DescriptionList, Dialog, LoadingBlock, StatusMessage, TextArea } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { consoleApi, ConsoleApiError, type OnboardingDecisionKind, type OnboardingDecisionResponse, type OnboardingDetail } from "../api/client";
import { describeError } from "../app/errors";
import { formatDateTime, identitySummary } from "../app/format";
import { navigate, organizationPath } from "../app/router";
import { useConsoleSession } from "../app/session";
import { displayStateLabel, displayStateTone } from "./OnboardingQueuePage";

const MIN_JUSTIFICATION = 10;
const MAX_JUSTIFICATION = 500;

const verbs: Record<OnboardingDecisionKind, { action: string; title: string; confirm: string; variant: "primary" | "danger" | "secondary" }> = {
  approve: { action: "Approve", title: "Approve this onboarding request", confirm: "Record my approval", variant: "primary" },
  reject: { action: "Reject", title: "Reject this onboarding request", confirm: "Reject request", variant: "danger" },
  withdraw: { action: "Withdraw my approval", title: "Withdraw your approval", confirm: "Withdraw my approval", variant: "secondary" },
  cancel: { action: "Cancel request", title: "Cancel this onboarding request", confirm: "Cancel request", variant: "danger" },
  initialize: { action: "Initialize organization", title: "Initialize this organization", confirm: "Initialize organization", variant: "primary" },
};

const verificationLabels: Record<string, string> = {
  SupportCallback: "Support call-back",
  ContractSignatory: "Contract signatory",
  CustomerAdministratorAttestation: "Customer administrator attestation",
};

/** What the Authority reported, in the words of the screen. Only a confirmed response produces a success message. */
export function onboardingSuccessMessage(kind: OnboardingDecisionKind, response: OnboardingDecisionResponse): string {
  const recorded = Number(response.approvalsRecorded);
  const required = Number(response.approvalsRequired);
  switch (kind) {
    case "approve":
      return response.outcome === "Approved" ? `Request approved — ${required} of ${required}. Next: key provisioning.` : `Approval recorded — ${recorded} of ${required}`;
    case "withdraw":
      return "Approval withdrawn";
    case "reject":
      return "Request rejected";
    case "cancel":
      return "Request cancelled";
    default:
      return "Organization initialized — activating";
  }
}

const steps = ["Awaiting approval", "Approved", "Key provisioning", "Initialized", "Active"];
/** Index of the step the request is on; terminal closures have none. */
const currentStep = (displayState: string): number => ({ AwaitingApproval: 0, AwaitingKeyProvisioning: 2, Activating: 3, Active: 4 })[displayState] ?? -1;

function Stepper({ displayState }: { displayState: string }) {
  const current = currentStep(displayState);
  return (
    <ol aria-label="Onboarding progress" className="grid list-none gap-2 p-0 sm:grid-cols-5">
      {steps.map((label, index) => {
        const stateText = current < 0 ? "Not reached" : index < current || (index === current && displayState === "Active") ? "Done" : index === current ? "Current step" : "Upcoming";
        return (
          <li
            key={label}
            aria-current={index === current ? "step" : undefined}
            className={`rounded-control border p-3 text-sm ${index === current ? "border-2 border-primary font-semibold" : "border-line"}`}
          >
            <span className="block">{label}</span>
            <span className="block text-help text-muted">{stateText}</span>
          </li>
        );
      })}
    </ol>
  );
}

/** PG-074 details: the operator reads the Authority's current request and statement fingerprint, then decides through the BFF. */
export function OnboardingDetailPage({ requestId }: { requestId: string }) {
  const { touch, handleError } = useConsoleSession();
  // Every view is audited by the Authority: fetch once per visit, and again only on an explicit refresh or after a decision.
  const detail = useQuery({ queryKey: ["onboarding-detail", requestId], queryFn: () => consoleApi.onboardingDetail(requestId), staleTime: Infinity, gcTime: 0 });
  const [justification, setJustification] = useState("");
  const [dialog, setDialog] = useState<OnboardingDecisionKind | null>(null);
  const [reviewed, setReviewed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ReturnType<typeof describeError> | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
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

  const open = (kind: OnboardingDecisionKind) => {
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
      const response = await consoleApi.decideOnboarding(kind, requestId, { expectedStatementSha256: data.statementSha256, justification: trimmed });
      touch();
      setDialog(null);
      setJustification("");
      setSuccess(onboardingSuccessMessage(kind, response));
      await detail.refetch();
    } catch (error) {
      setDialog(null);
      if (!handleError(error)) {
        setFailure(describeError(error));
        if (error instanceof ConsoleApiError && error.status === 409) {
          // Changed, expired, not pending, key missing: show the current request, never the stale one.
          void detail.refetch();
        }
      }
    } finally {
      setBusy(false);
    }
  };

  const copyKeyName = async (keyName: string) => {
    try {
      await navigator.clipboard.writeText(keyName);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  };

  const loadFailure = detail.isError ? describeError(detail.error) : null;
  return (
    <div className="flex flex-col gap-4">
      <div>
        <Button variant="ghost" size="sm" onClick={() => navigate("/platform/onboarding")}>
          ← Back to onboarding
        </Button>
        <h1 ref={headingRef} tabIndex={-1} className="mt-1 text-2xl font-semibold">
          Onboarding request
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
      {data ? (
        <DetailBody
          data={data}
          justification={justification}
          onJustification={setJustification}
          justificationError={justificationError}
          justificationOk={justificationOk}
          onDecide={open}
          onReload={() => void reload()}
          reloading={detail.isFetching}
          copied={copied}
          onCopy={(name) => void copyKeyName(name)}
        />
      ) : null}
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
              { term: "Initial owners", detail: String(data.owners.length) },
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

function DetailBody({
  data,
  justification,
  onJustification,
  justificationError,
  justificationOk,
  onDecide,
  onReload,
  reloading,
  copied,
  onCopy,
}: {
  data: OnboardingDetail;
  justification: string;
  onJustification: (value: string) => void;
  justificationError: string | undefined;
  justificationOk: boolean;
  onDecide: (kind: OnboardingDecisionKind) => void;
  onReload: () => void;
  reloading: boolean;
  copied: boolean;
  onCopy: (keyName: string) => void;
}) {
  const e = data.myEligibility;
  const stateTone = displayStateTone(data.displayState);
  const awaitingKey = data.state === "AwaitingKeyCustody" || data.displayState === "AwaitingKeyProvisioning";
  const pending = data.displayState === "AwaitingApproval";
  const anyAction = e.canApprove || e.canWithdraw || e.canReject || e.canCancel || e.canInitialize;
  const refusalText = (code: string | null, detail: string | null): string | null => (code ? describeError(new ConsoleApiError(409, code, detail ?? "", false)).message : null);
  const approveWhy = !e.canApprove && pending ? (refusalText(e.approveRefusalCode, e.approveRefusalDetail) ?? "You cannot approve this request.") : null;
  const initializeWhy = !e.canInitialize && awaitingKey ? (refusalText(e.initializeRefusalCode, e.initializeRefusalDetail) ?? "The organization cannot be initialized yet.") : null;
  const closed = currentStep(data.displayState) < 0;
  const custody = data.keyCustody;
  return (
    <>
      <Card title="Progress">
        <Stepper displayState={data.displayState} />
        <p className="text-sm">
          Status: <Badge tone={stateTone}>{displayStateLabel[data.displayState] ?? data.displayState}</Badge>
          {closed && data.closureReason ? <span> · {data.closureReason}</span> : null}
        </p>
        {closed && data.closureJustification ? <p className="whitespace-pre-wrap text-sm">{data.closureJustification}</p> : null}
        {data.lastInitializationFailureCode ? (
          <StatusMessage tone="warning" title="The last initialization attempt failed">
            Code {data.lastInitializationFailureCode}
            {data.lastInitializationFailureOn ? ` at ${formatDateTime(data.lastInitializationFailureOn)}` : ""}. The organization was not initialized by that attempt; check the key status below and
            try again.
          </StatusMessage>
        ) : null}
      </Card>
      <div className="grid gap-4 lg:grid-cols-2">
        <Card title="Request">
          <DescriptionList
            items={[
              { term: "Organization", detail: data.organizationDisplayName },
              { term: "Identity issuer", detail: <span className="break-all">{data.identityIssuerUri}</span> },
              { term: "Created by", detail: data.createdByOperatorDisplayName },
              { term: "Justification", detail: <span className="whitespace-pre-wrap">{data.justification}</span> },
              { term: "Created", detail: formatDateTime(data.createdOn) },
              { term: "Expires", detail: formatDateTime(data.expiresOn) },
              ...(data.approvedOn ? [{ term: "Approved", detail: formatDateTime(data.approvedOn) }] : []),
              ...(data.initializedOn ? [{ term: "Initialized", detail: formatDateTime(data.initializedOn) }] : []),
              { term: "Request ID", detail: <code className="break-all text-help">{data.requestId}</code> },
            ]}
          />
        </Card>
        <Card title="Approvals">
          <p className="text-sm">
            Approvals recorded: {Number(data.approvalsRecorded)} of {Number(data.approvalsRequired)}
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
      <Card title="Initial owners">
        <ul aria-label="Initial owners" className="flex list-none flex-col gap-2 p-0">
          {data.owners.map((o) => {
            const identity = identitySummary(o);
            return (
              <li key={o.authorityUserId} className="rounded-control border border-line p-3">
                <p className="break-words font-medium">{o.displayNameHint}</p>
                <p className="break-words text-help text-muted">
                  {identity.subject} · {identity.issuer}
                </p>
                <p className="text-help">
                  Verified by {verificationLabels[o.verificationMethod] ?? o.verificationMethod} · reference {o.verificationReference}
                </p>
                {o.memberOfOtherOrganizations ? <p className="text-help">Also a member of another organization.</p> : null}
              </li>
            );
          })}
        </ul>
      </Card>
      <Card
        title="Exact statement"
        actions={
          <Button variant="secondary" size="sm" onClick={onReload} busy={reloading}>
            Refresh
          </Button>
        }
      >
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
      {awaitingKey ? (
        <Card title="Key custody">
          <DescriptionList
            items={[
              { term: "Key name", detail: <code className="break-all">{custody.keyName}</code> },
              { term: "Key status", detail: <Badge tone={custody.status === "Ready" ? "success" : custody.status === "Missing" ? "warning" : "danger"}>{custody.status}</Badge> },
              ...(data.keyCustodyExpiresOn ? [{ term: "Custody deadline", detail: formatDateTime(data.keyCustodyExpiresOn) }] : []),
            ]}
          />
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="secondary" size="sm" onClick={() => onCopy(custody.keyName)}>
              Copy key name
            </Button>
            <span role="status" className="text-help text-muted">
              {copied ? "Key name copied" : ""}
            </span>
          </div>
          {custody.status === "Missing" ? (
            <p className="text-sm">
              Waiting for key custody. Ask the key custodian to provision this key: <code className="break-all">devstack.py onboarding provision-key {data.requestId}</code> (development).
            </p>
          ) : custody.status === "Ready" ? (
            <p className="text-sm font-medium">Key ready — the organization can be initialized.</p>
          ) : (
            <p className="text-sm">
              The key is {custody.status === "Invalid" ? "not valid" : "unavailable"}
              {custody.detail ? `: ${custody.detail}` : "."} The organization cannot be initialized until key custody resolves this.
            </p>
          )}
        </Card>
      ) : null}
      {data.activation ? (
        <Card
          title="Activation"
          actions={
            <Button variant="secondary" size="sm" onClick={onReload} busy={reloading}>
              Refresh
            </Button>
          }
        >
          <p className="text-sm" role="status">
            {data.activation.status === "Active"
              ? "The organization is active."
              : data.activation.status === "Activating"
                ? "The organization is activating. The Authority is applying its authorization state; refresh to see when it becomes active."
                : "The organization is quarantined by the Authority and needs a security-operator recovery."}
          </p>
          <DescriptionList
            items={[
              { term: "Status", detail: <Badge tone={displayStateTone(data.activation.status)}>{data.activation.status}</Badge> },
              { term: "Authorization revision", detail: String(data.activation.authorizationRevision) },
              { term: "Projection applied revision", detail: String(data.activation.projectionAppliedRevision) },
            ]}
          />
          <Button variant="secondary" size="sm" className="self-start" onClick={() => navigate(organizationPath(data.organizationId))}>
            View organization
          </Button>
        </Card>
      ) : null}
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
          {pending ? (
            <>
              <Button variant="primary" disabled={!e.canApprove || !justificationOk} onClick={() => onDecide("approve")}>
                Approve
              </Button>
              <Button variant="danger" disabled={!e.canReject || !justificationOk} onClick={() => onDecide("reject")}>
                Reject
              </Button>
            </>
          ) : null}
          {e.canWithdraw ? (
            <Button variant="secondary" disabled={!justificationOk} onClick={() => onDecide("withdraw")}>
              Withdraw my approval
            </Button>
          ) : null}
          {awaitingKey ? (
            <>
              <Button variant="primary" disabled={!e.canInitialize || !justificationOk} onClick={() => onDecide("initialize")}>
                Initialize organization
              </Button>
              <Button variant="danger" disabled={!e.canCancel || !justificationOk} onClick={() => onDecide("cancel")}>
                Cancel request
              </Button>
            </>
          ) : null}
        </div>
        <ul className="space-y-1 text-help text-muted" aria-label="Why an action may be unavailable">
          {approveWhy ? <li>Approve is unavailable: {approveWhy}</li> : null}
          {initializeWhy ? <li>Initialize is unavailable: {initializeWhy}</li> : null}
          {anyAction && !justificationOk ? (
            <li>
              Enter a justification of {MIN_JUSTIFICATION} to {MAX_JUSTIFICATION} characters to enable the decision buttons.
            </li>
          ) : null}
          {!anyAction ? <li>No decision is available for this request{closed ? ", which is closed" : ""}.</li> : null}
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
