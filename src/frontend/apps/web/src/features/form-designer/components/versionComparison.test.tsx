import type { FormVersionResponse, LayoutComparisonResponse, PublishImpactResponse } from "@enterpriseforms/api-client";
import { render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

/**
 * Phase C9 M2: the comparison view groups additions, removals (values kept), behaviour changes and presentation-only
 * changes (collapsed, never presented as destructive); the publish review says publishing never deletes values, lists
 * potential impact and the approximate counts over visible records; the compare panel chooses sensible defaults.
 */

vi.mock("@tanstack/react-router", () => ({
  Link: ({ children, to }: { children: ReactNode; to: string }) => <a href={to}>{children}</a>,
}));

const { comparisonSummary, describeDetail, changeGroup } = await import("./comparisonText");
const { LayoutComparisonView } = await import("./LayoutComparisonView");
const { PublishReview } = await import("./PublishReview");
const { defaultComparison, DRAFT } = await import("./VersionComparePanel");

const detail = (aspect: string, extra: Partial<{ significance: string; before: string | null; after: string | null; subject: string | null; tightened: boolean }> = {}) => ({ aspect, significance: "Behaviour", before: null, after: null, subject: null, tightened: false, ...extra });

const comparison: LayoutComparisonResponse = {
  addedCount: 1,
  removedCount: 1,
  behaviourChangeCount: 1,
  presentationChangeCount: 1,
  hasChanges: true,
  changes: [
    { target: "Field", elementId: "f-alias", changeType: "Added", significance: "Structure", label: "Alias", sectionTitle: "Main", details: [detail("Required", { after: "yes", tightened: true })] },
    { target: "Field", elementId: "f-given", changeType: "Modified", significance: "Behaviour", label: "First name", sectionTitle: "Main", details: [detail("Label", { significance: "Presentation", before: "Full name", after: "First name" }), detail("MaxLength", { before: "100", after: "50", tightened: true })] },
    { target: "Field", elementId: "f-notes", changeType: "Moved", significance: "Presentation", label: "Notes", sectionTitle: "Main", details: [detail("Position", { significance: "Presentation" })] },
    { target: "Field", elementId: "f-phone", changeType: "Removed", significance: "Structure", label: "Phone number", sectionTitle: "Main", details: [] },
  ],
};

describe("comparison wording", () => {
  it("words details without claiming data loss", () => {
    expect(describeDetail(detail("MaxLength", { before: "100", after: "50", tightened: true }))).toBe("Maximum length: “100” → “50” (stricter)");
    expect(describeDetail(detail("Required", { after: "yes" }))).toBe("Now required");
    expect(describeDetail(detail("OptionDeactivated", { subject: "Closed" }))).toMatch(/existing selections are kept/);
    expect(describeDetail(detail("ShowInForm", { after: "no" }))).toMatch(/stored values are kept/);
    expect(describeDetail(detail("VisibleWhen", { before: "none", after: "set" }))).toBe("Visibility rule added");
    expect(describeDetail(detail("Section", { before: "Main", after: "Contact" }))).toBe("Moved from section “Main” to “Contact”");
  });

  it("summarises the counts and groups changes", () => {
    expect(comparisonSummary(comparison)).toBe("1 added, 1 removed from the layout, 1 behaviour change, 1 presentation-only change.");
    expect(comparisonSummary({ ...comparison, hasChanges: false })).toBe("No differences.");
    expect(comparison.changes.map(changeGroup)).toEqual(["added", "behaviour", "presentation", "removed"]);
  });
});

describe("LayoutComparisonView", () => {
  it("lists additions, removals with their values kept, behaviour changes, and collapses presentation-only changes", () => {
    const { container } = render(<LayoutComparisonView comparison={comparison} fromLabel="v1" toLabel="v2" />);
    expect(screen.getByText(/v1 → v2/)).toBeInTheDocument();
    expect(within(screen.getByRole("list", { name: "Added" })).getByText("Alias")).toBeInTheDocument();
    expect(within(screen.getByRole("list", { name: "Removed from the layout" })).getByText("Phone number")).toBeInTheDocument();
    expect(screen.getByText(/Stored values are kept/)).toBeInTheDocument();
    const behaviour = within(screen.getByRole("list", { name: "Changes to how the form behaves" }));
    expect(behaviour.getByText("Maximum length: “100” → “50” (stricter)")).toBeInTheDocument();
    expect(behaviour.getByText("Label: “Full name” → “First name”")).toBeInTheDocument();
    const disclosure = container.querySelector("details");
    expect(disclosure).not.toBeNull();
    expect(disclosure!.open).toBe(false);
    expect(within(disclosure as HTMLElement).getByText("Notes")).toBeInTheDocument();
    expect(screen.getAllByRole("heading").map((heading) => heading.textContent)).toEqual(["Added (1)", "Removed from the layout (1)", "Changes to how the form behaves (1)", "Presentation-only changes (1)"]);
  });

  it("says when there are no differences", () => {
    render(<LayoutComparisonView comparison={{ addedCount: 0, removedCount: 0, behaviourChangeCount: 0, presentationChangeCount: 0, hasChanges: false, changes: [] }} />);
    expect(screen.getByText("No differences.")).toBeInTheDocument();
    expect(screen.queryAllByRole("heading")).toHaveLength(0);
  });
});

describe("PublishReview", () => {
  const impact: PublishImpactResponse = {
    formDefinitionId: "form",
    draftRevision: 7,
    currentVersionNumber: 1,
    nextVersionNumber: 2,
    versionBehavior: "Latest",
    canPublish: true,
    blockedReasonCode: null,
    blockedReason: null,
    comparison,
    notices: [
      { code: "NEW_REQUIRED_FIELDS", severity: "Attention", elementLabels: ["Alias"] },
      { code: "ELEMENTS_REMOVED", severity: "Information", elementLabels: ["Phone number"] },
    ],
    records: { visibleRecordCount: 3, shownWithNewVersionCount: 3, editedWithNewVersionCount: 3, unavailableRecordCount: 0, authorizationRevision: 9, countsAreApproximate: true, scopeStatement: "Counts cover only records you can discover." },
    dataStatement: "Publishing creates a new immutable version. It never deletes or changes stored values.",
  };
  const query = (data: PublishImpactResponse | undefined) => ({ data, isError: false, error: null }) as unknown as Parameters<typeof PublishReview>[0]["impact"];

  it("separates potential impact from data loss and shows approximate counts over visible records", () => {
    render(<PublishReview impact={query(impact)} onRetry={() => undefined} />);
    expect(screen.getByText("Stored values are never changed by publishing")).toBeInTheDocument();
    expect(within(screen.getByRole("list", { name: "Potential impact" })).getByText(/Newly required fields:/)).toBeInTheDocument();
    expect(within(screen.getByRole("list", { name: "Other effects" })).getByText(/Removed from the layout/)).toBeInTheDocument();
    const counts = within(screen.getByRole("list", { name: "Approximate record impact" }));
    expect(counts.getByText("3 records of this form that you can see.")).toBeInTheDocument();
    expect(counts.getByText("3 records would be edited with the new version.")).toBeInTheDocument();
    expect(screen.getByText("Counts cover only records you can discover.")).toBeInTheDocument();
    expect(screen.getByText("Changes from v1 to v2")).toBeInTheDocument();
  });

  it("explains a draft that is identical to the current version", () => {
    render(<PublishReview impact={query({ ...impact, canPublish: false, blockedReasonCode: "NO_DRAFT_CHANGES", blockedReason: "identical", notices: [{ code: "NO_CHANGES", severity: "Information", elementLabels: [] }], comparison: { ...comparison, hasChanges: false, changes: [] } })} onRetry={() => undefined} />);
    expect(screen.getByText("Nothing to publish")).toBeInTheDocument();
  });

  it("shows a loading state until the review arrives", () => {
    render(<PublishReview impact={query(undefined)} onRetry={() => undefined} />);
    expect(screen.getByRole("status")).toHaveTextContent("Reviewing what this version changes");
  });
});

describe("defaultComparison", () => {
  const version = (n: number) => ({ formVersionId: `v${n}`, versionNumber: n }) as unknown as FormVersionResponse;
  it("compares the current version with the draft, otherwise the previous version with the current one", () => {
    expect(defaultComparison([version(1), version(3), version(2)], true)).toEqual({ from: "v3", to: DRAFT });
    expect(defaultComparison([version(1), version(3), version(2)], false)).toEqual({ from: "v2", to: "v3" });
    expect(defaultComparison([version(1)], false)).toEqual({ from: undefined, to: undefined });
  });
});
