import type { LayoutChangeResponse, LayoutComparisonResponse } from "@enterpriseforms/api-client";
import { Badge } from "@enterpriseforms/ui";
import { GROUP_EXPLANATIONS, GROUP_HEADINGS, changeGroup, comparisonSummary, describeDetail, targetName, type ChangeGroup } from "./comparisonText";

const ORDER: ChangeGroup[] = ["added", "removed", "behaviour", "presentation"];

/**
 * Phase C9 M2 (WP-3/WP-5): an accessible rendering of a semantic layout comparison. Changes are grouped as additions,
 * removals (stored values kept), behaviour changes and presentation-only changes; presentation-only changes are collapsed
 * under a disclosure so they are never presented as destructive. Text labels carry the meaning (never colour alone).
 */
export function LayoutComparisonView({ comparison, headingLevel = 3, fromLabel, toLabel }: { comparison: LayoutComparisonResponse; headingLevel?: 3 | 4; fromLabel?: string; toLabel?: string }) {
  const Heading = headingLevel === 3 ? "h3" : "h4";
  const groups = new Map<ChangeGroup, LayoutChangeResponse[]>(ORDER.map((group) => [group, []]));
  for (const change of comparison.changes) {
    groups.get(changeGroup(change))!.push(change);
  }
  return (
    <div className="flex flex-col gap-4" data-layout-comparison>
      <p className="m-0 text-sm" aria-live="polite">
        {fromLabel && toLabel ? (
          <>
            <span className="font-medium">
              {fromLabel} → {toLabel}:
            </span>{" "}
          </>
        ) : null}
        {comparisonSummary(comparison)}
      </p>
      {ORDER.map((group) => {
        const items = groups.get(group)!;
        if (items.length === 0) {
          return null;
        }
        const list = (
          <ul className="m-0 flex list-none flex-col gap-3 p-0" aria-label={GROUP_HEADINGS[group]}>
            {items.map((change) => (
              <ChangeItem key={`${change.target}:${change.elementId}:${change.changeType}`} change={change} />
            ))}
          </ul>
        );
        return (
          <section key={group} className="flex flex-col gap-2" aria-label={GROUP_HEADINGS[group]}>
            {group === "presentation" ? (
              <details>
                <summary className="cursor-pointer">
                  <Heading className="m-0 inline text-base font-semibold">
                    {GROUP_HEADINGS[group]} ({items.length})
                  </Heading>
                </summary>
                <p className="mb-2 mt-1 text-sm text-muted">{GROUP_EXPLANATIONS[group]}</p>
                {list}
              </details>
            ) : (
              <>
                <Heading className="m-0 text-base font-semibold">
                  {GROUP_HEADINGS[group]} ({items.length})
                </Heading>
                <p className="m-0 text-sm text-muted">{GROUP_EXPLANATIONS[group]}</p>
                {list}
              </>
            )}
          </section>
        );
      })}
    </div>
  );
}

function ChangeItem({ change }: { change: LayoutChangeResponse }) {
  return (
    <li className="rounded-control border border-line p-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="break-words font-medium">{change.label}</span>
        <Badge tone="neutral">{targetName(change.target)}</Badge>
        {change.changeType === "Moved" ? <Badge tone="neutral">Moved</Badge> : null}
        {change.sectionTitle && change.target !== "Section" ? <span className="text-sm text-muted">in {change.sectionTitle}</span> : null}
      </div>
      {change.details.length > 0 ? (
        <ul className="mb-0 mt-2 flex list-disc flex-col gap-1 pl-5 text-sm">
          {change.details.map((detail, index) => (
            <li key={`${detail.aspect}:${detail.subject ?? ""}:${index}`}>{describeDetail(detail)}</li>
          ))}
        </ul>
      ) : null}
    </li>
  );
}
