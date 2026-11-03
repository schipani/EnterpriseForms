import { describe, expect, it } from "vitest";
import {
  FALLBACK_SETTINGS,
  createFormatter,
  instantToLocalDateTime,
  isValidTimeZone,
  localDateTimeToInstant,
  resolveFormatting,
  zoneOffsetLabel,
  zoneOffsetMinutes,
  type FormattingCatalogs,
  type OrganizationRegionalDefaults,
  type RegionalPreferenceInput,
} from "./formatting";

const catalogs: FormattingCatalogs = {
  datePresets: [
    { dateFormatPresetKey: "iso-24h", intlLocaleTag: "en-CA", monthStyle: "2-digit", hourCycle: "h23" },
    { dateFormatPresetKey: "mdy-12h", intlLocaleTag: "en-US", monthStyle: "2-digit", hourCycle: "h12" },
    { dateFormatPresetKey: "dmy-text-24h", intlLocaleTag: "en-GB", monthStyle: "short", hourCycle: "h23" },
  ],
  numberPresets: [
    { numberFormatPresetKey: "en-ca", intlLocaleTag: "en-CA" },
    { numberFormatPresetKey: "de-de", intlLocaleTag: "de-DE" },
    { numberFormatPresetKey: "en-in", intlLocaleTag: "en-IN" },
  ],
};
const org: OrganizationRegionalDefaults = { defaultIanaTimeZoneId: "America/Winnipeg", defaultLanguageCode: "fr", defaultDateFormatPresetKey: "iso-24h", defaultNumberFormatPresetKey: "en-ca" };
const browser = { timeZone: "Europe/Rome", languages: ["de-DE", "en"] };
const allOrg: RegionalPreferenceInput = { timeZonePreferenceMode: "OrganizationDefault", dateFormatPreferenceMode: "OrganizationDefault", numberFormatPreferenceMode: "OrganizationDefault", languagePreferenceMode: "OrganizationDefault" };

describe("resolveFormatting precedence (explicit > organization > browser, per setting)", () => {
  it("a missing preference row means organization defaults for all four", () => {
    const s = resolveFormatting({ preferences: null, organization: org, catalogs, browser });
    expect(s.timeZone).toBe("America/Winnipeg");
    expect(s.datePreset.intlLocaleTag).toBe("en-CA");
    expect(s.numberLocale).toBe("en-CA");
    expect(s.language).toBe("fr");
  });

  it("browser mode uses the browser's own values and is never replaced by the organization's", () => {
    const s = resolveFormatting({ preferences: { ...allOrg, timeZonePreferenceMode: "BrowserDefault", numberFormatPreferenceMode: "BrowserDefault", languagePreferenceMode: "BrowserDefault", dateFormatPreferenceMode: "BrowserDefault" }, organization: org, catalogs, browser });
    expect(s.timeZone).toBe("Europe/Rome");
    expect(s.numberLocale).toBe("de-DE");
    expect(s.datePreset.intlLocaleTag).toBe("de-DE");
    expect(s.language).toBe("de");
  });

  it("each setting is independent: browser zone, explicit date format, organization number format", () => {
    const s = resolveFormatting({ preferences: { ...allOrg, timeZonePreferenceMode: "BrowserDefault", dateFormatPreferenceMode: "Explicit", dateFormatPresetKey: "mdy-12h" }, organization: org, catalogs, browser });
    expect(s.timeZone).toBe("Europe/Rome");
    expect(s.datePreset).toEqual({ intlLocaleTag: "en-US", monthStyle: "2-digit", hourCycle: "h12" });
    expect(s.numberLocale).toBe("en-CA");
  });

  it("explicit values win over both the organization and the browser", () => {
    const s = resolveFormatting({ preferences: { ...allOrg, timeZonePreferenceMode: "Explicit", ianaTimeZoneId: "America/Toronto", numberFormatPreferenceMode: "Explicit", numberFormatPresetKey: "en-in", languagePreferenceMode: "Explicit", languageCode: "hi" }, organization: org, catalogs, browser });
    expect(s.timeZone).toBe("America/Toronto");
    expect(s.numberLocale).toBe("en-IN");
    expect(s.language).toBe("hi");
  });

  it("an unknown key or an invalid zone never wins: resolution falls through", () => {
    const s = resolveFormatting({ preferences: { ...allOrg, timeZonePreferenceMode: "Explicit", ianaTimeZoneId: "EST", numberFormatPreferenceMode: "Explicit", numberFormatPresetKey: "nope" }, organization: org, catalogs, browser });
    expect(s.timeZone).toBe("America/Winnipeg");
    expect(s.numberLocale).toBe("en-CA");
  });

  it("without any source the invariant Canadian fallback applies", () => {
    expect(resolveFormatting({ preferences: null, organization: null, catalogs: undefined, browser: { timeZone: undefined, languages: [] } })).toEqual({ ...FALLBACK_SETTINGS });
  });

  it("browser languages map to a supported language, Chinese to Simplified, else English", () => {
    const pick = (languages: string[]) => resolveFormatting({ preferences: { ...allOrg, languagePreferenceMode: "BrowserDefault" }, organization: org, catalogs, browser: { timeZone: "Etc/UTC", languages } }).language;
    expect(pick(["zh-CN"])).toBe("zh-Hans");
    expect(pick(["xx", "pt-BR"])).toBe("pt");
    expect(pick(["xx"])).toBe("en");
  });
});

