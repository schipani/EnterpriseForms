import { expect, test, type Page } from "@playwright/test";
import { signIn } from "./support";

/**
 * Dashboard chart layout (owner acceptance defect, 2026-10-31): at every supported width the three charts stay inside their
 * cards — donut legend entries never overlap (colour, status, count and percentage each readable), column charts keep all
 * bars, axis labels and the final period inside the card — and the page never scrolls horizontally. 640 px and 320 px stand
 * in for 200% and 400% zoom of a 1280 px window. Screenshots are written to test-results/dashboard-charts/ for visual review.
 */

const VIEWPORTS = [
  { name: "320-mobile", width: 320, height: 900 },
  { name: "640-zoom200", width: 640, height: 900 },
  { name: "768-tablet", width: 768, height: 1000 },
  { name: "1024-desktop", width: 1024, height: 900 },
  { name: "1440-desktop", width: 1440, height: 900 },
  { name: "1920-wide", width: 1920, height: 1080 },
];

type Box = { left: number; right: number; top: number; bottom: number; text: string; role: string };

/** Every visible element of the card's figure, plus the card box, measured in the page. */
async function measure(page: Page, cardTitle: string) {
  return page.evaluate((title) => {
    const heading = [...document.querySelectorAll("h2")].find((h) => h.textContent?.trim() === title);
    const card = heading?.closest("section");
    if (!card) {
      return null;
    }
    // The element box unioned with its own rendered text nodes: overflowing text (nowrap, shrunk flex items) extends past its box.
    const box = (el: Element, role: string): Box => {
      const r = el.getBoundingClientRect();
      let { left, right, top, bottom } = r;
      for (const node of el.childNodes) {
        if (node.nodeType !== Node.TEXT_NODE || (node.textContent ?? "").trim().length === 0) {
          continue;
        }
        const range = document.createRange();
        range.selectNode(node);
        const t = range.getBoundingClientRect();
        if (t.width > 0) {
          left = Math.min(left, t.left);
          right = Math.max(right, t.right);
          top = Math.min(top, t.top);
          bottom = Math.max(bottom, t.bottom);
        }
      }
      return { left, right, top, bottom, text: (el.textContent ?? "").trim(), role };
    };
    const visible = (el: Element) => {
      const r = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      return r.width > 0 && r.height > 0 && style.visibility !== "hidden" && style.display !== "none" && !el.closest("details:not([open]) > div");
    };
    const figure = card.querySelector("figure");
    const elements = figure ? [...figure.querySelectorAll("*")].filter((el) => visible(el) && !el.closest(".sr-only") && !el.classList.contains("sr-only")).map((el) => box(el, el.tagName)) : [];
    const legendRows = [...card.querySelectorAll("ul[aria-label$='legend'] > li")].map((li) => ({
      row: box(li, "li"),
      parts: [...li.children].filter(visible).map((child) => box(child, child.tagName)),
    }));
    const axisLabels = [...card.querySelectorAll("[data-chart-axis-label]")].filter(visible).map((el) => box(el, "axis"));
    const bars = [...card.querySelectorAll("[data-chart-bucket]")].map((el) => box(el, "bucket"));
    const style = getComputedStyle(card);
    const contentLeft = card.getBoundingClientRect().left + parseFloat(style.borderLeftWidth);
    const contentRight = card.getBoundingClientRect().right - parseFloat(style.borderRightWidth);
    return { card: { ...box(card, "card"), left: contentLeft, right: contentRight }, elements, legendRows, axisLabels, bars };
  }, cardTitle);
}

const overlaps = (a: Box, b: Box) => a.left < b.right - 0.5 && b.left < a.right - 0.5 && a.top < b.bottom - 0.5 && b.top < a.bottom - 0.5;

