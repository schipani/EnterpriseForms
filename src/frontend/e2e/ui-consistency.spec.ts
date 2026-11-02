import { expect, test, type Page } from "@playwright/test";
import { signIn } from "./support";

/**
 * Visual-consistency regression (docs/ui/UI_CONSISTENCY_STANDARD.md): My Records and its Advanced Search are the reference.
 * Computed styles of the rendered controls on every other page must equal the reference, so a page that hand-rolls a textbox
 * or button (or reintroduces a 4 px radius) fails here. Asserts computed values, not pixels or screenshots.
 *
 * Intentional exceptions: unstyled text buttons and links (no background and no border), pills and status dots (fully round),
 * the 24 px checkbox box (role=checkbox, 4 px radius so the box stays square-looking), and the dense designer canvas tools, which are documented as compact controls (36 px / 32 px, same 8 px radius).
 */
type Measured = { kind: string; label: string; radius: string; height: number; fontSize: string; borderWidth: string; where: string };

async function measure(page: Page): Promise<Measured[]> {
  return page.evaluate(() => {
    const visible = (el: Element) => {
      if (el.closest("[aria-hidden=true]")) return false; // Radix renders a hidden native select beside each dropdown
      const r = (el as HTMLElement).getBoundingClientRect();
      const s = getComputedStyle(el);
      return r.width > 0 && r.height > 0 && s.visibility !== "hidden" && s.display !== "none";
    };
    const out: Measured[] = [];
    const take = (kind: string, el: Element) => {
      const s = getComputedStyle(el);
      const r = (el as HTMLElement).getBoundingClientRect();
      const label = ((el.getAttribute("aria-label") ?? el.textContent ?? "").trim().slice(0, 30) + " [" + (typeof el.className === "string" ? el.className : "").slice(0, 70) + "]");
      out.push({ kind, label, radius: s.borderTopLeftRadius, height: Math.round(r.height * 10) / 10, fontSize: s.fontSize, borderWidth: s.borderTopWidth, where: el.closest("[role=dialog]") ? "dialog" : "page" });
    };
    document.querySelectorAll("input:not([type=checkbox]):not([type=radio]):not([type=file]):not([type=hidden]):not([type=range]), textarea").forEach((el) => visible(el) && take("textbox", el));
    document.querySelectorAll("select, [role=combobox]").forEach((el) => visible(el) && take("dropdown", el));
    document.querySelectorAll("button").forEach((el) => {
      if (!visible(el) || el.getAttribute("role") === "tab" || el.getAttribute("role") === "checkbox" || el.getAttribute("role") === "menuitem" || el.getAttribute("role") === "option") return;
      const s = getComputedStyle(el);
      const unstyled = s.backgroundColor === "rgba(0, 0, 0, 0)" && s.borderTopWidth === "0px";
      if (!unstyled) take("button", el);
    });
    return out;
  });
}

const pages: Array<[string, string]> = [
  ["Dashboard", "/app/dashboard"],
  ["Forms", "/app/forms"],
  ["New form", "/app/forms/new"],
  ["Activity", "/app/activity"],
  ["Upload Center", "/app/uploads"],
  ["Preferences", "/app/me/preferences"],
  ["Organization overview", "/app/organization"],
  ["People", "/app/organization/users"],
  ["Teams", "/app/organization/teams"],
  ["Branding", "/app/organization/branding"],
  ["Data protection", "/app/organization/data-protection"],
  ["Security Administrators", "/app/organization/security-administrators"],
  ["Search approvals", "/app/organization/search-approvals"],
];

test.describe("UI consistency against My Records / Advanced Search", () => {
  test("every textbox, dropdown and button on every page matches the reference computed styles", async ({ page }) => {
    await signIn(page, "owner");

    // Reference: My Records with the Advanced Search panel (always visible).
    await page.goto("/app/records");
    await expect(page.getByRole("textbox", { name: "Search records" })).toBeVisible();
    const reference = await measure(page);
    const refTextbox = reference.find((m) => m.kind === "textbox");
    const refButton = reference.find((m) => m.kind === "button");
    expect(refTextbox, "reference textbox").toBeTruthy();
    expect(refButton, "reference button").toBeTruthy();
    expect(refTextbox!.radius).toBe("8px");
    expect(refTextbox!.height).toBe(40);
    expect(refTextbox!.fontSize).toBe("14px");
    expect(refButton!.radius).toBe("8px");

    const offenders: string[] = [];
    const check = (name: string, items: Measured[]) => {
      for (const m of items) {
        if (m.radius !== "8px") offenders.push(`${name}: ${m.kind} "${m.label}" radius ${m.radius} (expected 8px)`);
        if (m.kind === "textbox" && m.where === "page" && (m.height !== refTextbox!.height || m.fontSize !== refTextbox!.fontSize) && m.height !== 36 && m.height < 60)
          offenders.push(`${name}: textbox "${m.label}" ${m.height}px/${m.fontSize} (expected ${refTextbox!.height}px/${refTextbox!.fontSize})`);
        if (m.kind === "textbox" && m.borderWidth !== refTextbox!.borderWidth) offenders.push(`${name}: textbox "${m.label}" border ${m.borderWidth} (expected ${refTextbox!.borderWidth})`);
      }
    };
    check("My Records", reference);

    for (const [name, path] of pages) {
      await page.goto(path);
      await expect(page.getByRole("heading", { level: 1 }).first()).toBeVisible();
      check(name, await measure(page));
    }

    // Record entry (renderer), and the designer with its Add field dialog.
    await page.goto("/app/forms");
    const design = page.locator('a[href*="/design"]').first();
    if (await design.count()) {
      await design.click();
      await expect(page.getByRole("tab", { name: /^Fields/ })).toBeVisible();
      check("Form Designer", await measure(page));
      await page.getByRole("tab", { name: /^Fields/ }).click();
      await page.getByRole("button", { name: "Add field", exact: true }).click();
      await expect(page.getByRole("dialog", { name: "Add field" })).toBeVisible();
      check("Add field dialog", await measure(page));
      await page.keyboard.press("Escape");
    }

    expect(offenders, offenders.join("\n")).toEqual([]);
  });
});
