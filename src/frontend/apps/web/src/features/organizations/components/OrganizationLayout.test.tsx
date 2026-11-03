import { render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

let capabilities: string[] = [];

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useSession: () => ({ hasCapability: (c: string) => capabilities.includes(c) }),
}));
vi.mock("@tanstack/react-router", () => ({ Link: ({ to, children, className }: { to: string; children: React.ReactNode; className?: string }) => <a href={to} className={className}>{children}</a> }));

const { OrganizationLayout } = await import("./OrganizationLayout");

const renderLayout = () =>
  render(
    <OrganizationLayout title="Overview">
      <p>content</p>
    </OrganizationLayout>,
  );

beforeEach(() => {
  capabilities = ["organization.manage"];
});

describe("OrganizationLayout navigation", () => {
  it("renders three labelled groups, each a list of links in the specified order", () => {
    renderLayout();
    const nav = screen.getByRole("navigation", { name: "Organization" });
    const groups = within(nav).getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(groups).toEqual(["Organization", "Appearance & Localization", "Security & Governance"]);
    const lists = within(nav).getAllByRole("list");
    expect(lists.map((l) => within(l).getAllByRole("link").map((a) => a.textContent))).toEqual([
      ["Overview", "People", "Teams"],
      ["Branding", "Regional Settings", "Language", "Text Size"],
      ["Data Protection", "Security Administrators"],
    ]);
    // Each list is named by its group heading, and the headings are text, not links.
    expect(lists.map((l) => l.getAttribute("aria-labelledby"))).toEqual(groups.map((_, i) => within(nav).getAllByRole("heading", { level: 2 })[i]!.id));
    expect(within(nav).queryByRole("link", { name: "Appearance & Localization" })).not.toBeInTheDocument();
  });

  it("hides the icons from assistive technology and sizes group icons 18px and item icons 16px", () => {
    renderLayout();
    const nav = screen.getByRole("navigation", { name: "Organization" });
    const icons = [...nav.querySelectorAll("svg")];
    expect(icons).toHaveLength(3 + 9);
    expect(icons.every((i) => i.getAttribute("aria-hidden") === "true" && i.getAttribute("stroke-width") === "2")).toBe(true);
    expect(icons.slice(0, 1)[0]!.getAttribute("width")).toBe("18");
    expect(nav.querySelectorAll("li svg")[0]!.getAttribute("width")).toBe("16");
    expect(icons.every((i) => i.closest("a, h2"))).toBe(true);
  });

  it("uses the exact Lucide icons for the groups and items", () => {
    renderLayout();
    const nav = screen.getByRole("navigation", { name: "Organization" });
    const classes = [...nav.querySelectorAll("svg")].map((s) => [...s.classList].find((c) => c.startsWith("lucide-") && c !== "lucide")?.replace(/^lucide-/, ""));
    expect(classes).toEqual([
      "building-complex", // Lucide 1.x registers the specified name "building-2" as an alias of this icon (same glyph)
      "layout-dashboard", "users", "users-round",
      "palette", "paintbrush", "globe", "languages", "accessibility",
      "shield-check", "lock-keyhole", "shield-user",
    ]);
  });

  it("omits a group with no visible item instead of rendering an empty heading", () => {
    capabilities = ["security.administer"];
    renderLayout();
    const nav = screen.getByRole("navigation", { name: "Organization" });
    expect(within(nav).getAllByRole("heading", { level: 2 }).map((h) => h.textContent)).toEqual(["Organization", "Security & Governance"]);
    expect(within(nav).getByRole("link", { name: "Search Approvals" })).toBeInTheDocument();
    expect(within(nav).queryByRole("link", { name: "Branding" })).not.toBeInTheDocument();
  });

  it("shows no navigation and no group headings without any administration capability", () => {
    capabilities = [];
    renderLayout();
    expect(screen.queryByRole("navigation", { name: "Organization" })).not.toBeInTheDocument();
    expect(screen.getByText("No permission")).toBeInTheDocument();
  });
});
