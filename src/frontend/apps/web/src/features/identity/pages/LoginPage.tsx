import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Button, Card, LoadingBlock, PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Navigate, useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { ErrorPanel } from "../../support";

/**
 * PG-001 sign-in. Production uses the Entra ID BFF flow (ADR-PB-008); this build fails closed for Entra and offers
 * the seeded development personas only when the server reports the test identity provider (Development/Testing).
 */
export function LoginPage() {
  const session = useSession();
  const api = useApi();
  const navigate = useNavigate();
  const [busyPersona, setBusyPersona] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const developmentAvailable = session.identityProviderKind === "TestIdentityProvider";
  const personas = useQuery({ queryKey: ["session", "personas"], queryFn: () => api.session.personas().then((r) => r.result), enabled: developmentAvailable });

  if (session.status === "authenticated") {
    return <Navigate to="/app/dashboard" />;
  }

  return (
    <div className="mx-auto max-w-2xl">
      <PageHeader title="Sign in" lead="Enterprise Forms uses your organization's identity provider. No passwords are stored by this application." />
      {!developmentAvailable ? (
        <StatusMessage tone="warning" title="Organization sign-in is not configured in this build">
          Microsoft Entra ID sign-in is specified (ADR-PB-008) but not operable here; the server refuses to start a session without a verified identity provider. Development personas are disabled outside Development/Testing environments.
        </StatusMessage>
      ) : (
        <Card title="Development sign-in (synthetic identities)">
          <StatusMessage tone="info" title="Development only">
            These personas are seeded through the authority operator plane and exist only in Development/Testing. The server refuses this login path in any other environment, so it is not a production bypass.
          </StatusMessage>
          {personas.isPending ? <LoadingBlock label="Loading personas" /> : null}
          {personas.isError ? <ErrorPanel error={personas.error} onRetry={() => void personas.refetch()} /> : null}
          {error ? <ErrorPanel error={error} /> : null}
          <ul className="flex flex-col gap-2">
            {(personas.data ?? []).map((persona) => (
              <li key={persona.personaKey} className="flex flex-wrap items-center justify-between gap-2 rounded border border-line p-3">
                <div>
                  <p className="font-medium">{persona.displayName}</p>
                  <p className="text-sm text-muted">
                    {persona.organizationDisplayName} · {persona.organizationRoles.join(", ") || "member"}
                  </p>
                </div>
                <Button
                  busy={busyPersona === persona.personaKey}
                  disabled={busyPersona !== null}
                  aria-label={`Sign in as ${persona.displayName} (${persona.personaKey})`}
                  onClick={() => {
                    setBusyPersona(persona.personaKey);
                    setError(null);
                    session
                      .loginWithPersona(persona.personaKey)
                      .then(() => navigate({ to: "/app/dashboard" }))
                      .catch((failure: unknown) => setError(failure))
                      .finally(() => setBusyPersona(null));
                  }}
                >
                  Sign in as {persona.displayName.split(" ")[0]}
                </Button>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </div>
  );
}
