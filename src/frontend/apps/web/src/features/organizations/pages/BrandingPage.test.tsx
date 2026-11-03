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

describe("BrandingPage", () => {
  it("asks for no reason: saving and uploading are available straight away", async () => {
    renderPage();
    const save = await screen.findByRole("button", { name: "Save branding" });
    expect(screen.queryByRole("textbox", { name: /Reason/ })).not.toBeInTheDocument();
    expect(save).toBeEnabled();
    expect(screen.getByRole("button", { name: "Upload logo (PNG)" })).toBeEnabled();
  });

  it("saves with the revision that was read and sends no reason", async () => {
    const user = userEvent.setup();
    organization.updateBranding.mockResolvedValue({ result: branding });
    renderPage();
    await user.click(await screen.findByRole("button", { name: "Save branding" }));
    await waitFor(() => expect(organization.updateBranding).toHaveBeenCalled());
    const request = organization.updateBranding.mock.calls[0]![0] as Record<string, unknown>;
    expect(request).toMatchObject({ expectedBrandingRevision: 3, organizationDisplayName: "Northwind" });
    expect(request).not.toHaveProperty("changeReason");
  });

  it("uploads a logo without a reason", async () => {
    const user = userEvent.setup();
    organization.uploadLogo.mockResolvedValue({ result: { ...branding, hasLogo: true } });
    renderPage();
    await screen.findByRole("button", { name: "Save branding" });
    const file = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], "logo.png", { type: "image/png" });
    Object.defineProperty(file, "arrayBuffer", { value: () => Promise.resolve(new ArrayBuffer(4)) });
    await user.upload(screen.getByLabelText("Choose a PNG logo"), file);
    await waitFor(() => expect(organization.uploadLogo).toHaveBeenCalled());
    expect(organization.uploadLogo).toHaveBeenCalledWith(expect.any(ArrayBuffer));
  });
});
