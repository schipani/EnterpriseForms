import * as RadixCheckbox from "@radix-ui/react-checkbox";
import { useId } from "react";
import { errorTextClass, helpTextClass } from "./Field";

export interface CheckboxProps {
  label: string;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  helpText?: string | undefined;
  error?: string | undefined;
  disabled?: boolean | undefined;
  required?: boolean | undefined;
  name?: string | undefined;
}

/** Obvious checked state (ADR-PB-014): black fill plus a white check mark inside a 44px row, never color alone. */
export function Checkbox({ label, checked, onCheckedChange, helpText, error, disabled, required, name }: CheckboxProps) {
  const id = useId();
  const helpId = `${id}-help`;
  const errorId = `${id}-error`;
  const describedBy = [helpText ? helpId : null, error ? errorId : null].filter(Boolean).join(" ") || undefined;
  return (
    <div className="flex flex-col gap-1">
      <div className="flex min-h-control items-center gap-3">
        <RadixCheckbox.Root
          id={id}
          {...(name !== undefined ? { name } : {})}
          checked={checked}
          {...(disabled !== undefined ? { disabled } : {})}
          {...(required !== undefined ? { required } : {})}
          aria-describedby={describedBy}
          aria-invalid={error ? true : undefined}
          onCheckedChange={(value) => onCheckedChange(value === true)}
          className="flex h-6 w-6 shrink-0 items-center justify-center rounded border-2 border-input bg-surface data-[state=checked]:border-primary data-[state=checked]:bg-primary disabled:cursor-not-allowed disabled:opacity-60 aria-[invalid=true]:border-danger"
        >
          <RadixCheckbox.Indicator>
            <svg aria-hidden="true" viewBox="0 0 16 16" className="h-4 w-4 text-primary-foreground" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round">
              <path d="M3 8.5l3.2 3L13 4.5" />
            </svg>
          </RadixCheckbox.Indicator>
        </RadixCheckbox.Root>
        <label htmlFor={id} className="text-label font-medium text-body">
          {label}
          {required ? <span className="sr-only"> (required)</span> : null}
        </label>
      </div>
      {helpText ? (
        <p id={helpId} className={`pl-9 ${helpTextClass}`}>
          {helpText}
        </p>
      ) : null}
      {error ? (
        <p id={errorId} role="alert" className={`pl-9 ${errorTextClass}`}>
          {error}
        </p>
      ) : null}
    </div>
  );
}
