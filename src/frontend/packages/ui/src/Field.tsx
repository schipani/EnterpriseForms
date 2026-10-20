import * as Label from "@radix-ui/react-label";
import { forwardRef, useId, type InputHTMLAttributes, type ReactNode, type TextareaHTMLAttributes } from "react";

export interface FieldShellProps {
  label: string;
  helpText?: string | undefined;
  /** Short status note rendered directly below the control (e.g. "Encrypted for privacy — not searchable."); also announced via aria-describedby. */
  note?: string | undefined;
  error?: string | undefined;
  required?: boolean | undefined;
  /** Children receive the ids needed to wire aria attributes to the control. */
  children: (ids: { inputId: string; describedBy: string | undefined; invalid: boolean }) => ReactNode;
  className?: string;
}

/** Label, help text and error message stay adjacent to their control and are associated by id (WCAG 1.3.1 / 3.3.1). */
export function FieldShell({ label, helpText, note, error, required, children, className = "" }: FieldShellProps) {
  const id = useId();
  const inputId = `${id}-input`;
  const helpId = `${id}-help`;
  const errorId = `${id}-error`;
  const noteId = `${id}-note`;
  const describedBy = [helpText ? helpId : null, note ? noteId : null, error ? errorId : null].filter(Boolean).join(" ") || undefined;
  return (
    <div className={`flex min-w-0 flex-col gap-1 ${className}`}>
      <Label.Root htmlFor={inputId} className="text-sm font-medium text-body">
        {label}
        {required ? (
          <span className="ml-1 text-[#991b1b]" aria-hidden="true">
            *
          </span>
        ) : null}
        {required ? <span className="sr-only"> (required)</span> : null}
      </Label.Root>
      {helpText ? (
        <p id={helpId} className="text-sm text-muted">
          {helpText}
        </p>
      ) : null}
      {children({ inputId, describedBy, invalid: Boolean(error) })}
      {note ? (
        <p id={noteId} className="text-sm text-muted">
          {note}
        </p>
      ) : null}
      {error ? (
        <p id={errorId} role="alert" className="text-sm font-medium text-[#991b1b]">
          {error}
        </p>
      ) : null}
    </div>
  );
}

export const inputClass = "min-h-11 w-full rounded border border-input bg-surface px-3 py-2 text-base text-body placeholder:text-muted read-only:bg-page disabled:bg-page disabled:text-muted aria-[invalid=true]:border-[#991b1b]";

export interface TextInputProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "id"> {
  label: string;
  helpText?: string | undefined;
  note?: string | undefined;
  error?: string | undefined;
}

export const TextInput = forwardRef<HTMLInputElement, TextInputProps>(function TextInput({ label, helpText, note, error, required, className = "", ...rest }, ref) {
  return (
    <FieldShell label={label} helpText={helpText} note={note} error={error} required={required} className={className}>
      {({ inputId, describedBy, invalid }) => <input ref={ref} id={inputId} aria-describedby={describedBy} aria-invalid={invalid || undefined} aria-required={required || undefined} className={inputClass} {...rest} />}
    </FieldShell>
  );
});

export interface TextAreaProps extends Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, "id"> {
  label: string;
  helpText?: string | undefined;
  note?: string | undefined;
  error?: string | undefined;
}

export const TextArea = forwardRef<HTMLTextAreaElement, TextAreaProps>(function TextArea({ label, helpText, note, error, required, className = "", rows = 4, ...rest }, ref) {
  return (
    <FieldShell label={label} helpText={helpText} note={note} error={error} required={required} className={className}>
      {({ inputId, describedBy, invalid }) => <textarea ref={ref} id={inputId} rows={rows} aria-describedby={describedBy} aria-invalid={invalid || undefined} aria-required={required || undefined} className={`${inputClass} min-h-24`} {...rest} />}
    </FieldShell>
  );
});
