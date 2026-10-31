import type { ApprovalQuery, MembershipRequestResponse, OwnerProposalResponse } from "@enterpriseforms/api-client";
import { presentError, useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, LoadingBlock, Pager, Select, TextInput, useDebouncedValue } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useState, type ReactNode } from "react";
import { ErrorPanel } from "../../support";

/** Approval lists are paged five rows at a time on the server, newest first. */
export const APPROVAL_PAGE_SIZE = 5;

const PROPOSAL_LABELS: Record<string, string> = {
  GrantOwner: "Make Owner",
  RevokeOwner: "Remove Owner",
  DisableOwner: "Disable owner",
  EnableOwner: "Re-enable owner",
};

const RESTART_CODES = new Set(["CURSOR_INVALID", "CURSOR_SCOPE_MISMATCH"]);

const stateTone = (state: string) => (state === "Pending" ? "warning" : state === "Approved" || state === "Consumed" ? "success" : state === "Rejected" ? "danger" : "neutral");

const stateLabel = (state: string) => (state === "Consumed" ? "Approved" : state);

const STATUS_OPTIONS = [
  { value: "all", label: "Any status" },
  { value: "Pending", label: "Pending" },
  { value: "Approved", label: "Approved" },
  { value: "Rejected", label: "Rejected" },
  { value: "Expired", label: "Expired" },
];

const date = (value: string) => new Date(value).toLocaleDateString();

interface Column<T> {
  key: string;
  header: string;
  render: (row: T) => ReactNode;
  /** Grid track on wide containers. */
  width: string;
  right?: boolean;
}

/** Compact rows on wide containers, labelled cards on narrow ones (container query; the same markup, so reading order never changes). */
function ResponsiveRows<T>({ caption, rows, rowKey, columns, emptyMessage }: { caption: string; rows: T[]; rowKey: (row: T) => string; columns: Column<T>[]; emptyMessage: string }) {
  const template = columns.map((c) => c.width).join("_");
  return (
    <div className="@container w-full">
      <div role="table" aria-label={caption} className="w-full overflow-hidden text-sm @3xl:rounded-control @3xl:border @3xl:border-line">
        <div role="rowgroup" className="sr-only @3xl:not-sr-only">
          <div
            role="row"
            className={`grid gap-3 border-b border-line bg-page px-3 py-2 text-label font-semibold @3xl:[grid-template-columns:var(--cols)]`}
            style={{ "--cols": template.replaceAll("_", " ") } as React.CSSProperties}
          >
            {columns.map((c) => (
              <div key={c.key} role="columnheader" className={c.right ? "text-right" : ""}>
                {c.header}
              </div>
            ))}
          </div>
        </div>
        <div role="rowgroup" className="flex flex-col gap-3 @3xl:gap-0">
          {rows.length === 0 ? (
            <div role="row" className="px-3 py-4 text-muted">
              <div role="cell">{emptyMessage}</div>
            </div>
          ) : (
            rows.map((row) => (
              <div
                key={rowKey(row)}
                role="row"
                className="grid grid-cols-1 gap-x-3 gap-y-2 rounded-panel border border-line bg-surface p-4 @xl:grid-cols-2 @3xl:items-center @3xl:rounded-none @3xl:border-0 @3xl:border-b @3xl:px-3 @3xl:py-2 @3xl:[grid-template-columns:var(--cols)] @3xl:last:border-b-0"
                style={{ "--cols": template.replaceAll("_", " ") } as React.CSSProperties}
              >
                {columns.map((c, index) =>
                  index === 0 ? (
                    <div key={c.key} role="rowheader" className="min-w-0 font-normal @xl:col-span-2 @3xl:col-span-1">
                      {c.render(row)}
                    </div>
                  ) : (
                    <div
                      key={c.key}
                      role="cell"
                      data-label={c.header}
                      className={`min-w-0 before:mb-0.5 before:block before:text-help before:text-muted before:content-[attr(data-label)] @3xl:before:hidden ${c.right ? "@3xl:text-right" : ""}`}
                    >
                      {c.render(row)}
                    </div>
                  ),
                )}
              </div>
            ))
          )}
        </div>
      </div>
    </div>
  );
}

/** Name with a short muted second line; the full text is the tooltip. */
function TwoLine({ primary, secondary }: { primary: string; secondary?: string | undefined }) {
  return (
    <>
      <div className="truncate font-semibold" title={primary}>
        {primary}
      </div>
      {secondary ? (
        <div className="truncate text-help text-muted" title={secondary}>
          {secondary}
        </div>
      ) : null}
    </>
  );
}

/** Filters and paging state of one list; changing a filter always returns to the first page. */
function useApprovalList() {
  const [text, setText] = useState("");
  const [status, setStatus] = useState("all");
  const [cursor, setCursor] = useState<string | undefined>(undefined);
  const settled = useDebouncedValue(text);
  const query: ApprovalQuery = {
    pageSize: APPROVAL_PAGE_SIZE,
    ...(settled.trim() ? { q: settled.trim() } : {}),
    ...(status !== "all" ? { status: status as NonNullable<ApprovalQuery["status"]> } : {}),
  };
  return {
    text,
    status,
    cursor,
    setCursor,
    query,
    filtered: text !== "" || status !== "all",
    changeText: (value: string) => (setText(value), setCursor(undefined)),
    changeStatus: (value: string) => (setStatus(value), setCursor(undefined)),
    clear: () => (setText(""), setStatus("all"), setCursor(undefined)),
  };
}

