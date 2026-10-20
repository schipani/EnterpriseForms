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

  it("places the designer's help text above the input and the encryption notice below it, both announced with the input", () => {
    const encryptedFields = fields.map((field) => (field.fieldKey === "given_name" ? { ...field, applicationEncrypted: true } : field));
    const helpLayout: FormLayout = { ...layout, sections: [{ ...layout.sections[0]!, elements: [{ ...layout.sections[0]!.elements[0]!, helpText: "As on the charge sheet" } as FormLayout["sections"][number]["elements"][number]] }] };
    render(<FormRenderer layout={helpLayout} fields={encryptedFields} values={{}} breakpoint="desktop" />);
    const input = screen.getByLabelText(/Given name/);
    const help = screen.getByText("As on the charge sheet");
    const note = screen.getByText("Encrypted for privacy — not searchable.");
    expect(help.compareDocumentPosition(input) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy(); // help precedes the input
    expect(input.compareDocumentPosition(note) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy(); // the notice follows it
    expect(input.getAttribute("aria-describedby")!.split(" ")).toEqual([help.id, note.id]);
  });
});
