import { useApi } from "@enterpriseforms/app-runtime";
import type { UserPreferencesResponse } from "@enterpriseforms/api-client";
import { Button, Card, FormattingPreview, LoadingBlock, RadioGroup, Select, SearchableSelect, StatusMessage, readBrowserEnvironment, resolveFormatting, type RegionalPreferenceInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { ErrorPanel } from "../../support";

type Mode = "OrganizationDefault" | "BrowserDefault" | "Explicit";
type Draft = { timeZone: Mode; zone: string; date: Mode; dateKey: string; number: Mode; numberKey: string; language: Mode; languageCode: string };

const toDraft = (p: UserPreferencesResponse): Draft => ({
  timeZone: p.timeZonePreferenceMode as Mode,
  zone: p.ianaTimeZoneId ?? p.organizationDefaults.defaultIanaTimeZoneId,
  date: p.dateFormatPreferenceMode as Mode,
  dateKey: p.dateFormatPresetKey ?? p.organizationDefaults.defaultDateFormatPresetKey,
  number: p.numberFormatPreferenceMode as Mode,
  numberKey: p.numberFormatPresetKey ?? p.organizationDefaults.defaultNumberFormatPresetKey,
  language: p.languagePreferenceMode as Mode,
  languageCode: p.languageCode ?? p.organizationDefaults.defaultLanguageCode,
});

const toInput = (d: Draft): RegionalPreferenceInput => ({
  timeZonePreferenceMode: d.timeZone,
  ianaTimeZoneId: d.timeZone === "Explicit" ? d.zone : null,
  dateFormatPreferenceMode: d.date,
  dateFormatPresetKey: d.date === "Explicit" ? d.dateKey : null,
  numberFormatPreferenceMode: d.number,
  numberFormatPresetKey: d.number === "Explicit" ? d.numberKey : null,
  languagePreferenceMode: d.language,
  languageCode: d.language === "Explicit" ? d.languageCode : null,
});

/**
 * Personal regional preferences (PG-007): time zone, date format, number format and language, each independently
 * "organization default", "browser settings" or an explicit choice. Presentation only (nothing stored changes); only the
 * signed-in user's own row is written. Browser values are shown for transparency and are never saved.
 */
export function RegionalPreferencesCard({ area = "regional" }: { area?: "regional" | "language" }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const prefs = useQuery({ queryKey: ["regional-preferences"], queryFn: () => api.regional.preferences().then((r) => r.result), staleTime: 60_000 });
  const zones = useQuery({ queryKey: ["catalog", "time-zones", "organization"], queryFn: () => api.regional.timeZones(true).then((r) => r.result), staleTime: Infinity });
  const languages = useQuery({ queryKey: ["catalog", "languages"], queryFn: () => api.regional.languages().then((r) => r.result), staleTime: Infinity });
  const dates = useQuery({ queryKey: ["catalog", "date-formats"], queryFn: () => api.regional.dateFormats().then((r) => r.result), staleTime: Infinity });
  const numbers = useQuery({ queryKey: ["catalog", "number-formats"], queryFn: () => api.regional.numberFormats().then((r) => r.result), staleTime: Infinity });
  const [draft, setDraft] = useState<Draft | null>(null);
  const [saved, setSaved] = useState(false);
  const browser = useMemo(() => readBrowserEnvironment(), []);
  useEffect(() => {
    if (prefs.data && !draft) {
      setDraft(toDraft(prefs.data));
    }
  }, [prefs.data, draft]);

  const save = useMutation({
    mutationFn: () => {
      // Each area saves on its own: this area's choices come from the form, the other area's from what was last saved.
      const saved = toInput(toDraft(prefs.data!));
      const edited = toInput(draft!);
      const input =
        area === "regional"
          ? { ...saved, timeZonePreferenceMode: edited.timeZonePreferenceMode, ianaTimeZoneId: edited.ianaTimeZoneId, dateFormatPreferenceMode: edited.dateFormatPreferenceMode, dateFormatPresetKey: edited.dateFormatPresetKey, numberFormatPreferenceMode: edited.numberFormatPreferenceMode, numberFormatPresetKey: edited.numberFormatPresetKey }
          : { ...saved, languagePreferenceMode: edited.languagePreferenceMode, languageCode: edited.languageCode };
      return api.regional
        .updatePreferences(
          {
            timeZonePreferenceMode: input.timeZonePreferenceMode,
            ianaTimeZoneId: input.ianaTimeZoneId ?? null,
            dateFormatPreferenceMode: input.dateFormatPreferenceMode,
            dateFormatPresetKey: input.dateFormatPresetKey ?? null,
            numberFormatPreferenceMode: input.numberFormatPreferenceMode,
            numberFormatPresetKey: input.numberFormatPresetKey ?? null,
            languagePreferenceMode: input.languagePreferenceMode,
            languageCode: input.languageCode ?? null,
          },
          Number(prefs.data!.preferenceRevision),
        )
        .then((r) => r.result);
    },
    onSuccess: (result) => {
      setSaved(true);
      queryClient.setQueryData(["regional-preferences"], result);
      setDraft(toDraft(result));
    },
  });

  const preview = useMemo(() => {
    if (!draft || !prefs.data || !dates.data || !numbers.data) {
      return null;
    }
    return resolveFormatting({ preferences: toInput(draft), organization: prefs.data.organizationDefaults, catalogs: { datePresets: dates.data.presets, numberPresets: numbers.data.presets }, browser });
  }, [draft, prefs.data, dates.data, numbers.data, browser]);

  const queries = [prefs, zones, languages, dates, numbers];
  const failed = queries.find((q) => q.isError);
  if (failed?.isError) {
    return <ErrorPanel error={failed.error} onRetry={() => void failed.refetch()} />;
  }
  const zoneList = zones.data;
  const languageList = languages.data;
  const dateList = dates.data;
  const numberList = numbers.data;
  if (queries.some((q) => q.isPending) || !draft || !preview || !prefs.data || !zoneList || !languageList || !dateList || !numberList) {
    return <LoadingBlock label="Loading regional preferences" />;
  }
  const org = prefs.data.organizationDefaults;
  const label = (list: Array<{ key: string; text: string }>, key: string) => list.find((i) => i.key === key)?.text ?? key;
  const zoneName = (id: string) => zoneList.timeZones.find((z) => z.ianaTimeZoneId === id)?.timeZoneDisplayName ?? id;
  const dateItems = dateList.presets.map((p) => ({ key: p.dateFormatPresetKey, text: p.presetDisplayName }));
  const numberItems = numberList.presets.map((p) => ({ key: p.numberFormatPresetKey, text: p.presetDisplayName }));
  const languageItems = languageList.languages.map((l) => ({ key: l.languageCode, text: `${l.languageDisplayName} — ${l.nativeLanguageName}` }));
  const set = (patch: Partial<Draft>) => {
    setSaved(false);
    setDraft({ ...draft, ...patch });
  };
  const browserLocale = browser.languages[0] ?? "not available";
  const regional = area === "regional";

  return (
    <div className={regional ? "grid min-w-0 grid-cols-1 items-start gap-4 xl:grid-cols-[minmax(0,1fr)_28rem]" : "grid min-w-0 grid-cols-1 items-start gap-4"}>
      <Card title={<span id="regional-preferences-heading">{regional ? "Regional settings" : "Language"}</span>} aria-labelledby="regional-preferences-heading">
        <p className="text-sm text-muted">
          {regional
            ? "These choices change only how dates, times and numbers are shown to you. Stored values, permissions and the audit history are never changed. Each setting is independent."
            : "Your language choice is saved with your other preferences. It changes nothing that is stored."}
        </p>
        <form
          className="flex w-full min-w-0 max-w-xl flex-col gap-6"
          onSubmit={(event) => {
            event.preventDefault();
            save.mutate();
          }}
        >
          {regional ? (
            <>
            <RadioGroup
              legend="Time zone"
              value={draft.timeZone}
              onValueChange={(v) => set({ timeZone: v as Mode })}
              options={[
                { value: "OrganizationDefault", label: `Use organization default (${zoneName(org.defaultIanaTimeZoneId)})` },
                { value: "BrowserDefault", label: `Use browser settings (${browser.timeZone ?? "not available"})` },
                {
                  value: "Explicit",
                  label: "Choose my own",
                  details: <SearchableSelect label="My time zone" filterLabel="Filter time zones" value={draft.zone} onValueChange={(zone) => set({ zone })} options={zoneList.timeZones.map((z) => ({ value: z.ianaTimeZoneId, label: `${z.timeZoneDisplayName} · ${z.ianaTimeZoneId}` }))} />,
                },
              ]}
            />
            <RadioGroup
              legend="Date and time format"
              value={draft.date}
              onValueChange={(v) => set({ date: v as Mode })}
              options={[
                { value: "OrganizationDefault", label: `Use organization default (${label(dateItems, org.defaultDateFormatPresetKey)})` },
                { value: "BrowserDefault", label: `Use browser settings (${browserLocale})` },
                { value: "Explicit", label: "Choose my own", details: <Select label="My date and time format" value={draft.dateKey} onValueChange={(dateKey) => set({ dateKey })} options={dateItems.map((i) => ({ value: i.key, label: i.text }))} /> },
              ]}
            />
            <RadioGroup
              legend="Number format"
              value={draft.number}
              onValueChange={(v) => set({ number: v as Mode })}
              options={[
                { value: "OrganizationDefault", label: `Use organization default (${label(numberItems, org.defaultNumberFormatPresetKey)})` },
                { value: "BrowserDefault", label: `Use browser settings (${browserLocale})` },
                { value: "Explicit", label: "Choose my own", details: <Select label="My number format" value={draft.numberKey} onValueChange={(numberKey) => set({ numberKey })} options={numberItems.map((i) => ({ value: i.key, label: i.text }))} /> },
              ]}
            />
            </>
          ) : null}
          {!regional ? (
            <RadioGroup
              legend="Language"
              value={draft.language}
              onValueChange={(v) => set({ language: v as Mode })}
              helpText="Your language choice is saved. Translation of the application (menus, buttons, messages and form text) is planned but not available yet, so the application is shown in English for now. Anything people enter and uploaded documents are never translated automatically. English remains the fallback."
              options={[
                { value: "OrganizationDefault", label: `Use organization default (${label(languageItems, org.defaultLanguageCode)})` },
                { value: "BrowserDefault", label: "Use browser settings" },
                { value: "Explicit", label: "Choose my own", details: <Select label="My language" value={draft.languageCode} onValueChange={(languageCode) => set({ languageCode })} options={languageItems.map((i) => ({ value: i.key, label: i.text }))} /> },
              ]}
            />
          ) : null}
          {save.isError ? <ErrorPanel error={save.error} /> : null}
          {saved ? <StatusMessage tone="success" title="Preferences saved and applied" /> : null}
          <div className="flex flex-wrap gap-2">
            <Button type="submit" busy={save.isPending}>
              Save preferences
            </Button>
            <Button
              type="button"
              variant="secondary"
              onClick={() => set(regional ? { timeZone: "OrganizationDefault", date: "OrganizationDefault", number: "OrganizationDefault" } : { language: "OrganizationDefault" })}
            >
              Reset to organization defaults
            </Button>
          </div>
        </form>
      </Card>
      {regional ? (
        <Card title={<span id="regional-preview-heading">Preview</span>} aria-labelledby="regional-preview-heading">
          <p className="text-sm text-muted">How a fixed sample, just after the March daylight-saving change, will look with the choices above.</p>
          <FormattingPreview settings={preview} />
        </Card>
      ) : null}
    </div>
  );
}
