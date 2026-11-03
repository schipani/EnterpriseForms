import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, FormattingPreview, LoadingBlock, Select, StatusMessage, TextInput, SearchableSelect, resolveFormatting, type FormattingSettings } from "@enterpriseforms/ui";
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
export function RegionalSettingsCard() {
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
  const [reason, setReason] = useState("");
  const [saved, setSaved] = useState(false);
  useEffect(() => {
    if (settings.data && !draft) {
      setDraft({ country: settings.data.defaultCountryCode, zone: settings.data.defaultIanaTimeZoneId, language: settings.data.defaultLanguageCode, date: settings.data.defaultDateFormatPresetKey, number: settings.data.defaultNumberFormatPresetKey });
    }
  }, [settings.data, draft]);

  const save = useMutation({
    mutationFn: () =>
      api.regional
        .updateSettings({ defaultCountryCode: draft!.country, defaultIanaTimeZoneId: draft!.zone, defaultLanguageCode: draft!.language, defaultDateFormatPresetKey: draft!.date, defaultNumberFormatPresetKey: draft!.number, changeReason: reason.trim() }, Number(settings.data!.organizationSettingRevision))
        .then((r) => r.result),
    onSuccess: async () => {
      setSaved(true);
      setReason("");
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
  const valid = draft.country && draft.zone && draft.language && draft.date && draft.number && reason.trim().length >= 3;
  const set = (patch: Partial<Draft>) => {
    setSaved(false);
    setDraft({ ...draft, ...patch });
  };
  const readOnly = !canManage;

  return (
    <div className="flex min-w-0 flex-col gap-4">
      <Card title={<span id="regional-defaults-heading">Regional defaults</span>} aria-labelledby="regional-defaults-heading">
        <p className="text-sm text-muted">
          These defaults apply to every member who has not chosen their own. They change only how dates, times and numbers are shown; stored values, permissions and audit history are not affected.{" "}
          {settings.data.isSystemDefault ? <Badge tone="warning">Not yet saved — showing the Canadian default</Badge> : <Badge tone="success">{`Revision ${settings.data.organizationSettingRevision}`}</Badge>}
        </p>
        {readOnly ? (
          <StatusMessage tone="info" title="Read only">
            Only an organization owner can change the regional defaults.
          </StatusMessage>
        ) : null}
        <form
          className="flex min-w-0 flex-col gap-4"
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !readOnly) {
              save.mutate();
            }
          }}
        >
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
          <Select label="Default language" value={draft.language} disabled={readOnly} onValueChange={(language) => set({ language })} options={languageList.languages.map((l) => ({ value: l.languageCode, label: `${l.languageDisplayName} — ${l.nativeLanguageName}` }))} />
          <Select label="Default date format" value={draft.date} disabled={readOnly} onValueChange={(date) => set({ date })} options={dateList.presets.map((p) => ({ value: p.dateFormatPresetKey, label: p.presetDisplayName }))} />
          <Select label="Default number format" value={draft.number} disabled={readOnly} onValueChange={(number) => set({ number })} options={numberList.presets.map((p) => ({ value: p.numberFormatPresetKey, label: p.presetDisplayName }))} />
          {readOnly ? null : (
            <>
              <TextInput label="Reason for the change" value={reason} maxLength={500} required helpText="Recorded in the audit history (3 to 500 characters)." onChange={(event) => setReason(event.target.value)} />
              {save.isError ? <ErrorPanel error={save.error} /> : null}
              {saved ? <StatusMessage tone="success" title="Regional defaults saved" /> : null}
              <div>
                <Button type="submit" busy={save.isPending} disabled={!valid}>
                  Save regional defaults
                </Button>
              </div>
            </>
          )}
        </form>
      </Card>
      <Card title={<span id="regional-preview-heading">Preview</span>} aria-labelledby="regional-preview-heading">
        <p className="text-sm text-muted">How a member who uses the organization defaults will see a fixed sample just after the March daylight-saving change.</p>
        <FormattingPreview settings={preview} />
      </Card>
    </div>
  );
}
