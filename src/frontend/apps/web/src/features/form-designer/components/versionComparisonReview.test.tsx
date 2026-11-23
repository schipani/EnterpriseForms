import type { FormVersionResponse, PublishImpactResponse } from "@enterpriseforms/api-client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Corrective review of PR #71: Publish is available only on a loaded, current, successful review of exactly the draft
 * revision being published; the compare panel never compares something that no longer exists, follows new versions while
 * the person has not chosen, keeps a valid choice, and re-reads a draft comparison when the draft revision changes.
 */

const api = { forms: { compareVersions: vi.fn() } };

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
}));

vi.mock("@tanstack/react-router", () => ({
  Link: ({ children, to }: { children: ReactNode; to: string }) => <a href={to}>{children}</a>,
}));

const { publishGate, PublishGateNote } = await import("./PublishReview");
const { VersionComparePanel, reconcileSelection, DRAFT } = await import("./VersionComparePanel");

const review = (draftRevision: number, canPublish = true) => ({ draftRevision, canPublish }) as unknown as PublishImpactResponse;
const version = (n: number) => ({ formVersionId: `v${n}`, versionNumber: n }) as unknown as FormVersionResponse;
const empty = { addedCount: 0, removedCount: 0, behaviourChangeCount: 0, presentationChangeCount: 0, hasChanges: false, changes: [] };

beforeEach(() => {
  vi.clearAllMocks();
  api.forms.compareVersions.mockImplementation((_form: string, from: string, to: string) => Promise.resolve({ result: { formDefinitionId: "form", from: { kind: from === DRAFT ? "Draft" : "Version" }, to: { kind: to === DRAFT ? "Draft" : "Version" }, comparison: empty } }));
});

describe("publishGate", () => {
  it("allows publishing only on a loaded, current, successful review that says the draft can be published", () => {
    expect(publishGate({ data: review(7), isError: false, isFetching: false }, 7)).toMatchObject({ allowed: true, state: "ready" });
  });

  it("blocks while the review is loading", () => {
    expect(publishGate({ data: undefined, isError: false, isFetching: true }, 7)).toMatchObject({ allowed: false, state: "loading" });
  });

  it("blocks when the review failed, even if an earlier review is still held", () => {
    expect(publishGate({ data: undefined, isError: true, isFetching: false }, 7)).toMatchObject({ allowed: false, state: "failed" });
    expect(publishGate({ data: review(7), isError: true, isFetching: false }, 7)).toMatchObject({ allowed: false, state: "failed" });
  });

  it("blocks a review made for another draft revision and a review that is being refreshed", () => {
    expect(publishGate({ data: review(6), isError: false, isFetching: false }, 7)).toMatchObject({ allowed: false, state: "stale" });
    expect(publishGate({ data: review(7), isError: false, isFetching: true }, 7)).toMatchObject({ allowed: false, state: "refreshing" });
  });

  it("blocks a draft the server says cannot be published", () => {
    expect(publishGate({ data: review(7, false), isError: false, isFetching: false }, 7)).toMatchObject({ allowed: false, state: "blocked" });
  });

  it("says why Publish is not available yet", () => {
    render(<PublishGateNote gate={publishGate({ data: undefined, isError: true, isFetching: false }, 7)} />);
    expect(screen.getByRole("status")).toHaveTextContent("couldn't be loaded");
  });
});

describe("reconcileSelection", () => {
  const versions = [version(1), version(2)];
  it("follows the defaults while the person has not chosen", () => {
    const first = reconcileSelection({ from: "v1", to: "v2", chosen: false }, versions, false);
    expect(first).toEqual({ selection: { from: "v1", to: "v2", chosen: false }, reset: false });
    expect(reconcileSelection(first.selection, [...versions, version(3)], false)).toEqual({ selection: { from: "v2", to: "v3", chosen: false }, reset: false });
  });

  it("keeps a choice that still exists", () => {
    const chosen = { from: "v1", to: DRAFT, chosen: true };
    expect(reconcileSelection(chosen, [...versions, version(3)], true)).toEqual({ selection: chosen, reset: false });
  });

  it("resets and reports a choice that no longer exists", () => {
    expect(reconcileSelection({ from: "v1", to: DRAFT, chosen: true }, versions, false)).toEqual({ selection: { from: "v1", to: "v2", chosen: false }, reset: true });
    expect(reconcileSelection({ from: "v9", to: "v2", chosen: true }, versions, false).reset).toBe(true);
  });
});

describe("VersionComparePanel", () => {
  function renderPanel(versions: FormVersionResponse[], draftRevision: number | null) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const view = render(
      <QueryClientProvider client={client}>
        <VersionComparePanel formDefinitionId="form" versions={versions} draftRevision={draftRevision} />
      </QueryClientProvider>,
    );
    const rerender = (nextVersions: FormVersionResponse[], nextDraft: number | null) =>
      view.rerender(
        <QueryClientProvider client={client}>
          <VersionComparePanel formDefinitionId="form" versions={nextVersions} draftRevision={nextDraft} />
        </QueryClientProvider>,
      );
    return { rerender };
  }

  it("moves the default comparison forward when a new version is published", async () => {
    const { rerender } = renderPanel([version(1), version(2)], null);
    await waitFor(() => expect(api.forms.compareVersions).toHaveBeenLastCalledWith("form", "v1", "v2", expect.anything()));
    rerender([version(1), version(2), version(3)], null);
    await waitFor(() => expect(api.forms.compareVersions).toHaveBeenLastCalledWith("form", "v2", "v3", expect.anything()));
  });

  it("re-reads a draft comparison when the draft revision changes", async () => {
    const { rerender } = renderPanel([version(1)], 4);
    await waitFor(() => expect(api.forms.compareVersions).toHaveBeenCalledTimes(1));
    expect(screen.getByText("Draft (unpublished, revision 4)")).toBeInTheDocument();
    rerender([version(1)], 5);
    await waitFor(() => expect(api.forms.compareVersions).toHaveBeenCalledTimes(2));
    expect(screen.getByText("Draft (unpublished, revision 5)")).toBeInTheDocument();
  });

  it("keeps a chosen comparison while it is valid and reports when it is no longer available", async () => {
    const { rerender } = renderPanel([version(1), version(2)], 4); // default v2 → draft
    await waitFor(() => expect(api.forms.compareVersions).toHaveBeenLastCalledWith("form", "v2", DRAFT, expect.anything()));
    fireEvent.click(screen.getByRole("button", { name: "Swap" })); // a deliberate choice: draft → v2
    await waitFor(() => expect(api.forms.compareVersions).toHaveBeenLastCalledWith("form", DRAFT, "v2", expect.anything()));
    rerender([version(1), version(2), version(3)], 4); // still valid: kept
    await waitFor(() => expect(api.forms.compareVersions).toHaveBeenLastCalledWith("form", DRAFT, "v2", expect.anything()));
    expect(screen.queryByText("Comparison reset")).not.toBeInTheDocument();
    rerender([version(1), version(2), version(3)], null); // the draft is gone: never compared, reset and reported
    expect(await screen.findByText("Comparison reset")).toBeInTheDocument();
    await waitFor(() => expect(api.forms.compareVersions).toHaveBeenLastCalledWith("form", "v2", "v3", expect.anything()));
  });
});
