import { OrganizationLayout } from "../components/OrganizationLayout";
import { OrganizationTextSizeCard } from "../components/OrganizationTextSizeCard";
import { RegionalSettingsCard } from "../components/RegionalSettingsCard";

/** PG-043 organization regional defaults (Phase C8 M3): owners edit, other administrators read. */
export function RegionalSettingsPage() {
  return (
    <OrganizationLayout title="Regional settings" lead="The country, time zone, date format and number format that members see unless they choose their own.">
      <RegionalSettingsCard area="regional" />
    </OrganizationLayout>
  );
}

/** The organization's default language (Phase C8 M3): owners edit, other administrators read. */
export function LanguageSettingsPage() {
  return (
    <OrganizationLayout title="Language" lead="The language members use unless they choose their own.">
      <RegionalSettingsCard area="language" />
    </OrganizationLayout>
  );
}

/** The organization's default text size (moved here from Branding): owners edit, other administrators read. */
export function TextSizeSettingsPage() {
  return (
    <OrganizationLayout title="Text size" lead="The base text size members see unless they choose their own.">
      <OrganizationTextSizeCard />
    </OrganizationLayout>
  );
}
