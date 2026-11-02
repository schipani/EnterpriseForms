import { useId, type ReactNode } from "react";
import { RequiredMarker, errorTextClass, helpTextClass, legendClass } from "./Field";

export interface RadioGroupOption {
  value: string;
  label: ReactNode;
  disabled?: boolean | undefined;
  /** Content that belongs to this choice (for example a follow-up selector), shown indented beneath it while it is selected. */
  details?: ReactNode;
}

export interface RadioGroupProps {
  legend: string;
  value: string | undefined;
  onValueChange: (value: string) => void;
  options: RadioGroupOption[];
  helpText?: string | undefined;
  error?: string | undefined;
  required?: boolean | undefined;
  /** Shared name of the native radios; generated when omitted. */
  name?: string | undefined;
}

/**
 * A single choice among a few visible options: native radio inputs (arrow-key navigation, forced-colors support) inside a
 * fieldset whose legend names the group, styled like the other choice controls (Checkbox) and the field standard.
 */
export function RadioGroup({ legend, value, onValueChange, options, helpText, error, required, name }: RadioGroupProps) {
  const id = useId();
  const groupName = name ?? `${id}-radio`;
  const helpId = `${id}-help`;
  const errorId = `${id}-error`;
  const describedBy = [helpText ? helpId : null, error ? errorId : null].filter(Boolean).join(" ") || undefined;
  return (
    <fieldset className="flex min-w-0 flex-col gap-1" aria-describedby={describedBy} aria-invalid={error ? true : undefined}>
      <legend className={`mb-1 ${legendClass}`}>
        {legend}
        {required ? <RequiredMarker /> : null}
      </legend>
      {options.map((option) => {
        const optionId = `${id}-${option.value}`;
        return (
          <div key={option.value} className="flex flex-col gap-2">
            <div className="flex min-h-control items-start gap-3 py-2.5">
              <input
                type="radio"
                id={optionId}
                name={groupName}
                value={option.value}
                checked={value === option.value}
                disabled={option.disabled}
                required={required}
                onChange={() => onValueChange(option.value)}
                className="h-5 w-5 shrink-0 accent-primary disabled:cursor-not-allowed"
              />
              <label htmlFor={optionId} className={`text-label font-medium ${option.disabled ? "text-muted" : "text-body"}`}>
                {option.label}
              </label>
            </div>
            {option.details && value === option.value ? <div className="pl-8">{option.details}</div> : null}
          </div>
        );
      })}
      {helpText ? (
        <p id={helpId} className={helpTextClass}>
          {helpText}
        </p>
      ) : null}
      {error ? (
        <p id={errorId} role="alert" className={errorTextClass}>
          {error}
        </p>
      ) : null}
    </fieldset>
  );
}
