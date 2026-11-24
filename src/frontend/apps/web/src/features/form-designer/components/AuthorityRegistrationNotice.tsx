import type { FormAuthorityRegistrationResponse } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import { Button, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ErrorPanel } from "../../support";

export const authorityRegistrationKey = (formDefinitionId: string) => ["forms", formDefinitionId, "authority-registration"] as const;

const outcomeText: Record<string, string> = {
  AlreadyRegistered: "The current version was already registered. Field search on this form is available again.",
  Registered: "The current version is now registered. Field search on this form is available again.",
  Abandoned: "A version that was never published was withdrawn from the Authority. Field search on this form is available again.",
  NotRecoverable: "This version cannot be registered after the fact. Publish the form again to register a new version.",
};

/**
 * C-188 BF-1: shown to designers when the form's current published version is not registered with the Authorization Authority.
 * While a registration is pending, field search on the form is unavailable (the Authority fails closed); "Retry registration"
 * completes it without publishing again. Nothing is shown when the version is registered or the form is unpublished.
 */
export function AuthorityRegistrationNotice({ formDefinitionId }: { formDefinitionId: string }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const status = useQuery<FormAuthorityRegistrationResponse>({
    queryKey: authorityRegistrationKey(formDefinitionId),
    queryFn: ({ signal }) => api.forms.authorityRegistration(formDefinitionId, signal).then((r) => r.result),
  });
  const recover = useMutation({
    mutationFn: () => api.forms.recoverAuthorityRegistration(formDefinitionId).then((r) => r.result),
    onSuccess: (result) => queryClient.setQueryData(authorityRegistrationKey(formDefinitionId), result),
  });

  const result = recover.data;
  const state = (result ?? status.data)?.registrationState;
  if (result?.outcome && result.registrationState === "Registered") {
    return (
      <StatusMessage tone="success" title="Registration complete" actions={<Button size="sm" variant="ghost" onClick={() => recover.reset()}>Dismiss</Button>}>
        {outcomeText[result.outcome] ?? "Field search on this form is available again."}
      </StatusMessage>
    );
  }

  if (state !== "Pending" && state !== "NotRegistered") {
    return null;
  }

  const pending = state === "Pending";
  return (
    <StatusMessage
      tone="warning"
      title={pending ? "This version is not yet registered with the Authority" : "This version was never registered with the Authority"}
      actions={
        pending ? (
          <Button size="sm" variant="secondary" busy={recover.isPending} onClick={() => recover.mutate()}>
            Retry registration
          </Button>
        ) : undefined
      }
    >
      {pending
        ? "Field search on this form is unavailable until the latest published version is registered. Records and the form itself are unaffected. Retrying does not publish anything again."
        : "Field search uses the last version the Authority registered. Publish the form again to register its current layout."}
      {result?.outcome === "Refused" ? ` The Authority refused the registration (${result.failureReason ?? "no reason given"}).` : null}
      {result?.outcome === "NotRecoverable" ? ` ${outcomeText.NotRecoverable}` : null}
      {recover.isError ? <ErrorPanel error={recover.error} /> : null}
    </StatusMessage>
  );
}
