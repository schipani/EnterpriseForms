import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

/** C10 M3: the Authority history card labels pending commands as Pending, never "In effect", and explains invalidations. */

const api = { shares: { history: vi.fn() } };

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
}));

const { RecordSecurityHistoryCard } = await import("./RecordSecurityHistoryCard");

describe("RecordSecurityHistoryCard", () => {
  it("shows each command with its own state and explains cascaded invalidations", async () => {
    api.shares.history.mockResolvedValue({
      result: {
        formRecordId: "r",
        truncated: false,
        authorizationRevision: 9,
        items: [
          {
            securityCommandId: "c2",
            commandKind: "TransferOwnership",
            commandState: "CommittedAwaitingProjection",
            occurredOn: "2026-11-26T10:00:00Z",
            effectiveOn: null,
            committedAuthorizationRevision: 9,
            actorUserId: "u1",
            actorDisplayName: "Alice",
            changes: [{ changeKind: "OwnerAdded", recordShareId: null, shareEndState: null, invalidationReasonCode: null, subjectKind: "User", subjectId: "u2", subjectDisplayName: "Bob" }],
          },
          {
            securityCommandId: "c1",
            commandKind: "RevokeShare",
            commandState: "Effective",
            occurredOn: "2026-11-26T09:00:00Z",
            effectiveOn: "2026-11-26T09:00:05Z",
            committedAuthorizationRevision: 8,
            actorUserId: "u1",
            actorDisplayName: "Alice",
            changes: [{ changeKind: "ShareEnded", recordShareId: "s2", shareEndState: "Invalidated", invalidationReasonCode: "UpstreamRevoked", subjectKind: null, subjectId: null, subjectDisplayName: null }],
          },
        ],
      },
    });
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RecordSecurityHistoryCard formRecordId="r" />
      </QueryClientProvider>,
    );

    expect(await screen.findByText("Ownership transferred")).toBeTruthy();
    expect(screen.getByText("Pending")).toBeTruthy();
    expect(screen.getByText("In effect")).toBeTruthy();
    expect(screen.getAllByText("In effect")).toHaveLength(1); // the pending transfer is not shown as in effect
    expect(screen.getByText("New owner: Bob")).toBeTruthy();
    expect(screen.getByText(/ended because the share it was delegated from was revoked/)).toBeTruthy();
  });
});
