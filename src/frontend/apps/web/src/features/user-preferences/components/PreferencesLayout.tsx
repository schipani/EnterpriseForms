import { PageHeader } from "@enterpriseforms/ui";
import { Link } from "@tanstack/react-router";
import type { PropsWithChildren } from "react";

export type PreferencesNavItem = { to: string; label: string; exact?: boolean };

/** The personal preference areas, in order. Each is its own route so a tab can be linked to and reloaded. */
export const PREFERENCES_NAVIGATION: PreferencesNavItem[] = [
  { to: "/app/settings/preferences", label: "Time zone, date, number and language", exact: true },
  { to: "/app/settings/preferences/text-size", label: "Text size" },
];

/**
 * PG-007 shell: page header plus a left-hand navigation that collapses to a horizontally scrollable strip below `lg`, the same
 * pattern as the Organization pages. Every signed-in member sees both areas; each saves on its own.
 */
export function PreferencesLayout({ children }: PropsWithChildren) {
  return (
    <>
      <PageHeader title="Preferences" lead="How you see dates, times, numbers and text. These choices apply to you only." />
      <div className="grid min-w-0 grid-cols-1 gap-4 lg:grid-cols-[16rem_minmax(0,1fr)] lg:items-start">
        <nav aria-label="Preferences" className="min-w-0 rounded-panel border border-line bg-surface p-3 lg:sticky lg:top-4">
          <ul className="-mx-1 flex min-w-0 flex-row gap-1 overflow-x-auto px-1 pb-1 lg:flex-col lg:overflow-visible lg:pb-0">
            {PREFERENCES_NAVIGATION.map((item) => (
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
