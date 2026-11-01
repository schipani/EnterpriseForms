import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { consoleApi, ConsoleApiError, endsSession, setCsrfToken, type ConsoleSessionView } from "../api/client";
import { navigate } from "./router";

export type SessionState =
  | { status: "loading" }
  | { status: "signed-out"; notice?: string | undefined }
  | { status: "signed-in"; session: ConsoleSessionView; idleDeadline: number; idleMilliseconds: number };

interface SessionContextValue {
  state: SessionState;
  /** Call after any successful authenticated request: the BFF reset the idle clock, so the local deadline moves too. */
  touch: () => void;
  /** Route every failed authenticated call through here: a session-ending refusal returns the app to sign-in. */
  handleError: (error: unknown) => boolean;
  signIn: (subjectIdentifier: string) => Promise<void>;
  signOut: () => Promise<void>;
}

const SessionContext = createContext<SessionContextValue | null>(null);

export const useConsoleSession = (): SessionContextValue => {
  const value = useContext(SessionContext);
  if (!value) {
    throw new Error("useConsoleSession outside SessionProvider");
  }
  return value;
};

const endedNotice = (error: ConsoleApiError): string =>
  error.code === "SESSION_REQUIRED"
    ? "Your session ended or expired. Sign in again to continue."
    : "Your operator access is no longer valid, so the session was ended. Sign in again or contact another operator.";

const fromSession = (session: ConsoleSessionView): SessionState => {
  const idleMilliseconds = Math.max(0, Date.parse(session.idleExpiresOn) - Date.now());
  setCsrfToken(session.csrfToken);
  return { status: "signed-in", session, idleDeadline: Date.now() + idleMilliseconds, idleMilliseconds };
};

export function SessionProvider({ children, onSignedOut }: { children: ReactNode; onSignedOut?: () => void }) {
  const [state, setState] = useState<SessionState>({ status: "loading" });
  const stateRef = useRef(state);
  stateRef.current = state;

  useEffect(() => {
    let cancelled = false;
    consoleApi
      .session()
      .then((session) => {
        if (!cancelled) {
          setState(fromSession(session));
        }
      })
      .catch((error: unknown) => {
        if (cancelled) {
          return;
        }
        setCsrfToken("");
        setState({ status: "signed-out", notice: error instanceof ConsoleApiError && error.code !== "SESSION_REQUIRED" ? `The Platform Console could not verify your session (${error.code}).` : undefined });
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // The idle deadline is tracked locally from the last authenticated response; nothing polls the server, so this check never keeps a session alive.
  useEffect(() => {
    if (state.status !== "signed-in") {
      return;
    }
    const timer = window.setInterval(() => {
      const current = stateRef.current;
      if (current.status === "signed-in" && Date.now() >= Math.min(current.idleDeadline, Date.parse(current.session.absoluteExpiresOn))) {
        setCsrfToken("");
        setState({ status: "signed-out", notice: "Your session ended or expired. Sign in again to continue." });
        onSignedOut?.();
      }
    }, 5000);
    return () => window.clearInterval(timer);
  }, [state.status, onSignedOut]);

  const touch = useCallback(() => {
    setState((current) => (current.status === "signed-in" ? { ...current, idleDeadline: Date.now() + current.idleMilliseconds } : current));
  }, []);

  const handleError = useCallback(
    (error: unknown) => {
      if (endsSession(error)) {
        setCsrfToken("");
        setState({ status: "signed-out", notice: endedNotice(error) });
        onSignedOut?.();
        return true;
      }
      return false;
    },
    [onSignedOut],
  );

  const signIn = useCallback(async (subjectIdentifier: string) => {
    const session = await consoleApi.signIn(subjectIdentifier);
    setState(fromSession(session));
  }, []);

  const signOut = useCallback(async () => {
    try {
      await consoleApi.signOut();
    } catch (error) {
      // A session that is already gone is signed out. Any other failure keeps the session visible so sign-out can be retried.
      if (!(error instanceof ConsoleApiError && (error.status === 401 || endsSession(error)))) {
        throw error;
      }
    }
    setCsrfToken("");
    setState({ status: "signed-out" });
    onSignedOut?.();
    navigate("/platform/sign-in");
  }, [onSignedOut]);

  const value = useMemo(() => ({ state, touch, handleError, signIn, signOut }), [state, touch, handleError, signIn, signOut]);
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}
