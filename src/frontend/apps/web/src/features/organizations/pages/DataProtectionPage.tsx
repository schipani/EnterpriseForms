import { OrganizationLayout } from "../components/OrganizationLayout";
import { SecurityPolicyCard } from "../components/SecurityPolicyCard";

/** PG-046 data-protection floor on its own page (Phase C8 M1; the card and its behaviour are unchanged: owners edit, Security Administrators read). */
export function DataProtectionPage() {
  return (
    <OrganizationLayout title="Data protection" lead="The organization's minimum field classification and whether plaintext Public/Internal fields are permitted (ADR-PB-004). Published to the Authorization Authority, which enforces it; never decrypts or re-protects stored values.">
      <SecurityPolicyCard />
    </OrganizationLayout>
  );
}
