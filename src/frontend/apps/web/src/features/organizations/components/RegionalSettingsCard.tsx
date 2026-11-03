import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, FormattingPreview, LoadingBlock, Select, StatusMessage, SearchableSelect, resolveFormatting, type FormattingSettings } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { ErrorPanel } from "../../support";

type Draft = { country: string; zone: string; language: string; date: string; number: string };

/**
 * Organization regional defaults (PG-043): the country, IANA time zone, language, date and number presets that members use
 * unless they choose their own. Presentation only: nothing here changes stored instants, dates, numbers, permissions,
 * encryption or search. The Authority decides who may save (manage_configuration); Security Administrators and others read.
 * The list of zones puts the selected country's zones first, then the persisted order; every zone appears once.
 */
export function RegionalSettingsCard({ area = "regional" }: { area?: "regional" | "language" }) {
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const canManage = session.hasCapability("organization.manage");
  const settings = useQuery({ queryKey: ["organization", "settings"], queryFn: () => api.regional.settings().then((r) => r.result) });
  const countries = useQuery({ queryKey: ["catalog", "countries"], queryFn: () => api.regional.countries().then((r) => r.result), staleTime: Infinity });
  const zones = useQuery({ queryKey: ["catalog", "time-zones"], queryFn: () => api.regional.timeZones(false).then((r) => r.result), staleTime: Infinity });
  const languages = useQuery({ queryKey: ["catalog", "languages"], queryFn: () => api.regional.languages().then((r) => r.result), staleTime: Infinity });
  const dates = useQuery({ queryKey: ["catalog", "date-formats"], queryFn: () => api.regional.dateFormats().then((r) => r.result), staleTime: Infinity });
  const numbers = useQuery({ queryKey: ["catalog", "number-formats"], queryFn: () => api.regional.numberFormats().then((r) => r.result), staleTime: Infinity });
  const [draft, setDraft] = useState<Draft | null>(null);
  const [saved, setSaved] = useState(false);
  useEffect(() => {
    if (settings.data && !draft) {
      setDraft({ country: settings.data.defaultCountryCode, zone: settings.data.defaultIanaTimeZoneId, language: settings.data.defaultLanguageCode, date: settings.data.defaultDateFormatPresetKey, number: settings.data.defaultNumberFormatPresetKey });
    }
  }, [settings.data, draft]);

  const save = useMutation({
    // Each area saves on its own: this area's choices come from the form, the other area's from what was last saved.
    mutationFn: () => {
      const s = settings.data!;
      const d = draft!;
      const request =
        area === "regional"
          ? { defaultCountryCode: d.country, defaultIanaTimeZoneId: d.zone, defaultLanguageCode: s.defaultLanguageCode, defaultDateFormatPresetKey: d.date, defaultNumberFormatPresetKey: d.number }
          : { defaultCountryCode: s.defaultCountryCode, defaultIanaTimeZoneId: s.defaultIanaTimeZoneId, defaultLanguageCode: d.language, defaultDateFormatPresetKey: s.defaultDateFormatPresetKey, defaultNumberFormatPresetKey: s.defaultNumberFormatPresetKey };
      return api.regional.updateSettings(request, Number(s.organizationSettingRevision)).then((r) => r.result);
    },
    onSuccess: async () => {
      setSaved(true);
      setDraft(null);
      await Promise.all([queryClient.invalidateQueries({ queryKey: ["organization", "settings"] }), queryClient.invalidateQueries({ queryKey: ["regional-preferences"] })]);
    },
  });

  const orderedZones = useMemo(() => {
    const list = zones.data?.timeZones ?? [];
    const country = draft?.country;
    return [...list].sort((a, b) => {
      const pa = country && a.countryCodes.includes(country) ? 0 : 1;
      const pb = country && b.countryCodes.includes(country) ? 0 : 1;
      return pa - pb || Number(a.timeZoneSortOrder) - Number(b.timeZoneSortOrder) || a.ianaTimeZoneId.localeCompare(b.ianaTimeZoneId, "en");
    });
  }, [zones.data, draft?.country]);

  const preview: FormattingSettings | null = useMemo(() => {
    if (!draft || !dates.data || !numbers.data) {
      return null;
    }
    return resolveFormatting({
      preferences: null,
      organization: { defaultIanaTimeZoneId: draft.zone, defaultLanguageCode: draft.language, defaultDateFormatPresetKey: draft.date, defaultNumberFormatPresetKey: draft.number },
      catalogs: { datePresets: dates.data.presets, numberPresets: numbers.data.presets },
    });
  }, [draft, dates.data, numbers.data]);

  const queries = [settings, countries, zones, languages, dates, numbers];
  const failed = queries.find((q) => q.isError);
  if (failed?.isError) {
    return <ErrorPanel error={failed.error} onRetry={() => void failed.refetch()} />;
  }
  const countryList = countries.data;
  const languageList = languages.data;
  const dateList = dates.data;
  const numberList = numbers.data;
  if (queries.some((q) => q.isPending) || !draft || !preview || !settings.data || !countryList || !languageList || !dateList || !numberList) {
    return <LoadingBlock label="Loading regional settings" />;
  }
  const valid = draft.country && draft.zone && draft.language && draft.date && draft.number;
  const set = (patch: Partial<Draft>) => {
    setSaved(false);
    setDraft({ ...draft, ...patch });
  };
  const readOnly = !canManage;
  const regional = area === "regional";

  return (
    <div className={regional ? "grid min-w-0 grid-cols-1 items-start gap-4 xl:grid-cols-[minmax(0,1fr)_28rem]" : "grid min-w-0 grid-cols-1 items-start gap-4"}>
      <Card title={<span id="regional-defaults-heading">{regional ? "Regional defaults" : "Default language"}</span>} aria-labelledby="regional-defaults-heading">
        <p className="text-sm text-muted">
          {regional
            ? "These defaults apply to every member who has not chosen their own. They change only how dates, times and numbers are shown; stored values, permissions and audit history are not affected."
            : "The language members use unless they choose their own on their Preferences page. It changes nothing that is stored."}{" "}
          {settings.data.isSystemDefault ? <Badge tone="warning">Not yet saved — showing the Canadian default</Badge> : <Badge tone="success">{`Revision ${settings.data.organizationSettingRevision}`}</Badge>}
        </p>
        {readOnly ? (
          <StatusMessage tone="info" title="Read only">
            Only an organization owner can change {regional ? "the regional defaults" : "the default language"}.
          </StatusMessage>
        ) : null}
        <form
          className="flex w-full min-w-0 max-w-xl flex-col gap-4"
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !readOnly) {
              save.mutate();
            }
          }}
        >
          {regional ? (
            <>
            <Select
              label="Default country"
              value={draft.country}
              disabled={readOnly}
              helpText="Europe is a region: choose the country. The country puts its time zones first in the list."
              onValueChange={(country) => set({ country })}
              options={countryList.countries.map((c) => ({ value: c.countryCode, label: c.geographicRegionCode === "Europe" ? `${c.countryDisplayName} (Europe)` : c.countryDisplayName }))}
            />
            <SearchableSelect
              label="Default time zone"
              filterLabel="Filter time zones"
              value={draft.zone}
              disabled={readOnly}
              helpText="An IANA time zone such as America/Toronto. Canada spans several zones; confirm the one that fits your head office."
              onValueChange={(zone) => set({ zone })}
              options={orderedZones.map((z) => ({ value: z.ianaTimeZoneId, label: `${z.timeZoneDisplayName} · ${z.ianaTimeZoneId}` }))}
            />
            <Select label="Default date format" value={draft.date} disabled={readOnly} onValueChange={(date) => set({ date })} options={dateList.presets.map((p) => ({ value: p.dateFormatPresetKey, label: p.presetDisplayName }))} />
            <Select label="Default number format" value={draft.number} disabled={readOnly} onValueChange={(number) => set({ number })} options={numberList.presets.map((p) => ({ value: p.numberFormatPresetKey, label: p.presetDisplayName }))} />
            </>
          ) : (
            <Select label="Default language" helpText="Saved as the organization's default language. Translation of the application is planned but not available yet, so the application is shown in English for now. Values people enter and uploaded documents are never translated automatically." value={draft.language} disabled={readOnly} onValueChange={(language) => set({ language })} options={languageList.languages.map((l) => ({ value: l.languageCode, label: `${l.languageDisplayName} — ${l.nativeLanguageName}` }))} />
          )}
          {readOnly ? null : (
            <>
              {save.isError ? <ErrorPanel error={save.error} /> : null}
              {saved ? <StatusMessage tone="success" title={regional ? "Regional defaults saved" : "Default language saved"} /> : null}
              <div>
                <Button type="submit" busy={save.isPending} disabled={!valid}>
                  {regional ? "Save regional defaults" : "Save default language"}
                </Button>
              </div>
            </>
          )}
        </form>
      </Card>
      {regional ? (
        <Card title={<span id="regional-preview-heading">Preview</span>} aria-labelledby="regional-preview-heading">
          <p className="text-sm text-muted">How a member who uses the organization defaults will see a fixed sample just after the March daylight-saving change.</p>
          <FormattingPreview settings={preview} />
        </Card>
      ) : null}
    </div>
  );
}
