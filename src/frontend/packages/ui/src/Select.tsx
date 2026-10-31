import * as RadixSelect from "@radix-ui/react-select";
import type { ReactNode } from "react";
import { FieldShell } from "./Field";

export interface SelectOption {
  value: string;
  label: string;
  disabled?: boolean | undefined;
  /** Extra content after the label in the open list only (for example a presence indicator); the closed control shows the plain label. */
  trailing?: ReactNode;
}

export interface SelectProps {
  label: string;
  value: string | undefined;
  onValueChange: (value: string) => void;
  options: SelectOption[];
  placeholder?: string | undefined;
  helpText?: string | undefined;
  note?: string | undefined;
  error?: string | undefined;
  required?: boolean | undefined;
  disabled?: boolean | undefined;
  name?: string | undefined;
  /** Buttons that belong to this control, in the same row (see FieldShell). */
  actions?: ReactNode;
}

/** Radix Select: keyboard typeahead, roving focus, portal-safe; the trigger is the labelled control. */
export function Select({ label, value, onValueChange, options, placeholder = "Select an option", helpText, note, error, required, disabled, name, actions }: SelectProps) {
  return (
    <FieldShell label={label} helpText={helpText} note={note} error={error} required={required} actions={actions}>
      {({ inputId, describedBy, invalid }) => (
        <RadixSelect.Root value={value ?? ""} onValueChange={onValueChange} {...(disabled !== undefined ? { disabled } : {})} {...(name !== undefined ? { name } : {})} {...(required !== undefined ? { required } : {})}>
          <RadixSelect.Trigger
            id={inputId}
            aria-describedby={describedBy}
            aria-invalid={invalid || undefined}
            className="flex min-h-control w-full items-center justify-between gap-2 rounded-control border border-input bg-surface px-3 py-2 text-left text-control text-body data-[placeholder]:text-muted disabled:cursor-not-allowed disabled:bg-page disabled:text-muted aria-[invalid=true]:border-danger"
          >
            <RadixSelect.Value placeholder={placeholder} />
            <RadixSelect.Icon aria-hidden="true">
              <svg viewBox="0 0 16 16" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M4 6l4 4 4-4" />
              </svg>
            </RadixSelect.Icon>
          </RadixSelect.Trigger>
          <RadixSelect.Portal>
            <RadixSelect.Content position="popper" sideOffset={4} className="z-50 max-h-96 min-w-[var(--radix-select-trigger-width)] overflow-auto rounded-control border border-input bg-surface p-1 shadow-lg">
              <RadixSelect.Viewport>
                {options.map((option) => (
                  <RadixSelect.Item key={option.value} value={option.value} {...(option.disabled !== undefined ? { disabled: option.disabled } : {})} className="flex min-h-10 cursor-default select-none items-center rounded-control px-3 py-2 text-control text-body outline-none data-[disabled]:opacity-50 data-[highlighted]:bg-primary data-[highlighted]:text-primary-foreground">
                    <RadixSelect.ItemText>{option.label}</RadixSelect.ItemText>
                    {option.trailing ? <span className="ml-3">{option.trailing}</span> : null}
                    <RadixSelect.ItemIndicator className="ml-auto pl-2" aria-hidden="true">
                      <svg viewBox="0 0 16 16" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="3">
                        <path d="M3 8.5l3.2 3L13 4.5" />
                      </svg>
                    </RadixSelect.ItemIndicator>
                  </RadixSelect.Item>
                ))}
              </RadixSelect.Viewport>
            </RadixSelect.Content>
          </RadixSelect.Portal>
        </RadixSelect.Root>
      )}
    </FieldShell>
  );
}
