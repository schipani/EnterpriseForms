import { Link } from "@tanstack/react-router";

/** The two audit views (C8 M4-2/M4-3): the business audit log and the Authority's security-command timeline. */
export function AuditViewTabs({ current }: { current: "events" | "commands" }) {
  const item = (view: "events" | "commands", to: "/app/security/audit" | "/app/security/audit/commands", label: string) => (
    <Link
      to={to}
      aria-current={current === view ? "page" : undefined}
      className={`rounded-md px-3 py-1.5 text-sm underline-offset-4 ${current === view ? "bg-surface-raised font-semibold" : "hover:underline"}`}
    >
      {label}
    </Link>
  );
  return (
    <nav aria-label="Audit views" className="flex flex-wrap gap-2">
      {item("events", "/app/security/audit", "Audit events")}
      {item("commands", "/app/security/audit/commands", "Security commands")}
    </nav>
  );
}
