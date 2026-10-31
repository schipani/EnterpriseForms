import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ColumnChart, DonutChart, axisLabelIndices } from "./Charts";
import { KpiTile, Pager } from "./Collections";
import { formatCalendarDate, localDateRange } from "./dates";

describe("Pager", () => {
  it("shows position, totals and page count, and disables navigation at both ends", () => {
    const onNext = vi.fn();
    const { rerender } = render(<Pager label="Upload pages" noun="uploads" pageStart={1} itemCount={15} totalCount={32} pageSize={15} hasPrevious={false} hasNext onPrevious={() => undefined} onNext={onNext} />);
    const nav = screen.getByRole("navigation", { name: "Upload pages" });
    expect(nav).toHaveTextContent("Showing 1–15 of 32 uploads");
    expect(nav).toHaveTextContent("Page 1 of 3");
    expect(within(nav).getByRole("button", { name: "Previous" })).toBeDisabled();
    fireEvent.click(within(nav).getByRole("button", { name: "Next" }));
    expect(onNext).toHaveBeenCalledOnce();

    rerender(<Pager label="Upload pages" noun="uploads" pageStart={31} itemCount={2} totalCount={32} pageSize={15} hasPrevious hasNext={false} onPrevious={() => undefined} onNext={onNext} />);
    expect(nav).toHaveTextContent("Showing 31–32 of 32 uploads");
    expect(nav).toHaveTextContent("Page 3 of 3");
    expect(within(nav).getByRole("button", { name: "Next" })).toBeDisabled();
    expect(within(nav).getByRole("button", { name: "Previous" })).toBeEnabled();
  });

  it("states an empty list without a negative range", () => {
    render(<Pager label="Pages" noun="forms" pageStart={0} itemCount={0} totalCount={0} pageSize={15} hasPrevious={false} hasNext={false} onPrevious={() => undefined} onNext={() => undefined} />);
    expect(screen.getByRole("navigation", { name: "Pages" })).toHaveTextContent("No forms");
    expect(screen.getByRole("navigation", { name: "Pages" })).toHaveTextContent("Page 1 of 1");
  });
});

describe("KpiTile", () => {
  it("never shows a missing number as zero", () => {
    const { rerender } = render(<KpiTile label="Draft" value="0" loading />);
    expect(screen.getByLabelText("Draft: loading")).toBeInTheDocument();
    rerender(<KpiTile label="Draft" value="0" failed />);
    expect(screen.getByText("Unavailable")).toBeInTheDocument();
    expect(screen.queryByText("0")).not.toBeInTheDocument();
  });
});

describe("DonutChart", () => {
  it("lists every category with its count and share, and the chart total matches the data", () => {
    render(
      <DonutChart
        title="Records by status"
        totalLabel="records"
        slices={[
          { key: "d", label: "Draft", value: 3, color: "chart-1" },
          { key: "s", label: "Submitted", value: 1, color: "chart-2" },
          { key: "a", label: "Archived", value: 0, color: "chart-3" },
        ]}
      />,
    );
    const legend = screen.getByRole("list", { name: "Records by status legend" });
    const rows = within(legend).getAllByRole("listitem");
    expect(rows.map((row) => row.textContent)).toEqual(["Draft375%", "Submitted125%", "Archived00%"]);
    expect(screen.getByRole("img")).toHaveAccessibleName("Records by status: Draft 3, Submitted 1, Archived 0; total 4.");
  });

  it("explains an empty dataset", () => {
    render(<DonutChart title="Incidents by status" totalLabel="incidents" emptyMessage="No incidents recorded." slices={[{ key: "o", label: "Open", value: 0, color: "chart-2" }]} />);
    expect(screen.getByText("No incidents recorded.")).toBeInTheDocument();
  });
});

