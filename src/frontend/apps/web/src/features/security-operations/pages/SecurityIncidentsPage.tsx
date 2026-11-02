import { useSession } from "@enterpriseforms/app-runtime";
import { Card, PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { IncidentList } from "../components/IncidentList";

/** PG-056 Security incidents. Navigation is a hint; the server refuses every incident request without administer_security. */
export function SecurityIncidentsPage() {
  const session = useSession();
  if (!session.hasCapability("security.administer")) {
    return (
      <>
        <PageHeader title="Security incidents" />
        <StatusMessage tone="info" title="No permission">
          Security incidents are available to Security Administrators. Access is decided by the independent authorization authority.
        </StatusMessage>
      </>
    );
  }
  return (
    <>
      <PageHeader title="Security incidents" lead="Verification problems the application observed while serving requests: integrity mismatches, quarantined records and verification outages." />
      <div className="flex flex-col gap-4">
        <StatusMessage tone="info" title="What this list can and cannot tell you">
          Incidents are recorded when a request meets a verification problem; they are not a scan of all data, so an empty list does not prove that every record was verified. A record that failed verification stays quarantined by the Authorization Authority and is never shown here; resolving an incident does not restore access to it.
        </StatusMessage>
        <Card title="Incidents">
          <IncidentList caption="Security incidents list" />
        </Card>
      </div>
    </>
  );
}
