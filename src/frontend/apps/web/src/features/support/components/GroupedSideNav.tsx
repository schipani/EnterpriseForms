import type { NavigationIconComponent } from "@enterpriseforms/ui";
import { Link } from "@tanstack/react-router";
import type { PropsWithChildren } from "react";

export type SideNavItem = { to: string; label: string; icon: NavigationIconComponent; exact?: boolean };
export type SideNavGroup = { id: string; title: string; icon: NavigationIconComponent; items: SideNavItem[] };

/**
 * The shared left-hand navigation of the Organization and User areas: labelled groups (heading with icon, items indented beneath),
 * a divider between groups, and the same sizes, spacing and semantics everywhere.
 *
 *  - Group headings are `h2` labels, not links; the list under each is named by its heading.
 *  - The active item is decided by the router (exact match only where `exact` is set, so Overview is not active on its sub pages).
 *    The router sets `aria-current="page"`; the style reads that attribute, so there is no separate selection state. The active
 *    item has a background, semibold text and a solid underline in the primary token under its label (not colour alone).
 *  - Icons are decorative (aria-hidden) beside visible text: 18px on headings, 16px on items, 2px stroke.
 *  - The column is 18rem wide from `lg`; below it the groups stack above the content (no scrolling strip), links are 44px high.
 *  - Sticky from `lg` below the compact application header (4.5rem = 3.5rem header + 1rem gap).
 */
export function SideNavLayout({ label, groups, children }: PropsWithChildren<{ label: string; groups: SideNavGroup[] }>) {
  return (
    <div className="grid min-w-0 grid-cols-1 gap-4 lg:grid-cols-[18rem_minmax(0,1fr)] lg:items-start">
      <nav aria-label={label} className="min-w-0 rounded-panel border border-line bg-surface p-3 lg:sticky lg:top-[4.5rem]">
        {groups.map((group, index) => {
          const GroupIcon = group.icon;
          return (
            <div key={group.id} className={index === 0 ? "" : "mt-3 border-t border-line pt-3"}>
              <h2 id={`${label.toLowerCase()}-nav-${group.id}`} className="m-0 flex min-h-9 items-center gap-2 px-3 text-base font-semibold text-body">
                <GroupIcon aria-hidden="true" focusable="false" size={18} strokeWidth={2} className="shrink-0" />
                <span>{group.title}</span>
              </h2>
              <ul aria-labelledby={`${label.toLowerCase()}-nav-${group.id}`} className="m-0 mt-1 flex list-none flex-col gap-1 p-0 pl-3">
                {group.items.map((item) => {
                  const ItemIcon = item.icon;
                  return (
                    <li key={item.to}>
                      <Link
                        to={item.to}
                        activeOptions={{ exact: item.exact ?? false }}
                        activeProps={{ "aria-current": "page" }}
                        className="group flex min-h-11 w-full items-center gap-2 rounded-control px-3 text-base font-medium text-muted hover:bg-page hover:text-body aria-[current=page]:bg-page aria-[current=page]:font-semibold aria-[current=page]:text-body"
                      >
                        <ItemIcon aria-hidden="true" focusable="false" size={16} strokeWidth={2} className="shrink-0" />
                        <span className="min-w-0 border-b-[3px] border-transparent group-aria-[current=page]:border-primary">{item.label}</span>
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
  );
}
