export type PresenceIndicatorState = "Available" | "Away" | "Offline" | "Unknown";

const META: Record<PresenceIndicatorState, { label: string; dot: string }> = {
  // Colour is never the only signal: the shape differs (filled, half, ring, dashed ring) and the text label is always available.
  Available: { label: "Available", dot: "border-[#15803d] bg-[#15803d]" },
  Away: { label: "Away", dot: "border-[#a16207] bg-[linear-gradient(90deg,#eab308_50%,#ffffff_50%)]" },
  Offline: { label: "Offline", dot: "border-[#64748b] bg-surface" },
  Unknown: { label: "Presence unknown", dot: "border-dashed border-[#64748b] bg-surface" },
};

/**
 * Small presence dot with an accessible text label (shared by People, team rosters and user pickers). Green = Available,
 * yellow = Away, grey = Offline, dashed grey = Unknown (presence could not be determined; never shown as Offline).
 * `showLabel` false keeps the label for assistive technology and as the tooltip (dense lists); the shape still differs.
 * Informational only: it takes no part in any permission.
 */
export function PresenceIndicator({ state, showLabel = true, className = "" }: { state: PresenceIndicatorState; showLabel?: boolean; className?: string }) {
  const meta = META[state];
  return (
    <span className={`inline-flex items-center gap-1.5 whitespace-nowrap ${className}`} title={showLabel ? undefined : meta.label} data-presence={state}>
      <span aria-hidden="true" className={`inline-block h-2.5 w-2.5 shrink-0 rounded-full border-2 ${meta.dot}`} />
      <span className={showLabel ? "text-help text-body" : "sr-only"}>{meta.label}</span>
    </span>
  );
}
