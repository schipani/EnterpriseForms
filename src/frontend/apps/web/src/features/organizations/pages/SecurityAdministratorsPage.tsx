import { OrganizationLayout } from "../components/OrganizationLayout";
import { SecurityAdministratorsCard } from "../components/SecurityAdministratorsCard";

/** Security Administrators (C-70) on their own page in the administration shell (Phase C8 M1); the card and its Authority commands are unchanged. */
export function SecurityAdministratorsPage() {
  return (
    <OrganizationLayout title="Security Administrators" lead="Security Administrators hold the Authorization Authority relation system_administrator. Only an existing Security Administrator can appoint or remove one; organization owners see the roster read-only.">
      <SecurityAdministratorsCard />
    </OrganizationLayout>
  );
}
