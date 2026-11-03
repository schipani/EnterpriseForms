import { OrganizationLayout } from "../components/OrganizationLayout";
import { RegionalSettingsCard } from "../components/RegionalSettingsCard";

/** PG-043 organization regional defaults (Phase C8 M3): owners edit, other administrators read. */
export function RegionalSettingsPage() {
  return (
    <OrganizationLayout title="Regional settings" lead="The country, time zone, language, date format and number format that members see unless they choose their own.">
      <RegionalSettingsCard />
    </OrganizationLayout>
  );
}
