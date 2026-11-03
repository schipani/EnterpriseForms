import { FALLBACK_SETTINGS, setActiveFormatting } from "@enterpriseforms/ui";
import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { FormRenderer, fromLocalDateTimeInput, toLocalDateTimeInput } from "./FormRenderer";
import { emptyLayout, type FormLayout } from "./layout";
import type { RendererField } from "./validation";

const id = "0199a2b3-c4d5-7e6f-8123-456789abcd01";
const fields: RendererField[] = [{ fieldDefinitionId: id, fieldKey: "appointment", fieldDisplayName: "Appointment", fieldDataType: "DateTime", options: [] }];
const layout: FormLayout = { ...emptyLayout("Visit"), sections: [{ sectionKey: "main", title: "Visit", gridColumns: 4, elements: [{ kind: "field", fieldDefinitionId: id, control: "dateTime", label: "Appointment", required: false, layout: { columnSpan: 4, startOnNewRow: true } }] }] };

afterEach(() => setActiveFormatting(FALLBACK_SETTINGS));

describe("zone-aware date-time entry", () => {
  it("converts between the stored UTC instant and wall-clock time in the effective zone, not the browser's", () => {
    expect(toLocalDateTimeInput("2026-07-01T14:15:00Z", "America/Winnipeg")).toBe("2026-07-01T09:15");
    expect(toLocalDateTimeInput("2026-07-01T14:15:00Z", "America/Toronto")).toBe("2026-07-01T10:15");
    expect(fromLocalDateTimeInput("2026-07-01T09:15", "America/Winnipeg")).toEqual({ kind: "ok", wire: "2026-07-01T14:15:00Z" });
    expect(fromLocalDateTimeInput("", "America/Toronto")).toEqual({ kind: "empty" });
  });

  it("reports a DST gap and a fold instead of choosing silently", () => {
    expect(fromLocalDateTimeInput("2026-03-08T02:30", "America/Toronto")).toEqual({ kind: "gap" });
    expect(fromLocalDateTimeInput("2026-11-01T01:30", "America/Toronto")).toEqual({ kind: "ambiguous", earlier: "2026-11-01T05:30:00Z", later: "2026-11-01T06:30:00Z" });
  });

  it("rejects a nonexistent time with a message and emits no value", () => {
    const onChange = vi.fn();
    render(<FormRenderer layout={layout} fields={fields} values={{}} onChange={onChange} breakpoint="desktop" />);
    fireEvent.change(screen.getByLabelText(/Appointment/), { target: { value: "2026-03-08T02:30" } });
    expect(onChange).toHaveBeenLastCalledWith("appointment", "");
    expect(screen.getByRole("alert")).toHaveTextContent("does not exist in America/Toronto");
  });

  it("makes the user choose between the two occurrences of an ambiguous time", async () => {
    const onChange = vi.fn();
    render(<FormRenderer layout={layout} fields={fields} values={{}} onChange={onChange} breakpoint="desktop" />);
    fireEvent.change(screen.getByLabelText(/Appointment/), { target: { value: "2026-11-01T01:30" } });
    expect(onChange).toHaveBeenLastCalledWith("appointment", "");
    expect(screen.getByRole("alert")).toHaveTextContent("happens twice");
    await userEvent.click(screen.getByRole("button", { name: /^Second time \(UTC−05:00\)/ }));
    expect(onChange).toHaveBeenLastCalledWith("appointment", "2026-11-01T06:30:00Z");
    await userEvent.click(screen.getByRole("button", { name: /^First time \(UTC−04:00\)/ }));
    expect(onChange).toHaveBeenLastCalledWith("appointment", "2026-11-01T05:30:00Z");
  });

  it("shows the stored instant as wall-clock time in the user's explicit zone", () => {
    setActiveFormatting({ ...FALLBACK_SETTINGS, timeZone: "Europe/Rome" });
    render(<FormRenderer layout={layout} fields={fields} values={{ appointment: "2026-07-01T14:15:00Z" }} onChange={vi.fn()} breakpoint="desktop" />);
    expect(screen.getByLabelText(/Appointment/)).toHaveValue("2026-07-01T16:15");
  });
});
