import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * RecordSharingPage tests: authenticated standalone route for sharing manager (C10 M3 first part).
 * Tests route/render/permission gating and appropriate denied/pending/unavailable states.
 * Missing M2 endpoints remain mocked/unavailable; no fabricated grant data.
 */

const recordId = "0199a2b3-c4d5-7e6f-8123-456789abcdef";

const baseProjection = {
  formRecordId: recordId,
  formDefinitionId: "form-1",
  formDefinitionName: "Inspection record",
  record: {
    recordId,
    recordStatus: "Draft",
    contentRevision: 1,
    authorizationRevision: 5,
    committedOperationId: "op-1",
    integrityStatus: "Verified" as const,
    values: [],
    groups: [],
  },
  fields: [],
  layoutVersion: { layoutJson: { layoutSchemaVersion: 1, sections: [] } },
  editLayoutVersionId: null,
  permissions: {
    canRead: true,
    canEdit: false,
    canManageAccess: false,
    canTransferOwnership: false,
    isCompleteProjection: true,
    contributorWritesEnabled: false,
    canEditSharedFields: false,
  },
  lifecycle: { status: "Draft" as const, contentEditable: true },
  relationships: [],
  documents: [],
  sharedFieldAccess: [],
  parentFormRecordId: null,
};

const api = {
  records: { projection: vi.fn() },
  shares: { list: vi.fn() },
  audit: { record: vi.fn() },
};

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
}));

vi.mock("@tanstack/react-router", () => ({
  useParams: () => ({ formRecordId: recordId }),
  Link: ({ children, to, params, className }: { children: ReactNode; to: string; params?: { formRecordId: string }; className?: string }) => (
    <a href={to.replace("$formRecordId", params?.formRecordId ?? "")} className={className}>
      {children}
    </a>
  ),
}));

const { RecordSharingPage } = await import("./RecordSharingPage");

function renderPage(cachedProjection?: typeof baseProjection) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (cachedProjection) {
    client.setQueryData(["records", "projection", recordId], cachedProjection);
  }
  return render(
    <QueryClientProvider client={client}>
      <RecordSharingPage />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("RecordSharingPage", () => {
  it("shows loading while verifying projection", () => {
    api.records.projection.mockImplementation(() => new Promise(() => {})); // Never resolves
    renderPage();
    expect(screen.getByRole("status")).toHaveTextContent("Verifying record and sharing permissions");
  });

  it("shows error state if projection fetch fails", async () => {
    api.records.projection.mockRejectedValue(new Error("Network error"));
    renderPage();
    await waitFor(() => {
      expect(screen.getByText("Access denied or unavailable")).toBeInTheDocument();
      expect(screen.getByText(/Network error/i)).toBeInTheDocument();
    });
  });

  it("shows access denied when user cannot manage access", async () => {
    api.records.projection.mockResolvedValue({
      result: { ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: false } },
    });
    renderPage();

    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Sharing" })).toBeInTheDocument();
      expect(screen.getByText("You cannot manage sharing for this record")).toBeInTheDocument();
      expect(screen.getByText(/Only the owner or a delegated Share Manager/i)).toBeInTheDocument();
    });

    // Should have link back to record
    const backLink = screen.getByRole("link", { name: "Back to record" });
    expect(backLink).toHaveAttribute("href", `/app/records/${recordId}`);
  });

  it("shows existing sharing data without mutation controls when the verified caller can manage access", async () => {
    api.records.projection.mockResolvedValue({
      result: { ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: true } },
    });
    api.shares.list.mockResolvedValue({ result: { items: [], totalCount: 0 } });
    api.audit.record.mockResolvedValue({ result: { events: [] } });

    renderPage();

    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Sharing" })).toBeInTheDocument();
      expect(screen.getByText("Inspection record")).toBeInTheDocument();
      expect(screen.getByText("No shares to display.")).toBeInTheDocument();
    });

    // Should show RecordAccessPanel content
    expect(screen.getByText("Your permissions on this record")).toBeInTheDocument();

    // Should have refresh and back buttons
    expect(screen.getByRole("button", { name: "Refresh sharing data" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Back to record" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Share fields" })).not.toBeInTheDocument();
    expect(screen.queryByRole("columnheader", { name: "Actions" })).not.toBeInTheDocument();
  });

  it("does not use cached manage access while rechecking the current projection", async () => {
    api.records.projection.mockResolvedValue({ result: baseProjection });
    renderPage({ ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: true } });

    expect(await screen.findByText("You cannot manage sharing for this record")).toBeInTheDocument();
    expect(api.shares.list).not.toHaveBeenCalled();
    expect(api.audit.record).not.toHaveBeenCalled();
  });

  it("renders RecordAccessPanel with correct projection when manager", async () => {
    const projection = {
      ...baseProjection,
      permissions: { ...baseProjection.permissions, canManageAccess: true },
      record: {
        ...baseProjection.record,
        values: [{ fieldKey: "given_name", stringValue: "Alice" }],
      },
    };
    api.records.projection.mockResolvedValue({ result: projection });
    api.shares.list.mockResolvedValue({ result: { items: [], totalCount: 0 } });
    api.audit.record.mockResolvedValue({ result: { events: [] } });

    renderPage();

    await waitFor(() => {
      // RecordAccessPanel should be rendered with the projection
      expect(screen.getByText("Your permissions on this record")).toBeInTheDocument();
      expect(screen.getByText(/Authorization revision/i)).toBeInTheDocument();
    });
  });

  it("handles partial projection correctly", async () => {
    const projection = {
      ...baseProjection,
      permissions: {
        ...baseProjection.permissions,
        canManageAccess: true,
        isCompleteProjection: false,
      },
    };
    api.records.projection.mockResolvedValue({ result: projection });
    api.shares.list.mockResolvedValue({ result: { items: [], totalCount: 0 } });
    // audit.record should not be called for partial projections (RecordAccessPanel logic)

    renderPage();

    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Sharing" })).toBeInTheDocument();
      expect(screen.getByText("Your permissions on this record")).toBeInTheDocument();
    });

    // Should not attempt to fetch audit for partial projection
    expect(api.audit.record).not.toHaveBeenCalled();
  });

  it("includes formDefinitionName in lead text", async () => {
    api.records.projection.mockResolvedValue({
      result: {
        ...baseProjection,
        formDefinitionName: "Investigation Record",
        permissions: { ...baseProjection.permissions, canManageAccess: true },
      },
    });
    api.shares.list.mockResolvedValue({ result: { items: [], totalCount: 0 } });
    api.audit.record.mockResolvedValue({ result: { events: [] } });

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("Investigation Record")).toBeInTheDocument();
    });
  });

  it("shows back link to record with correct params", async () => {
    api.records.projection.mockResolvedValue({
      result: { ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: false } },
    });

    renderPage();

    await waitFor(() => {
      const link = screen.getByRole("link", { name: "Back to record" });
      expect(link).toHaveAttribute("href", `/app/records/${recordId}`);
    });
  });
});
