/**
 * The one client-side date, time and number formatting resolver (C8 M3, CLAUDE.md §4E).
 *
 * Presentation only: stored instants are UTC and date-only values are `yyyy-MM-dd`; nothing here changes, shifts or
 * reinterprets stored data. This is the ONLY module allowed to call `Intl.DateTimeFormat`, `Intl.NumberFormat` or
 * `toLocale*String` (an ESLint rule enforces it), so every screen resolves organization → personal → browser identically.
 *
 * Resolution, independently for time zone, date format, number format and language:
 *   Explicit (a catalog key the user chose) > OrganizationDefault (the active organization's setting) > BrowserDefault
 *   (the browser's own value, never mistaken for an organization value) > invariant fallback.
 * A mode of BrowserDefault uses the browser; a missing preference row means OrganizationDefault.
 */

export type PreferenceMode = "OrganizationDefault" | "BrowserDefault" | "Explicit";

/** Structured date/time options of a catalog preset: every client formats from the same locale tag and options (never a free-form pattern). */
export type DatePresetSpec = { intlLocaleTag: string; monthStyle: "2-digit" | "short"; hourCycle: "h23" | "h12" };

export type FormattingSettings = {
  /** Valid IANA zone, e.g. America/Toronto. */
  timeZone: string;
  datePreset: DatePresetSpec;
  /** BCP 47 locale tag for digit grouping and the decimal separator. */
  numberLocale: string;
  /** One of the ten supported UI languages (BCP 47). */
  language: string;
};

export type RegionalPreferenceInput = {
  timeZonePreferenceMode: string;
  ianaTimeZoneId?: string | null;
  dateFormatPreferenceMode: string;
  dateFormatPresetKey?: string | null;
  numberFormatPreferenceMode: string;
  numberFormatPresetKey?: string | null;
  languagePreferenceMode: string;
  languageCode?: string | null;
};

export type OrganizationRegionalDefaults = {
  defaultIanaTimeZoneId: string;
  defaultLanguageCode: string;
  defaultDateFormatPresetKey: string;
  defaultNumberFormatPresetKey: string;
};

export type FormattingCatalogs = {
  datePresets: ReadonlyArray<{ dateFormatPresetKey: string; intlLocaleTag: string; monthStyle: string; hourCycle: string }>;
  numberPresets: ReadonlyArray<{ numberFormatPresetKey: string; intlLocaleTag: string }>;
};

export type BrowserEnvironment = { timeZone?: string | undefined; languages: readonly string[] };

export const SUPPORTED_LANGUAGES = ["en", "fr", "es", "it", "zh-Hans", "de", "pt", "ar", "hi", "ru"] as const;

/** Invariant fallback when nothing else resolves: Canada-first, unambiguous ISO order, 24-hour clock. */
export const FALLBACK_SETTINGS: FormattingSettings = {
  timeZone: "America/Toronto",
  datePreset: { intlLocaleTag: "en-CA", monthStyle: "2-digit", hourCycle: "h23" },
  numberLocale: "en-CA",
  language: "en",
};

export function isValidTimeZone(id: string | null | undefined): id is string {
  // Area/Location identifiers only: the runtime also accepts legacy aliases such as "EST" and "PST8PDT", which are not valid choices.
  if (!id || !/^[A-Z][A-Za-z_]*\/[A-Za-z0-9_+\-/]+$/.test(id)) {
    return false;
  }
  try {
    new Intl.DateTimeFormat("en", { timeZone: id }).format(0);
    return true;
  } catch {
    return false;
  }
}

/** What the browser itself reports. Isolated so tests can supply it; never persisted. */
export function readBrowserEnvironment(): BrowserEnvironment {
  let timeZone: string | undefined;
  try {
    timeZone = new Intl.DateTimeFormat().resolvedOptions().timeZone || undefined;
  } catch {
    timeZone = undefined;
  }
  const languages = typeof navigator === "undefined" ? [] : navigator.languages?.length ? navigator.languages : navigator.language ? [navigator.language] : [];
  return { timeZone, languages };
}

function browserHourCycle(locale: string): "h23" | "h12" {
  try {
    const cycle = new Intl.DateTimeFormat(locale, { hour: "numeric" }).resolvedOptions().hourCycle;
    return cycle === "h11" || cycle === "h12" ? "h12" : "h23";
  } catch {
    return "h23";
  }
}

