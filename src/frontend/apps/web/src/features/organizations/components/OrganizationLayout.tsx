import { useSession } from "@enterpriseforms/app-runtime";
import { NavigationIcons, PageHeader, StatusMessage, type NavigationIconComponent } from "@enterpriseforms/ui";
import { Link } from "@tanstack/react-router";
import type { PropsWithChildren, ReactNode } from "react";

export type OrganizationNavItem = { to: string; label: string; icon: NavigationIconComponent; exact?: boolean };
export type OrganizationNavGroup = { id: string; title: string; icon: NavigationIconComponent; items: OrganizationNavItem[] };

type Capabilities = { manage: boolean; security: boolean; administrator: boolean };

/** One item of the declarative configuration: where it goes, how it is labelled and which capability shows it. */
type ConfiguredItem = OrganizationNavItem & { visible: (c: Capabilities) => boolean };
type ConfiguredGroup = Omit<OrganizationNavGroup, "items"> & { items: ConfiguredItem[] };

/**
 * The Organization administration navigation: three groups with their icons, items in the specified order. Visibility is decided
 * per item from the capabilities the server projected from the Authority (navigation hints only: every page and action is
 * authorized again by the server on each request). A group with no visible item is not shown at all.
 */
const CONFIGURATION: ConfiguredGroup[] = [
  {
    id: "organization",
    title: "Organization",
    icon: NavigationIcons.building2,
    items: [
      { to: "/app/organization", label: "Overview", icon: NavigationIcons.layoutDashboard, exact: true, visible: (c) => c.administrator },
      { to: "/app/organization/users", label: "People", icon: NavigationIcons.users, visible: (c) => c.administrator },
      { to: "/app/organization/teams", label: "Teams", icon: NavigationIcons.usersRound, visible: (c) => c.administrator },
    ],
  },
  {
    id: "appearance",
    title: "Appearance & Localization",
    icon: NavigationIcons.palette,
    items: [
      { to: "/app/organization/branding", label: "Branding", icon: NavigationIcons.paintbrush, visible: (c) => c.manage },
      { to: "/app/organization/regional", label: "Regional Settings", icon: NavigationIcons.globe, visible: (c) => c.manage },
      { to: "/app/organization/language", label: "Language", icon: NavigationIcons.languages, visible: (c) => c.manage },
      { to: "/app/organization/text-size", label: "Text Size", icon: NavigationIcons.accessibility, visible: (c) => c.manage },
    ],
  },
  {
    id: "security",
    title: "Security & Governance",
    icon: NavigationIcons.shieldCheck,
    items: [
      { to: "/app/organization/data-protection", label: "Data Protection", icon: NavigationIcons.lockKeyhole, visible: (c) => c.administrator },
      { to: "/app/organization/security-administrators", label: "Security Administrators", icon: NavigationIcons.shieldUser, visible: (c) => c.administrator },
      { to: "/app/organization/search-approvals", label: "Search Approvals", icon: NavigationIcons.clipboardCheck, visible: (c) => c.security },
    ],
  },
];

/** The groups and items a session may see, in order. Pure, so it can be unit-tested without a router. */
export function organizationNavigation(hasCapability: (capability: string) => boolean): OrganizationNavGroup[] {
  const manage = hasCapability("organization.manage");
  const security = hasCapability("security.administer");
  const capabilities: Capabilities = { manage, security, administrator: manage || security };
  return CONFIGURATION.map((group) => ({
    id: group.id,
    title: group.title,
    icon: group.icon,
    items: group.items.filter((item) => item.visible(capabilities)).map((item) => ({ to: item.to, label: item.label, icon: item.icon, ...(item.exact ? { exact: true } : {}) })),
  })).filter((group) => group.items.length > 0);
}

/**
 * Phase C8 M1 administration shell (architecture §3.3): page header plus a left-hand "Organization" navigation of three labelled groups
 * (stacked above the page content below `lg`, never a horizontally scrolling strip). A session with neither organization.manage nor
 * security.administer sees a generic "no permission" message and no navigation, so the page itself hints at nothing.
 */
export function OrganizationLayout({ title, lead, actions, children }: PropsWithChildren<{ title: string; lead?: ReactNode; actions?: ReactNode }>) {
  const session = useSession();
  const groups = organizationNavigation(session.hasCapability);
  if (groups.length === 0) {
    return (
      <>
        <PageHeader title="Organization" />
        <StatusMessage tone="info" title="No permission">
          Organization administration is for organization owners and Security Administrators. Access is decided by the independent authorization authority.
        </StatusMessage>
      </>
    );
  }
  return (
    <>
      <PageHeader title={title} lead={lead} actions={actions} />
      <div className="grid min-w-0 grid-cols-1 gap-4 lg:grid-cols-[18rem_minmax(0,1fr)] lg:items-start">
        <nav aria-label="Organization" className="min-w-0 rounded-panel border border-line bg-surface p-3 lg:sticky lg:top-4">
          {groups.map((group, index) => {
            const GroupIcon = group.icon;
            return (
              <div key={group.id} className={index === 0 ? "" : "mt-3 border-t border-line pt-3"}>
                <h2 id={`organization-nav-${group.id}`} className="m-0 flex min-h-9 items-center gap-2 px-3 text-base font-semibold text-body">
                  <GroupIcon aria-hidden="true" focusable="false" size={18} strokeWidth={2} className="shrink-0" />
                  <span>{group.title}</span>
                </h2>
                <ul aria-labelledby={`organization-nav-${group.id}`} className="m-0 mt-1 flex list-none flex-col gap-1 p-0 pl-3">
                  {group.items.map((item) => {
                    const ItemIcon = item.icon;
                    return (
                      <li key={item.to}>
                        <Link
                          to={item.to}
                          activeOptions={{ exact: item.exact ?? false }}
                          className="flex min-h-11 w-full items-center gap-2 rounded-control border-l-[3px] border-transparent px-3 text-base font-medium text-muted hover:bg-page hover:text-body"
                          activeProps={{ className: "flex min-h-11 w-full items-center gap-2 rounded-control border-l-[3px] border-primary bg-page px-3 text-base font-semibold text-body", "aria-current": "page" }}
                        >
                          <ItemIcon aria-hidden="true" focusable="false" size={16} strokeWidth={2} className="shrink-0" />
                          <span className="min-w-0">{item.label}</span>
                        </Link>
                      </li>
                    );
                  })}
                </ul>
              </div>
            );
          })}
        </nav>
        <div className="min-w-0">{children}</div>
      </div>
    </>
  );
}
