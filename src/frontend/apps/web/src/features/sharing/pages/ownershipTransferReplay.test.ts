import { describe, expect, it, vi } from "vitest";
import { submitUntilEffective, transferReplayAttempts } from "./ownershipTransferReplay";

const pending = { command: { commandId: "c1", effective: false } };
const effective = { command: { commandId: "c1", effective: true } };

describe("ownership transfer replay (C10 review F4)", () => {
  it("replays a pending transfer with the same idempotency key until it is effective", async () => {
    const submit = vi.fn().mockResolvedValueOnce(pending).mockResolvedValueOnce(pending).mockResolvedValueOnce(effective);
    const wait = vi.fn().mockResolvedValue(undefined);

    const result = await submitUntilEffective(submit, "key-1", wait);

    expect(result).toBe(effective);
    expect(submit).toHaveBeenCalledTimes(3);
    expect(submit.mock.calls.every(([key]) => key === "key-1")).toBe(true);
    expect(wait).toHaveBeenCalledTimes(2);
  });

  it("stops after the bounded number of attempts and reports the transfer as still pending", async () => {
    const submit = vi.fn().mockResolvedValue(pending);

    const result = await submitUntilEffective(submit, "key-2", () => Promise.resolve());

    expect(result.command.effective).toBe(false);
    expect(submit).toHaveBeenCalledTimes(transferReplayAttempts);
  });

  it("submits once when the transfer is effective at once", async () => {
    const submit = vi.fn().mockResolvedValue(effective);

    await submitUntilEffective(submit, "key-3", () => Promise.resolve());

    expect(submit).toHaveBeenCalledTimes(1);
  });
});
