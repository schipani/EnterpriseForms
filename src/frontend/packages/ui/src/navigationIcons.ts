import { Accessibility, Building2, ClipboardCheck, Globe, LayoutDashboard, Languages, LockKeyhole, Paintbrush, Palette, ShieldCheck, ShieldUser, Users, UsersRound, UserRound, CircleUserRound, Menu, type LucideIcon } from "lucide-react";

export type NavigationIconComponent = LucideIcon;

/**
 * Icons of the Organization administration navigation (group headings and items), from the same Lucide library as the action
 * icons. Add an entry here rather than importing an icon in a feature. The keys are the Lucide icon names in camel case.
 */
export const NavigationIcons = {
  building2: Building2,
  layoutDashboard: LayoutDashboard,
  users: Users,
  usersRound: UsersRound,
  palette: Palette,
  paintbrush: Paintbrush,
  globe: Globe,
  languages: Languages,
  accessibility: Accessibility,
  shieldCheck: ShieldCheck,
  lockKeyhole: LockKeyhole,
  shieldUser: ShieldUser,
  clipboardCheck: ClipboardCheck,
  userRound: UserRound,
  circleUserRound: CircleUserRound,
  /** The compact header's menu button. */
  menu: Menu,
} as const satisfies Record<string, NavigationIconComponent>;