// The owner of the shared test organization has an empty dashboard; the clerk owns records and uploads, so bars, counts
// and percentages are exercised too. The 90-day period uses the longest bucket list.
// The Security Administrator's dashboard adds the security monitoring donut and column chart (same components).
const CASES = VIEWPORTS.flatMap((viewport) => [
  { viewport, persona: "owner" as const, period: null },
  { viewport, persona: "clerk" as const, period: null },
  { viewport, persona: "clerk" as const, period: "Last 90 days" },
  { viewport, persona: "security" as const, period: null },
]);

for (const { viewport, persona, period } of CASES) {
  test(`charts stay inside their cards at ${viewport.name} (${persona}${period ? ", " + period : ""})`, async ({ page }) => {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    await signIn(page, persona);
    if (period) {
      await page.getByRole("combobox", { name: "Period" }).click();
      await page.getByRole("option", { name: period }).click();
    }
    await expect(page.getByRole("list", { name: "Records by status legend" })).toBeVisible();
    await expect(page.getByRole("group", { name: /^Record activity/ })).toBeVisible();
    await expect(page.getByRole("group", { name: /^Uploads by outcome/ })).toBeVisible();
    await page.waitForTimeout(300);

    const problems: string[] = [];
    const titles = ["Records by status", "Record activity", "Upload activity", ...(persona === "security" ? ["Incidents by status", "Incidents detected over time"] : [])];
    if (persona === "security") {
      await expect(page.getByRole("list", { name: "Incidents by status legend" })).toBeVisible();
    }
    for (const title of titles) {
      const m = await measure(page, title);
      expect(m, `${title} card`).not.toBeNull();
      for (const el of m!.elements) {
        if (el.left < m!.card.left - 1 || el.right > m!.card.right + 1) {
          problems.push(`${title}: <${el.role}> "${el.text.slice(0, 30)}" spans ${el.left.toFixed(0)}–${el.right.toFixed(0)} outside card ${m!.card.left.toFixed(0)}–${m!.card.right.toFixed(0)}`);
        }
      }
      for (const { row, parts } of m!.legendRows) {
        for (let i = 0; i < parts.length; i++) {
          for (let j = i + 1; j < parts.length; j++) {
            if (overlaps(parts[i]!, parts[j]!)) {
              problems.push(`${title}: legend parts overlap in "${row.text}": "${parts[i]!.text}" / "${parts[j]!.text}"`);
            }
          }
        }
      }
      const rows = m!.legendRows.map((r) => r.row);
      for (let i = 0; i < rows.length; i++) {
        for (let j = i + 1; j < rows.length; j++) {
          if (overlaps(rows[i]!, rows[j]!)) {
            problems.push(`${title}: legend rows overlap: "${rows[i]!.text}" / "${rows[j]!.text}"`);
          }
        }
      }
      if (!title.endsWith("by status")) {
        const labels = m!.axisLabels.filter((l) => l.text.length > 0);
        expect(labels.length, `${title} axis labels`).toBeGreaterThan(0);
        for (let i = 0; i < labels.length - 1; i++) {
          if (labels[i]!.right > labels[i + 1]!.left + 0.5) {
            problems.push(`${title}: axis labels overlap: "${labels[i]!.text}" / "${labels[i + 1]!.text}"`);
          }
        }
        const lastBucket = m!.bars.at(-1)!;
        if (lastBucket.right > m!.card.right + 1 || lastBucket.right - lastBucket.left < 1) {
          problems.push(`${title}: final period not fully visible (${lastBucket.left.toFixed(0)}–${lastBucket.right.toFixed(0)})`);
        }
        const lastLabel = labels.at(-1)!;
        if (lastLabel.right > m!.card.right + 1) {
          problems.push(`${title}: final axis label "${lastLabel.text}" is outside the card`);
        }
      }
    }
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    if (overflow > 0) {
      problems.push(`page scrolls horizontally by ${overflow}px`);
    }
    await page.locator("main").screenshot({ path: `test-results/dashboard-charts/${viewport.name}-${persona}${period ? "-90d" : ""}.png` });
    expect(problems, problems.join("\n")).toEqual([]);
  });
}