function supportedLanguageFor(languages: readonly string[]): string {
  for (const tag of languages) {
    const lower = tag.toLowerCase();
    if (lower.startsWith("zh")) {
      return "zh-Hans";
    }
    const primary = lower.split("-")[0] ?? "";
    const match = SUPPORTED_LANGUAGES.find((code) => code.toLowerCase() === primary);
    if (match) {
      return match;
    }
  }
  return "en";
}

function specFor(catalogs: FormattingCatalogs | undefined, key: string | null | undefined): DatePresetSpec | undefined {
  const preset = catalogs?.datePresets.find((p) => p.dateFormatPresetKey === key);
  if (!preset) {
    return undefined;
  }
  return { intlLocaleTag: preset.intlLocaleTag, monthStyle: preset.monthStyle === "short" ? "short" : "2-digit", hourCycle: preset.hourCycle === "h12" ? "h12" : "h23" };
}

function numberLocaleFor(catalogs: FormattingCatalogs | undefined, key: string | null | undefined): string | undefined {
  return catalogs?.numberPresets.find((p) => p.numberFormatPresetKey === key)?.intlLocaleTag;
}

/**
 * Resolves the four settings. `preferences` null means "no row": every mode is OrganizationDefault. A value that is not valid
 * (an unknown key, a zone this runtime cannot format) never wins: resolution falls through to the next source.
 */
export function resolveFormatting(input: {
  preferences?: RegionalPreferenceInput | null | undefined;
  organization?: OrganizationRegionalDefaults | null | undefined;
  catalogs?: FormattingCatalogs | undefined;
  browser?: BrowserEnvironment | undefined;
}): FormattingSettings {
  const preferences = input.preferences ?? null;
  const org = input.organization ?? null;
  const browser = input.browser ?? readBrowserEnvironment();
  const browserLocale = browser.languages[0] ?? FALLBACK_SETTINGS.numberLocale;

  const orgZone = isValidTimeZone(org?.defaultIanaTimeZoneId) ? org.defaultIanaTimeZoneId : undefined;
  const timeZone = ((): string => {
    const mode = preferences?.timeZonePreferenceMode ?? "OrganizationDefault";
    if (mode === "Explicit" && isValidTimeZone(preferences?.ianaTimeZoneId)) {
      return preferences.ianaTimeZoneId;
    }
    if (mode === "BrowserDefault" && isValidTimeZone(browser.timeZone)) {
      return browser.timeZone;
    }
    return orgZone ?? (isValidTimeZone(browser.timeZone) ? browser.timeZone : FALLBACK_SETTINGS.timeZone);
  })();

  const orgDate = specFor(input.catalogs, org?.defaultDateFormatPresetKey);
  const datePreset = ((): DatePresetSpec => {
    const mode = preferences?.dateFormatPreferenceMode ?? "OrganizationDefault";
    const explicit = mode === "Explicit" ? specFor(input.catalogs, preferences?.dateFormatPresetKey) : undefined;
    if (explicit) {
      return explicit;
    }
    if (mode === "BrowserDefault") {
      return { intlLocaleTag: browserLocale, monthStyle: "2-digit", hourCycle: browserHourCycle(browserLocale) };
    }
    return orgDate ?? FALLBACK_SETTINGS.datePreset;
  })();

  const orgNumber = numberLocaleFor(input.catalogs, org?.defaultNumberFormatPresetKey);
  const numberLocale = ((): string => {
    const mode = preferences?.numberFormatPreferenceMode ?? "OrganizationDefault";
    const explicit = mode === "Explicit" ? numberLocaleFor(input.catalogs, preferences?.numberFormatPresetKey) : undefined;
    if (explicit) {
      return explicit;
    }
    if (mode === "BrowserDefault") {
      return browserLocale;
    }
    return orgNumber ?? FALLBACK_SETTINGS.numberLocale;
  })();

  const language = ((): string => {
    const mode = preferences?.languagePreferenceMode ?? "OrganizationDefault";
    if (mode === "Explicit" && SUPPORTED_LANGUAGES.includes(preferences?.languageCode as (typeof SUPPORTED_LANGUAGES)[number])) {
      return preferences!.languageCode as string;
    }
    if (mode === "BrowserDefault") {
      return supportedLanguageFor(browser.languages);
    }
    return SUPPORTED_LANGUAGES.includes(org?.defaultLanguageCode as (typeof SUPPORTED_LANGUAGES)[number]) ? (org!.defaultLanguageCode as string) : FALLBACK_SETTINGS.language;
  })();

  return { timeZone, datePreset, numberLocale, language };
}

export type InstantStyle = "dateTime" | "date" | "time";

