import { useApi } from "@enterpriseforms/app-runtime";
import type { PublishImpactResponse } from "@enterpriseforms/api-client";
import { Button, Card, LoadingBlock, StatusMessage } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { ErrorPanel } from "../../support";
import { LayoutComparisonView } from "./LayoutComparisonView";
import { noticeText } from "./comparisonText";

/**
 * Phase C9 M2 (WP-4, PG-038): before publishing, what the new version would change — the semantic comparison with the
 * current version, notices about potential impact on future edits, and approximate counts limited to records the designer
 * can see (D9-4). The comparison and the record counts refresh independently. It states plainly that publishing never
 * deletes or changes stored values. Publishing itself stays the same audited, revision-checked operation.
 */
export function usePublishImpact(formDefinitionId: string, draftRevision: number, enabled: boolean) {
  const api = useApi();
  return useQuery<PublishImpactResponse>({
    queryKey: ["forms", formDefinitionId, "publish-impact", draftRevision],
    queryFn: ({ signal }) => api.forms.publishImpact(formDefinitionId, draftRevision, signal).then((r) => r.result),
    enabled,
    retry: false,
    staleTime: 0,
  });
}

export function PublishReview({ impact, onRetry }: { impact: ReturnType<typeof usePublishImpact>; onRetry: () => void }) {
  if (impact.isError) {
    return <ErrorPanel error={impact.error} onRetry={onRetry} />;
  }
  if (!impact.data) {
    return <LoadingBlock label="Reviewing what this version changes" />;
  }
  const data = impact.data;
  const records = data.records;
  const visible = Number(records.visibleRecordCount);
  const recordWord = (n: number) => (n === 1 ? "1 record" : `${n} records`);
  const attention = data.notices.filter((notice) => notice.severity === "Attention");
  const information = data.notices.filter((notice) => notice.severity !== "Attention" && notice.code !== "NO_CHANGES");
  return (
    <div className="flex flex-col gap-4" data-publish-review>
      {!data.canPublish ? (
        <StatusMessage tone="warning" title={data.blockedReasonCode === "NO_DRAFT_CHANGES" ? "Nothing to publish" : "This draft can't be published yet"}>
          {data.blockedReasonCode === "NO_DRAFT_CHANGES" ? "The draft is the same as the current version." : data.blockedReason}
        </StatusMessage>
      ) : null}
      <StatusMessage tone="info" title="Stored values are never changed by publishing">
        {data.dataStatement}
      </StatusMessage>
      {attention.length > 0 ? (
        <StatusMessage tone="warning" title="May need attention when records are next edited">
          <ul className="m-0 flex list-disc flex-col gap-2 pl-5" aria-label="Potential impact">
            {attention.map((notice) => (
              <li key={notice.code}>
                <span className="font-medium">{noticeText(notice).title}:</span> {notice.elementLabels.join(", ")}. {noticeText(notice).body}
              </li>
            ))}
          </ul>
        </StatusMessage>
      ) : null}
      {information.length > 0 ? (
        <ul className="m-0 flex list-disc flex-col gap-1 pl-5 text-sm" aria-label="Other effects">
          {information.map((notice) => (
            <li key={notice.code}>
              <span className="font-medium">{noticeText(notice).title}</span>
              {notice.elementLabels.length > 0 ? `: ${notice.elementLabels.join(", ")}` : ""}. {noticeText(notice).body}
            </li>
          ))}
        </ul>
      ) : null}
      <Card title="Records you can see">
        <ul className="m-0 flex list-disc flex-col gap-1 pl-5 text-sm" aria-label="Approximate record impact">
          <li>{visible === 0 ? "You can't see any records of this form." : `${recordWord(visible)} of this form that you can see.`}</li>
          {visible > 0 ? <li>{`${recordWord(Number(records.shownWithNewVersionCount))} would be shown with the new version.`}</li> : null}
          {visible > 0 ? <li>{`${recordWord(Number(records.editedWithNewVersionCount))} would be edited with the new version.`}</li> : null}
          {Number(records.unavailableRecordCount) > 0 ? <li>{`${recordWord(Number(records.unavailableRecordCount))} you can access couldn't be described and ${Number(records.unavailableRecordCount) === 1 ? "isn't" : "aren't"} counted.`}</li> : null}
        </ul>
        <p className="mb-0 mt-2 text-sm text-muted">{records.scopeStatement}</p>
      </Card>
      <Card title={data.currentVersionNumber === null || data.currentVersionNumber === undefined ? "What the first version contains" : `Changes from v${String(data.currentVersionNumber)} to v${String(data.nextVersionNumber)}`}>
        <LayoutComparisonView comparison={data.comparison} headingLevel={4} />
      </Card>
      <div>
        <Button size="sm" variant="ghost" onClick={onRetry}>
          Refresh review
        </Button>
      </div>
    </div>
  );
}
