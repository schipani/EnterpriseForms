import * as RadixDropdownMenu from "@radix-ui/react-dropdown-menu";
import type { ReactNode } from "react";
import { Button } from "./Button";

/** Shared dropdown-menu styling (row actions, the account menu, the designer's menus): one look for every menu. */
export const menuContentClass = "z-50 min-w-48 rounded-control border border-input bg-surface p-1 shadow-lg";
export const menuItemClass = "flex min-h-10 w-full cursor-default items-center rounded-control px-3 text-left text-control outline-none data-[highlighted]:bg-primary data-[highlighted]:text-primary-foreground";
const itemClass = menuItemClass;

export interface OverflowMenuItem {
  key: string;
  /** Rendered as the menu item's content; pass a Button/Link so existing onClick/href behavior is preserved. */
  content: ReactNode;
}

/**
 * Accessible "more actions" (…) overflow menu (Radix dropdown): roving tabindex, Escape to close, typeahead. Pass
 * `trigger` (a single button whose visible text names the menu) to use it as a labelled menu button instead of "…".
 */
export function OverflowMenu({ label, items, trigger }: { label: string; items: OverflowMenuItem[]; trigger?: ReactNode }) {
  if (items.length === 0) {
    return null;
  }
  return (
    <RadixDropdownMenu.Root>
      <RadixDropdownMenu.Trigger asChild>
        {trigger ?? (
          <Button variant="secondary" size="sm" aria-label={label}>
            <span aria-hidden="true">&hellip;</span>
          </Button>
        )}
      </RadixDropdownMenu.Trigger>
      <RadixDropdownMenu.Portal>
        <RadixDropdownMenu.Content align="end" sideOffset={6} className={menuContentClass}>
          {items.map((item) => (
            <RadixDropdownMenu.Item key={item.key} asChild className={itemClass}>
              {item.content}
            </RadixDropdownMenu.Item>
          ))}
        </RadixDropdownMenu.Content>
      </RadixDropdownMenu.Portal>
    </RadixDropdownMenu.Root>
  );
}

/**
 * Standard row-actions cell content (C1-UX §D): one visible primary action, plus a consistent "…" overflow menu for
 * the rest. Use inside a {@link DataTableColumn} marked `isActions: true`. Omitting `primary` or passing an empty
 * `overflow` array is fine — the container still right-aligns whatever is present.
 */
export function RowActions({ primary, overflow, overflowLabel }: { primary?: ReactNode; overflow?: OverflowMenuItem[]; overflowLabel: string }) {
  return (
    <div className="flex flex-wrap items-center justify-end gap-1">
      {primary}
      <OverflowMenu label={overflowLabel} items={overflow ?? []} />
    </div>
  );
}
