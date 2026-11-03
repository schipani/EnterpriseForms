import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Button, Card, LoadingBlock, Select, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";

const SCALES = [
  { value: "100", label: "100% (default)" },
  { value: "112.5", label: "112.5%" },
  { value: "125", label: "125%" },
  { value: "150", label: "150%" },
];

/**
 * The organization's default text size: what members see unless they choose their own on their Preferences page. It is stored
 * with the organization's branding, so saving sends the branding exactly as last saved with only the text size changed
 * (same revision check, same Authority decision, same contrast validation on the server). Owners edit; others read.
 */
export function OrganizationTextSizeCard() {
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const canManage = session.hasCapability("organization.manage");
  const branding = useQuery({ queryKey: ["organization", "branding"], queryFn: () => api.organization.branding().then((r) => r.result) });
  const [choice, setChoice] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const save = useMutation({
    mutationFn: (scale: string) => {
      const b = branding.data!;
      return api.organization
        .updateBranding({
          expectedBrandingRevision: b.brandingRevision,
          organizationDisplayName: b.organizationDisplayName,
          logoAlternativeText: b.logoAlternativeText ?? null,
          primaryButtonBackgroundColor: b.primaryButtonBackgroundColor,
          primaryButtonForegroundColor: b.primaryButtonForegroundColor,
          secondaryButtonBackgroundColor: b.secondaryButtonBackgroundColor,
          secondaryButtonForegroundColor: b.secondaryButtonForegroundColor,
          secondaryButtonBorderColor: b.secondaryButtonBorderColor,
          organizationDefaultTextScalePercent: Number(scale),
          organizationColorMode: b.organizationColorMode,
        })
        .then((r) => r.result);
    },
    onSuccess: async () => {
      setSaved(true);
      setChoice(null);
      await queryClient.invalidateQueries({ queryKey: ["organization", "branding"] });
      await session.refresh();
    },
  });

  if (branding.isError) {
    return <ErrorPanel error={branding.error} onRetry={() => void branding.refetch()} />;
  }
  if (!branding.data) {
    return <LoadingBlock label="Loading text size" />;
  }
  const current = String(branding.data.organizationDefaultTextScalePercent);
  const value = choice ?? current;
  return (
    <Card title={<span id="organization-text-size-heading">Default text size</span>} aria-labelledby="organization-text-size-heading">
      <p className="text-sm text-muted">The base text size members see unless they choose their own on their Preferences page. Layouts reflow; nothing is clipped at 200% or 400% zoom.</p>
      {canManage ? null : (
        <StatusMessage tone="info" title="Read only">
          Only an organization owner can change the default text size.
        </StatusMessage>
      )}
      <form
        className="flex w-full min-w-0 max-w-xl flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault();
          if (canManage) {
            setSaved(false);
            save.mutate(value);
          }
        }}
      >
        <Select label="Organization default text size" value={value} disabled={!canManage} options={SCALES} onValueChange={(next) => { setSaved(false); setChoice(next); }} />
        {save.isError ? <ErrorPanel error={save.error} /> : null}
        {saved ? <StatusMessage tone="success" title="Default text size saved" /> : null}
        {canManage ? (
          <div>
            <Button type="submit" busy={save.isPending} disabled={value === current}>
              Save default text size
            </Button>
          </div>
        ) : null}
      </form>
    </Card>
  );
}
