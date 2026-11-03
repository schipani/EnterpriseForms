import type { UserProfileResponse } from "@enterpriseforms/api-client";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ProfileForm, profileRequest } from "./ProfileForm";

globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;

const profile: UserProfileResponse = {
  userId: "u1",
  displayName: "Avery Owner",
  identityDisplayName: "Avery Owner",
  displayNameOverridden: false,
  preferredName: "Av",
  jobTitle: null,
  profileRevision: 3,
  profileModifiedOn: null,
  identityIssuer: "https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0",
  identitySubject: "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
  isCurrentUser: true,
  canEdit: true,
};

const values = (over: Partial<Parameters<typeof profileRequest>[1]> = {}) => ({ displayName: "Avery Owner", preferredName: "Av", jobTitle: "", useIdentityName: false, ...over });

describe("profileRequest", () => {
  it("is null when nothing changed, so there is nothing to save", () => {
    expect(profileRequest(profile, values())).toBeNull();
    expect(profileRequest(profile, values({ displayName: "  Avery   Owner " }))).toBeNull(); // whitespace-only differences are not changes
  });

  it("sends the revision token and only the non-security fields", () => {
    const request = profileRequest(profile, values({ displayName: "Avery Q. Owner", jobTitle: " Intake lead " }));
    expect(request).toEqual({ displayName: "Avery Q. Owner", preferredName: "Av", jobTitle: "Intake lead", expectedRevision: 3, resetDisplayName: false });
    expect(Object.keys(request!).sort()).toEqual(["displayName", "expectedRevision", "jobTitle", "preferredName", "resetDisplayName"]); // no identity, role or membership field exists
  });

  it("clearing an optional field sends null; resetting restores the identity provider's name", () => {
    expect(profileRequest(profile, values({ preferredName: "" }))?.preferredName).toBeNull();
    const overridden = { ...profile, displayName: "Edited", displayNameOverridden: true };
    expect(profileRequest(overridden, values({ displayName: "Edited", useIdentityName: true }))).toEqual({
      displayName: null,
      preferredName: "Av",
      jobTitle: null,
      expectedRevision: 3,
      resetDisplayName: true,
    });
    expect(profileRequest(profile, values({ useIdentityName: true }))).toBeNull(); // already following the identity provider
  });
});

describe("ProfileForm", () => {
  it("shows the verified identity read-only and disables Save until something changes", async () => {
    const onSubmit = vi.fn();
    render(<ProfileForm profile={profile} busy={false} onSubmit={onSubmit} />);
    expect(screen.getByText("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0 · Microsoft Entra ID")).toBeInTheDocument();
    expect(screen.getByText(/cannot be changed in a profile/)).toBeInTheDocument();
    expect(screen.queryByLabelText(/email/i)).toBeNull();
    const save = screen.getByRole("button", { name: "Save profile" });
    expect(save).toBeDisabled();
    const user = userEvent.setup();
    await user.type(screen.getByLabelText("Job title"), "Lead");
    expect(save).toBeEnabled();
    await user.click(save);
    expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ jobTitle: "Lead", expectedRevision: 3 }));
  });

  it("cannot save an empty display name", async () => {
    render(<ProfileForm profile={profile} busy={false} onSubmit={vi.fn()} />);
    const user = userEvent.setup();
    await user.clear(screen.getByLabelText(/Display name/));
    expect(screen.getByRole("button", { name: "Save profile" })).toBeDisabled();
  });

  it("offers the identity provider's name only when the display name was edited", async () => {
    const { rerender } = render(<ProfileForm profile={profile} busy={false} onSubmit={vi.fn()} />);
    expect(screen.queryByRole("checkbox", { name: /identity provider/ })).toBeNull();
    rerender(<ProfileForm profile={{ ...profile, displayName: "Edited", displayNameOverridden: true }} busy={false} onSubmit={vi.fn()} />);
    expect(screen.getByRole("checkbox", { name: /Use the name from the identity provider/ })).toBeInTheDocument();
  });
});
