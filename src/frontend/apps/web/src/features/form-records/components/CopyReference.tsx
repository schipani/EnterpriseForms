import { Button } from "@enterpriseforms/ui";
import { useEffect, useRef, useState } from "react";

type CopyState = "idle" | "copied" | "failed";

/** The record's short reference with a Copy reference action (Phase C6); paste it into Find a record on My Records. */
export function CopyReference({ reference }: { reference: string }) {
  const [state, setState] = useState<CopyState>("idle");
  const textRef = useRef<HTMLSpanElement>(null);
  useEffect(() => {
    if (state !== "copied") {
      return;
    }
    const timer = window.setTimeout(() => setState("idle"), 4000);
    return () => window.clearTimeout(timer);
  }, [state]);

  const copy = async () => {
    try {
      if (!navigator.clipboard?.writeText) {
        throw new Error("clipboard unavailable");
      }
      await navigator.clipboard.writeText(reference);
      setState("copied");
    } catch {
      // Clipboard access can be refused (permissions, insecure context): select the text so the user can copy it.
      const node = textRef.current;
      const selection = window.getSelection();
      if (node && selection) {
        const range = document.createRange();
        range.selectNodeContents(node);
        selection.removeAllRanges();
        selection.addRange(range);
      }
      setState("failed");
    }
  };

  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <span ref={textRef} className="font-mono">
        {reference}
      </span>
      <Button size="sm" variant="secondary" onClick={() => void copy()} aria-label={`Copy reference ${reference}`}>
        Copy reference
      </Button>
      <span role="status" className="text-sm text-muted">
        {state === "copied" ? "Reference copied" : state === "failed" ? "Couldn't copy automatically. The reference is selected: copy it with Ctrl+C (Cmd+C on a Mac)" : ""}
      </span>
    </span>
  );
}
