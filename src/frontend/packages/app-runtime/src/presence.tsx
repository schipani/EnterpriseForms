import type { PresenceState } from "@enterpriseforms/api-client";
import { useQueries } from "@tanstack/react-query";
import { useEffect, useMemo, useRef } from "react";
import { useApi } from "./runtime";

/** One heartbeat a minute; the server treats a person as Offline after 150 seconds without one. */
const HEARTBEAT_MILLISECONDS = 60_000;
/** Interaction-driven early beats (resuming after being Away) are never closer together than this. */
const MINIMUM_SPACING_MILLISECONDS = 15_000;
/** A wake-up beat (first interaction after a long idle spell) only needs to avoid a double send. */
const WAKE_SPACING_MILLISECONDS = 2_000;
/** After this long without any interaction the tab stops beating (so the person becomes Offline instead of Away forever). */
const STOP_AFTER_MILLISECONDS = 30 * 60_000;
/** Interacting after this long idle sends a beat immediately, so the person shows Available at once instead of up to a minute later. */
const WAKE_AFTER_MILLISECONDS = 4 * 60_000;
const QUERY_CHUNK = 100;

/**
 * Presence heartbeat for the signed-in person (shell, authenticated only). It reports how long ago the person last interacted
 * (pointer, key, touch or scroll in this tab); the server derives Available/Away/Offline and takes the user from the session, never
 * from this request. A beat is sent on mount and once a minute while the person interacts; a failed beat is simply skipped (never
 * retried or queued), so a network failure cannot keep anyone Available. It sits in the coordinator's low-priority "polling" lane.
 * Many tabs and devices share one presence record on the server, so closing a tab never marks the person Offline.
 */
export function PresenceHeartbeat() {
  const api = useApi();
  const lastInteraction = useRef(Date.now());
  const lastBeat = useRef(0);

  useEffect(() => {
    let disposed = false;
    const beat = (wake = false) => {
      const now = Date.now();
      const idle = now - lastInteraction.current;
      if (disposed || idle > STOP_AFTER_MILLISECONDS || now - lastBeat.current < (wake ? WAKE_SPACING_MILLISECONDS : MINIMUM_SPACING_MILLISECONDS)) {
        return;
      }

      lastBeat.current = now;
      api.presence.heartbeat(Math.min(Math.floor(idle / 1000), 86_400)).catch(() => undefined);
    };
    const interacted = () => {
      const now = Date.now();
      const wasIdle = now - lastInteraction.current;
      lastInteraction.current = now;
      if (wasIdle > WAKE_AFTER_MILLISECONDS) {
        beat(true);
      }
    };
    const visibility = () => {
      if (document.visibilityState === "visible") {
        interacted();
      }
    };
    const events = ["pointerdown", "keydown", "touchstart", "wheel", "pointermove"] as const;
    for (const name of events) {
      window.addEventListener(name, interacted, { passive: true });
    }

    document.addEventListener("visibilitychange", visibility);
    beat();
    const timer = window.setInterval(() => beat(), HEARTBEAT_MILLISECONDS);
    return () => {
      disposed = true;
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", visibility);
      for (const name of events) {
        window.removeEventListener(name, interacted);
      }
    };
  }, [api]);

  return null;
}

/**
 * Presence of the given people (their own organization only; the server enforces it). A person whose presence has not loaded, or
 * that could not be determined, is "Unknown" (never "Offline"). Refreshes once a minute while the page is visible; there is no
 * background polling of hidden tabs. Pass only the ids actually on screen.
 */
export function usePresence(userIds: readonly string[], enabled = true): Record<string, PresenceState> {
  const api = useApi();
  const chunks = useMemo(() => {
    const unique = [...new Set(userIds)].sort();
    const result: string[][] = [];
    for (let index = 0; index < unique.length; index += QUERY_CHUNK) {
      result.push(unique.slice(index, index + QUERY_CHUNK));
    }

    return result;
  }, [userIds]);
  const results = useQueries({
    queries: chunks.map((ids) => ({
      queryKey: ["presence", ids.join(",")],
      queryFn: ({ signal }: { signal: AbortSignal }) => api.presence.query(ids, signal).then((r) => r.result),
      enabled,
      staleTime: 30_000,
      refetchInterval: HEARTBEAT_MILLISECONDS,
      refetchIntervalInBackground: false,
      retry: false,
    })),
  });
  return useMemo(() => {
    const states: Record<string, PresenceState> = {};
    for (const id of userIds) {
      states[id] = "Unknown";
    }

    for (const result of results) {
      for (const item of result.data?.presence ?? []) {
        states[item.userId] = item.state as PresenceState;
      }
    }

    return states;
  }, [results, userIds]);
}