describe("ColumnChart", () => {
  const buckets = [
    { key: "2026-10-28", label: "Oct 28", fullLabel: "Oct 28, 2026" },
    { key: "2026-10-29", label: "Oct 29", fullLabel: "Oct 29, 2026" },
    { key: "2026-10-30", label: "Oct 30", fullLabel: "Oct 30, 2026" },
  ];

  it("has a table alternative whose totals match the series", () => {
    render(
      <ColumnChart
        title="Uploads"
        valueNoun="uploads"
        stacked
        buckets={buckets}
        series={[
          { key: "ok", label: "Completed", color: "chart-3", values: [1, 0, 4] },
          { key: "bad", label: "Failed", color: "chart-2", values: [0, 2, 1] },
        ]}
      />,
    );
    const table = screen.getByRole("table", { name: "Uploads", hidden: true });
    const rows = within(table).getAllByRole("row", { hidden: true }).slice(1);
    expect(rows.map((row) => Array.from(row.children).map((cell) => cell.textContent))).toEqual([
      ["Oct 28, 2026", "1", "0", "1"],
      ["Oct 29, 2026", "0", "2", "2"],
      ["Oct 30, 2026", "4", "1", "5"],
    ]);
    expect(screen.getByRole("group", { name: /^Uploads\. 8 uploads in total/ })).toBeInTheDocument();
  });

  it("is readable by keyboard: arrow keys move the tooltip between periods", () => {
    render(<ColumnChart title="Record activity" valueNoun="record events" buckets={buckets} series={[{ key: "c", label: "Created", color: "chart-1", values: [2, 5, 0] }, { key: "s", label: "Submitted", color: "chart-2", values: [1, 0, 0] }]} />);
    const plot = screen.getByRole("group", { name: /^Record activity./ });
    fireEvent.focus(plot);
    expect(screen.getByRole("status")).toHaveTextContent("Oct 30, 2026");
    fireEvent.keyDown(plot, { key: "ArrowLeft" });
    expect(screen.getByRole("status")).toHaveTextContent("Oct 29, 2026Created5Submitted0");
    fireEvent.keyDown(plot, { key: "Home" });
    expect(screen.getByRole("status")).toHaveTextContent("Oct 28, 2026");
  });

  it("says when a period has no activity", () => {
    render(<ColumnChart title="Record activity" valueNoun="record events" emptyMessage="Nothing happened." buckets={buckets} series={[{ key: "c", label: "Created", color: "chart-1", values: [0, 0, 0] }]} />);
    expect(screen.getByText("Nothing happened.")).toBeInTheDocument();
  });
});

describe("date helpers", () => {
  it("turns an inclusive local date range into a half-open instant range", () => {
    const range = localDateRange("2026-10-01", "2026-10-03");
    expect(new Date(range.from!).getDate()).toBe(1);
    expect(new Date(range.before!).getDate()).toBe(4);
    expect(localDateRange("", "")).toEqual({});
  });

  it("formats a calendar date without shifting it across time zones", () => {
    expect(formatCalendarDate("2026-01-01", "long")).toMatch(/2026/);
    expect(formatCalendarDate("2026-01-01")).toMatch(/1/);
  });
});

describe("axisLabelIndices", () => {
  const days = Array.from({ length: 30 }, (_, i) => `Oct ${i + 1}`);

  it("always labels the first and the last period and never places two labels closer than one step", () => {
    for (const width of [200, 320, 600, 1200]) {
      const indices = axisLabelIndices(days, width);
      expect(indices[0]).toBe(0);
      expect(indices.at(-1)).toBe(29);
      const step = Math.ceil((6 * 7 + 16) / (width / 30));
      for (let i = 1; i < indices.length; i++) {
        expect(indices[i]! - indices[i - 1]!).toBeGreaterThanOrEqual(Math.min(step, 29));
      }
    }
  });

  it("labels every period when there is room, and about seven without a measurement", () => {
    expect(axisLabelIndices(days.slice(0, 7), 1200)).toEqual([0, 1, 2, 3, 4, 5, 6]);
    expect(axisLabelIndices(days, null).length).toBeLessThanOrEqual(8);
    expect(axisLabelIndices(["Oct 1"], 100)).toEqual([0]);
    expect(axisLabelIndices([], 100)).toEqual([]);
  });
});
