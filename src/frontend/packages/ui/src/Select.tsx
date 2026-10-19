import * as RadixSelect from "@radix-ui/react-select";
import { FieldShell } from "./Field";

export interface SelectOption {
  value: string;
  label: string;
  disabled?: boolean | undefined;
}

export interface SelectProps {
  label: string;
  value: string | undefined;
  onValueChange: (value: string) => void;
  options: SelectOption[];
  placeholder?: string | undefined;
  helpText?: string | undefined;
  error?: string | undefined;
  required?: boolean | undefined;
  disabled?: boolean | undefined;
  name?: string | undefined;
}

/** Radix Select: keyboard typeahead, roving focus, portal-safe; the trigger is the labelled control. */
export function Select({ label, value, onValueChange, options, placeholder = "Select an option", helpText, error, required, disabled, name }: SelectProps) {
  return (
    <FieldShell label={label} helpText={helpText} error={error} required={required}>
      {({ inputId, describedBy, invalid }) => (
        <RadixSelect.Root value={value ?? ""} onValueChange={onValueChange} {...(disabled !== undefined ? { disabled } : {})} {...(name !== undefined ? { name } : {})} {...(required !== undefined ? { required } : {})}>
          <RadixSelect.Trigger
            id={inputId}
            aria-describedby={describedBy}
            aria-invalid={invalid || undefined}
            className="flex min-h-11 w-full items-center justify-between gap-2 rounded border border-input bg-surface px-3 py-2 text-left text-base text-body data-[placeholder]:text-muted disabled:bg-page disabled:text-muted aria-[invalid=true]:border-[#991b1b]"
          >
            <RadixSelect.Value placeholder={placeholder} />
            <RadixSelect.Icon aria-hidden="true">
              <svg viewBox="0 0 16 16" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M4 6l4 4 4-4" />
              </svg>
            </RadixSelect.Icon>
          </RadixSelect.Trigger>
          <RadixSelect.Portal>
            <RadixSelect.Content position="popper" sideOffset={4} className="z-50 max-h-96 min-w-[var(--radix-select-trigger-width)] overflow-auto rounded border border-input bg-surface p-1 shadow-lg">
              <RadixSelect.Viewport>
                {options.map((option) => (
                  <RadixSelect.Item key={option.value} value={option.value} {...(option.disabled !== undefined ? { disabled: option.disabled } : {})} className="flex min-h-10 cursor-default select-none items-center rounded px-3 py-2 text-base text-body outline-none data-[disabled]:opacity-50 data-[highlighted]:bg-primary data-[highlighted]:text-primary-foreground">
                    <RadixSelect.ItemText>{option.label}</RadixSelect.ItemText>
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