export type Formatter = {
  readonly settings: FormattingSettings;
  /** An exact instant (ISO 8601 with offset, epoch ms or Date) shown in the effective time zone. Invalid input returns the empty marker. */
  instant(value: string | number | Date | null | undefined, style?: InstantStyle, options?: { seconds?: boolean }): string;
  /** The calendar date an instant falls on in the effective time zone. */
  date(value: string | number | Date | null | undefined): string;
  /** A date-only value (`yyyy-MM-dd`) shown with the date preset's order. It is never shifted by any time zone. */
  dateOnly(value: string | null | undefined): string;
  /** Long human date (weekday, month name) for headings, in the effective zone. */
  longDate(value: string | number | Date): string;
  /** A short calendar label for a chart axis (`yyyy-MM-dd` already in its zone). Never shifted. */
  calendarDate(isoDate: string, style?: "short" | "long"): string;
  number(value: number | string | null | undefined, options?: { minimumFractionDigits?: number; maximumFractionDigits?: number }): string;
};

const EMPTY = "—";

function toDate(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === "") {
    return null;
  }
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

export function createFormatter(settings: FormattingSettings): Formatter {
  const { timeZone, datePreset, numberLocale } = settings;
  const dateParts: Intl.DateTimeFormatOptions = { year: "numeric", month: datePreset.monthStyle, day: "2-digit" };
  const timeParts = (seconds: boolean): Intl.DateTimeFormatOptions => ({ hour: datePreset.hourCycle === "h12" ? "numeric" : "2-digit", minute: "2-digit", ...(seconds ? { second: "2-digit" } : {}), hourCycle: datePreset.hourCycle });
  const cache = new Map<string, Intl.DateTimeFormat>();
  const build = (key: string, options: Intl.DateTimeFormatOptions, locale = datePreset.intlLocaleTag) => {
    const cacheKey = `${locale}|${key}`;
    let format = cache.get(cacheKey);
    if (!format) {
      try {
        format = new Intl.DateTimeFormat(locale, { ...options, timeZone });
      } catch {
        format = new Intl.DateTimeFormat(FALLBACK_SETTINGS.datePreset.intlLocaleTag, { ...options, timeZone });
      }
      cache.set(cacheKey, format);
    }
    return format;
  };
  const numberCache = new Map<string, Intl.NumberFormat>();

  return {
    settings,
    instant(value, style = "dateTime", options) {
      const date = toDate(value);
      if (!date) {
        return EMPTY;
      }
      const seconds = options?.seconds === true;
      if (style === "date") {
        return build("date", dateParts).format(date);
      }
      if (style === "time") {
        return build(`time${seconds}`, timeParts(seconds)).format(date);
      }
      return build(`dateTime${seconds}`, { ...dateParts, ...timeParts(seconds) }).format(date);
    },
    date(value) {
      const date = toDate(value);
      return date ? build("date", dateParts).format(date) : EMPTY;
    },
    dateOnly(value) {
      const match = value ? /^(\d{4})-(\d{2})-(\d{2})$/.exec(value) : null;
      if (!match) {
        return value || EMPTY;
      }
      // Noon UTC formatted in UTC: the calendar date is identical in every zone and at every DST boundary.
      const noon = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]), 12));
      return new Intl.DateTimeFormat(datePreset.intlLocaleTag, { ...dateParts, timeZone: "UTC" }).format(noon);
    },
    longDate(value) {
      const date = toDate(value);
      return date ? build("longDate", { weekday: "long", year: "numeric", month: "long", day: "numeric" }, settings.language).format(date) : EMPTY;
    },
    calendarDate(isoDate, style = "short") {
      const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(isoDate);
      if (!match) {
        return isoDate;
      }
      const noon = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]), 12));
      return new Intl.DateTimeFormat(datePreset.intlLocaleTag, style === "short" ? { month: "short", day: "numeric", timeZone: "UTC" } : { year: "numeric", month: "short", day: "numeric", timeZone: "UTC" }).format(noon);
    },
    number(value, options) {
      if (value === null || value === undefined || value === "") {
        return "";
      }
      const numeric = typeof value === "number" ? value : Number(value);
      if (!Number.isFinite(numeric)) {
        return String(value);
      }
      const key = `${options?.minimumFractionDigits ?? ""}|${options?.maximumFractionDigits ?? ""}`;
      let format = numberCache.get(key);
      if (!format) {
        try {
          format = new Intl.NumberFormat(numberLocale, options);
        } catch {
          format = new Intl.NumberFormat(FALLBACK_SETTINGS.numberLocale, options);
        }
        numberCache.set(key, format);
      }
      return format.format(numeric);
    },
  };
}

