import type { OrganizationOverviewResponse, OrganizationSecurityPolicyResponse, SecurityAdministratorsResponse } from "@enterpriseforms/api-client";
import { ActionIcons, Badge, Button, Card, DescriptionList, StatusMessage } from "@enterpriseforms/ui";
import { Link } from "@tanstack/react-router";
import type { ReactNode } from "react";

/** One overview card: title, an independent refresh action, an "open" link and either its content or its unavailable notice. */
export function OverviewCard({ id, title, to, openLabel, refreshing, onRefresh, unavailableReason, children }: { id: string; title: string; to?: string | undefined; openLabel?: string | undefined; refreshing: boolean; onRefresh: () => void; unavailableReason?: string | null | undefined; children: ReactNode }) {
  return (
    <Card
      title={<span id={`overview-${id}-heading`}>{title}</span>}
      aria-labelledby={`overview-${id}-heading`}
      actions={
        <Button size="sm" variant="ghost" icon={ActionIcons.refresh} busy={refreshing} aria-label={`Refresh ${title}`} onClick={onRefresh}>
          Refresh
        </Button>
      }
    >
      {unavailableReason ? (
        <StatusMessage tone="warning" title="Unavailable">
          {unavailableReason}. Nothing is guessed; refresh to try again.
        </StatusMessage>
      ) : (
        children
      )}
      {to ? (
        <p className="m-0">
          <Link to={to} className="underline underline-offset-4">
            {openLabel ?? `Open ${title}`}
          </Link>
        </p>
      ) : null}
    </Card>
  );
}

/** Organization, members, teams and authentication cards from the overview aggregate (one refresh for the aggregate). */
export function OverviewAggregateCards({ overview, refreshing, onRefresh }: { overview: OrganizationOverviewResponse; refreshing: boolean; onRefresh: () => void }) {
  const o = overview.organization;
  const m = overview.members;
  const t = overview.teams;
  const a = overview.authentication;
  return (
    <>
      <OverviewCard id="organization" title="Organization" to={overview.canManageConfiguration ? "/app/organization/branding" : undefined} openLabel="Open branding" refreshing={refreshing} onRefresh={onRefresh} unavailableReason={o.status.available ? null : o.status.unavailableReason}>
        <DescriptionList
          items={[
            { term: "Display name", detail: o.organizationDisplayName ?? "" },
            { term: "Logo", detail: o.hasLogo ? "Set (shown top-left)" : "Not set; the organization initials are shown" },
            { term: "Branding revision", detail: o.brandingRevision === null || o.brandingRevision === undefined ? "—" : String(o.brandingRevision) },
          ]}
        />
      </OverviewCard>
      <OverviewCard id="members" title="Members and roles" to="/app/organization/users" openLabel="Open People" refreshing={refreshing} onRefresh={onRefresh} unavailableReason={m.status.available ? null : m.status.unavailableReason}>
        <DescriptionList
          items={[
            { term: "People seen in this organization", detail: m.projectedMemberCount === null || m.projectedMemberCount === undefined ? "Unavailable" : String(m.projectedMemberCount) },
            { term: "Security Administrators", detail: m.securityAdministratorCount === null || m.securityAdministratorCount === undefined ? "Unavailable" : String(m.securityAdministratorCount) },
          ]}
        />
        <p className="m-0 text-sm text-muted">Member count source: {m.memberCountSource}. Security Administrators come from the Authorization Authority. Owner and designer roles are not shown yet (M2).</p>
      </OverviewCard>
      <OverviewCard id="teams" title="Teams" to="/app/organization/teams" openLabel="Open Teams" refreshing={refreshing} onRefresh={onRefresh} unavailableReason={t.status.available ? null : t.status.unavailableReason}>
        <DescriptionList items={[{ term: "Active teams", detail: t.teamCount === null || t.teamCount === undefined ? "Unavailable" : String(t.teamCount) }]} />
      </OverviewCard>
      <OverviewCard id="authentication" title="Authentication" refreshing={refreshing} onRefresh={onRefresh}>
        <div className="flex flex-wrap gap-2">
          <Badge tone="neutral">{a.authenticationRequirementName}</Badge>
          <Badge tone="warning">{a.authenticationRolloutStatus === "NotEnforced" ? "Not enforced — awaits PB-08" : a.authenticationRolloutStatus}</Badge>
        </div>
        <p className="m-0 text-sm text-muted">{a.statusExplanation}</p>
      </OverviewCard>
    </>
  );
}

/** Data-protection floor summary (its own query, its own refresh). */
export function DataProtectionOverviewCard({ policy, refreshing, onRefresh, unavailableReason }: { policy: OrganizationSecurityPolicyResponse | undefined; refreshing: boolean; onRefresh: () => void; unavailableReason?: string | null | undefined }) {
  return (
    <OverviewCard id="data-protection" title="Data protection" to="/app/organization/data-protection" openLabel="Open Data protection" refreshing={refreshing} onRefresh={onRefresh} unavailableReason={unavailableReason}>
      {policy ? (
        <>
          <div className="flex flex-wrap gap-2">
            <Badge tone={policy.isUnreviewedDefault ? "warning" : "success"}>{policy.isUnreviewedDefault ? "Development default — not yet reviewed" : "Reviewed"}</Badge>
            <Badge tone="neutral">Revision {String(policy.securityPolicyRevision)}</Badge>
          </div>
          <DescriptionList
            items={[
              { term: "Minimum field classification", detail: policy.minimumFieldClassification },
              { term: "Plaintext Public/Internal fields", detail: policy.plaintextInternalFieldsPermitted ? "Permitted" : "Not permitted" },
            ]}
          />
        </>
      ) : null}
    </OverviewCard>
  );
}

/** Security Administrators count (the roster query, its own refresh). */
export function SecurityAdministratorsOverviewCard({ roster, refreshing, onRefresh, unavailableReason }: { roster: SecurityAdministratorsResponse | undefined; refreshing: boolean; onRefresh: () => void; unavailableReason?: string | null | undefined }) {
  return (
    <OverviewCard id="security-administrators" title="Security Administrators" to="/app/organization/security-administrators" openLabel="Open Security Administrators" refreshing={refreshing} onRefresh={onRefresh} unavailableReason={unavailableReason}>
      {roster ? (
        <>
          <DescriptionList items={[{ term: "Appointed", detail: String(roster.administrators.length) }]} />
          {roster.administrators.length === 0 ? <StatusMessage tone="warning" title="No Security Administrator">The first one is appointed through the independently authorized operator process.</StatusMessage> : null}
          <p className="m-0 text-sm text-muted">{roster.canManage ? "You can appoint and remove Security Administrators." : "Only Security Administrators can appoint or remove Security Administrators."}</p>
        </>
      ) : null}
    </OverviewCard>
  );
}
