import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from "react";
import type { ActionIconComponent } from "./actionIcons";

export type ButtonVariant = "primary" | "secondary" | "danger" | "ghost";
export type ButtonSize = "md" | "sm";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  busy?: boolean;
  /** Decorative icon shown left of the label (icon-left, text-right). The visible text stays the accessible name; never use an icon instead of a primary action's label. */
  icon?: ActionIconComponent;
}

const base = "inline-flex items-center justify-center gap-2 rounded-control font-medium leading-none whitespace-nowrap disabled:cursor-not-allowed disabled:opacity-60";
const variants: Record<ButtonVariant, string> = {
  // Default primary is #111111 with a white label (ADR-PB-014); organization branding only overrides validated tokens.
  primary: "bg-primary text-primary-foreground border border-primary hover:opacity-90",
  secondary: "bg-secondary text-secondary-foreground border border-secondary-border hover:bg-page",
  danger: "bg-danger text-white border border-danger hover:opacity-90",
  ghost: "bg-transparent text-body border border-transparent hover:bg-page underline-offset-4 hover:underline",
};
// md: every button next to or below a field (the control height). sm: table rows and dense toolbars only.
const sizes: Record<ButtonSize, string> = { md: "min-h-control px-4 py-2 text-control", sm: "min-h-control-compact px-3 py-1.5 text-control" };

/** The button look for an element that must not be a <button> (for example a download link): same variants and sizes. */
export function buttonClass(variant: ButtonVariant = "secondary", size: ButtonSize = "md"): string {
  return `${base} ${variants[variant]} ${sizes[size]}`;
}

/** Buttons are real button elements with an explicit type; a busy button stays in the tab order and announces its state. */
export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button({ variant = "primary", size = "md", busy = false, icon: Icon, className = "", children, disabled, type = "button", ...rest }, ref) {
  return (
    <button ref={ref} type={type} aria-busy={busy || undefined} disabled={disabled || busy} className={`${base} ${variants[variant]} ${sizes[size]} ${className}`} {...rest}>
      {busy ? <span aria-hidden="true" className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-current border-r-transparent" /> : null}
      {!busy && Icon ? <Icon aria-hidden="true" focusable="false" strokeWidth={2} className={size === "sm" ? "h-4 w-4 shrink-0" : "h-[1.125rem] w-[1.125rem] shrink-0"} /> : null}
      {children}
    </button>
  );
});

export interface IconButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, "aria-label" | "children"> {
  /** Accessible name and tooltip; required because the button shows only an icon. */
  label: string;
  /** The decorative icon (an ActionIcon or an inline svg); marked aria-hidden by the caller or the icon set. */
  children: ReactNode;
  size?: ButtonSize;
  tone?: "default" | "danger";
}

/** Icon-only buttons (close, collapse, move): square, the control radius, a visible label as tooltip and accessible name. */
export const IconButton = forwardRef<HTMLButtonElement, IconButtonProps>(function IconButton({ label, children, size = "md", tone = "default", className = "", type = "button", title, ...rest }, ref) {
  const box = size === "sm" ? "h-8 w-8" : "h-control w-control";
  return (
    <button ref={ref} type={type} aria-label={label} title={title ?? label} className={`inline-flex shrink-0 items-center justify-center rounded-control hover:bg-page disabled:cursor-not-allowed disabled:opacity-50 ${box} ${tone === "danger" ? "text-danger" : "text-body"} ${className}`} {...rest}>
      {children}
    </button>
  );
});
