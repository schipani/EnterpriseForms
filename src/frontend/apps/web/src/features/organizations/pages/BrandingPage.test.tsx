import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const organization = { branding: vi.fn(), updateBranding: vi.fn(), uploadLogo: vi.fn() };
const refresh = vi.fn();

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => ({ organization }),
  useSession: () => ({ hasCapability: (c: string) => c === "organization.manage", refresh }),
}));
vi.mock("@tanstack/react-router", () => ({ Link: ({ to, children }: { to: string; children: React.ReactNode }) => <a href={to}>{children}</a> }));

const { BrandingPage } = await import("./BrandingPage");

const branding = {
  organizationDisplayName: "Northwind",
  hasLogo: false,
  logoAlternativeText: null,
  primaryButtonBackgroundColor: "#111111",
  primaryButtonForegroundColor: "#FFFFFF",
  secondaryButtonBackgroundColor: "#FFFFFF",
  secondaryButtonForegroundColor: "#111111",
  secondaryButtonBorderColor: "#111111",
  organizationDefaultTextScalePercent: 100,
  organizationColorMode: "Light",
  brandingRevision: 3,
};

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <BrandingPage />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  organization.branding.mockResolvedValue({ result: branding });
  vi.stubGlobal("ResizeObserver", class { observe() {} unobserve() {} disconnect() {} });
});

describe("BrandingPage change reason", () => {
  it("cannot save or upload a logo until a reason of at least three characters is entered", async () => {
    const user = userEvent.setup();
    renderPage();
    const save = await screen.findByRole("button", { name: "Save branding" });
    const upload = screen.getByRole("button", { name: "Upload logo (PNG)" });
    expect(save).toBeDisabled();
    expect(upload).toBeDisabled();
    expect(screen.getByText(/Enter a reason for change above to upload or replace the logo/)).toBeInTheDocument();
    await user.type(screen.getByRole("textbox", { name: /Reason for change/ }), "ab");
    expect(save).toBeDisabled();
    await user.type(screen.getByRole("textbox", { name: /Reason for change/ }), "c");
    expect(save).toBeEnabled();
    expect(upload).toBeEnabled();
  });

  it("sends the trimmed reason with the branding update and the revision that was read", async () => {
    const user = userEvent.setup();
    organization.updateBranding.mockResolvedValue({ result: branding });
    renderPage();
    await user.type(await screen.findByRole("textbox", { name: /Reason for change/ }), "  New brand colours  ");
    await user.click(screen.getByRole("button", { name: "Save branding" }));
    await waitFor(() => expect(organization.updateBranding).toHaveBeenCalled());
    expect(organization.updateBranding).toHaveBeenCalledWith(expect.objectContaining({ expectedBrandingRevision: 3, organizationDisplayName: "Northwind", changeReason: "New brand colours" }));
  });

  it("sends the reason with a logo upload", async () => {
    const user = userEvent.setup();
    organization.uploadLogo.mockResolvedValue({ result: { ...branding, hasLogo: true } });
    renderPage();
    await user.type(await screen.findByRole("textbox", { name: /Reason for change/ }), "Add the office logo");
    const file = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], "logo.png", { type: "image/png" });
    Object.defineProperty(file, "arrayBuffer", { value: () => Promise.resolve(new ArrayBuffer(4)) });
    await user.upload(screen.getByLabelText("Choose a PNG logo"), file);
    await waitFor(() => expect(organization.uploadLogo).toHaveBeenCalled());
    expect(organization.uploadLogo).toHaveBeenCalledWith(expect.any(ArrayBuffer), "Add the office logo");
  });
});
