import type { BootstrapResponse, SessionResponse } from "@enterpriseforms/api-client";
import { useQueryClient } from "@tanstack/react-query";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { applyBranding, resetBranding } from "./branding";
import { presentError } from "./errors";
import { useRuntime } from "./runtime";

export type SessionStatus = "loading" | "anonymous" | "authenticated" | "expired" | "unavailable";

export interface SessionState {
  status: SessionStatus;
  session: SessionResponse | null;
  bootstrap: BootstrapResponse | null;
  identityProviderKind: string;
  error: string | null;
}

export interface SessionApi extends SessionState {
  refresh: () => Promise<void>;
  loginWithPersona: (personaKey: string) => Promise<void>;
  logout: () => Promise<void>;
  hasCapability: (capability: string) => boolean;
  applyPreferences: (textScalePercent: number) => void;
}

const SessionContext = createContext<SessionApi | null>(null);

/**
 * BFF session lifecycle (B-01): GET /session → GET /bootstrap. The coordinator's security context is set from the
 * bootstrap (organization, CSRF, authorization revision) and cleared on logout; a 401 from any operation flips the
 * state to "expired" so the shell can prompt for re-authentication without losing in-flight uploads' metadata.
 */
export function SessionProvider({ children }: { children: ReactNode }) {
  const { api, coordinator, uploads } = useRuntime();
  const queryClient = useQueryClient();
  const [state, setState] = useState<SessionState>({ status: "loading", session: null, bootstrap: null, identityProviderKind: "unknown", error: null });
  const organizationRef = useRef<string | null>(null);

  const load = useCallback(async () => {
    try {
      const { result: session } = await api.session.get();
      if (!session.authenticated || !session.organizationId) {
        coordinator.clearSecurityContext();
        resetBranding();
        setState({ status: "anonymous", session, bootstrap: null, identityProviderKind: session.identityProviderKind, error: null });
        return;
      }
      coordinator.setSecurityContext({ organizationId: session.organizationId, csrfToken: session.csrfToken, authorizationEpoch: null });
      const { result: bootstrap } = await api.session.bootstrap();
      coordinator.setSecurityContext({ organizationId: session.organizationId, csrfToken: bootstrap.session.csrfToken, authorizationEpoch: String(bootstrap.authorizationRevision) });
      if (organizationRef.current && organizationRef.current !== session.organizationId) {
        uploads.reset();
        queryClient.clear();
      }
      organizationRef.current = session.organizationId;
      applyBranding(bootstrap.branding, Number(bootstrap.preferences.effectiveTextScalePercent));
      setState({ status: "authenticated", session: bootstrap.session, bootstrap, identityProviderKind: session.identityProviderKind, error: null });
    } catch (error) {
      const presented = presentError(error);
      if (presented.code === "CANCELLED") {
        // A probe cancelled because a concurrent probe already settled the session (e.g. it found no session and
        // cleared the security context) is not a failure; the settled state stands.
        return;
      }
      setState((current) => ({ ...current, status: presented.kind === "session-expired" ? "expired" : "unavailable", error: `${presented.title}: ${presented.detail}` }));
    }
  }, [api, coordinator, queryClient, uploads]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(
    () =>
      coordinator.subscribe((event) => {
        if (event.type === "failed" && event.status === 401) {
          setState((current) => (current.status === "authenticated" ? { ...current, status: "expired" } : current));
        }
      }),
    [coordinator],
  );

  const loginWithPersona = useCallback(
    async (personaKey: string) => {
      await api.session.developmentLogin(personaKey);
      await load();
    },
    [api, load],
  );

  const logout = useCallback(async () => {
    try {
      await api.session.logout();
    } catch (error) {
      const presented = presentError(error);
      if (presented.code === "SESSION_STATE_UNAVAILABLE") {
        // G23: the server could not confirm that the session was revoked, so it may still be valid. Stay signed in and
        // say so, rather than showing a sign-out that did not happen; the user can retry.
        setState((current) => ({ ...current, error: `Sign-out could not be confirmed. ${presented.detail}` }));
        throw error;
      }
    }
    uploads.reset();
    coordinator.clearSecurityContext();
    queryClient.clear();
    resetBranding();
    organizationRef.current = null;
    setState({ status: "anonymous", session: null, bootstrap: null, identityProviderKind: state.identityProviderKind, error: null });
  }, [api, coordinator, queryClient, state.identityProviderKind, uploads]);

  const value = useMemo<SessionApi>(
    () => ({
      ...state,
      refresh: load,
      loginWithPersona,
      logout,
      hasCapability: (capability) => state.bootstrap?.session.capabilities.includes(capability) ?? false,
      applyPreferences: (textScalePercent) => applyBranding(state.bootstrap?.branding ?? null, textScalePercent),
    }),
    [state, load, loginWithPersona, logout],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionApi {
  const session = useContext(SessionContext);
  if (!session) {
    throw new Error("SessionProvider is missing above this component");
  }
  return session;
}
