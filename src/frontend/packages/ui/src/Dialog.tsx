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
}

/** Modal dialog with focus trap, Escape/overlay dismissal and a labelled title/description (Radix). */
export function Dialog({ open, onOpenChange, title, description, children, footer, locked }: DialogProps) {
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
          className="fixed left-1/2 top-1/2 z-50 flex max-h-[90vh] w-[min(95vw,40rem)] -translate-x-1/2 -translate-y-1/2 flex-col gap-4 overflow-auto rounded-lg border border-input bg-surface p-5 text-body shadow-xl"
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
          <RadixDialog.Title className="text-xl font-semibold">{title}</RadixDialog.Title>
          {description ? <RadixDialog.Description className="text-sm text-muted">{description}</RadixDialog.Description> : <RadixDialog.Description className="sr-only">{title}</RadixDialog.Description>}
          <div className="flex flex-col gap-3">{children}</div>
          {footer ? <div className="flex flex-wrap justify-end gap-2 border-t border-line pt-3">{footer}</div> : null}
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}