function Filters({ list, idPrefix, noun }: { list: ReturnType<typeof useApprovalList>; idPrefix: string; noun: string }) {
  return (
    <div className="grid items-start gap-x-3 gap-y-2 sm:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_auto]">
      <TextInput
        label={`Search ${noun}`}
        placeholder="Name, identity or reason"
        value={list.text}
        autoComplete="off"
        name={`${idPrefix}-search`}
        onChange={(event) => list.changeText(event.target.value)}
      />
      <Select label="Status" value={list.status} options={STATUS_OPTIONS} onValueChange={list.changeStatus} />
      {list.filtered ? (
        <div className="sm:pt-6">
          <Button size="sm" variant="ghost" onClick={list.clear}>
            Clear filters
          </Button>
        </div>
      ) : null}
    </div>
  );
}

function CopyId({ id, name }: { id: string; name: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <Button
      size="sm"
      variant="ghost"
      aria-label={`Copy ID: ${name}`}
      onClick={() => {
        void navigator.clipboard?.writeText(id).then(() => setCopied(true));
        window.setTimeout(() => setCopied(false), 2000);
      }}
    >
      {copied ? "Copied" : "Copy ID"}
    </Button>
  );
}

/**
 * Owner proposals and membership requests (C8 M2) as two independent lists, each paged on the server (five rows, newest first)
 * with its own search, status filter and cursor. A proposal is approved by a different owner than the proposer and the person;
 * membership requests are decided by two operators, never from this page. Long object IDs are truncated; the tooltip and the
 * "Copy ID" action give the full value.
 */
