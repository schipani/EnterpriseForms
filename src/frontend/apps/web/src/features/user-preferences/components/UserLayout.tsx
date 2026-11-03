import { NavigationIcons, PageHeader } from "@enterpriseforms/ui";
import type { PropsWithChildren, ReactNode } from "react";
import { SideNavLayout, type SideNavGroup } from "../../support";

/**
 * The signed-in person's own area (/app/user): My Profile, and the Experience & Preferences group (Regional Settings, Language,
 * Text Size). Every signed-in member sees every item, so there is no capability logic here: the pages only ever read and write
 * the caller's own profile and preferences, and the server enforces that.
 */
export const USER_NAVIGATION: SideNavGroup[] = [
  {
    id: "user",
    title: "User",
    icon: NavigationIcons.circleUserRound,
    items: [{ to: "/app/user", label: "My Profile", icon: NavigationIcons.userRound, exact: true }],
  },
  {
    id: "experience",
    title: "Experience & Preferences",
    icon: NavigationIcons.palette,
    items: [
      { to: "/app/user/regional", label: "Regional Settings", icon: NavigationIcons.globe },
      { to: "/app/user/language", label: "Language", icon: NavigationIcons.languages },
      { to: "/app/user/text-size", label: "Text Size", icon: NavigationIcons.accessibility },
    ],
  },
];

/** Page header plus the User sidebar (same layout as the Organization pages). */
export function UserLayout({ title, lead, children }: PropsWithChildren<{ title: string; lead?: ReactNode }>) {
  return (
    <>
      <PageHeader title={title} lead={lead} />
      <SideNavLayout label="User" groups={USER_NAVIGATION}>
        {children}
      </SideNavLayout>
    </>
  );
}
