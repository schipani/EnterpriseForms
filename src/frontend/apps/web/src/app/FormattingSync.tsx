import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { FALLBACK_SETTINGS, resolveFormatting, setActiveFormatting } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect } from "react";

/** Query keys shared with the user-preferences feature (the shell may not import a feature implementation, only its public entry). */
export const REGIONAL_PREFERENCES_KEY = ["regional-preferences"] as const;
export const DATE_FORMATS_KEY = ["catalog", "date-formats"] as const;
export const NUMBER_FORMATS_KEY = ["catalog", "number-formats"] as const;

/**
 * Feeds the single client formatter (C8 M3): the signed-in user's regional preferences, the active organization's defaults
 * and the server catalog are resolved by `resolveFormatting` (explicit > organization > browser, per setting) and published
 * to the shared formatter every screen reads. Renders nothing. While anything is loading, or when anonymous, the formatter
 * keeps the invariant Canadian fallback, so no screen ever waits on it. A new tenant context re-resolves because the
 * query cache is cleared on a tenant switch or sign-out.
 */
export function FormattingSync() {
  const api = useApi();
  const session = useSession();
  const enabled = session.status === "authenticated";
  const preferences = useQuery({ queryKey: REGIONAL_PREFERENCES_KEY, queryFn: () => api.regional.preferences().then((r) => r.result), enabled, staleTime: 60_000 });
  const dateFormats = useQuery({ queryKey: DATE_FORMATS_KEY, queryFn: () => api.regional.dateFormats().then((r) => r.result), enabled, staleTime: Infinity });
  const numberFormats = useQuery({ queryKey: NUMBER_FORMATS_KEY, queryFn: () => api.regional.numberFormats().then((r) => r.result), enabled, staleTime: Infinity });

  const prefs = preferences.data;
  const dates = dateFormats.data;
  const numbers = numberFormats.data;
  useEffect(() => {
    if (!enabled || !prefs || !dates || !numbers) {
      setActiveFormatting(FALLBACK_SETTINGS);
      return;
    }
    const settings = resolveFormatting({ preferences: prefs, organization: prefs.organizationDefaults, catalogs: { datePresets: dates.presets, numberPresets: numbers.presets } });
    setActiveFormatting(settings);
    document.documentElement.lang = settings.language;
  }, [enabled, prefs, dates, numbers]);

  return null;
}