// ---------------------------------------------------------------------------------------------------------------------
// Time zones: offsets for display, and local date-time entry with explicit DST gap / fold handling.
// ---------------------------------------------------------------------------------------------------------------------

/** The zone's UTC offset in minutes at an instant (positive east of UTC). Derived from tzdb through Intl, never from a stored offset. */
export function zoneOffsetMinutes(timeZone: string, instant: Date): number {
  const parts = new Intl.DateTimeFormat("en-US", { timeZone, hourCycle: "h23", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit" }).formatToParts(instant);
  const get = (type: string) => Number(parts.find((p) => p.type === type)?.value);
  const asUtc = Date.UTC(get("year"), get("month") - 1, get("day"), get("hour") % 24, get("minute"), get("second"));
  return Math.round((asUtc - Math.floor(instant.getTime() / 1000) * 1000) / 60000);
}

/** Display-only label such as "UTC−05:00" for the offset in force at the reference instant (it changes with DST, so it is never stored). */
export function zoneOffsetLabel(timeZone: string, instant: Date): string {
  const minutes = zoneOffsetMinutes(timeZone, instant);
  const sign = minutes < 0 ? "−" : "+";
  const abs = Math.abs(minutes);
  return `UTC${sign}${String(Math.floor(abs / 60)).padStart(2, "0")}:${String(abs % 60).padStart(2, "0")}`;
}

export type LocalDateTime = { year: number; month: number; day: number; hour: number; minute: number; second?: number };

export type LocalToInstant =
  | { kind: "ok"; instant: Date }
  /** The wall-clock time does not exist (spring-forward gap): the caller must reject it with a helpful message. */
  | { kind: "gap" }
  /** The wall-clock time occurs twice (fall-back fold): the caller must disambiguate; it is never chosen silently. */
  | { kind: "ambiguous"; earlier: Date; later: Date };

/** Converts a local wall-clock time in an IANA zone to an exact instant, reporting DST gaps and folds explicitly. */
export function localDateTimeToInstant(local: LocalDateTime, timeZone: string): LocalToInstant {
  const wall = Date.UTC(local.year, local.month - 1, local.day, local.hour, local.minute, local.second ?? 0);
  const offsets = new Set<number>();
  for (const probe of [-36, -12, 12, 36]) {
    offsets.add(zoneOffsetMinutes(timeZone, new Date(wall + probe * 3600_000)));
  }
  const valid = [...offsets]
    .map((offset) => new Date(wall - offset * 60_000))
    .filter((candidate) => zoneOffsetMinutes(timeZone, candidate) * 60_000 === wall - candidate.getTime())
    .sort((a, b) => a.getTime() - b.getTime());
  if (valid.length === 0) {
    return { kind: "gap" };
  }
  if (valid.length === 1) {
    return { kind: "ok", instant: valid[0]! };
  }
  return { kind: "ambiguous", earlier: valid[0]!, later: valid[valid.length - 1]! };
}

/** The wall-clock fields an instant has in a zone (for an edit field that shows an exact instant). */
export function instantToLocalDateTime(instant: Date, timeZone: string): LocalDateTime {
  const parts = new Intl.DateTimeFormat("en-US", { timeZone, hourCycle: "h23", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit" }).formatToParts(instant);
  const get = (type: string) => Number(parts.find((p) => p.type === type)?.value);
  return { year: get("year"), month: get("month"), day: get("day"), hour: get("hour") % 24, minute: get("minute"), second: get("second") };
}

// ---------------------------------------------------------------------------------------------------------------------
// The active formatter for the current session. The app shell sets it; components read it with `useFormatter` (a store).
// ---------------------------------------------------------------------------------------------------------------------

let active: Formatter = createFormatter(FALLBACK_SETTINGS);
let version = 0;
const listeners = new Set<() => void>();

/** Replaces the session's effective settings (called by the app shell when preferences or organization defaults change). */
export function setActiveFormatting(settings: FormattingSettings): void {
  const current = active.settings;
  if (JSON.stringify(current) === JSON.stringify(settings)) {
    return;
  }
  active = createFormatter(settings);
  version += 1;
  for (const listener of listeners) {
    listener();
  }
}

export function getActiveFormatter(): Formatter {
  return active;
}

export function subscribeFormatting(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function getFormattingVersion(): number {
  return version;
}
