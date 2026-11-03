import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const regional = {
  settings: vi.fn(),
  updateSettings: vi.fn(),
  countries: vi.fn(),
  timeZones: vi.fn(),
  languages: vi.fn(),
  dateFormats: vi.fn(),
  numberFormats: vi.fn(),
};
let capabilities: string[] = ["organization.manage"];

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => ({ regional }),
  useSession: () => ({ hasCapability: (c: string) => capabilities.includes(c) }),
}));

const { RegionalSettingsCard } = await import("./RegionalSettingsCard");

function renderCard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <RegionalSettingsCard />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  capabilities = ["organization.manage"];
  regional.settings.mockResolvedValue({ result: { defaultCountryCode: "CA", defaultIanaTimeZoneId: "America/Toronto", defaultLanguageCode: "en", defaultDateFormatPresetKey: "iso-24h", defaultNumberFormatPresetKey: "en-ca", organizationSettingRevision: 0, isSystemDefault: true, catalogVersion: "t" } });
  regional.countries.mockResolvedValue({ result: { catalogVersion: "t", countries: [{ countryCode: "CA", countryDisplayName: "Canada", geographicRegionCode: "NorthAmerica", displaySortOrder: 1 }, { countryCode: "IT", countryDisplayName: "Italy", geographicRegionCode: "Europe", displaySortOrder: 8 }] } });
  regional.timeZones.mockResolvedValue({ result: { catalogVersion: "t", organizationCountryCode: null, timeZones: [{ ianaTimeZoneId: "America/Toronto", timeZoneDisplayName: "Eastern Time (Toronto)", countryCodes: ["CA"], timeZoneSortOrder: 1, contextPriority: 1 }, { ianaTimeZoneId: "Europe/Rome", timeZoneDisplayName: "Central European Time (Rome)", countryCodes: ["IT"], timeZoneSortOrder: 27, contextPriority: 1 }] } });
  regional.languages.mockResolvedValue({ result: { catalogVersion: "t", languages: [{ languageCode: "en", languageDisplayName: "English", nativeLanguageName: "English", displaySortOrder: 1 }] } });
  regional.dateFormats.mockResolvedValue({ result: { catalogVersion: "t", presets: [{ dateFormatPresetKey: "iso-24h", presetDisplayName: "2026-11-03, 24-hour clock", intlLocaleTag: "en-CA", monthStyle: "2-digit", hourCycle: "h23", displaySortOrder: 1 }] } });
  regional.numberFormats.mockResolvedValue({ result: { catalogVersion: "t", presets: [{ numberFormatPresetKey: "en-ca", presetDisplayName: "1,234.56 (Canada, English)", intlLocaleTag: "en-CA", exampleText: "1,234.56", displaySortOrder: 1 }] } });
});

describe("RegionalSettingsCard", () => {
  it("starts from the Canadian default, says it is not yet saved, and previews it", async () => {
    renderCard();
    expect(await screen.findByText("Not yet saved — showing the Canadian default")).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: /Default country/ })).toHaveTextContent("Canada");
    expect(screen.getByTestId("preview-zone")).toHaveTextContent("America/Toronto (UTC−04:00 on the sample date)");
    expect(screen.getByTestId("preview-numbers")).toHaveTextContent("1,234,567.89");
  });

  it("lays the form (about 65%) and the preview (about 35%) side by side on wide screens and stacks them on narrow ones, with a language note that says translation is not available yet", async () => {
    renderCard();
    const form = await screen.findByRole("region", { name: "Regional defaults" });
    const grid = form.parentElement!;
    expect(grid.className).toContain("grid-cols-1");
    expect(grid.className).toContain("xl:grid-cols-[minmax(0,13fr)_minmax(0,7fr)]");
    expect(grid.className).toContain("items-start");
    expect(screen.getByRole("region", { name: "Preview" }).parentElement).toBe(grid);
    expect(screen.getByText(/Translation of the application is planned but not available yet/)).toBeInTheDocument();
    expect(screen.getByText(/never translated automatically/)).toBeInTheDocument();
  });

  it("saves the five values with the revision that was read, without asking for a reason", async () => {
    const user = userEvent.setup();
    regional.updateSettings.mockResolvedValue({ result: { defaultCountryCode: "CA", defaultIanaTimeZoneId: "America/Toronto", defaultLanguageCode: "en", defaultDateFormatPresetKey: "iso-24h", defaultNumberFormatPresetKey: "en-ca", organizationSettingRevision: 1, isSystemDefault: false, catalogVersion: "t" } });
    renderCard();
    const save = await screen.findByRole("button", { name: "Save regional defaults" });
    expect(screen.queryByRole("textbox", { name: /Reason/ })).not.toBeInTheDocument();
    expect(save).toBeEnabled();
    await user.click(save);
    await waitFor(() => expect(regional.updateSettings).toHaveBeenCalled());
    expect(regional.updateSettings).toHaveBeenCalledWith(
      { defaultCountryCode: "CA", defaultIanaTimeZoneId: "America/Toronto", defaultLanguageCode: "en", defaultDateFormatPresetKey: "iso-24h", defaultNumberFormatPresetKey: "en-ca" },
      0,
    );
    expect(await screen.findByText("Regional defaults saved")).toBeInTheDocument();
  });

  it("is read only without the owner capability", async () => {
    capabilities = ["security.administer"];
    renderCard();
    expect(await screen.findByText("Only an organization owner can change the regional defaults.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save regional defaults" })).not.toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: /Default country/ })).toBeDisabled();
  });

  it("offers real countries only: Europe is a region, so its countries are labelled and there is no Europe choice", async () => {
    renderCard();
    await screen.findByRole("combobox", { name: /Default country/ });
    expect(screen.queryByRole("option", { name: "Europe" })).not.toBeInTheDocument();
  });
});
