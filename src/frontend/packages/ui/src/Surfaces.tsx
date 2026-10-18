import type { HTMLAttributes, ReactNode } from "react";

export function Card({ title, actions, children, className = "", as: Tag = "section", ...rest }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string; as?: "section" | "article" | "div" } & Omit<HTMLAttributes<HTMLElement>, "title">) {
  return (
    <Tag className={`flex min-w-0 flex-col gap-3 rounded-lg border border-line bg-surface p-4 ${className}`} {...rest}>
      {title || actions ? (
        <header className="flex flex-wrap items-start justify-between gap-2">
          {title ? <h2 className="text-lg font-semibold">{title}</h2> : <span />}
          {actions ? <div className="flex flex-wrap gap-2">{actions}</div> : null}
        </header>
      ) : null}
      {children}
    </Tag>
  );
}

export function PageHeader({ title, lead, actions }: { title: string; lead?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="mb-4 flex flex-wrap items-end justify-between gap-3">
      <div className="min-w-0">
        <h1 className="text-2xl font-semibold leading-tight">{title}</h1>
        {lead ? <p className="mt-1 max-w-prose text-muted">{lead}</p> : null}
      </div>
      {actions ? <div className="flex flex-wrap gap-2">{actions}</div> : null}
    </div>
  );
}

export type StatusTone = "info" | "success" | "warning" | "danger";
const tones: Record<StatusTone, string> = {
  info: "border-[#1d4ed8] bg-[#eff6ff] text-[#1e3a8a]",
  success: "border-[#166534] bg-[#f0fdf4] text-[#14532d]",
  warning: "border-[#92400e] bg-[#fffbeb] text-[#78350f]",
  danger: "border-[#991b1b] bg-[#fef2f2] text-[#7f1d1d]",
};
const toneLabel: Record<StatusTone, string> = { info: "Information", success: "Success", warning: "Warning", danger: "Error" };

/** Status messages are announced and never rely on color alone: a text prefix identifies the tone. */
export function StatusMessage({ tone = "info", title, children, actions }: { tone?: StatusTone; title?: string; children?: ReactNode; actions?: ReactNode }) {
  return (
    <div role={tone === "danger" ? "alert" : "status"} className={`flex flex-col gap-2 rounded border-l-4 px-4 py-3 ${tones[tone]}`}>
      <p className="font-semibold">
        {title ?? toneLabel[tone]}
        {title ? <span className="sr-only"> ({toneLabel[tone]})</span> : null}
      </p>
      {children ? <div className="text-sm">{children}</div> : null}
      {actions ? <div className="flex flex-wrap gap-2">{actions}</div> : null}
    </div>
  );
}

export function Badge({ children, tone = "info" }: { children: ReactNode; tone?: StatusTone | "neutral" }) {
  const cls = tone === "neutral" ? "border-input bg-page text-body" : tones[tone];
  return <span className={`inline-flex items-center rounded border px-2 py-0.5 text-xs font-medium ${cls}`}>{children}</span>;
}

export function EmptyState({ title, children, actions }: { title: string; children?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="flex flex-col items-start gap-2 rounded border border-dashed border-input bg-page p-6">
      <p className="font-semibold">{title}</p>
      {children ? <p className="text-sm text-muted">{children}</p> : null}
      {actions ? <div className="mt-2 flex flex-wrap gap-2">{actions}</div> : null}
    </div>
  );
}

export function LoadingBlock({ label = "Loading" }: { label?: string }) {
  return (
    <div role="status" className="flex items-center gap-2 py-4 text-muted">
      <span aria-hidden="true" className="inline-block h-5 w-5 animate-spin rounded-full border-2 border-current border-r-transparent" />
      <span>{label}&hellip;</span>
    </div>
  );
}

export function ProgressBar({ value, max = 100, label }: { value: number; max?: number; label: string }) {
  const percent = max === 0 ? 0 : Math.min(100, Math.round((value / max) * 100));
  return (
    <div className="flex flex-col gap-1">
      <div className="flex justify-between text-sm">
        <span>{label}</span>
        <span aria-hidden="true">{percent}%</span>
      </div>
      <div role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent} className="h-2 w-full overflow-hidden rounded bg-page ring-1 ring-input">
        <div className="h-full bg-primary transition-[width]" style={{ width: `${percent}%` }} />
      </div>
    </div>
  );
}

export function DescriptionList({ items }: { items: Array<{ term: string; detail: ReactNode }> }) {
  return (
    <dl className="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
      {items.map((item) => (
        <div key={item.term} className="contents">
          <dt className="text-sm font-medium text-muted">{item.term}</dt>
          <dd className="min-w-0 break-words">{item.detail}</dd>
        </div>
      ))}
    </dl>
  );
}
