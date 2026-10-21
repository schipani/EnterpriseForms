import * as RadixDialog from "@radix-ui/react-dialog";
import { useEffect, useRef, type ReactNode } from "react";

export interface DialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string | undefined;
  children: ReactNode;
  footer?: ReactNode;
  /** Prevents dismissal while a mutation is in flight. */
  locked?: boolean | undefined;
  /**
   * `"large"` is for content too big for the default card (e.g. a full field-configuration workspace with several
   * tabs): ~90vw up to 1200px, ~85vh tall, full-screen below `sm`. Its title and footer stay pinned while only the
   * middle region scrolls, so there is exactly one scroll surface inside the dialog. Default unchanged.
   */
  size?: "default" | "large" | undefined;
}

/** Modal dialog with focus trap, Escape/overlay dismissal and a labelled title/description (Radix). */
export function Dialog({ open, onOpenChange, title, description, children, footer, locked, size = "default" }: DialogProps) {
  const large = size === "large";
  // Focus returns to the control that opened the dialog (WCAG 2.4.3). Radix only knows a Trigger; our dialogs are
  // opened from state, so remember the opener ourselves and restore it even when the dialog is unmounted on close.
  const opener = useRef<HTMLElement | null>(null);
  useEffect(() => {
    if (open) {
      opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    }
  }, [open]);
  useEffect(() => () => {
    const target = opener.current;
    if (target && target.isConnected && document.activeElement === document.body) {
      target.focus();
    }
  }, []);
  return (
    <RadixDialog.Root open={open} onOpenChange={(next) => (locked ? undefined : onOpenChange(next))}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className="fixed inset-0 z-40 bg-black/50" />
        <RadixDialog.Content
          className={
            large
              ? "fixed inset-0 z-50 flex flex-col overflow-hidden bg-surface text-body shadow-xl sm:inset-auto sm:left-1/2 sm:top-1/2 sm:h-[85vh] sm:max-h-[85vh] sm:w-[90vw] sm:max-w-[1200px] sm:-translate-x-1/2 sm:-translate-y-1/2 sm:rounded-lg sm:border sm:border-input"
              : "fixed left-1/2 top-1/2 z-50 flex max-h-[90vh] w-[min(95vw,40rem)] -translate-x-1/2 -translate-y-1/2 flex-col gap-4 overflow-auto rounded-lg border border-input bg-surface p-5 text-body shadow-xl"
          }
          onEscapeKeyDown={(event) => (locked ? event.preventDefault() : undefined)}
          onInteractOutside={(event) => (locked ? event.preventDefault() : undefined)}
          onCloseAutoFocus={(event) => {
            const target = opener.current;
            if (target && target.isConnected) {
              event.preventDefault();
              target.focus();
            }
          }}
        >
          {large ? (
            <>
              <div className="flex items-start gap-3 border-b border-line px-5 py-4">
                <div className="min-w-0 flex-1">
                  <RadixDialog.Title className="text-xl font-semibold">{title}</RadixDialog.Title>
                  {description ? <RadixDialog.Description className="mt-1 text-sm text-muted">{description}</RadixDialog.Description> : <RadixDialog.Description className="sr-only">{title}</RadixDialog.Description>}
                </div>
                <RadixDialog.Close asChild>
                  <button type="button" aria-label="Close" title="Close" disabled={locked} className="flex h-9 w-9 shrink-0 items-center justify-center rounded text-body hover:bg-page disabled:opacity-50">
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
                      <path d="M18 6L6 18M6 6l12 12" />
                    </svg>
                  </button>
                </RadixDialog.Close>
              </div>
              <div className="min-h-0 flex-1 overflow-y-auto px-5 py-4">{children}</div>
              {footer ? <div className="flex flex-wrap justify-end gap-2 border-t border-line px-5 py-4">{footer}</div> : null}
            </>
          ) : (
            <>
              <RadixDialog.Title className="text-xl font-semibold">{title}</RadixDialog.Title>
              {description ? <RadixDialog.Description className="text-sm text-muted">{description}</RadixDialog.Description> : <RadixDialog.Description className="sr-only">{title}</RadixDialog.Description>}
              <div className="flex flex-col gap-3">{children}</div>
              {footer ? <div className="flex flex-wrap justify-end gap-2 border-t border-line pt-3">{footer}</div> : null}
            </>
          )}
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}
