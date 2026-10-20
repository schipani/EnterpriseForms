import * as RadixTabs from "@radix-ui/react-tabs";
import type { ReactNode } from "react";

export interface SideNavItem {
  value: string;
  label: ReactNode;
  /** Decorative icon (rendered aria-hidden). */
  icon?: ReactNode;
  content: ReactNode;
}

/**
 * Workspace layout with a left-hand vertical navigation (C1 designer rebuild): a header block above a vertical
 * `role="tablist"` on desktop, collapsing to a horizontally scrollable tab strip under the header below `lg`.
 * Implements the WAI-ARIA tabs pattern (Radix), so Up/Down arrows move between items and every item is reachable
 * without a pointer. The active item uses the primary theme token for its indicator — never a hard-coded colour —
 * so an organization's validated branding applies here as everywhere else.
 */
export function SideNavTabs({ label, header, items, value, onValueChange }: { label: string; header?: ReactNode; items: SideNavItem[]; value: string; onValueChange: (value: string) => void }) {
  return (
    <RadixTabs.Root value={value} onValueChange={onValueChange} orientation="vertical" className="grid min-w-0 grid-cols-1 gap-4 lg:grid-cols-[13.5rem_minmax(0,1fr)] lg:items-start">
      <div className="flex min-w-0 flex-col gap-3 rounded-lg border border-line bg-surface p-3 lg:sticky lg:top-4 lg:max-h-[calc(100vh-2rem)] lg:overflow-y-auto">
        {header}
        <RadixTabs.List aria-label={label} aria-orientation="vertical" className="-mx-1 flex min-w-0 flex-row gap-1 overflow-x-auto px-1 pb-1 lg:flex-col lg:overflow-visible lg:pb-0">
          {items.map((item) => (
            <RadixTabs.Trigger
              key={item.value}
              value={item.value}
              className="flex min-h-11 shrink-0 items-center gap-3 whitespace-nowrap rounded border-l-[3px] border-transparent px-3 py-2 text-left text-base font-medium text-muted hover:bg-page hover:text-body data-[state=active]:border-primary data-[state=active]:bg-page data-[state=active]:font-semibold data-[state=active]:text-body lg:whitespace-normal"
            >
              {item.icon ? (
                <span aria-hidden="true" className="flex h-5 w-5 shrink-0 items-center justify-center">
                  {item.icon}
                </span>
              ) : null}
              <span className="min-w-0">{item.label}</span>
            </RadixTabs.Trigger>
          ))}
        </RadixTabs.List>
      </div>
      <div className="min-w-0">
        {items.map((item) => (
          <RadixTabs.Content key={item.value} value={item.value} className="min-w-0 focus-visible:outline-none">
            {item.content}
          </RadixTabs.Content>
        ))}
      </div>
    </RadixTabs.Root>
  );
}
