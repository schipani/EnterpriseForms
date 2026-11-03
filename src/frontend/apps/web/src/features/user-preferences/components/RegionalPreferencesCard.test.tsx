import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const regional = {
  preferences: vi.fn(),
  updatePreferences: vi.fn(),
  timeZones: vi.fn(),
  languages: vi.fn(),
  dateFormats: vi.fn(),
  numberFormats: vi.fn(),
};

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => ({ regional }),
}));

const { RegionalPreferencesCard } = await import("./RegionalPreferencesCard");

const organizationDefaults = { defaultCountryCode: "CA", defaultIanaTimeZoneId: "America/Winnipeg", defaultLanguageCode: "fr", defaultDateFormatPresetKey: "iso-24h", defaultNumberFormatPresetKey: "en-ca", organizationSettingRevision: 3, isSystemDefault: false, catalogVersion: "t" };
const preferences = (extra: Record<string, unknown> = {}) => ({
  timeZonePreferenceMode: "OrganizationDefault",
  ianaTimeZoneId: null,
  dateFormatPreferenceMode: "OrganizationDefault",
  dateFormatPresetKey: null,
  numberFormatPreferenceMode: "OrganizationDefault",
  numberFormatPresetKey: null,
  languagePreferenceMode: "OrganizationDefault",
  languageCode: null,
  preferenceRevision: 4,
  organizationDefaults,
  ...extra,
});

function renderCard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <RegionalPreferencesCard />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  regional.timeZones.mockResolvedValue({
    result: {
      catalogVersion: "t",
      organizationCountryCode: "CA",
      timeZones: [
        { ianaTimeZoneId: "America/Toronto", timeZoneDisplayName: "Eastern Time (Toronto)", countryCodes: ["CA"], timeZoneSortOrder: 1, contextPriority: 0 },
        { ianaTimeZoneId: "America/Winnipeg", timeZoneDisplayName: "Central Time (Winnipeg)", countryCodes: ["CA"], timeZoneSortOrder: 2, contextPriority: 0 },
        { ianaTimeZoneId: "Europe/Rome", timeZoneDisplayName: "Central European Time (Rome)", countryCodes: ["IT"], timeZoneSortOrder: 27, contextPriority: 1 },
      ],
    },
  });
  regional.languages.mockResolvedValue({ result: { catalogVersion: "t", languages: [{ languageCode: "en", languageDisplayName: "English", nativeLanguageName: "English", displaySortOrder: 1 }, { languageCode: "fr", languageDisplayName: "French", nativeLanguageName: "Français", displaySortOrder: 2 }] } });
  regional.dateFormats.mockResolvedValue({ result: { catalogVersion: "t", presets: [{ dateFormatPresetKey: "iso-24h", presetDisplayName: "2026-11-03, 24-hour clock", intlLocaleTag: "en-CA", monthStyle: "2-digit", hourCycle: "h23", displaySortOrder: 1 }, { dateFormatPresetKey: "mdy-12h", presetDisplayName: "11/03/2026, 12-hour clock", intlLocaleTag: "en-US", monthStyle: "2-digit", hourCycle: "h12", displaySortOrder: 4 }] } });
  regional.numberFormats.mockResolvedValue({ result: { catalogVersion: "t", presets: [{ numberFormatPresetKey: "en-ca", presetDisplayName: "1,234.56 (Canada, English)", intlLocaleTag: "en-CA", exampleText: "1,234.56", displaySortOrder: 1 }, { numberFormatPresetKey: "de-de", presetDisplayName: "1.234,56 (Germany)", intlLocaleTag: "de-DE", exampleText: "1.234,56", displaySortOrder: 5 }] } });
});