describe("time zone helpers", () => {
  it("accepts IANA identifiers and rejects offsets and abbreviations", () => {
    expect(isValidTimeZone("America/Toronto")).toBe(true);
    expect(isValidTimeZone("America/Winnipeg")).toBe(true);
    expect(isValidTimeZone("EST")).toBe(false);
    expect(isValidTimeZone("UTC-05:00")).toBe(false);
    expect(isValidTimeZone("")).toBe(false);
  });

  it("offsets follow DST: Toronto is UTC-5 in winter and UTC-4 in summer", () => {
    expect(zoneOffsetMinutes("America/Toronto", new Date("2026-01-15T12:00:00Z"))).toBe(-300);
    expect(zoneOffsetMinutes("America/Toronto", new Date("2026-07-15T12:00:00Z"))).toBe(-240);
    expect(zoneOffsetMinutes("America/Winnipeg", new Date("2026-07-15T12:00:00Z"))).toBe(-300);
    expect(zoneOffsetLabel("America/Toronto", new Date("2026-07-15T12:00:00Z"))).toBe("UTC−04:00");
  });
});

describe("DST gaps and folds on local entry", () => {
  it("a wall-clock time inside the spring-forward gap does not exist (Toronto 2026-03-08 02:30)", () => {
    expect(localDateTimeToInstant({ year: 2026, month: 3, day: 8, hour: 2, minute: 30 }, "America/Toronto")).toEqual({ kind: "gap" });
    expect(localDateTimeToInstant({ year: 2026, month: 3, day: 8, hour: 2, minute: 30 }, "America/Winnipeg")).toEqual({ kind: "gap" });
  });

  it("a wall-clock time inside the fall-back fold is ambiguous and never chosen silently (Toronto 2026-11-01 01:30)", () => {
    const result = localDateTimeToInstant({ year: 2026, month: 11, day: 1, hour: 1, minute: 30 }, "America/Toronto");
    expect(result.kind).toBe("ambiguous");
    if (result.kind === "ambiguous") {
      expect(result.earlier.toISOString()).toBe("2026-11-01T05:30:00.000Z");
      expect(result.later.toISOString()).toBe("2026-11-01T06:30:00.000Z");
    }
  });

  it("ordinary times convert exactly and round-trip", () => {
    const result = localDateTimeToInstant({ year: 2026, month: 7, day: 1, hour: 9, minute: 15 }, "America/Winnipeg");
    expect(result.kind).toBe("ok");
    if (result.kind === "ok") {
      expect(result.instant.toISOString()).toBe("2026-07-01T14:15:00.000Z");
      expect(instantToLocalDateTime(result.instant, "America/Winnipeg")).toMatchObject({ year: 2026, month: 7, day: 1, hour: 9, minute: 15 });
    }
    const justAfterGap = localDateTimeToInstant({ year: 2026, month: 3, day: 8, hour: 3, minute: 0 }, "America/Toronto");
    expect(justAfterGap.kind).toBe("ok");
  });
});

