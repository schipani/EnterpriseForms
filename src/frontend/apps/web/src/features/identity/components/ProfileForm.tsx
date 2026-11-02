import type { UpdateUserProfileRequest, UserProfileResponse } from "@enterpriseforms/api-client";
import { Button, Checkbox, DescriptionList, TextInput } from "@enterpriseforms/ui";
import { useState, type ReactNode } from "react";
import { providerName } from "./MemberTable";

export const DISPLAY_NAME_MAXIMUM = 200;
export const PREFERRED_NAME_MAXIMUM = 100;
export const JOB_TITLE_MAXIMUM = 100;

/** What the form would send, or null when nothing changed. Exported for tests. */
export function profileRequest(profile: UserProfileResponse, values: { displayName: string; preferredName: string; jobTitle: string; useIdentityName: boolean }): UpdateUserProfileRequest | null {
  const preferred = values.preferredName.trim() || null;
  const title = values.jobTitle.trim() || null;
  const reset = values.useIdentityName;
  const name = values.displayName.trim().replace(/\s+/g, " ");
  const nameChanged = reset ? profile.displayNameOverridden : name !== profile.displayName;
  if (!nameChanged && preferred === (profile.preferredName ?? null) && title === (profile.jobTitle ?? null)) {
    return null;
  }

  return { displayName: reset ? null : name, preferredName: preferred, jobTitle: title, expectedRevision: profile.profileRevision, resetDisplayName: reset };
}

/**
 * Non-security profile fields (display name, preferred name, job title) with the verified identity shown read-only. Used by the
 * People "Edit profile" dialog (owners) and the "My profile" page. Nothing here changes identity, membership or roles.
 */
export function ProfileForm({
  profile,
  busy,
  error,
  onSubmit,
  onCancel,
}: {
  profile: UserProfileResponse;
  busy: boolean;
  error?: ReactNode;
  onSubmit: (request: UpdateUserProfileRequest) => void;
  onCancel?: (() => void) | undefined;
}) {
  const [displayName, setDisplayName] = useState(profile.displayName);
  const [preferredName, setPreferredName] = useState(profile.preferredName ?? "");
  const [jobTitle, setJobTitle] = useState(profile.jobTitle ?? "");
  const [useIdentityName, setUseIdentityName] = useState(false);
  const request = profileRequest(profile, { displayName, preferredName, jobTitle, useIdentityName });
  const nameMissing = !useIdentityName && displayName.trim().length === 0;
  const identityName = profile.identityDisplayName;
  return (
    <form
      className="flex flex-col gap-4"
      onSubmit={(event) => {
        event.preventDefault();
        if (request && !nameMissing) {
          onSubmit(request);
        }
      }}
    >
      <TextInput
        label="Display name"
        required
        value={useIdentityName ? identityName : displayName}
        disabled={useIdentityName}
        maxLength={DISPLAY_NAME_MAXIMUM}
        autoComplete="off"
        helpText="Shown to everyone in this organization wherever your name appears."
        onChange={(event) => setDisplayName(event.target.value)}
      />
      {profile.displayNameOverridden || identityName !== profile.displayName ? (
        <Checkbox
          label="Use the name from the identity provider"
          checked={useIdentityName}
          onCheckedChange={(checked) => setUseIdentityName(checked === true)}
          helpText={`Your identity provider currently says “${identityName}”. An edited display name is kept at every sign-in until you choose this.`}
        />
      ) : null}
      <TextInput
        label="Preferred name"
        value={preferredName}
        maxLength={PREFERRED_NAME_MAXIMUM}
        autoComplete="off"
        helpText="Optional. How you like to be addressed."
        onChange={(event) => setPreferredName(event.target.value)}
      />
      <TextInput label="Job title" value={jobTitle} maxLength={JOB_TITLE_MAXIMUM} autoComplete="off" helpText="Optional." onChange={(event) => setJobTitle(event.target.value)} />
      <DescriptionList
        items={[
          { term: "Verified identity", detail: <span title={`${profile.identitySubject} · ${profile.identityIssuer}`}>{`${profile.identitySubject} · ${providerName(profile.identityIssuer)}`}</span> },
        ]}
      />
      <p className="m-0 text-help text-muted">Your verified identity, organization roles and membership are managed by the Authorization Authority and cannot be changed in a profile.</p>
      {error}
      <div className="flex flex-wrap justify-end gap-2">
        {onCancel ? (
          <Button type="button" variant="secondary" onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
        ) : null}
        <Button type="submit" busy={busy} disabled={!request || nameMissing}>
          Save profile
        </Button>
      </div>
    </form>
  );
}
