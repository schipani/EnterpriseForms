import * as Label from "@radix-ui/react-label";
import { forwardRef, useId, type InputHTMLAttributes, type ReactNode, type TextareaHTMLAttributes } from "react";

export interface FieldShellProps {
  label: string;
  helpText?: string | undefined;
  /** Short status note rendered directly below the control (e.g. "Encrypted for privacy — not searchable."); also announced via aria-describedby. */
  note?: string | undefined;
  error?: string | undefined;
  required?: boolean | undefined;
  /**
   * Buttons that belong to this control (for example Search and Clear next to a search box). They sit in the same row as the
   * control, top-aligned with it at the control's height, and wrap beneath it on narrow screens; help text and the error always
   * stay below the whole group, so a message appearing never moves them.
   */
  actions?: ReactNode;
  /** Children receive the ids needed to wire aria attributes to the control. */
  children: (ids: { inputId: string; describedBy: string | undefined; invalid: boolean }) => ReactNode;
  className?: string;
}

/**
 * Deprecated no-op kept so existing wrappers keep compiling. Help text now ALWAYS follows the control everywhere (see
 * `docs/ui/FORM_CONTROL_LAYOUT_STANDARD.md`); there is no longer a per-screen order.
 */
export function FieldLayoutProvider({ children }: { helpAfterControl?: boolean; children: ReactNode }) {
  return <>{children}</>;
}

/**
 * The one field layout of the application (docs/ui/FORM_CONTROL_LAYOUT_STANDARD.md), top to bottom:
 *  1. label (with the required marker),
 *  2. the control (and its own action buttons, same row),
 *  3. help text, then a status note,
 *  4. the validation message.
 * Help text is never placed between the label and the control. `aria-describedby` ties help, note and error to the control
 * (WCAG 1.3.1 / 3.3.1); the error is announced when it appears.
 */
export function FieldShell({ label, helpText, note, error, required, actions, children, className = "" }: FieldShellProps) {
  const id = useId();
  const inputId = `${id}-input`;
  const helpId = `${id}-help`;
  const errorId = `${id}-error`;
  const noteId = `${id}-note`;
  const describedBy = [helpText ? helpId : null, note ? noteId : null, error ? errorId : null].filter(Boolean).join(" ") || undefined;
  const control = children({ inputId, describedBy, invalid: Boolean(error) });
  return (
    <div className={`flex min-w-0 flex-col gap-1.5 ${className}`}>
      <Label.Root htmlFor={inputId} className={fieldLabelClass}>
        {label}
        {required ? <RequiredMarker /> : null}
      </Label.Root>
      {actions ? (
        <div className="flex flex-wrap items-start gap-2">
          <div className="min-w-[min(100%,14rem)] flex-1">{control}</div>
          <div className="flex flex-wrap items-start gap-2">{actions}</div>
        </div>
      ) : (
        control
      )}
      {helpText || note || error ? (
        <div className="flex flex-col gap-1">
          {helpText ? (
            <p id={helpId} className={helpTextClass}>
              {helpText}
            </p>
          ) : null}
          {note ? (
            <p id={noteId} className={helpTextClass}>
              {note}
            </p>
          ) : null}
          {error ? (
            <p id={errorId} role="alert" className={errorTextClass}>
              {error}
            </p>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

/**
 * Shared text styles of the field standard (docs/ui/FORM_CONTROL_LAYOUT_STANDARD.md §3). Use these for any label, legend,
 * help text or validation message a feature cannot get from FieldShell (for example a fieldset legend), never ad-hoc sizes.
 */
export const fieldLabelClass = "text-label font-medium text-body";
export const legendClass = "text-label font-medium text-body";
export const helpTextClass = "text-help text-muted";
export const errorTextClass = "text-help font-medium text-danger";

/** The required-field marker: a decorative asterisk plus "(required)" for screen readers. */
export function RequiredMarker() {
  return (
    <>
      <span className="ml-1 text-danger" aria-hidden="true">
        *
      </span>
      <span className="sr-only"> (required)</span>
    </>
  );
}

/** Text inputs, text areas and select triggers: one height, border, radius, type size and state treatment everywhere. */
const controlSurfaceClass = "w-full rounded-control border border-input bg-surface px-3 py-2 text-control text-body placeholder:text-muted read-only:bg-page disabled:cursor-not-allowed disabled:bg-page disabled:text-muted aria-[invalid=true]:border-danger";
export const inputClass = `min-h-control ${controlSurfaceClass}`;
const textAreaClass = `min-h-24 ${controlSurfaceClass}`;

export interface TextInputProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "id"> {
  label: string;
  helpText?: string | undefined;
  note?: string | undefined;
  error?: string | undefined;
  /** Buttons shown in the same row as the input (see FieldShell). */
  actions?: ReactNode;
}

export const TextInput = forwardRef<HTMLInputElement, TextInputProps>(function TextInput({ label, helpText, note, error, required, actions, className = "", ...rest }, ref) {
  return (
    <FieldShell label={label} helpText={helpText} note={note} error={error} required={required} actions={actions} className={className}>
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
      {({ inputId, describedBy, invalid }) => <textarea ref={ref} id={inputId} rows={rows} aria-describedby={describedBy} aria-invalid={invalid || undefined} aria-required={required || undefined} className={textAreaClass} {...rest} />}
    </FieldShell>
  );
});
