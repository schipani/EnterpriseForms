import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { validateThemePairs, defaultTheme, type OpaqueHex } from "@enterpriseforms/design-tokens";
import { Button, Card, LoadingBlock, Select, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { ErrorPanel } from "../../support";
import { OrganizationLayout } from "../components/OrganizationLayout";

type Draft = { organizationDisplayName: string; logoAlternativeText: string; primary: string; primaryText: string; secondary: string; secondaryText: string; secondaryBorder: string; textScale: string; colorMode: string };

/** PG-04x organization branding: logo, button colours (validated against the WCAG floor on both client and server), default text size. */
export function BrandingPage() {
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const branding = useQuery({ queryKey: ["organization", "branding"], queryFn: () => api.organization.branding().then((r) => r.result) });
  const [draft, setDraft] = useState<Draft | null>(null);
  const logoInput = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (branding.data && !draft) {
      setDraft({ organizationDisplayName: branding.data.organizationDisplayName, logoAlternativeText: branding.data.logoAlternativeText ?? "", primary: branding.data.primaryButtonBackgroundColor, primaryText: branding.data.primaryButtonForegroundColor, secondary: branding.data.secondaryButtonBackgroundColor, secondaryText: branding.data.secondaryButtonForegroundColor, secondaryBorder: branding.data.secondaryButtonBorderColor, textScale: String(branding.data.organizationDefaultTextScalePercent), colorMode: branding.data.organizationColorMode });
    }
  }, [branding.data, draft]);

  const save = useMutation({
    mutationFn: (current: Draft) =>
      api.organization.updateBranding({ expectedBrandingRevision: branding.data!.brandingRevision, organizationDisplayName: current.organizationDisplayName, logoAlternativeText: current.logoAlternativeText || null, primaryButtonBackgroundColor: current.primary, primaryButtonForegroundColor: current.primaryText, secondaryButtonBackgroundColor: current.secondary, secondaryButtonForegroundColor: current.secondaryText, secondaryButtonBorderColor: current.secondaryBorder, organizationDefaultTextScalePercent: Number(current.textScale), organizationColorMode: current.colorMode }).then((r) => r.result),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["organization", "branding"] });
      setDraft(null);
      await session.refresh();
    },
  });
  const uploadLogo = useMutation({
    mutationFn: (file: File) => file.arrayBuffer().then((bytes) => api.organization.uploadLogo(bytes)).then((r) => r.result),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["organization", "branding"] });
      await session.refresh();
    },
  });

  if (!session.hasCapability("organization.manage")) {
    // Phase C8 M1: a Security Administrator who is not an owner has the overview, people, teams, data protection and
    // Security Administrators pages; branding itself is owner-only.
    return (
      <OrganizationLayout title="Organization branding">
        <StatusMessage tone="info" title="Organization owners only">
          Only an organization owner can change branding.
        </StatusMessage>
      </OrganizationLayout>
    );
  }
  const current = branding.data;
  if (!current || !draft) {
    return <OrganizationLayout title="Organization branding">{branding.isError ? <ErrorPanel error={branding.error} onRetry={() => void branding.refetch()} /> : <LoadingBlock label="Loading branding" />}</OrganizationLayout>;
  }
  const hex = (value: string): OpaqueHex => (/^#[0-9a-fA-F]{6}$/.test(value) ? (value.toLowerCase() as OpaqueHex) : "#000000");
  const failures = validateThemePairs({ ...defaultTheme, primaryBackground: hex(draft.primary), primaryForeground: hex(draft.primaryText), secondaryBackground: hex(draft.secondary), secondaryForeground: hex(draft.secondaryText), secondaryBorder: hex(draft.secondaryBorder) });
  const colorField = (label: string, key: keyof Draft) => <TextInput label={label} value={draft[key]} pattern="#[0-9a-fA-F]{6}" helpText="Opaque #RRGGBB" onChange={(event) => setDraft({ ...draft, [key]: event.target.value })} />;

  return (
    <OrganizationLayout title="Organization branding" lead="Branding can only override validated colour pairs; anything below the accessibility floor is refused by the server and never applied.">
      <div className="grid grid-cols-1 gap-6 xl:grid-cols-2">
        <Card title="Identity">
          <TextInput label="Organization display name" value={draft.organizationDisplayName} maxLength={200} onChange={(event) => setDraft({ ...draft, organizationDisplayName: event.target.value })} />
          <TextInput label="Logo alternative text" value={draft.logoAlternativeText} maxLength={200} onChange={(event) => setDraft({ ...draft, logoAlternativeText: event.target.value })} />
          <div className="flex flex-wrap items-center gap-3">
            <Button variant="secondary" busy={uploadLogo.isPending} onClick={() => logoInput.current?.click()}>
              {current.hasLogo ? "Replace logo (PNG)" : "Upload logo (PNG)"}
            </Button>
            <input ref={logoInput} type="file" accept="image/png" className="sr-only" aria-label="Choose a PNG logo" onChange={(event) => { const file = event.target.files?.[0]; event.target.value = ""; if (file) { uploadLogo.mutate(file); } }} />
            <span className="text-sm text-muted">{current.hasLogo ? "A logo is set (shown top-left)." : "No logo yet; the organization initials are shown instead."}</span>
          </div>
          {uploadLogo.isError ? <ErrorPanel error={uploadLogo.error} /> : null}
          <Select label="Organization default text size" value={draft.textScale} options={[{ value: "100", label: "100% (default)" }, { value: "112.5", label: "112.5%" }, { value: "125", label: "125%" }, { value: "150", label: "150%" }]} onValueChange={(value) => setDraft({ ...draft, textScale: value })} />
          <Select label="Colour mode" value={draft.colorMode} options={[{ value: "Light", label: "Light (white/neutral)" }]} onValueChange={(value) => setDraft({ ...draft, colorMode: value })} />
        </Card>
        <Card title="Button colours">
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            {colorField("Primary button background", "primary")}
            {colorField("Primary button text", "primaryText")}
            {colorField("Secondary button background", "secondary")}
            {colorField("Secondary button text", "secondaryText")}
            {colorField("Secondary button border", "secondaryBorder")}
          </div>
          {failures.length > 0 ? (
            <StatusMessage tone="warning" title="These colours fail the contrast floor">
              <ul className="list-disc pl-5">
                {failures.map((failure) => (
                  <li key={failure.pair}>
                    {failure.pair}: {failure.ratio.toFixed(2)}:1 (needs {failure.required}:1)
                  </li>
                ))}
              </ul>
            </StatusMessage>
          ) : (
            <StatusMessage tone="success" title="Contrast floor met" />
          )}
          <div className="flex flex-wrap gap-2" aria-label="Preview">
            <span className="inline-flex min-h-control items-center rounded-control border px-4 text-control font-medium" style={{ background: draft.primary, color: draft.primaryText, borderColor: draft.primary }}>
              Primary preview
            </span>
            <span className="inline-flex min-h-control items-center rounded-control border px-4 text-control font-medium" style={{ background: draft.secondary, color: draft.secondaryText, borderColor: draft.secondaryBorder }}>
              Secondary preview
            </span>
          </div>
        </Card>
      </div>
      {save.isError ? <div className="mt-4"><ErrorPanel error={save.error} /></div> : null}
      {save.isSuccess ? <div className="mt-4"><StatusMessage tone="success" title="Branding saved" /></div> : null}
      <div className="mt-4 flex flex-wrap gap-2">
        <Button busy={save.isPending} disabled={failures.length > 0} onClick={() => save.mutate(draft)}>
          Save branding
        </Button>
        <Button variant="secondary" onClick={() => setDraft(null)}>
          Discard changes
        </Button>
      </div>
    </OrganizationLayout>
  );
}
