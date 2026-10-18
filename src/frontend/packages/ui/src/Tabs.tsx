import * as RadixTabs from "@radix-ui/react-tabs";
import type { ReactNode } from "react";

export interface TabItem {
  value: string;
  label: string;
  content: ReactNode;
}

export function Tabs({ items, label, defaultValue, value, onValueChange }: { items: TabItem[]; label: string; defaultValue?: string; value?: string; onValueChange?: (value: string) => void }) {
  return (
    <RadixTabs.Root {...(value !== undefined ? { value } : { defaultValue: defaultValue ?? items[0]?.value ?? "" })} {...(onValueChange ? { onValueChange } : {})} className="flex flex-col gap-3">
      <RadixTabs.List aria-label={label} className="flex flex-wrap gap-1 border-b border-line">
        {items.map((item) => (
          <RadixTabs.Trigger key={item.value} value={item.value} className="min-h-11 border-b-4 border-transparent px-3 py-2 text-base font-medium text-muted data-[state=active]:border-primary data-[state=active]:text-body">
            {item.label}
          </RadixTabs.Trigger>
        ))}
      </RadixTabs.List>
      {items.map((item) => (
        <RadixTabs.Content key={item.value} value={item.value}>
          {item.content}
        </RadixTabs.Content>
      ))}
    </RadixTabs.Root>
  );
}
