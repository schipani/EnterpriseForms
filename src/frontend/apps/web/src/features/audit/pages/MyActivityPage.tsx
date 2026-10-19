import { useApi } from "@enterpriseforms/app-runtime";
import { Button, Card, LoadingBlock, PageHeader } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { ErrorPanel } from "../../support";
import { ActivityTimeline } from "../components/ActivityTimeline";

export function MyActivityPage() {
  const api = useApi();
  const activity = useQuery({ queryKey: ["audit", "mine"], queryFn: () => api.audit.mine(50).then((r) => r.result) });
  return (
    <>
      <PageHeader title="My activity" lead="Your own recent actions in this organization (local audit projection)." actions={<Button variant="secondary" busy={activity.isFetching} onClick={() => void activity.refetch()}>Refresh</Button>} />
      <Card>
        {activity.isPending ? <LoadingBlock label="Loading activity" /> : activity.isError ? <ErrorPanel error={activity.error} onRetry={() => void activity.refetch()} /> : <ActivityTimeline events={activity.data} />}
      </Card>
    </>
  );
}
