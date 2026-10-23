import { presentError, useApi } from "@enterpriseforms/app-runtime";
import type { RecordListItemResponse, RecordLookupResponse } from "@enterpriseforms/api-client";
import { Badge, Button, Card, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useQuery, type UseQueryResult } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState, type FormEvent } from "react";
import { formatDateTime, parseLookupInput, recordStatusLabel, referenceFor, type LookupInput } from "./recordList";

const INVALID_MESSAGE = "Enter a record reference such as REC-1A2B3C4D or a full record ID.";

/**
 * "Find a record" on My Records (Phase C6). Exact lookup only: a full record id or a short reference REC-XXXXXXXX. The
 * server decides matches with the authority, so "no record found" never reveals whether an inaccessible record exists.
 * A short reference can belong to more than one record; then every authorized match is listed and none is chosen.
 * Technical failures are reported separately from "not found". Archived and related records are included.
 */
export function RecordLookup() {
  const api = useApi();
  const [text, setText] = useState("");
  const [invalid, setInvalid] = useState(false);
  const [lookup, setLookup] = useState<LookupInput | null>(null);
  const result = useQuery<RecordLookupResponse>({
    queryKey: ["records", "lookup", lookup?.value],
    queryFn: ({ signal }) => api.records.lookup(lookup!.value, signal).then((r) => r.result),
    enabled: lookup !== null,
    // A failure is shown at once as a technical problem with its own retry, never as "not found".
    retry: false,
    staleTime: 0,
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const parsed = parseLookupInput(text);
    setInvalid(parsed === null);
    if (parsed === null) {
      return;
    }
    if (parsed.value === lookup?.value) {
      void result.refetch();
    }
    setLookup(parsed);
  };

  const clear = () => {
    setText("");
    setInvalid(false);
    setLookup(null);
  };

  return (
    <Card title="Find a record" aria-label="Find a record">
      <form role="search" aria-label="Find a record by reference or ID" onSubmit={submit} className="flex flex-col gap-3 sm:flex-row sm:items-end" noValidate>
        <TextInput
          label="Record reference or ID"
          helpText="Paste a reference such as REC-1A2B3C4D or a full record ID. This finds one exact record; it does not search inside records."
          value={text}
          autoComplete="off"
          spellCheck={false}
          onChange={(event) => {
            setText(event.target.value);
            setInvalid(false);
          }}
          error={invalid ? INVALID_MESSAGE : undefined}
          className="min-w-0 flex-1"
        />
        <div className="flex gap-2 sm:mb-0.5">
          <Button type="submit" busy={lookup !== null && result.isFetching}>
            Search
          </Button>
          {lookup !== null || text ? (
            <Button type="button" variant="ghost" onClick={clear}>
              Clear
            </Button>
          ) : null}
        </div>
      </form>
      <div aria-live="polite">{lookup ? <LookupOutcome lookup={lookup} result={result} /> : null}</div>
    </Card>
  );
}

function LookupOutcome({ lookup, result }: { lookup: LookupInput; result: UseQueryResult<RecordLookupResponse> }) {
  if (result.isPending && result.isFetching) {
    return <p className="text-sm text-muted">Looking up {lookup.value}…</p>;
  }
  if (result.isError) {
    const error = presentError(result.error);
    if (error.code === "LOOKUP_INVALID") {
      return <StatusMessage tone="warning" title="Not a record reference or ID">{INVALID_MESSAGE}</StatusMessage>;
    }
    if (error.code === "RECORD_LIST_TOO_LARGE") {
      return (
        <StatusMessage tone="warning" title="This reference can't be checked completely">
          You can access more records than a short reference can be checked against completely, so no partial answer is shown. Paste the full record ID instead.
        </StatusMessage>
      );
    }
    return (
      <StatusMessage tone="danger" title="The lookup could not be completed" actions={<Button size="sm" variant="secondary" onClick={() => void result.refetch()}>Try again</Button>}>
        This is a technical problem, not a missing record: we could not check {lookup.value} right now.
        {error.correlationId ? <> Reference for support: {error.correlationId}.</> : null}
      </StatusMessage>
    );
  }
  const data = result.data;
  if (!data) {
    return null;
  }
  const matches = data.matches ?? [];
  const unavailable = data.unavailableRecordIds ?? [];
  const total = Number(data.matchCount ?? 0);
  if (total === 0) {
    return (
      <StatusMessage tone="info" title="No record found">
        No record {lookup.kind === "Reference" ? "with reference" : "with ID"} {data.normalizedQuery} exists that you can access. Check the {lookup.kind === "Reference" ? "reference" : "ID"}, or ask the record's owner to share it with you.
      </StatusMessage>
    );
  }
  return (
    <div className="flex flex-col gap-2">
      <StatusMessage tone={total === 1 ? "success" : "info"} title={total === 1 ? "Record found" : `${total} records have reference ${data.normalizedQuery}`}>
        {total === 1 ? "Open it below." : "A short reference can belong to more than one record. Choose the one you mean; check the form and the dates."}
        {matches.length + unavailable.length < total ? ` Showing the ${matches.length + unavailable.length} most recently changed.` : null}
      </StatusMessage>
      <ul className="flex flex-col gap-2" aria-label="Matching records">
        {matches.map((match) => (
          <MatchRow key={match.formRecordId} match={match} />
        ))}
        {unavailable.map((id) => (
          <li key={id} className="flex flex-wrap items-center justify-between gap-2 rounded border border-line bg-page px-3 py-2">
            <span>
              <span className="font-medium">{referenceFor(id)}</span>
              <span className="text-sm text-muted"> · the application database holds no details for this record; open it to check</span>
            </span>
            <Link to="/app/records/$formRecordId" params={{ formRecordId: id }} aria-label={`Open record ${referenceFor(id)}`}>
              <Button size="sm">Open record</Button>
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}

function MatchRow({ match }: { match: RecordListItemResponse }) {
  const name = match.formDefinitionName || "Unnamed form";
  return (
    <li className="flex flex-wrap items-center justify-between gap-2 rounded border border-line bg-page px-3 py-2">
      <span className="flex min-w-0 flex-col">
        <span className="flex flex-wrap items-center gap-2">
          <span className="font-medium">{name}</span>
          <span className="text-sm">{match.recordReference}</span>
          <Badge tone={match.recordStatus === "Archived" ? "warning" : "neutral"}>{recordStatusLabel(match.recordStatus)}</Badge>
        </span>
        <span className="text-xs text-muted">
          Created {formatDateTime(match.createdOn)}
          {match.createdByCurrentUser ? " by you" : match.createdByDisplayName ? ` by ${match.createdByDisplayName}` : ""} · last changed {formatDateTime(match.modifiedOn)}
          {match.parentFormRecordId ? ` · related to ${referenceFor(match.parentFormRecordId)}` : ""}
          {match.recordStatus === "Archived" ? " · archived records are hidden from the list below unless included" : ""}
        </span>
      </span>
      <Link to="/app/records/$formRecordId" params={{ formRecordId: match.formRecordId }} aria-label={`Open record ${name} ${match.recordReference}`}>
        <Button size="sm">Open record</Button>
      </Link>
    </li>
  );
}
