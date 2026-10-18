import type { AuditEventResponse } from "@enterpriseforms/api-client";
import { Badge } from "@enterpriseforms/ui";

/** Chronological activity list (event time + sequence ordering is server-side; this is a local projection, B-06). */
export function ActivityTimeline({ events }: { events: readonly AuditEventResponse[] }) {
  if (events.length === 0) {
    return <p className="text-muted">No activity recorded.</p>;
  }
  return (
    <ol className="flex flex-col divide-y divide-line">
      {events.map((event) => (
        <li key={event.auditEventId} className="flex flex-col gap-1 py-2 sm:flex-row sm:items-start sm:gap-4">
          <time dateTime={event.eventOccurredOn} className="shrink-0 text-sm text-muted sm:w-44">
            {new Date(event.eventOccurredOn).toLocaleString()}
          </time>
          <div className="min-w-0 flex-1">
            <p className="font-medium">
              {event.eventAction.replaceAll("_", " ").toLowerCase()} <Badge tone={event.eventOutcome === "Success" ? "success" : event.eventOutcome === "Denied" ? "danger" : "neutral"}>{event.eventOutcome}</Badge>
            </p>
            <p className="text-sm text-muted">
              {event.eventDescription}
              {event.actorDisplayName ? ` · by ${event.actorDisplayName}` : ""}
            </p>
          </div>
        </li>
      ))}
    </ol>
  );
}
