import { Button, Card, LoadingBlock, StatusMessage } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { consoleApi } from "../api/client";
import { describeError } from "../app/errors";
import { navigate } from "../app/router";
import { useConsoleSession } from "../app/session";

/**
 * Development sign-in. The browser names one configured synthetic operator; the BFF asks the Authority whether that identity is an
 * active registered operator and binds the session to it. There is no password and no organization selector: operator access is
 * separate from any organization membership.
 */
export function SignInPage({ notice }: { notice?: string | undefined }) {
  const { signIn } = useConsoleSession();
  const operators = useQuery({ queryKey: ["development-operators"], queryFn: consoleApi.developmentOperators, staleTime: Infinity });
  const [pending, setPending] = useState<string | null>(null);
  const [error, setError] = useState<ReturnType<typeof describeError> | null>(null);

  const choose = async (subjectIdentifier: string) => {
    setPending(subjectIdentifier);
    setError(null);
    try {
      await signIn(subjectIdentifier);
      navigate("/platform/approvals", true);
    } catch (failure) {
      setError(describeError(failure));
    } finally {
      setPending(null);
    }
  };

  return (
    <div className="mx-auto flex w-full max-w-xl flex-col gap-4">
      <h1 className="text-2xl font-semibold">Sign in to the Platform Console</h1>
      <p className="text-sm text-muted">
        The Platform Console is for Platform Operators who review and approve organization membership requests. It is a separate site from the business application and shares no session with it.
      </p>
      {notice ? (
        <StatusMessage tone="warning" title="Signed out">
          {notice}
        </StatusMessage>
      ) : null}
      {error ? (
        <StatusMessage tone={error.tone} title={error.title}>
          {error.message}
        </StatusMessage>
      ) : null}
      <Card title="Development operators">
        <p className="mb-3 text-sm text-muted">{operators.data?.environmentNotice ?? "Development operators: approvals made here are synthetic and are not two-person evidence."}</p>
        {operators.isLoading ? <LoadingBlock label="Loading development operators" /> : null}
        {operators.isError ? (
          <StatusMessage tone="danger" title="Operators could not be loaded" actions={<Button variant="secondary" size="sm" onClick={() => void operators.refetch()}>Try again</Button>}>
            {describeError(operators.error).message}
          </StatusMessage>
        ) : null}
        {operators.data ? (
          <ul className="flex flex-col gap-2" aria-label="Development operators">
            {operators.data.operators.map((operator) => (
              <li key={operator.subjectIdentifier} className="flex flex-wrap items-center justify-between gap-3 rounded-control border border-line px-3 py-2">
                <div className="min-w-0">
                  <p className="font-medium">{operator.displayName}</p>
                  <p className="truncate text-help text-muted">{operator.subjectIdentifier}</p>
                </div>
                <Button variant="primary" size="sm" busy={pending === operator.subjectIdentifier} disabled={pending !== null} onClick={() => void choose(operator.subjectIdentifier)} aria-label={`Sign in as ${operator.displayName}`}>
                  Sign in
                </Button>
              </li>
            ))}
          </ul>
        ) : null}
      </Card>
      <p className="text-help text-muted">{operators.data?.authenticationMode ?? "Development operator selection; not production authentication."}</p>
    </div>
  );
}
