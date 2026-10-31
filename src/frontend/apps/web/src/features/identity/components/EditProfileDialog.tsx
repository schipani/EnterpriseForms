import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { presentError, useApi, useSession } from "@enterpriseforms/app-runtime";
import { Dialog, LoadingBlock, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ErrorPanel } from "../../support";
import { memberName } from "./MemberTable";
import { ProfileForm } from "./ProfileForm";

/**
 * Owner edit of a person's directory profile (People → Actions → Edit profile). The profile is read fresh with its revision; a save
 * made on a stale revision is refused (409) and the person is told to reload. Only non-security fields change.
 */
export function EditProfileDialog({ member, onClose, onDone }: { member: OrganizationMemberResponse | null; onClose: () => void; onDone: (message: string) => void }) {
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const userId = member?.userId ?? "";
  const profile = useQuery({
    queryKey: ["organization", "profile", userId],
    queryFn: ({ signal }) => api.organization.memberProfile(userId, signal).then((r) => r.result),
    enabled: member !== null,
    gcTime: 0,
  });
  const save = useMutation({
    mutationFn: (request: Parameters<typeof api.organization.updateMemberProfile>[1]) => api.organization.updateMemberProfile(userId, request).then((r) => r.result),
    onSuccess: async (updated) => {
      onDone(`Profile saved for ${updated.displayName}.`);
      onClose();
      await queryClient.invalidateQueries({ queryKey: ["organization"] });
      await queryClient.invalidateQueries({ queryKey: ["users"] });
      await session.refresh();
    },
  });
  const conflict = save.isError && presentError(save.error).code === "PROFILE_REVISION_CONFLICT";
  return (
    <Dialog
      open={member !== null}
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={member ? `Edit profile: ${memberName(member)}` : ""}
      description="Display name, preferred name and job title. Verified identity, roles and membership cannot be changed here."
      locked={save.isPending}
    >
      {profile.isPending ? <LoadingBlock label="Loading profile" /> : null}
      {profile.isError ? <ErrorPanel error={profile.error} onRetry={() => void profile.refetch()} /> : null}
      {profile.data ? (
        <ProfileForm
          key={`${profile.data.userId}-${profile.data.profileRevision}`}
          profile={profile.data}
          busy={save.isPending}
          onCancel={onClose}
          onSubmit={(request) => save.mutate(request)}
          error={
            conflict ? (
              <StatusMessage tone="warning" title="This profile changed">
                Someone else changed this profile. Your edits were not saved. Reload the latest profile and try again.
              </StatusMessage>
            ) : save.isError ? (
              <ErrorPanel error={save.error} />
            ) : null
          }
        />
      ) : null}
      {conflict ? (
        <div>
          <button type="button" className="text-sm underline" onClick={() => void profile.refetch().then(() => save.reset())}>
            Reload the latest profile
          </button>
        </div>
      ) : null}
    </Dialog>
  );
}
