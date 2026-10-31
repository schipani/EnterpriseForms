import { presentError, useApi, useSession } from "@enterpriseforms/app-runtime";
import { Card, LoadingBlock, PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { ProfileForm } from "../components/ProfileForm";

/**
 * My profile: the signed-in person edits their own display name, preferred name and job title. The name changes everywhere in the
 * application (header, people lists, pickers) once saved. Identity, roles and membership are shown read-only.
 */
export function MyProfilePage() {
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const [saved, setSaved] = useState(false);
  const profile = useQuery({ queryKey: ["profile", "me"], queryFn: ({ signal }) => api.profile.mine(signal).then((r) => r.result), gcTime: 0 });
  const save = useMutation({
    mutationFn: (request: Parameters<typeof api.profile.updateMine>[0]) => api.profile.updateMine(request).then((r) => r.result),
    onSuccess: async () => {
      setSaved(true);
      await queryClient.invalidateQueries({ queryKey: ["profile"] });
      await queryClient.invalidateQueries({ queryKey: ["organization"] });
      await queryClient.invalidateQueries({ queryKey: ["users"] });
      await session.refresh();
    },
  });
  const conflict = save.isError && presentError(save.error).code === "PROFILE_REVISION_CONFLICT";
  return (
    <>
      <PageHeader title="My profile" lead="How your name appears to people in this organization." />
      <Card>
        {profile.isPending ? <LoadingBlock label="Loading your profile" /> : null}
        {profile.isError ? <ErrorPanel error={profile.error} onRetry={() => void profile.refetch()} /> : null}
        {saved ? <StatusMessage tone="success" title="Profile saved" /> : null}
        {profile.data ? (
          <ProfileForm
            key={`${profile.data.userId}-${profile.data.profileRevision}`}
            profile={profile.data}
            busy={save.isPending}
            onSubmit={(request) => {
              setSaved(false);
              save.mutate(request);
            }}
            error={
              conflict ? (
                <StatusMessage tone="warning" title="Your profile changed">
                  Your profile was changed elsewhere (for example by an organization owner). Your edits were not saved; the latest version is shown after you reload.
                </StatusMessage>
              ) : save.isError ? (
                <ErrorPanel error={save.error} />
              ) : null
            }
          />
        ) : null}
      </Card>
    </>
  );
}
