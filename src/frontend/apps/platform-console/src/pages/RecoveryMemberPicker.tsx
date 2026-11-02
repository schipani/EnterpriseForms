import { Button, StatusMessage, TextInput, useDebouncedValue } from "@enterpriseforms/ui";
import { useInfiniteQuery } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { consoleApi, type RecoveryMemberItem } from "../api/client";
import { describeError } from "../app/errors";
import { formatDateTime, identitySummary } from "../app/format";
import { useConsoleSession } from "../app/session";

const PAGE_SIZE = 10;

/** Case-insensitive identity comparison key: issuer plus subject. */
export const memberIdentityKey = (issuerUri: string, subjectIdentifier: string) => `${issuerUri.trim()}|${subjectIdentifier.trim().toLowerCase()}`;

export interface RecoveryMemberPickerProps {
  organizationId: string;
  /** Authority user ids already chosen as replacements. */
  selectedUserIds: readonly string[];
  /** `issuer|subject` of every identity already proposed (members or new identities). */
  proposedIdentities: ReadonlySet<string>;
  /** True when no further replacement can be added. */
  full: boolean;
  onPick: (member: RecoveryMemberItem) => void;
  /** Organization owner capacity from the Authority (active owners and the maximum). */
  onCapacity?: (capacity: { active: number; maximum: number }) => void;
  /** Identity keys of the members currently listed. */
  onListed?: (keys: ReadonlySet<string>) => void;
}

/** Searchable list of the organization's enabled non-owner members (the Authority filters and pages it); the user id is used internally and never typed. */
export function RecoveryMemberPicker({ organizationId, selectedUserIds, proposedIdentities, full, onPick, onCapacity, onListed }: RecoveryMemberPickerProps) {
  const { touch, handleError } = useConsoleSession();
  const [search, setSearch] = useState("");
  const q = useDebouncedValue(search.trim(), 350);
  const exclude = useMemo(() => [...selectedUserIds].sort(), [selectedUserIds]);
  const query = useInfiniteQuery({
    queryKey: ["recovery-members", organizationId, q, exclude],
    queryFn: ({ pageParam }) => consoleApi.recoveryMembers(organizationId, { q, cursor: pageParam, limit: PAGE_SIZE, exclude }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    gcTime: 0,
    retry: false,
  });
  useEffect(() => {
    if (query.isSuccess) {
      touch();
    }
  }, [query.isSuccess, query.dataUpdatedAt, touch]);
  useEffect(() => {
    if (query.isError) {
      handleError(query.error);
    }
  }, [query.isError, query.error, handleError]);

  const items = useMemo(() => query.data?.pages.flatMap((page) => page.items) ?? [], [query.data]);
  const firstPage = query.data?.pages[0];
  const activeOwners = firstPage ? Number(firstPage.activeOwnerCount) : null;
  const maximumOwners = firstPage ? Number(firstPage.maximumOwners) : null;
  useEffect(() => {
    if (activeOwners !== null && maximumOwners !== null) {
      onCapacity?.({ active: activeOwners, maximum: maximumOwners });
    }
  }, [activeOwners, maximumOwners, onCapacity]);
  useEffect(() => {
    onListed?.(new Set(items.map((m) => memberIdentityKey(m.issuerUri, m.subjectIdentifier))));
  }, [items, onListed]);
  const total = Number(query.data?.pages[0]?.totalCount ?? 0);
  const failure = query.isError ? describeError(query.error) : null;
  const settled = query.isSuccess && !query.isFetching;
  const summary = settled ? (items.length === 0 ? "No members found." : `Showing ${items.length} of ${total} active ${total === 1 ? "member" : "members"}.`) : "Searching members.";

  return (
    <div className="flex flex-col gap-3 rounded-control border border-line p-4">
      <h3 className="text-sm font-semibold">Add an existing member</h3>
      <TextInput
        label="Search members"
        type="search"
        value={search}
        onChange={(event) => setSearch(event.target.value)}
        placeholder="Name or subject identifier"
        maxLength={200}
        autoComplete="off"
        helpText="Only active members who are not owners or Platform Operators are listed."
      />
      <p aria-live="polite" className="text-help text-muted">
        {failure ? "" : summary}
      </p>
      {full ? <p className="text-sm font-medium">Maximum of three replacement owners reached</p> : null}
      {failure ? (
        <StatusMessage tone={failure.tone} title={failure.title}>
          {failure.message}
          {failure.reference ? ` Reference ${failure.reference}.` : ""}
          <span className="mt-2 block">
            <Button type="button" variant="secondary" size="sm" onClick={() => void query.refetch()}>
              Try again
            </Button>
          </span>
        </StatusMessage>
      ) : null}
      {settled && items.length === 0 ? (
        <p className="text-sm text-muted">{q ? "No active member matches this search." : "This organization has no other active members who can become owners."}</p>
      ) : null}
      {items.length > 0 ? (
        <ul aria-label="Members" className="flex list-none flex-col gap-2 p-0">
          {items.map((member) => {
            const identity = identitySummary(member);
            const chosen = selectedUserIds.includes(member.authorityUserId);
            const duplicate = !chosen && proposedIdentities.has(memberIdentityKey(member.issuerUri, member.subjectIdentifier));
            return (
              <li key={member.authorityUserId} className="flex flex-wrap items-center justify-between gap-2 rounded-control border border-line p-3">
                <div className="min-w-0">
                  <p className="break-words font-medium">{member.displayName}</p>
                  <p className="break-words text-help text-muted">
                    {identity.subject} · {identity.issuer}
                  </p>
                  <p className="text-help">Active member since {formatDateTime(member.memberSince)}</p>
                </div>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={chosen || duplicate || full}
                  aria-label={chosen || duplicate ? `${member.displayName} already added` : `Add ${member.displayName}`}
                  onClick={() => onPick(member)}
                >
                  {chosen || duplicate ? "Added" : "Add"}
                </Button>
              </li>
            );
          })}
        </ul>
      ) : null}
      {query.hasNextPage ? (
        <Button type="button" variant="secondary" size="sm" className="self-start" busy={query.isFetchingNextPage} disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
          Load more
        </Button>
      ) : null}
    </div>
  );
}