describe("RegionalPreferencesCard", () => {
  it("shows the organization default for each setting and previews it in the organization's time zone", async () => {
    regional.preferences.mockResolvedValue({ result: preferences() });
    renderCard();
    expect(await screen.findByRole("radio", { name: "Use organization default (Central Time (Winnipeg))", checked: true })).toBeInTheDocument();
    expect(screen.getAllByRole("radio", { checked: true })).toHaveLength(4);
    expect(screen.getByTestId("preview-zone")).toHaveTextContent("America/Winnipeg (UTC−06:00 on the sample date)");
    expect(screen.getByTestId("preview-date-only")).toHaveTextContent("2026-03-08");
  });

  it("puts the form on the left and the live preview on the right on wide screens, stacking on narrow ones, and explains the language setting honestly", async () => {
    regional.preferences.mockResolvedValue({ result: preferences() });
    renderCard();
    const form = await screen.findByRole("region", { name: "Time zone, date, number and language" });
    const grid = form.parentElement!;
    expect(grid.className).toContain("grid-cols-1");
    expect(grid.className).toContain("lg:grid-cols-[minmax(0,13fr)_minmax(0,7fr)]");
    expect(screen.getByRole("region", { name: "Preview" }).parentElement).toBe(grid);
    expect(screen.getByText(/Translation of the application \(menus, buttons, messages and form text\) is planned but not available yet/)).toBeInTheDocument();
    expect(screen.getByText(/English remains the fallback/)).toBeInTheDocument();
  });

  it("updates the preview immediately for an unsaved choice", async () => {
    const user = userEvent.setup();
    regional.preferences.mockResolvedValue({ result: preferences() });
    renderCard();
    await screen.findByTestId("preview-zone");
    expect(screen.getByTestId("preview-zone")).toHaveTextContent("America/Winnipeg");
    await user.click(within(screen.getByRole("group", { name: "Time zone" })).getByRole("radio", { name: /^Use browser settings/ }));
    expect(regional.updatePreferences).not.toHaveBeenCalled();
    expect(screen.getByTestId("preview-zone")).not.toHaveTextContent("America/Winnipeg");
  });

  it("saves each setting independently, sends no value unless Explicit, and carries the revision it read", async () => {
    const user = userEvent.setup();
    regional.preferences.mockResolvedValue({ result: preferences() });
    regional.updatePreferences.mockImplementation(async (request: Record<string, unknown>) => ({ result: preferences({ ...request, preferenceRevision: 5 }) }));
    renderCard();
    const zoneGroup = (await screen.findByRole("group", { name: "Time zone" })) as HTMLElement;
    await user.click(within(zoneGroup).getByRole("radio", { name: /^Use browser settings/ }));
    const numberGroup = screen.getByRole("group", { name: "Number format" });
    await user.click(within(numberGroup).getByRole("radio", { name: /^Use browser settings/ }));
    await user.click(screen.getByRole("button", { name: "Save preferences" }));
    await waitFor(() => expect(regional.updatePreferences).toHaveBeenCalled());
    expect(regional.updatePreferences).toHaveBeenCalledWith(
      {
        timeZonePreferenceMode: "BrowserDefault",
        ianaTimeZoneId: null,
        dateFormatPreferenceMode: "OrganizationDefault",
        dateFormatPresetKey: null,
        numberFormatPreferenceMode: "BrowserDefault",
        numberFormatPresetKey: null,
        languagePreferenceMode: "OrganizationDefault",
        languageCode: null,
      },
      4,
    );
    expect(await screen.findByText("Preferences saved and applied")).toBeInTheDocument();
  });

  it("keeps an explicit choice and resets all four to the organization defaults on request", async () => {
    const user = userEvent.setup();
    regional.preferences.mockResolvedValue({ result: preferences({ timeZonePreferenceMode: "Explicit", ianaTimeZoneId: "Europe/Rome", languagePreferenceMode: "Explicit", languageCode: "fr" }) });
    renderCard();
    const zoneGroup = await screen.findByRole("group", { name: "Time zone" });
    expect(within(zoneGroup).getByRole("radio", { name: "Choose my own", checked: true })).toBeInTheDocument();
    expect(screen.getByTestId("preview-zone")).toHaveTextContent("Europe/Rome");
    await user.click(screen.getByRole("button", { name: "Reset to organization defaults" }));
    expect(screen.getAllByRole("radio", { checked: true }).map((r) => r.getAttribute("value"))).toEqual(["OrganizationDefault", "OrganizationDefault", "OrganizationDefault", "OrganizationDefault"]);
    expect(screen.getByTestId("preview-zone")).toHaveTextContent("America/Winnipeg");
  });

  it("shows a problem instead of saving when the server rejects a stale revision", async () => {
    const user = userEvent.setup();
    regional.preferences.mockResolvedValue({ result: preferences() });
    regional.updatePreferences.mockRejectedValue(new Error("stale"));
    renderCard();
    await user.click(await screen.findByRole("button", { name: "Save preferences" }));
    await waitFor(() => expect(regional.updatePreferences).toHaveBeenCalled());
    expect(screen.queryByText("Preferences saved and applied")).not.toBeInTheDocument();
  });
});
