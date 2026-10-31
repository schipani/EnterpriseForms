import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const api = { presence: { heartbeat: vi.fn(), query: vi.fn() } };
vi.mock("./runtime", () => ({ useApi: () => api }));

const { PresenceHeartbeat, usePresence } = await import("./presence");

const MINUTE = 60_000;

describe("PresenceHeartbeat", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    api.presence.heartbeat.mockReset().mockResolvedValue(undefined);
  });
  afterEach(() => vi.useRealTimers());

  it("beats on mount with no idle time, then once a minute reporting how long ago the person last interacted", () => {
    const view = render(<PresenceHeartbeat />);
    expect(api.presence.heartbeat).toHaveBeenCalledTimes(1);
    expect(api.presence.heartbeat).toHaveBeenLastCalledWith(0);
    act(() => vi.advanceTimersByTime(MINUTE));
    expect(api.presence.heartbeat).toHaveBeenCalledTimes(2);
    expect(api.presence.heartbeat).toHaveBeenLastCalledWith(60);
    act(() => vi.advanceTimersByTime(4 * MINUTE));
    expect(api.presence.heartbeat).toHaveBeenLastCalledWith(300); // five minutes idle: the server shows Away
    view.unmount();
    act(() => vi.advanceTimersByTime(5 * MINUTE));
    expect(api.presence.heartbeat).toHaveBeenCalledTimes(6); // nothing after sign-out / unmount
  });

  it("an interaction resets the idle time and, after a long idle spell, beats at once so the person shows Available immediately", () => {
    render(<PresenceHeartbeat />);
    act(() => vi.advanceTimersByTime(6 * MINUTE));
    api.presence.heartbeat.mockClear();
    act(() => vi.advanceTimersByTime(3_000));
    act(() => {
      window.dispatchEvent(new Event("pointerdown"));
    });
    expect(api.presence.heartbeat).toHaveBeenCalledTimes(1);
    expect(api.presence.heartbeat).toHaveBeenLastCalledWith(0);
    act(() => {
      window.dispatchEvent(new Event("keydown")); // an ordinary interaction inside the spacing window does not beat again
    });
    expect(api.presence.heartbeat).toHaveBeenCalledTimes(1);
  });

  it("stops beating after thirty minutes without interaction, so the person becomes Offline rather than Away for ever", () => {
    render(<PresenceHeartbeat />);
    act(() => vi.advanceTimersByTime(31 * MINUTE));
    const beats = api.presence.heartbeat.mock.calls.length;
    act(() => vi.advanceTimersByTime(10 * MINUTE));
    expect(api.presence.heartbeat.mock.calls.length).toBe(beats);
  });

  it("never retries or queues a failed beat and never throws", async () => {
    api.presence.heartbeat.mockRejectedValue(new Error("network down"));
    render(<PresenceHeartbeat />);
    await Promise.resolve();
    expect(api.presence.heartbeat).toHaveBeenCalledTimes(1);
    act(() => vi.advanceTimersByTime(MINUTE));
    expect(api.presence.heartbeat).toHaveBeenCalledTimes(2); // only the next scheduled beat
  });
});

describe("usePresence", () => {
  let client: QueryClient;
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  beforeEach(() => {
    client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    api.presence.query.mockReset();
  });

  it("is Unknown (never Offline) until presence has loaded, then follows the server", async () => {
    api.presence.query.mockResolvedValue({
      result: {
        presence: [
          { userId: "a", state: "Available" },
          { userId: "b", state: "Away" },
        ],
        available: true,
      },
    });
    const { result } = renderHook(() => usePresence(["a", "b", "c"]), { wrapper });
    expect(result.current).toEqual({ a: "Unknown", b: "Unknown", c: "Unknown" });
    await waitFor(() => expect(result.current["a"]).toBe("Available"));
    expect(result.current).toEqual({ a: "Available", b: "Away", c: "Unknown" });
  });

  it("stays Unknown when the presence request fails", async () => {
    api.presence.query.mockRejectedValue(new Error("down"));
    const { result } = renderHook(() => usePresence(["a"]), { wrapper });
    await waitFor(() => expect(api.presence.query).toHaveBeenCalled());
    expect(result.current).toEqual({ a: "Unknown" });
  });

  it("asks for at most one hundred people per request", async () => {
    api.presence.query.mockImplementation((ids: string[]) => Promise.resolve({ result: { presence: ids.map((userId) => ({ userId, state: "Offline" })), available: true } }));
    const ids = Array.from({ length: 150 }, (_, i) => `user-${String(i).padStart(3, "0")}`);
    renderHook(() => usePresence(ids), { wrapper });
    await waitFor(() => expect(api.presence.query).toHaveBeenCalledTimes(2));
    expect(api.presence.query.mock.calls.map((call) => (call[0] as string[]).length).sort((a, b) => a - b)).toEqual([50, 100]);
  });

  it("does not ask while disabled", () => {
    renderHook(() => usePresence(["a"], false), { wrapper });
    expect(api.presence.query).not.toHaveBeenCalled();
  });
});
