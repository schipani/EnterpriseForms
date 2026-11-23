import { useApi } from "@enterpriseforms/app-runtime";
import type { FormVersionResponse, VersionComparisonResponse } from "@enterpriseforms/api-client";
import { Button, Card, LoadingBlock, Select } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { LayoutComparisonView } from "./LayoutComparisonView";

export const DRAFT = "draft";

/** Defaults: the current version against the draft when there is one, otherwise the previous version against the current one. */
export function defaultComparison(versions: readonly FormVersionResponse[], hasDraft: boolean): { from: string | undefined; to: string | undefined } {
  const ordered = [...versions].sort((a, b) => Number(b.versionNumber) - Number(a.versionNumber));
  if (hasDraft && ordered[0]) {
    return { from: ordered[0].formVersionId, to: DRAFT };
  }
  if (ordered.length >= 2) {
    return { from: ordered[1]!.formVersionId, to: ordered[0]!.formVersionId };
  }
  return { from: undefined, to: undefined };
}

/**
 * Phase C9 M2 (WP-5, PG-039): compare two published versions, or a published version and the unpublished draft, with the
 * server's semantic comparison. Read-only; the comparison refreshes independently of the version list.
 */
export function VersionComparePanel({ formDefinitionId, versions, draftRevision }: { formDefinitionId: string; versions: readonly FormVersionResponse[]; draftRevision: number | null }) {
  const api = useApi();
  const hasDraft = draftRevision !== null;
  const [from, setFrom] = useState<string | undefined>(() => defaultComparison(versions, hasDraft).from);
  const [to, setTo] = useState<string | undefined>(() => defaultComparison(versions, hasDraft).to);
  const ordered = [...versions].sort((a, b) => Number(b.versionNumber) - Number(a.versionNumber));
  const options = [...(hasDraft ? [{ value: DRAFT, label: `Draft (unpublished, revision ${draftRevision})` }] : []), ...ordered.map((version) => ({ value: version.formVersionId, label: `v${String(version.versionNumber)}` }))];
  const nameOf = (value: string | undefined) => options.find((option) => option.value === value)?.label ?? "";
  const ready = from !== undefined && to !== undefined && from !== to;
  const comparison = useQuery<VersionComparisonResponse>({
    queryKey: ["forms", formDefinitionId, "compare", from, to, from === DRAFT || to === DRAFT ? draftRevision : null],
    queryFn: ({ signal }) => api.forms.compareVersions(formDefinitionId, from!, to!, signal).then((r) => r.result),
    enabled: ready,
    retry: false,
  });
  if (options.length < 2) {
    return (
      <Card title="Compare versions">
        <p className="m-0 text-sm text-muted">Comparison needs two published versions, or a published version and a draft.</p>
      </Card>
    );
  }
  return (
    <Card title="Compare versions">
      <p className="m-0 text-sm text-muted">Fields, sections and options are matched by their permanent identity, so renaming or moving something is not shown as a removal. Comparing never changes a version or the draft.</p>
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-48 flex-1">
          <Select label="Compare from" value={from} options={options} onValueChange={setFrom} />
        </div>
        <div className="min-w-48 flex-1">
          <Select label="to" value={to} options={options} onValueChange={setTo} />
        </div>
        <Button
          size="sm"
          variant="secondary"
          disabled={from === undefined || to === undefined}
          onClick={() => {
            setFrom(to);
            setTo(from);
          }}
        >
          Swap
        </Button>
      </div>
      {!ready ? <p className="m-0 text-sm">Choose two different layouts to compare.</p> : null}
      {ready && comparison.isPending ? <LoadingBlock label="Comparing" /> : null}
      {ready && comparison.isError ? <ErrorPanel error={comparison.error} onRetry={() => void comparison.refetch()} /> : null}
      {ready && comparison.data ? <LayoutComparisonView comparison={comparison.data.comparison} fromLabel={nameOf(from)} toLabel={nameOf(to)} /> : null}
    </Card>
  );
}
