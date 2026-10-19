import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { FormRenderer } from "./FormRenderer";
import { emptyLayout, type FormLayout } from "./layout";
import type { RendererField } from "./validation";

const nameId = "0199a2b3-c4d5-7e6f-8123-456789abcdef";
const custodyId = "0199a2b3-c4d5-7e6f-8123-456789abcdf0";
const fields: RendererField[] = [
  { fieldDefinitionId: nameId, fieldKey: "given_name", fieldDisplayName: "Given name", fieldDataType: "String", options: [] },
  { fieldDefinitionId: custodyId, fieldKey: "in_custody", fieldDisplayName: "In custody", fieldDataType: "Boolean", options: [] },
  { fieldDefinitionId: "0199a2b3-c4d5-7e6f-8123-456789abcdf9", fieldKey: "hidden", fieldDisplayName: "Hidden", fieldDataType: "String", options: [] },
];
const layout: FormLayout = {
  ...emptyLayout("Accused"),
  sections: [
    {
      sectionKey: "main",
      title: "Identity",
      gridColumns: 4,
      elements: [
        { kind: "field", fieldDefinitionId: nameId, control: "text", label: "Given name", required: true, layout: { columnSpan: 2, startOnNewRow: false } },
        { kind: "field", fieldDefinitionId: custodyId, control: "checkbox", label: "In custody", required: false, layout: { columnSpan: 2, startOnNewRow: false } },
        { kind: "field", fieldDefinitionId: "0199a2b3-c4d5-7e6f-8123-456789abcde0", control: "text", label: "Unauthorized field", required: false, layout: { columnSpan: 4, startOnNewRow: true } },
      ],
    },
  ],
};

describe("FormRenderer", () => {
  it("renders labelled controls, omits fields the projection did not authorize and reports changes by field key", async () => {
    const onChange = vi.fn();
    render(<FormRenderer layout={layout} fields={fields} values={{ given_name: "Av" }} onChange={onChange} breakpoint="desktop" />);
    const name = screen.getByLabelText(/Given name/);
    expect(name).toHaveValue("Av");
    await userEvent.type(name, "a");
    expect(onChange).toHaveBeenLastCalledWith("given_name", "Ava");
    await userEvent.click(screen.getByLabelText("In custody"));
    expect(onChange).toHaveBeenLastCalledWith("in_custody", "true");
    expect(screen.queryByText("Unauthorized field")).toBeNull();
    expect(screen.getByRole("group", { name: "Identity" })).toBeInTheDocument();
  });

  it("shows errors next to the control and read-only values without inputs", () => {
    const { rerender } = render(<FormRenderer layout={layout} fields={fields} values={{}} errors={{ given_name: "Given name is required" }} breakpoint="phone" />);
    expect(screen.getByRole("alert")).toHaveTextContent("Given name is required");
    expect(screen.getByLabelText(/Given name/)).toHaveAttribute("aria-invalid", "true");
    rerender(<FormRenderer layout={layout} fields={fields} values={{ given_name: "Ava", in_custody: "false" }} readOnly breakpoint="phone" />);
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(screen.getByText("Ava")).toBeInTheDocument();
    expect(screen.getByText("No")).toBeInTheDocument();
  });
});
