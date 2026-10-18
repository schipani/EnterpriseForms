import { API_V1_PREFIX } from "@enterpriseforms/api-client";

/**
 * Phase 0 landing content. States plainly that no protected workflow is enabled: Stage A security proof
 * (ADR-PB-002/003) gates every business feature. This page performs no network I/O.
 */
export function EnvironmentStatusPage() {
  return (
    <section aria-labelledby="status-heading" className="rounded-lg border border-line bg-surface p-6">
      <h1 id="status-heading" className="text-2xl font-semibold">
        Phase 0 scaffold
      </h1>
      <p className="mt-2 text-muted">
        The application shell, design tokens and generated API types are in place. No business features are enabled:
        protected workflows remain gated until the Stage A security foundation passes its adversarial test gate.
      </p>
      <dl className="mt-4 grid grid-cols-1 gap-2 sm:grid-cols-2">
        <dt className="font-medium">Business API prefix</dt>
        <dd>
          <code>{API_V1_PREFIX}</code>
        </dd>
        <dt className="font-medium">Request transport</dt>
        <dd>App-shell request coordinator (not yet implemented; no feature may call fetch directly)</dd>
      </dl>
      <button type="button" className="mt-6 rounded bg-primary px-4 py-2 font-medium text-primary-foreground">
        Default primary action
      </button>
    </section>
  );
}
