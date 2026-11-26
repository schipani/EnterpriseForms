import { OperationError } from "@enterpriseforms/work-queue";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * C10 M3: the share edit dialog reads the share from the Authority and edits that committed revision (never the list row's),
 * asks for a reload on 412, reports a 409 without retrying, and saves only with an explicit step-up confirmation.
 */

const recordId = "0199a2b3-c4d5-7e6f-8123-456789abcdef";
const shareId = "0199a2b3-c4d5-7e6f-8123-456789abcd01";

const api = { shares: { get: vi.fn(), update: vi.fn() } };

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
}));

const { EditShareDialog, fromLocalInput, toLocalInput } = await import("./EditShareDialog");

const listRow = {
  recordShareId: shareId,
  formRecordId: recordId,
  sharedByUserId: "owner",
  sharedByDisplayName: "Owner",
  recipientUserId: "bob",
  recipientDisplayName: "Bob",
  recipientTeamId: null,
  recipientRole: "Reader",
  shareScope: "SelectedItems",
  shareStatus: "Active",
  shareRevision: 1, // the lagging projection
  shareCreatedOn: "2026-11-20T10:00:00Z",
  shareRevokedOn: null,
  fieldKeys: ["first"],
};

const details = (revision: number) => ({
  recordShareId: shareId,
  formRecordId: recordId,
  shareStatus: "Active",
  recipientUserId: "bob",
  recipientTeamId: null,
  recipientDisplayName: "Bob",
  recipientRole: "Reader",
  fieldValueItemIds: ["item-1"],
  fieldKeys: ["first"],
  shareExpiresOn: null,
  effectiveExpiresOn: null,
  shareRevision: revision,
  parentRecordShareId: null,
  delegationDepth: 1,
  legacyLineageUnresolved: false,
  invalidationReasonCode: null,
  grantorUserId: "owner",
  grantorDisplayName: "Owner",
  authorizationRevision: 40,
});

const projection = {
  record: { values: [{ fieldKey: "first", formRecordGroupId: null }, { fieldKey: "second", formRecordGroupId: null }], authorizationRevision: 40 },
  fields: [
    { fieldKey: "first", fieldDisplayName: "First" },
    { fieldKey: "second", fieldDisplayName: "Second" },
  ],
  permissions: { canTransferOwnership: true },
};

function renderDialog() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const onClose = vi.fn();
  render(
    <QueryClientProvider client={client}>
      {/* eslint-disable-next-line @typescript-eslint/no-explicit-any -- partial projection fixture */}
      <EditShareDialog formRecordId={recordId} share={listRow as any} projection={projection as any} onClose={onClose} />
    </QueryClientProvider>,
  );
  return onClose;
}

const failure = (status: number, errorCode: string) => new OperationError({ type: "failed", operationId: "op", status, errorCode, retryable: false });

describe("EditShareDialog", () => {
  beforeEach(() => {
    api.shares.get.mockReset();
    api.shares.update.mockReset();
  });

  it("edits the Authority's committed revision and requires the step-up confirmation", async () => {
    api.shares.get.mockResolvedValue({ result: details(5) });
    api.shares.update.mockResolvedValue({ result: { shareRevision: 6 } });
    const onClose = renderDialog();

    expect(await screen.findByText("5")).toBeTruthy(); // share revision from the Authority, not the list's 1
    const save = screen.getByRole("button", { name: "Save share" });
    expect((save as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole("checkbox", { name: "Second" }));
    fireEvent.click(screen.getByRole("checkbox", { name: /development-only step-up/ }));
    fireEvent.click(save);

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(api.shares.update).toHaveBeenCalledTimes(1);
    const [record, share, revision, request] = api.shares.update.mock.calls[0]!;
    expect([record, share, revision]).toEqual([recordId, shareId, 5]);
    expect(request).toMatchObject({ recipientRole: "Reader", fieldKeys: ["first", "second"], noExpiry: true, shareExpiresOn: null, useDevelopmentStepUp: true });
  });

  it("asks for a reload on a stale share revision and edits the reloaded one", async () => {
    api.shares.get.mockResolvedValueOnce({ result: details(5) }).mockResolvedValueOnce({ result: details(7) });
    api.shares.update.mockRejectedValueOnce(failure(412, "STALE_SHARE_REVISION"));
    renderDialog();
    await screen.findByText("5");
    fireEvent.click(screen.getByRole("checkbox", { name: /development-only step-up/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save share" }));

    expect(await screen.findByText("This share changed since you opened it")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save share" }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "Reload share" }));
    expect(await screen.findByText("7")).toBeTruthy();
    expect(api.shares.get).toHaveBeenCalledTimes(2);
  });

  it("reports an organization conflict without retrying", async () => {
    api.shares.get.mockResolvedValue({ result: details(5) });
    api.shares.update.mockRejectedValue(failure(409, "STALE_AUTHORIZATION_REVISION"));
    renderDialog();
    await screen.findByText("5");
    fireEvent.click(screen.getByRole("checkbox", { name: /development-only step-up/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save share" }));

    expect(await screen.findByText("Access in this organization changed while you were editing")).toBeTruthy();
    expect(api.shares.update).toHaveBeenCalledTimes(1);
  });

  it("shows an inactive share as not editable", async () => {
    api.shares.get.mockResolvedValue({ result: { ...details(3), shareStatus: "Invalidated", recipientRole: null, fieldKeys: [], fieldValueItemIds: [] } });
    renderDialog();

    expect(await screen.findByText("This share is no longer active and cannot be edited.")).toBeTruthy();
  });
});

describe("expiry input", () => {
  it("round-trips an instant through the viewer's time zone", () => {
    const local = toLocalInput("2026-12-01T15:30:00Z", "America/Toronto");
    expect(local).toBe("2026-12-01T10:30");
    expect(fromLocalInput(local, "America/Toronto").instant).toBe("2026-12-01T15:30:00.000Z");
  });

  it("refuses a time that does not exist or occurs twice", () => {
    expect(fromLocalInput("2026-03-08T02:30", "America/Toronto").problem).toMatch(/does not exist/);
    expect(fromLocalInput("2026-11-01T01:30", "America/Toronto").problem).toMatch(/occurs twice/);
    expect(fromLocalInput("not a date", "America/Toronto").problem).toBeTruthy();
  });
});