describe("formatter", () => {
  const instant = "2026-11-01T05:30:00.000Z"; // 01:30 EDT in Toronto (first pass of the fold), 23:30 on Oct 31 in Vancouver.

  it("shows the same instant in the effective time zone without changing it", () => {
    const toronto = createFormatter({ ...FALLBACK_SETTINGS, timeZone: "America/Toronto" });
    const vancouver = createFormatter({ ...FALLBACK_SETTINGS, timeZone: "America/Vancouver" });
    expect(toronto.instant(instant)).toContain("2026-11-01");
    expect(toronto.instant(instant)).toContain("01:30");
    expect(vancouver.instant(instant)).toContain("2026-10-31");
    expect(vancouver.instant(instant)).toContain("22:30");
    expect(new Date(instant).toISOString()).toBe(instant);
  });

  it("uses the date preset's order and clock", () => {
    const us = createFormatter({ ...FALLBACK_SETTINGS, timeZone: "America/Toronto", datePreset: { intlLocaleTag: "en-US", monthStyle: "2-digit", hourCycle: "h12" } });
    expect(us.instant("2026-07-04T18:05:00Z")).toMatch(/^07\/04\/2026,? 2:05\s?PM$/);
    const text = createFormatter({ ...FALLBACK_SETTINGS, timeZone: "Etc/UTC", datePreset: { intlLocaleTag: "en-GB", monthStyle: "short", hourCycle: "h23" } });
    expect(text.instant("2026-07-04T18:05:00Z")).toMatch(/^04 Jul 2026,? 18:05$/);
  });

  it("date-only values never shift with the zone, including across the DST change", () => {
    const west = createFormatter({ ...FALLBACK_SETTINGS, timeZone: "Pacific/Honolulu" });
    const east = createFormatter({ ...FALLBACK_SETTINGS, timeZone: "Asia/Tokyo" });
    for (const day of ["2026-03-08", "2026-11-01", "2026-12-31"]) {
      expect(west.dateOnly(day)).toBe(day);
      expect(east.dateOnly(day)).toBe(day);
    }
    expect(createFormatter({ ...FALLBACK_SETTINGS, datePreset: { intlLocaleTag: "en-US", monthStyle: "2-digit", hourCycle: "h12" } }).dateOnly("2026-03-08")).toBe("03/08/2026");
  });

  it("formats numbers by the number preset and leaves invalid input readable", () => {
    expect(createFormatter({ ...FALLBACK_SETTINGS, numberLocale: "en-CA" }).number(1234567.891, { maximumFractionDigits: 2 })).toBe("1,234,567.89");
    expect(createFormatter({ ...FALLBACK_SETTINGS, numberLocale: "de-DE" }).number(1234567.891, { maximumFractionDigits: 2 })).toBe("1.234.567,89");
    expect(createFormatter({ ...FALLBACK_SETTINGS, numberLocale: "en-IN" }).number(1234567)).toBe("12,34,567");
    expect(createFormatter(FALLBACK_SETTINGS).number("12")).toBe("12");
    expect(createFormatter(FALLBACK_SETTINGS).number("abc")).toBe("abc");
    expect(createFormatter(FALLBACK_SETTINGS).number(undefined)).toBe("");
  });

  it("invalid instants render as a dash instead of throwing", () => {
    expect(createFormatter(FALLBACK_SETTINGS).instant("not a date")).toBe("—");
    expect(createFormatter(FALLBACK_SETTINGS).instant(null)).toBe("—");
  });
});
