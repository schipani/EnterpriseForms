import { onlineManager, QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * RecordSharingPage tests: authenticated standalone route for sharing manager (C10 M3 first part).
 * Tests route/render/permission gating and appropriate denied/pending/unavailable states.
 * Missing M2 endpoints remain mocked/unavailable; no fabricated grant data.
 */

const recordId = "0199a2b3-c4d5-7e6f-8123-456789abcdef";
const identityScope = ["org-1", "user-1", "2026-11-26T18:00:00Z", 5] as const;
let sessionStatus = "authenticated";

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
  useSession: () => ({
    status: sessionStatus,
    session: { authenticated: true, organizationId: identityScope[0], userId: identityScope[1], expiresOn: identityScope[2] },
    bootstrap: { authorizationRevision: identityScope[3] },
  }),
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

function renderPage(cachedProjection?: typeof baseProjection, cachedRecipient?: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (cachedProjection) {
    client.setQueryData(["record-sharing", "projection", ...identityScope, recordId], cachedProjection);
  }
  if (cachedRecipient) {
    client.setQueryData(["record-sharing", "shares", ...identityScope, recordId, baseProjection.record.authorizationRevision],
      { items: [shareRow(cachedRecipient)], authorizationRevision: baseProjection.record.authorizationRevision });
  }
  return render(
    <QueryClientProvider client={client}>
      <RecordSharingPage />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  sessionStatus = "authenticated";
});

afterEach(() => {
  onlineManager.setOnline(true);
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
      result: { ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: true, canTransferOwnership: true } },
    });
    api.shares.list.mockResolvedValue({ result: { items: [], authorizationRevision: baseProjection.record.authorizationRevision } });
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
    expect(screen.getByRole("button", { name: "Refresh access" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Back to record" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Share fields" })).not.toBeInTheDocument();
    expect(screen.queryByRole("columnheader", { name: "Actions" })).not.toBeInTheDocument();
  });

  it("does not use cached manage access while rechecking the current projection", async () => {
    api.records.projection.mockResolvedValue({ result: baseProjection });
    renderPage({ ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: true, canTransferOwnership: true } });

    expect(await screen.findByText("You cannot manage sharing for this record")).toBeInTheDocument();
    expect(api.shares.list).not.toHaveBeenCalled();
    expect(api.audit.record).not.toHaveBeenCalled();
  });

  it("withholds previously verified rows during a new access check", async () => {
    const authorized = { ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: true, canTransferOwnership: true } };
    let completeRefresh: (value: { result: typeof baseProjection }) => void = () => {};
    api.records.projection.mockResolvedValueOnce({ result: authorized }).mockImplementationOnce(() =>
      new Promise<{ result: typeof baseProjection }>((resolve) => { completeRefresh = resolve; }),
    );
    api.shares.list.mockResolvedValue({ result: { items: [shareRow("Previously visible recipient")], authorizationRevision: baseProjection.record.authorizationRevision } });
    api.audit.record.mockResolvedValue({ result: { events: [] } });
    renderPage();

    expect(await screen.findByRole("rowheader", { name: "Previously visible recipient" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Refresh access" }));
    expect(await screen.findByText("Verifying record and sharing permissions")).toBeInTheDocument();
    expect(screen.queryByText("Previously visible recipient")).not.toBeInTheDocument();

    await act(async () => { completeRefresh({ result: baseProjection }); });
    expect(await screen.findByText("You cannot manage sharing for this record")).toBeInTheDocument();
    expect(screen.queryByText("Previously visible recipient")).not.toBeInTheDocument();
    expect(api.shares.list).toHaveBeenCalledTimes(1);
  });

  it("withholds sharing details if the cached permission cannot be reverified offline", () => {
    onlineManager.setOnline(false);
    renderPage({ ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: true, canTransferOwnership: true } });

    expect(screen.getByText("Waiting for a connection to verify sharing access")).toBeInTheDocument();
    expect(screen.queryByText("Your permissions on this record")).not.toBeInTheDocument();
    expect(api.records.projection).not.toHaveBeenCalled();
    expect(api.shares.list).not.toHaveBeenCalled();
    expect(api.audit.record).not.toHaveBeenCalled();
  });

  it("withholds sharing details when the shell session is expired", () => {
    sessionStatus = "expired";
    renderPage({ ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: true, canTransferOwnership: true } }, "Previously visible recipient");

    expect(screen.getByText("Sign in to verify sharing access")).toBeInTheDocument();
    expect(screen.queryByText("Previously visible recipient")).not.toBeInTheDocument();
    expect(api.records.projection).not.toHaveBeenCalled();
    expect(api.shares.list).not.toHaveBeenCalled();
    expect(api.audit.record).not.toHaveBeenCalled();
  });

  it("withholds a cached share when its data cannot be reverified", async () => {
    api.records.projection.mockResolvedValue({ result: { ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: true, canTransferOwnership: true } } });
    api.shares.list.mockRejectedValue(new Error("The current sharing request was denied"));
    api.audit.record.mockResolvedValue({ result: { events: [] } });
    renderPage(undefined, "Previously visible recipient");

    expect(await screen.findByText("The current sharing request was denied")).toBeInTheDocument();
    expect(screen.queryByText("Previously visible recipient")).not.toBeInTheDocument();
  });

  it("withholds record-wide grants returned under a different authorization revision", async () => {
    api.records.projection.mockResolvedValue({ result: { ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: true, canTransferOwnership: true } } });
    api.shares.list.mockResolvedValue({ result: { items: [shareRow("Other-revision recipient")], authorizationRevision: 6 } });
    api.audit.record.mockResolvedValue({ result: { events: [] } });
    renderPage();

    expect(await screen.findByText("Sharing access changed. Refresh access to see the latest shares.")).toBeInTheDocument();
    expect(screen.queryByText("Other-revision recipient")).not.toBeInTheDocument();
  });

  it("renders a fresh share without the sign-in-gated mutation actions", async () => {
    api.records.projection.mockResolvedValue({ result: { ...baseProjection, permissions: { ...baseProjection.permissions, canManageAccess: true, canTransferOwnership: true } } });
    api.shares.list.mockResolvedValue({ result: { items: [shareRow("Team recipient")], authorizationRevision: baseProjection.record.authorizationRevision } });
    api.audit.record.mockResolvedValue({ result: { events: [] } });
    renderPage();

    expect(await screen.findByRole("rowheader", { name: "Team recipient" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /revoke|share fields/i })).not.toBeInTheDocument();
  });

  it("renders RecordAccessPanel with correct projection when manager", async () => {
    const projection = {
      ...baseProjection,
      permissions: { ...baseProjection.permissions, canManageAccess: true, canTransferOwnership: true },
      record: {
        ...baseProjection.record,
        values: [{ fieldKey: "given_name", stringValue: "Alice" }],
      },
    };
    api.records.projection.mockResolvedValue({ result: projection });
    api.shares.list.mockResolvedValue({ result: { items: [], authorizationRevision: baseProjection.record.authorizationRevision } });
    api.audit.record.mockResolvedValue({ result: { events: [] } });

    renderPage();

    await waitFor(() => {
      // RecordAccessPanel should be rendered with the projection
      expect(screen.getByText("Your permissions on this record")).toBeInTheDocument();
      expect(screen.getByText(/Authorization revision/i)).toBeInTheDocument();
    });
  });

  it("keeps record-wide grants private from a delegated manager", async () => {
    const projection = {
      ...baseProjection,
      permissions: {
        ...baseProjection.permissions,
        canManageAccess: true,
        isCompleteProjection: false,
      },
    };
    api.records.projection.mockResolvedValue({ result: projection });
    api.shares.list.mockResolvedValue({ result: { items: [], authorizationRevision: baseProjection.record.authorizationRevision } });
    // audit.record should not be called for partial projections (RecordAccessPanel logic)

    renderPage();

    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Sharing" })).toBeInTheDocument();
      expect(screen.getByText("Your permissions on this record")).toBeInTheDocument();
    });

    // Should not attempt to fetch audit for partial projection
    expect(api.audit.record).not.toHaveBeenCalled();
    expect(api.shares.list).not.toHaveBeenCalled();
  });

  it("includes formDefinitionName in lead text", async () => {
    api.records.projection.mockResolvedValue({
      result: {
        ...baseProjection,
        formDefinitionName: "Investigation Record",
        permissions: { ...baseProjection.permissions, canManageAccess: true, canTransferOwnership: true },
      },
    });
    api.shares.list.mockResolvedValue({ result: { items: [], authorizationRevision: baseProjection.record.authorizationRevision } });
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

function shareRow(recipientDisplayName: string) {
  return {
    recordShareId: "share-1", recipientDisplayName, recipientRole: "Reader",
    shareStatus: "Active", fieldKeys: ["description"], sharedByDisplayName: "Record owner",
    shareCreatedOn: "2026-11-25T18:00:00Z",
  };
}
