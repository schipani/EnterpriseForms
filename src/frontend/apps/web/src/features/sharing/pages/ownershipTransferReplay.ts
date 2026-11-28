/**
 * C10 review F4: a transfer the authority accepted but has not yet made effective is replayed with the same idempotency key a bounded
 * number of times. The business handler is idempotent by that key: a replay never submits a second command, and the first replay that
 * finds the command Effective records the business projection (RECORD_OWNERSHIP_TRANSFERRED) exactly once, attributed to the caller.
 */
export const transferReplayAttempts = 5;
export const transferReplayDelayMs = 1500;

export async function submitUntilEffective<T extends { command: { effective: boolean } }>(
  submit: (idempotencyKey: string) => Promise<T>,
  idempotencyKey: string,
  wait: (ms: number) => Promise<void> = (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
): Promise<T> {
  let result = await submit(idempotencyKey);
  for (let attempt = 1; attempt < transferReplayAttempts && !result.command.effective; attempt++) {
    await wait(transferReplayDelayMs);
    result = await submit(idempotencyKey);
  }

  return result;
}

export function newTransferIdempotencyKey(): string {
  return typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}
