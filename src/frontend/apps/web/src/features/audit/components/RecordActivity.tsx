import { useApi } from "@enterpriseforms/app-runtime";
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
