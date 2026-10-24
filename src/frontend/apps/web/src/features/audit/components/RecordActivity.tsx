import { presentError, useApi } from "@enterpriseforms/app-runtime";
import { Button, LoadingBlock, StatusMessage } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { ErrorPanel } from "../../support";
import { ActivityTimeline } from "./ActivityTimeline";

export function RecordActivity({ formRecordId }: { formRecordId: string }) {
  const api = useApi();
  const activity = useQuery({ queryKey: ["audit", "record", formRecordId], queryFn: () => api.audit.record(formRecordId).then((r) => r.result) });
  if (activity.isPending) {
    return <LoadingBlock label="Loading activity" />;
  }
  if (activity.isError) {
    // Expected refusal (the person can't read the whole record, e.g. a field-share recipient who opened the address directly,
    // or access was just removed): a plain unavailable state, not a technical error. Real failures keep the error panel.
    const refusal = presentError(activity.error);
    if (refusal.status === 404 || refusal.status === 403) {
      return (
        <StatusMessage tone="info" title="Activity isn't available for this record">
          The activity log is shown to people who can see the whole record. Your access to this record is limited to what was shared with you.
        </StatusMessage>
      );
    }
    return <ErrorPanel error={activity.error} onRetry={() => void activity.refetch()} />;
  }
  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center justify-between gap-2">
        <StatusMessage tone="info" title="Trust note">
          {activity.data.trustNote}
        </StatusMessage>
        <Button size="sm" variant="secondary" busy={activity.isFetching} onClick={() => void activity.refetch()}>
          Refresh
        </Button>
      </div>
      <ActivityTimeline events={activity.data.events} />
    </div>
  );
}
