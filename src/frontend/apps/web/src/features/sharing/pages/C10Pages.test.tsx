import { OperationError } from "@enterpriseforms/work-queue";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * C10 M3: PG-023 conceals shares the caller may not see and never presents an expired share as access; PG-053 reports a pending transfer
 * as pending, not done, and requires reason and step-up; PG-066 lists the caller's own sources with the expiring-soon marker.
 */

const recordId = "0199a2b3-c4d5-7e6f-8123-456789abcdef";
const shareId = "0199a2b3-c4d5-7e6f-8123-456789abcd01";

const api = {
  shares: { get: vi.fn(), effectiveScope: vi.fn(), ownershipHistory: vi.fn(), transferOwnership: vi.fn(), mine: vi.fn() },
  records: { projection: vi.fn() },
  organization: { users: vi.fn() },
};

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
}));

vi.mock("@tanstack/react-router", () => ({
  useParams: () => ({ formRecordId: recordId, recordShareId: shareId }),
  Link: ({ children, className }: { children: ReactNode; className?: string }) => <a className={className}>{children}</a>,
}));

const { ShareDetailsPage } = await import("./ShareDetailsPage");
const { OwnershipTransferPage } = await import("./OwnershipTransferPage");
const { MyAccessPage } = await import("./MyAccessPage");

const renderWith = (node: ReactNode) =>
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>{node}</QueryClientProvider>);

const details = {
  recordShareId: shareId, formRecordId: recordId, shareStatus: "Active", recipientUserId: "bob", recipientTeamId: null, recipientDisplayName: "Bob", recipientRole: "Reader",
  fieldValueItemIds: ["i1"], fieldKeys: ["first"], shareExpiresOn: null, effectiveExpiresOn: "2026-11-27T10:00:00Z", shareRevision: 3, parentRecordShareId: "p1", delegationDepth: 2,
  legacyLineageUnresolved: false, invalidationReasonCode: null, grantorUserId: "amy", grantorDisplayName: "Amy", authorizationRevision: 9, documents: [],
};

describe("C10 M3 pages", () => {
  beforeEach(() => {
    Object.values(api).forEach((group) => Object.values(group).forEach((fn) => fn.mockReset()));
  });

  it("PG-023 shows a concealing message when the authority does not make the share available", async () => {
    api.shares.get.mockRejectedValue(new OperationError({ type: "failed", operationId: "op", status: 404, errorCode: "RECORD_NOT_FOUND", retryable: false }));
    api.shares.effectiveScope.mockRejectedValue(new OperationError({ type: "failed", operationId: "op", status: 404, errorCode: "RECORD_NOT_FOUND", retryable: false }));
    renderWith(<ShareDetailsPage />);

    expect(await screen.findByText("This share is not available to you")).toBeTruthy();
    expect(screen.queryByText("Bob")).toBeNull();
  });

  it("PG-023 presents an expired share as giving no access", async () => {
    api.shares.get.mockResolvedValue({ result: details });
    api.shares.effectiveScope.mockResolvedValue({
      result: { recordShareId: shareId, formRecordId: recordId, status: "Expired", recipientRole: null, effectiveExpiresOn: "2026-11-25T10:00:00Z", fieldValueItemIds: [], documents: [], canDelegate: false, lineage: [{ recordShareId: shareId, delegationDepth: 2, shareExpiresOn: null }, { recordShareId: "p1", delegationDepth: 1, shareExpiresOn: "2026-11-25T10:00:00Z" }], authorizationRevision: 9 },
    });
    renderWith(<ShareDetailsPage />);

    expect(await screen.findByText("Expired: gives no access")).toBeTruthy();
    expect(screen.getByText("Delegated (level 2)")).toBeTruthy();
    expect(screen.getByRole("list", { name: /Delegation chain/ }).querySelectorAll("li")).toHaveLength(2);
  });

  it("PG-053 requires reason and step-up and reports a pending transfer as pending", async () => {
    api.records.projection.mockResolvedValue({ result: { formDefinitionName: "Inspection", permissions: { canTransferOwnership: true } } });
    api.organization.users.mockResolvedValue({ result: [{ userId: "carol", displayName: "Carol" }] });
    api.shares.ownershipHistory.mockResolvedValue({ result: { formRecordId: recordId, items: [], truncated: false, authorizationRevision: 9 } });
    api.shares.transferOwnership.mockResolvedValue({ result: { command: { commandId: "c1", commandStatus: "CommittedAwaitingProjection", authorizationRevision: 10, effective: false } } });
    renderWith(<OwnershipTransferPage />);

    const button = await screen.findByRole("button", { name: "Transfer ownership" });
    expect((button as HTMLButtonElement).disabled).toBe(true);
    expect(await screen.findByText("No ownership changes yet.")).toBeTruthy();
    expect(api.shares.transferOwnership).not.toHaveBeenCalled();
  });

  it("PG-053 refuses callers who cannot transfer", async () => {
    api.records.projection.mockResolvedValue({ result: { formDefinitionName: "Inspection", permissions: { canTransferOwnership: false } } });
    api.organization.users.mockResolvedValue({ result: [] });
    renderWith(<OwnershipTransferPage />);

    expect(await screen.findByText("You cannot transfer this record")).toBeTruthy();
    expect(api.shares.ownershipHistory).not.toHaveBeenCalled();
  });

  it("PG-066 lists the caller's own sources and marks shares expiring soon", async () => {
    api.shares.mine.mockResolvedValue({
      result: {
        entries: [
          { formRecordId: recordId, formDefinitionName: "Inspection", accessSource: "DirectShare", recordShareId: shareId, teamId: null, recipientRole: "Reader", effectiveExpiresOn: "2026-11-28T10:00:00Z", expiringSoon: true },
          { formRecordId: "r2", formDefinitionName: "Permit", accessSource: "Owner", recordShareId: null, teamId: null, recipientRole: null, effectiveExpiresOn: null, expiringSoon: false },
        ],
        truncated: false,
        expiringWithinDays: 7,
        authorizationRevision: 9,
      },
    });
    renderWith(<MyAccessPage />);

    expect(await screen.findByText("Shared with you")).toBeTruthy();
    expect(screen.getByText("You own it")).toBeTruthy();
    expect(screen.getAllByText("Expiring soon")).toHaveLength(1);
  });

  it("PG-066 shows an empty state", async () => {
    api.shares.mine.mockResolvedValue({ result: { entries: [], truncated: false, expiringWithinDays: 7, authorizationRevision: 9 } });
    renderWith(<MyAccessPage />);

    await waitFor(() => expect(screen.getByText("No records yet")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
    await waitFor(() => expect(api.shares.mine).toHaveBeenCalledTimes(2));
  });
});
