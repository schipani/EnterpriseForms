import { useApi } from "@enterpriseforms/app-runtime";
import type { FormVersionResponse, VersionComparisonResponse } from "@enterpriseforms/api-client";
import { Button, Card, LoadingBlock, Select, StatusMessage } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
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

/** What is being compared; <c>chosen</c> is true once the person picked a side themselves. */
export interface CompareSelection {
  from: string | undefined;
  to: string | undefined;
  chosen: boolean;
}

/**
 * Keeps the selection consistent with the versions and draft that exist now. Defaults follow new versions until the
 * person chooses; a person's choice is kept while both sides still exist; a choice that no longer exists is replaced by
 * the defaults and reported, so a comparison of something that is gone is never shown.
 */
export function reconcileSelection(current: CompareSelection, versions: readonly FormVersionResponse[], hasDraft: boolean): { selection: CompareSelection; reset: boolean } {
  const valid = new Set([...versions.map((version) => version.formVersionId), ...(hasDraft ? [DRAFT] : [])]);
  const defaults = defaultComparison(versions, hasDraft);
  if (!current.chosen) {
    const same = current.from === defaults.from && current.to === defaults.to;
    return { selection: same ? current : { ...defaults, chosen: false }, reset: false };
  }
  const stillValid = (side: string | undefined) => side === undefined || valid.has(side);
  if (stillValid(current.from) && stillValid(current.to)) {
    return { selection: current, reset: false };
  }
  return { selection: { ...defaults, chosen: false }, reset: true };
}

/**
 * Phase C9 M2 (WP-5, PG-039): compare two published versions, or a published version and the unpublished draft, with the
 * server's semantic comparison. Read-only; the comparison refreshes independently of the version list, and a draft
 * comparison is re-read whenever the draft revision changes.
 */
export function VersionComparePanel({ formDefinitionId, versions, draftRevision }: { formDefinitionId: string; versions: readonly FormVersionResponse[]; draftRevision: number | null }) {
  const api = useApi();
  const hasDraft = draftRevision !== null;
  const [selection, setSelection] = useState<CompareSelection>(() => ({ ...defaultComparison(versions, hasDraft), chosen: false }));
  const [resetNotice, setResetNotice] = useState(false);
  const versionKey = versions.map((version) => version.formVersionId).join(",");
  // Re-check the selection whenever the versions or the draft's existence change (versionKey stands for the version list).
  useEffect(() => {
    const next = reconcileSelection(selection, versions, hasDraft);
    if (next.selection !== selection) {
      setSelection(next.selection);
    }
    if (next.reset) {
      setResetNotice(true);
    }
  }, [versionKey, hasDraft]);
  const { from, to } = selection;
  const choose = (side: "from" | "to", value: string) => {
    setResetNotice(false);
    setSelection((current) => ({ ...current, [side]: value, chosen: true }));
  };
  const ordered = [...versions].sort((a, b) => Number(b.versionNumber) - Number(a.versionNumber));
  const options = [...(hasDraft ? [{ value: DRAFT, label: `Draft (unpublished, revision ${draftRevision})` }] : []), ...ordered.map((version) => ({ value: version.formVersionId, label: `v${String(version.versionNumber)}` }))];
  const nameOf = (value: string | undefined) => options.find((option) => option.value === value)?.label ?? "";
  const valid = (value: string | undefined) => value !== undefined && options.some((option) => option.value === value);
  const ready = valid(from) && valid(to) && from !== to;
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
      {resetNotice ? (
        <StatusMessage tone="warning" title="Comparison reset">
          A version or draft you chose is no longer available, so the comparison now shows {nameOf(from) || "the defaults"}
          {to ? ` → ${nameOf(to)}` : ""}.
        </StatusMessage>
      ) : null}
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-48 flex-1">
          <Select label="Compare from" value={valid(from) ? from : undefined} options={options} placeholder="Choose a version" onValueChange={(value) => choose("from", value)} />
        </div>
        <div className="min-w-48 flex-1">
          <Select label="to" value={valid(to) ? to : undefined} options={options} placeholder="Choose a version" onValueChange={(value) => choose("to", value)} />
        </div>
        <Button
          size="sm"
          variant="secondary"
          disabled={!valid(from) || !valid(to)}
          onClick={() => {
            setResetNotice(false);
            setSelection((current) => ({ from: current.to, to: current.from, chosen: true }));
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
