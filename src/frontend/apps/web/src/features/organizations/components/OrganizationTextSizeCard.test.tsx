import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const organization = { branding: vi.fn(), updateBranding: vi.fn() };
const refresh = vi.fn();
let capabilities: string[] = ["organization.manage"];

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => ({ organization }),
  useSession: () => ({ hasCapability: (c: string) => capabilities.includes(c), refresh }),
}));

const { OrganizationTextSizeCard } = await import("./OrganizationTextSizeCard");

const branding = {
  organizationDisplayName: "Northwind",
  hasLogo: true,
  logoAlternativeText: "Northwind mark",
  primaryButtonBackgroundColor: "#111111",
  primaryButtonForegroundColor: "#FFFFFF",
  secondaryButtonBackgroundColor: "#FFFFFF",
  secondaryButtonForegroundColor: "#111111",
  secondaryButtonBorderColor: "#111111",
  organizationDefaultTextScalePercent: 100,
  organizationColorMode: "Light",
  brandingRevision: 7,
};

function renderCard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <OrganizationTextSizeCard />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  capabilities = ["organization.manage"];
  organization.branding.mockResolvedValue({ result: branding });
  vi.stubGlobal("ResizeObserver", class { observe() {} unobserve() {} disconnect() {} });
  // Radix Select opens with pointer events that jsdom does not implement.
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => undefined;
  Element.prototype.releasePointerCapture = () => undefined;
  Element.prototype.scrollIntoView = () => undefined;
});

describe("OrganizationTextSizeCard", () => {
  it("shows the saved default text size and offers nothing to save until it changes", async () => {
    renderCard();
    expect(await screen.findByRole("combobox", { name: "Organization default text size" })).toHaveTextContent("100%");
    expect(screen.getByRole("button", { name: "Save default text size" })).toBeDisabled();
  });

  it("is read only without the owner capability", async () => {
    capabilities = ["security.administer"];
    renderCard();
    expect(await screen.findByText("Only an organization owner can change the default text size.")).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Organization default text size" })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Save default text size" })).not.toBeInTheDocument();
  });

  it("saves through the branding update, sending everything else exactly as last saved with the revision that was read", async () => {
    const user = userEvent.setup();
    organization.updateBranding.mockResolvedValue({ result: { ...branding, organizationDefaultTextScalePercent: 125, brandingRevision: 8 } });
    renderCard();
    await user.click(await screen.findByRole("combobox", { name: "Organization default text size" }));
    await user.click(await screen.findByRole("option", { name: "125%" }));
    await user.click(screen.getByRole("button", { name: "Save default text size" }));
    await waitFor(() => expect(organization.updateBranding).toHaveBeenCalled());
    expect(organization.updateBranding).toHaveBeenCalledWith({
      expectedBrandingRevision: 7,
      organizationDisplayName: "Northwind",
      logoAlternativeText: "Northwind mark",
      primaryButtonBackgroundColor: "#111111",
      primaryButtonForegroundColor: "#FFFFFF",
      secondaryButtonBackgroundColor: "#FFFFFF",
      secondaryButtonForegroundColor: "#111111",
      secondaryButtonBorderColor: "#111111",
      organizationDefaultTextScalePercent: 125,
      organizationColorMode: "Light",
    });
    expect(await screen.findByText("Default text size saved")).toBeInTheDocument();
    await waitFor(() => expect(refresh).toHaveBeenCalled());
  });
});
