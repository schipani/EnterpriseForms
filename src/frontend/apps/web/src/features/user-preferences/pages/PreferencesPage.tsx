import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Button, Card, LoadingBlock, Select, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { PreferencesLayout } from "../components/PreferencesLayout";
import { RegionalPreferencesCard } from "../components/RegionalPreferencesCard";

/** Accessible text size: organization default or an explicit percentage, applied immediately through the font-scale token (B-08). */
function TextSizeCard() {
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const preferences = useQuery({ queryKey: ["preferences"], queryFn: () => api.preferences.get().then((r) => r.result) });
  const [mode, setMode] = useState<string | null>(null);
  const [scale, setScale] = useState<string | null>(null);
  const effectiveMode = mode ?? preferences.data?.textScalePreferenceMode ?? "OrganizationDefault";
  const effectiveScale = scale ?? String(preferences.data?.explicitTextScalePercent ?? 125);
  const save = useMutation({
    mutationFn: () => api.preferences.update({ textScalePreferenceMode: effectiveMode, explicitTextScalePercent: effectiveMode === "Explicit" ? Number(effectiveScale) : null }).then((r) => r.result),
    onSuccess: async (result) => {
      session.applyPreferences(Number(result.effectiveTextScalePercent));
      await queryClient.invalidateQueries({ queryKey: ["preferences"] });
    },
  });
  if (preferences.isPending) {
    return <LoadingBlock label="Loading preferences" />;
  }
  if (preferences.isError) {
    return <ErrorPanel error={preferences.error} onRetry={() => void preferences.refetch()} />;
  }
  return (
    <Card title={<span id="text-size-heading">Text size</span>} aria-labelledby="text-size-heading">
      <p className="text-sm text-muted">Choose a larger base text size for the whole application. Layouts reflow; nothing is clipped at 200% or 400% zoom.</p>
      <Select label="Text size preference" value={effectiveMode} options={[{ value: "OrganizationDefault", label: `Use organization default (${preferences.data.textScalePreferenceMode === "OrganizationDefault" ? `${preferences.data.effectiveTextScalePercent}%` : "organization setting"})` }, { value: "Explicit", label: "Choose my own" }]} onValueChange={setMode} />
      {effectiveMode === "Explicit" ? <Select label="My text size" value={effectiveScale} options={[{ value: "100", label: "100%" }, { value: "112.5", label: "112.5%" }, { value: "125", label: "125%" }, { value: "150", label: "150%" }, { value: "175", label: "175%" }, { value: "200", label: "200%" }]} onValueChange={setScale} /> : null}
      <p className="text-sm text-muted">Currently effective: {String(preferences.data.effectiveTextScalePercent)}%</p>
      {save.isError ? <ErrorPanel error={save.error} /> : null}
      {save.isSuccess ? <StatusMessage tone="success" title="Preference saved and applied" /> : null}
      <div>
        <Button busy={save.isPending} onClick={() => save.mutate()}>
          Save preference
        </Button>
      </div>
    </Card>
  );
}

/** PG-007 personal preferences, area 1: regional settings (time zone, date format, number format). */
export function PreferencesPage() {
  return (
    <PreferencesLayout>
      <RegionalPreferencesCard area="regional" />
    </PreferencesLayout>
  );
}

/** PG-007 personal preferences, area 2: language. */
export function LanguagePreferencePage() {
  return (
    <PreferencesLayout>
      <RegionalPreferencesCard area="language" />
    </PreferencesLayout>
  );
}

/** PG-007 personal preferences, area 3: text size. */
export function TextSizePreferencePage() {
  return (
    <PreferencesLayout>
      <TextSizeCard />
    </PreferencesLayout>
  );
}
