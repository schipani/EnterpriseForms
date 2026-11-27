import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * C10 independent review F-2: creating and revoking a share from the record Access tab are human share commands; the Authority refuses
 * them without step-up, so the tab asks for the development step-up and sends it (as the edit dialog and the transfer page do).
 */

const recordId = "0199a2b3-c4d5-7e6f-8123-456789abcdef";
const share = {
  recordShareId: "share-1",
  formRecordId: recordId,
  recipientUserId: "user-2",
  recipientTeamId: null,
  recipientDisplayName: "Bob",
  recipientRole: "Reader",
  fieldKeys: ["name"],
  shareStatus: "Active",
  sharedOn: "2026-11-26T18:00:00Z",
};

const api = {
  shares: {
    list: vi.fn(async () => ({ result: { items: [share], authorizationRevision: 5 } })),
    revoke: vi.fn(async () => ({ result: { commandId: "c", commandStatus: "Effective", authorizationRevision: 6, effective: true } })),
    create: vi.fn(async () => ({ result: share })),
  },
  audit: { record: vi.fn(async () => ({ result: { events: [] } })) },
  organization: { users: vi.fn(async () => ({ result: [{ userId: "user-2", displayName: "Bob" }] })) },
  recordSecurityHistory: vi.fn(),
};

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
  usePresence: () => new Map(),
}));

vi.mock("@tanstack/react-router", () => ({
  Link: ({ children }: { children: ReactNode }) => <a href="#">{children}</a>,
}));

vi.mock("./RecordSecurityHistoryCard", () => ({ RecordSecurityHistoryCard: () => null }));

const { RecordAccessPanel } = await import("./RecordAccessPanel");

const projection = {
  formRecordId: recordId,
  formDefinitionId: "form-1",
  formDefinitionName: "Inspection record",
  record: { recordId, recordStatus: "Draft", contentRevision: 1, authorizationRevision: 5, committedOperationId: "op-1", integrityStatus: "Verified", values: [{ fieldKey: "name", formRecordGroupId: null }], groups: [] },
  fields: [{ fieldKey: "name", fieldDisplayName: "Name" }],
  layoutVersion: { layoutJson: { layoutSchemaVersion: 1, sections: [] } },
  editLayoutVersionId: null,
  permissions: { canRead: true, canEdit: true, canManageAccess: true, canTransferOwnership: true, isCompleteProjection: true, contributorWritesEnabled: false, canEditSharedFields: false },
  lifecycle: { status: "Draft", contentEditable: true },
  relationships: [],
  documents: [],
  sharedFieldAccess: [],
  parentFormRecordId: null,
} as never;

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <RecordAccessPanel formRecordId={recordId} projection={projection} />
    </QueryClientProvider>,
  );
}

const STEP_UP = "Confirm with the development-only step-up";

describe("RecordAccessPanel step-up", () => {
  beforeEach(() => {
    Element.prototype.hasPointerCapture = () => false;
    Element.prototype.setPointerCapture = () => undefined;
    Element.prototype.releasePointerCapture = () => undefined;
    Element.prototype.scrollIntoView = () => undefined;
  });

  it("revokes only after the step-up is confirmed, and sends it", async () => {
    renderPanel();
    fireEvent.click(await screen.findByRole("button", { name: "Revoke" }));
    const dialog = await screen.findByRole("dialog");
    const confirm = within(dialog).getByRole("button", { name: "Revoke share" });
    expect(confirm).toBeDisabled();

    fireEvent.click(within(dialog).getByRole("checkbox", { name: STEP_UP }));
    fireEvent.click(confirm);

    await waitFor(() => expect(api.shares.revoke).toHaveBeenCalledWith(recordId, "share-1", true));
  });

  it("creates a share only after the step-up is confirmed, and sends it", async () => {
    renderPanel();
    fireEvent.click(await screen.findByRole("button", { name: "Share fields" }));
    const dialog = await screen.findByRole("dialog");
    const user = userEvent.setup();
    await user.click(await within(dialog).findByRole("combobox", { name: /person/i }));
    await user.click(await screen.findByRole("option", { name: /Bob/ }));
    fireEvent.click(within(dialog).getByRole("checkbox", { name: "Name" }));
    const submit = within(dialog).getAllByRole("button").find((b) => /^share/i.test(b.textContent ?? "") && b.textContent !== "Share fields")!;
    expect(submit).toBeDisabled();

    fireEvent.click(within(dialog).getByRole("checkbox", { name: STEP_UP }));
    fireEvent.click(submit);

    await waitFor(() => expect(api.shares.create).toHaveBeenCalledWith(recordId, expect.objectContaining({ useDevelopmentStepUp: true, fieldKeys: ["name"] })));
  });
});