export function ApprovalsPanel({ onApprove }: { onApprove: (proposal: OwnerProposalResponse) => void }) {
  const api = useApi();
  const proposals = useApprovalList();
  const requests = useApprovalList();
  const proposalQuery = useQuery({
    queryKey: ["organization", "approvals", "proposals", proposals.query, proposals.cursor],
    queryFn: ({ signal }) => api.organization.ownerProposals({ ...proposals.query, ...(proposals.cursor ? { continuationToken: proposals.cursor } : {}) }, signal).then((r) => r.result),
    placeholderData: (previous) => previous,
  });
  const requestQuery = useQuery({
    queryKey: ["organization", "approvals", "requests", requests.query, requests.cursor],
    queryFn: ({ signal }) => api.organization.membershipRequests({ ...requests.query, ...(requests.cursor ? { continuationToken: requests.cursor } : {}) }, signal).then((r) => r.result),
    placeholderData: (previous) => previous,
  });
  const proposalError = proposalQuery.error ? presentError(proposalQuery.error).code : null;
  const requestError = requestQuery.error ? presentError(requestQuery.error).code : null;
  useEffect(() => {
    if (proposalError && RESTART_CODES.has(proposalError) && proposals.cursor) {
      proposals.setCursor(undefined);
    }
  }, [proposalError, proposals]);
  useEffect(() => {
    if (requestError && RESTART_CODES.has(requestError) && requests.cursor) {
      requests.setCursor(undefined);
    }
  }, [requestError, requests]);

  const proposalData = proposalQuery.data;
  const requestData = requestQuery.data;
  return (
    <Card title="Owner proposals and membership requests">
      <p className="m-0 text-sm text-muted">
        Changes to an Organization Owner need a second owner within 24 hours. Membership requests need two operator approvals within 7 days; until then the person is not a member and cannot sign in
        (sign-in needs the Entra integration, PB-08).
      </p>

      <section aria-labelledby="owner-proposals-heading" className="flex flex-col gap-3">
        <h3 id="owner-proposals-heading" className="m-0 text-base font-semibold">
          Owner proposals{proposalData ? ` (${Number(proposalData.totalCount)})` : ""}
        </h3>
        <Filters list={proposals} idPrefix="proposals" noun="proposals" />
        {proposalQuery.isPending ? <LoadingBlock label="Loading owner proposals" /> : null}
        {proposalQuery.isError ? <ErrorPanel error={proposalQuery.error} onRetry={() => void proposalQuery.refetch()} /> : null}
        {proposalData ? (
          <>
            <ResponsiveRows<OwnerProposalResponse>
              caption="Owner proposals list"
              rows={[...proposalData.items]}
              rowKey={(row) => row.proposalId}
              emptyMessage={proposals.filtered ? "No proposals match." : "No owner proposals in the last 30 days."}
              columns={[
                { key: "person", header: "Person", width: "minmax(0,1.8fr)", render: (row) => <TwoLine primary={row.targetDisplayName} secondary={row.reason} /> },
                { key: "change", header: "Change", width: "minmax(0,1.2fr)", render: (row) => <span className="whitespace-nowrap">{PROPOSAL_LABELS[row.proposedChange] ?? row.proposedChange}</span> },
                {
                  key: "by",
                  header: "Proposed by",
                  width: "minmax(0,1.3fr)",
                  render: (row) => (
                    <div className="truncate" title={row.proposedByDisplayName}>
                      {row.proposedByDisplayName}
                    </div>
                  ),
                },
                {
                  key: "state",
                  header: "Status",
                  width: "minmax(0,1.3fr)",
                  render: (row) => (
                    <>
                      <span className="whitespace-nowrap">
                        <Badge tone={stateTone(row.proposalState)}>{stateLabel(row.proposalState)}</Badge>
                      </span>
                      <div className="truncate text-help text-muted">Expires {date(row.expiresOn)}</div>
                    </>
                  ),
                },
                {
                  key: "actions",
                  header: "Actions",
                  width: "9rem",
                  right: true,
                  render: (row) =>
                    row.canApprove ? (
                      <Button
                        size="sm"
                        variant="secondary"
                        aria-label={`Approve: ${PROPOSAL_LABELS[row.proposedChange] ?? row.proposedChange} for ${row.targetDisplayName}`}
                        onClick={() => onApprove(row)}
                      >
                        Approve
                      </Button>
                    ) : row.proposalState === "Pending" ? (
                      <span className="text-help text-muted">Another owner approves</span>
                    ) : null,
                },
              ]}
            />
            {Number(proposalData.totalCount) > 0 ? (
              <Pager
                label="Owner proposals pages"
                noun="proposals"
                pageStart={Number(proposalData.pageStart)}
                itemCount={proposalData.items.length}
                totalCount={Number(proposalData.totalCount)}
                pageSize={Number(proposalData.pageSize)}
                hasPrevious={!!proposalData.previousToken}
                hasNext={!!proposalData.nextToken}
                busy={proposalQuery.isFetching}
                onPrevious={() => proposals.setCursor(proposalData.previousToken ?? undefined)}
                onNext={() => proposals.setCursor(proposalData.nextToken ?? undefined)}
              />
            ) : null}
          </>
        ) : null}
      </section>

      <section aria-labelledby="membership-requests-heading" className="flex flex-col gap-3">
        <h3 id="membership-requests-heading" className="m-0 text-base font-semibold">
          Membership requests{requestData ? ` (${Number(requestData.totalCount)})` : ""}
        </h3>
        <Filters list={requests} idPrefix="requests" noun="requests" />
        {requestQuery.isPending ? <LoadingBlock label="Loading membership requests" /> : null}
        {requestQuery.isError ? <ErrorPanel error={requestQuery.error} onRetry={() => void requestQuery.refetch()} /> : null}
        {requestData ? (
          <>
            <ResponsiveRows<MembershipRequestResponse>
              caption="Membership requests list"
              rows={[...requestData.items]}
              rowKey={(row) => row.requestId}
              emptyMessage={requests.filtered ? "No requests match." : "No membership requests in the last 30 days."}
              columns={[
                { key: "person", header: "Person", width: "minmax(0,1.8fr)", render: (row) => <TwoLine primary={row.displayNameHint} secondary={row.subjectIdentifier} /> },
                { key: "role", header: "Initial role", width: "6.5rem", render: (row) => row.initialRoleName ?? <span className="text-muted">None</span> },
                {
                  key: "by",
                  header: "Requested by",
                  width: "minmax(0,1.3fr)",
                  render: (row) => (
                    <div className="truncate" title={row.requestedByDisplayName}>
                      {row.requestedByDisplayName}
                    </div>
                  ),
                },
                {
                  key: "state",
                  header: "Status",
                  width: "minmax(0,1.5fr)",
                  render: (row) => (
                    <>
                      <span className="whitespace-nowrap">
                        <Badge tone={stateTone(row.requestState)}>{row.requestState === "Pending" ? "Awaiting operators" : row.requestState}</Badge>
                      </span>
                      <div className="truncate text-help text-muted">Expires {date(row.expiresOn)}</div>
                    </>
                  ),
                },
                { key: "actions", header: "Actions", width: "6rem", right: true, render: (row) => <CopyId id={row.subjectIdentifier} name={row.displayNameHint || row.subjectIdentifier} /> },
              ]}
            />
            {Number(requestData.totalCount) > 0 ? (
              <Pager
                label="Membership requests pages"
                noun="requests"
                pageStart={Number(requestData.pageStart)}
                itemCount={requestData.items.length}
                totalCount={Number(requestData.totalCount)}
                pageSize={Number(requestData.pageSize)}
                hasPrevious={!!requestData.previousToken}
                hasNext={!!requestData.nextToken}
                busy={requestQuery.isFetching}
                onPrevious={() => requests.setCursor(requestData.previousToken ?? undefined)}
                onNext={() => requests.setCursor(requestData.nextToken ?? undefined)}
              />
            ) : null}
          </>
        ) : null}
      </section>
    </Card>
  );
}
