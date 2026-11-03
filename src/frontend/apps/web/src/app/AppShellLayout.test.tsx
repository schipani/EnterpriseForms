import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const logout = vi.fn(() => Promise.resolve());
const navigate = vi.fn();
let capabilities: string[] = [];

vi.mock("@enterpriseforms/app-runtime", () => ({
  LOGO_PATH: "/logo.png",
  PresenceHeartbeat: () => null,
  useRuntime: () => ({ coordinator: { clearSecurityContext: () => undefined } }),
  useSession: () => ({
    status: "authenticated",
    error: null,
    bootstrap: { branding: { hasLogo: false, organizationDisplayName: "Northwind" } },
    session: { displayName: "Avery Owner", organizationDisplayName: "Northwind" },
    identityProviderKind: "TestIdentityProvider",
    hasCapability: (c: string) => capabilities.includes(c),
    logout,
  }),
}));
vi.mock("@tanstack/react-router", () => ({
  Link: ({ to, children, className }: { to: string; children: React.ReactNode; className?: string }) => <a href={to} className={className}>{children}</a>,
  Navigate: () => null,
  useNavigate: () => navigate,
  useRouterState: () => false,
}));

const { AppShellLayout } = await import("./AppShellLayout");

const frames: FrameRequestCallback[] = [];

function scrollTo(y: number) {
  Object.defineProperty(window, "scrollY", { value: y, configurable: true });
  act(() => {
    window.dispatchEvent(new Event("scroll"));
    for (const frame of frames.splice(0)) {
      frame(0); // the animation frame the handler asked for
    }
  });
}

const compactBar = () => screen.getByTestId("compact-header");
// jsdom applies no stylesheet, so the inactive bar is still in the tree; the full header is queried by scope.
const fullHeaderEl = () => document.querySelector("header") as HTMLElement;

beforeEach(() => {
  vi.clearAllMocks();
  capabilities = ["records.discover", "organization.manage"];
  Object.defineProperty(window, "scrollY", { value: 0, configurable: true });
  frames.length = 0;
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => frames.push(callback));
  vi.stubGlobal("cancelAnimationFrame", () => undefined);
});

afterEach(() => vi.unstubAllGlobals());

describe("AppShellLayout header", () => {
  it("shows the full header at the top: logo and name, the primary navigation and the account menu; the compact bar is hidden and inert", () => {
    render(
      <AppShellLayout>
        <p>page</p>
      </AppShellLayout>,
    );
    expect(compactBar()).toHaveAttribute("data-active", "false");
    expect(compactBar().className).toContain("invisible"); // visibility: hidden removes it from the accessibility tree and the tab order
    expect(compactBar().className).not.toContain("visible translate-y-0");
    const fullHeader = fullHeaderEl();
    expect(fullHeader).not.toHaveAttribute("inert");
    // The full header's controls are the ones in use.
    expect(within(fullHeader).getByRole("navigation", { name: "Primary" })).toBeInTheDocument();
    expect(within(fullHeader).getByRole("button", { name: /Account menu/ })).toBeInTheDocument();
    expect(screen.getByText("Northwind", { selector: "span.text-lg" })).toBeInTheDocument();
    expect(within(within(fullHeaderEl()).getByRole("navigation", { name: "Primary" })).getAllByRole("link").map((a) => a.textContent)).toEqual(["Dashboard", "My Records", "Forms", "Uploads", "My activity", "Organization"]);
  });

  it("collapses to the pinned compact bar after about 72px and restores the full header near the top", () => {
    render(
      <AppShellLayout>
        <p>page</p>
      </AppShellLayout>,
    );
    scrollTo(60);
    expect(compactBar()).toHaveAttribute("data-active", "false"); // not yet
    scrollTo(73);
    expect(compactBar()).toHaveAttribute("data-active", "true");
    expect(compactBar().className).toContain("visible translate-y-0");
    expect(compactBar().className).not.toContain("invisible");
    // The full header is now hidden from assistive technology and inert.
    expect(document.querySelector("header")).toHaveAttribute("aria-hidden", "true");
    expect(document.querySelector("header")).toHaveAttribute("inert");
    expect(compactBar().className).toContain("fixed"); // pinned over the page; the full header keeps its place in the flow, so nothing jumps
    expect(compactBar().className).toContain("motion-reduce:transition-none");
    // The compact bar now carries the primary navigation and Sign out.
    expect(within(compactBar()).getByRole("navigation", { name: "Primary", hidden: true })).toBeInTheDocument();
    expect(within(compactBar()).getByRole("button", { name: "Sign out" })).toBeInTheDocument();
    scrollTo(50); // hysteresis: no flicker while hovering around the threshold
    expect(compactBar()).toHaveAttribute("data-active", "true");
    scrollTo(0);
    expect(compactBar()).toHaveAttribute("data-active", "false");
    expect(document.querySelector("header")).not.toHaveAttribute("aria-hidden"); // the full header is back
  });

  it("re-renders only when a threshold is crossed, not on every scroll event", () => {
    let renders = 0;
    function Probe() {
      renders += 1;
      return null;
    }
    render(
      <AppShellLayout>
        <Probe />
      </AppShellLayout>,
    );
    const before = renders;
    for (const y of [5, 10, 20, 30, 35, 38, 39]) {
      scrollTo(y);
    }
    expect(renders).toBe(before); // children are not re-rendered by scrolling inside the same state (AppShellLayout state unchanged)
  });

  it("keeps Sign out visible in the compact bar and uses the session's one logout operation", async () => {
    render(
      <AppShellLayout>
        <p>page</p>
      </AppShellLayout>,
    );
    scrollTo(200);
    await userEvent.click(within(compactBar()).getByRole("button", { name: "Sign out" }));
    expect(logout).toHaveBeenCalledTimes(1);
  });

  it("offers the primary links and, on a phone, the account actions in one accessible Menu button, which keeps the capability rules", async () => {
    capabilities = ["security.administer"];
    render(
      <AppShellLayout>
        <p>page</p>
      </AppShellLayout>,
    );
    scrollTo(200);
    const user = userEvent.setup();
    await user.click(within(compactBar()).getByRole("button", { name: "Main menu" }));
    const items = (await screen.findAllByRole("menuitem")).map((i) => i.textContent);
    expect(items).toEqual(["Dashboard", "Forms", "Uploads", "My activity", "Organization", "Security incidents", "My Profile", "Preferences"]);
    expect(items).not.toContain("My Records"); // records.discover is not held
    await user.click(screen.getByRole("menuitem", { name: "Organization" }));
    expect(navigate).toHaveBeenCalledWith({ to: "/app/organization" });
  });

  it("the account menu goes to the User area and uses the same Sign out", async () => {
    render(
      <AppShellLayout>
        <p>page</p>
      </AppShellLayout>,
    );
    const user = userEvent.setup();
    await user.click(within(fullHeaderEl()).getByRole("button", { name: /Account menu for Avery Owner/ }));
    await user.click(await screen.findByRole("menuitem", { name: "My Profile" }));
    expect(navigate).toHaveBeenCalledWith({ to: "/app/user" });
    await user.click(within(fullHeaderEl()).getByRole("button", { name: /Account menu for Avery Owner/ }));
    await user.click(await screen.findByRole("menuitem", { name: "Preferences" }));
    expect(navigate).toHaveBeenLastCalledWith({ to: "/app/user/regional" });
    await user.click(within(fullHeaderEl()).getByRole("button", { name: /Account menu for Avery Owner/ }));
    await user.click(await screen.findByRole("menuitem", { name: "Sign out" }));
    expect(logout).toHaveBeenCalledTimes(1);
  });
});
