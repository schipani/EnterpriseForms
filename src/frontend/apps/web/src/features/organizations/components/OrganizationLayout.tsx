import { useSession } from "@enterpriseforms/app-runtime";
import { PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { Link } from "@tanstack/react-router";
import type { PropsWithChildren, ReactNode } from "react";

export type OrganizationNavItem = { to: string; label: string; exact?: boolean };

/**
 * Which Organization pages a session may open, decided from the capabilities the server projected from the Authority
 * (navigation hints only: every page and action is authorized again by the server on each request). Pure, so it can be
 * unit-tested without a router.
 */
export function organizationNavigation(hasCapability: (capability: string) => boolean): OrganizationNavItem[] {
  const manage = hasCapability("organization.manage");
  const security = hasCapability("security.administer");
  const administrator = manage || security;
  const items: Array<OrganizationNavItem & { show: boolean }> = [
    { to: "/app/organization", label: "Overview", exact: true, show: administrator },
    { to: "/app/organization/users", label: "People", show: administrator },
    { to: "/app/organization/teams", label: "Teams", show: administrator },
    { to: "/app/organization/branding", label: "Branding", show: manage },
    { to: "/app/organization/data-protection", label: "Data protection", show: administrator },
    { to: "/app/organization/security-administrators", label: "Security administrators", show: administrator },
    { to: "/app/organization/search-approvals", label: "Search approvals", show: security },
  ];
  return items.filter((item) => item.show).map((item) => ({ to: item.to, label: item.label, ...(item.exact ? { exact: true } : {}) }));
}

/**
 * Phase C8 M1 administration shell (architecture §3.3): page header plus a left-hand "Organization" navigation that
 * collapses to a horizontally scrollable strip below `lg`. A session with neither organization.manage nor
 * security.administer sees a generic "no permission" message and no navigation, so the page itself hints at nothing.
 */
export function OrganizationLayout({ title, lead, actions, children }: PropsWithChildren<{ title: string; lead?: ReactNode; actions?: ReactNode }>) {
  const session = useSession();
  const items = organizationNavigation(session.hasCapability);
  if (items.length === 0) {
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
      <div className="grid min-w-0 grid-cols-1 gap-4 lg:grid-cols-[16rem_minmax(0,1fr)] lg:items-start">
        <nav aria-label="Organization" className="min-w-0 rounded-panel border border-line bg-surface p-3 lg:sticky lg:top-4">
          <ul className="-mx-1 flex min-w-0 flex-row gap-1 overflow-x-auto px-1 pb-1 lg:flex-col lg:overflow-visible lg:pb-0">
            {items.map((item) => (
              <li key={item.to} className="shrink-0">
                <Link
                  to={item.to}
                  activeOptions={{ exact: item.exact ?? false }}
                  className="inline-flex min-h-11 w-full items-center whitespace-nowrap rounded-control px-3 text-base font-medium text-muted hover:bg-page hover:text-body lg:whitespace-normal"
                  activeProps={{ className: "inline-flex min-h-11 w-full items-center whitespace-nowrap rounded-control px-3 text-base font-medium bg-page text-body underline underline-offset-8 decoration-4 lg:whitespace-normal", "aria-current": "page" }}
                >
                  {item.label}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
        <div className="min-w-0">{children}</div>
      </div>
    </>
  );
}
