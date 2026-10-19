import * as RadixTabs from "@radix-ui/react-tabs";
import type { ReactNode } from "react";

export interface TabItem {
  value: string;
  /** A plain string, or a short node (e.g. label plus an error badge) — a string still satisfies the accessible name. */
  label: ReactNode;
  content: ReactNode;
}

export type TabsOrientation = "horizontal" | "vertical";

/**
 * `orientation="vertical"` restyles the tab list as a left-hand navigation column (C1-UX §A toolbox zone) without
 * changing the underlying ARIA roles: tabs stay `role="tab"`/`role="tablist"` either way, so existing
 * `getByRole("tab", { name })` locators keep working. Vertical orientation also switches Radix's own keyboard
 * handling to Up/Down arrows per the WAI-ARIA tabs pattern.
 */
export function Tabs({ items, label, defaultValue, value, onValueChange, orientation = "horizontal" }: { items: TabItem[]; label: string; defaultValue?: string; value?: string; onValueChange?: (value: string) => void; orientation?: TabsOrientation }) {
  const vertical = orientation === "vertical";
  return (
    <RadixTabs.Root
      {...(value !== undefined ? { value } : { defaultValue: defaultValue ?? items[0]?.value ?? "" })}
      {...(onValueChange ? { onValueChange } : {})}
      orientation={orientation}
      className={vertical ? "flex flex-col gap-4 lg:flex-row lg:items-start" : "flex flex-col gap-3"}
    >
      <RadixTabs.List aria-label={label} className={vertical ? "flex shrink-0 flex-row gap-1 overflow-x-auto border-b border-line pb-1 lg:w-56 lg:flex-col lg:border-b-0 lg:border-r lg:pb-0 lg:pr-2" : "flex flex-wrap gap-1 border-b border-line"}>
        {items.map((item) => (
          <RadixTabs.Trigger
            key={item.value}
            value={item.value}
            className={
              vertical
                ? "min-h-11 whitespace-nowrap rounded border-l-4 border-transparent px-3 py-2 text-left text-base font-medium text-muted data-[state=active]:border-primary data-[state=active]:bg-page data-[state=active]:text-body lg:whitespace-normal"
                : "min-h-11 border-b-4 border-transparent px-3 py-2 text-base font-medium text-muted data-[state=active]:border-primary data-[state=active]:text-body"
            }
          >
            {item.label}
          </RadixTabs.Trigger>
        ))}
      </RadixTabs.List>
      {items.map((item) => (
        <RadixTabs.Content key={item.value} value={item.value} className={vertical ? "min-w-0 flex-1" : undefined}>
          {item.content}
        </RadixTabs.Content>
      ))}
    </RadixTabs.Root>
  );
}
