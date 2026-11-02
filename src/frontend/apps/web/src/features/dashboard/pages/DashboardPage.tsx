import { useSession } from "@enterpriseforms/app-runtime";
import { PageHeader, Select } from "@enterpriseforms/ui";
import { useState } from "react";
import { SecurityMonitoringSection } from "../../security-operations";
import { DashboardAnalytics, PERIOD_OPTIONS, type DashboardPeriod } from "../components/DashboardAnalytics";
import { ActivitySection, FormsSection, RecordsSection, UploadsSection } from "../components/DashboardSections";

/**
 * PG-006 dashboard: summary KPIs and charts, then independently loading, searchable, server-paged sections (15 rows),
 * then security monitoring for Security Administrators only. Sections load on open and whenever their own page or
 * filters change; there is no per-section refresh button and no polling. What a session sees is decided by the server:
 * capabilities only choose which sections to ask for.
 */
export function DashboardPage() {
  const session = useSession();
  const [period, setPeriod] = useState<DashboardPeriod>(30);
  const today = new Intl.DateTimeFormat(undefined, { weekday: "long", year: "numeric", month: "long", day: "numeric" }).format(new Date());
  return (
    <>
      <PageHeader
        title={`Welcome, ${session.session?.displayName ?? ""}`}
        lead={`${session.session?.organizationDisplayName ?? ""}${session.session?.organizationDisplayName ? " · " : ""}${today}`}
        actions={
          <div className="w-44">
            <Select label="Period" value={String(period)} options={PERIOD_OPTIONS} helpText="Charts and period counts" onValueChange={(value) => setPeriod(Number(value) as DashboardPeriod)} />
          </div>
        }
      />
      <div className="flex flex-col gap-6">
        <DashboardAnalytics period={period} />
        <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
          {session.hasCapability("records.discover") ? <RecordsSection /> : null}
          <UploadsSection />
          <ActivitySection />
          <FormsSection />
        </div>
        {session.hasCapability("security.administer") ? <SecurityMonitoringSection /> : null}
      </div>
    </>
  );
}
