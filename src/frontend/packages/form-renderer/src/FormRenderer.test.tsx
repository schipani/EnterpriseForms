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

describe("FormRenderer dropdowns, rules and field behavior (Phase C2)", () => {
  const kindId = "0199a2b3-c4d5-7e6f-8123-456789abe001";
  const companyId = "0199a2b3-c4d5-7e6f-8123-456789abe002";
  const topicsId = "0199a2b3-c4d5-7e6f-8123-456789abe003";
  const statusId = "0199a2b3-c4d5-7e6f-8123-456789abe004";
  const noteId = "0199a2b3-c4d5-7e6f-8123-456789abe005";
  const option = (key: string, text: string, order: number, isActive = true) => ({ fieldOptionId: `id-${key}`, optionKey: key, optionDisplayText: text, optionSortOrder: order, isActive });
  const richFields: RendererField[] = [
    { fieldDefinitionId: kindId, fieldKey: "kind", fieldDisplayName: "Kind", fieldDataType: "Option", options: [option("person", "Person", 1), option("company", "Company", 2)] },
    { fieldDefinitionId: companyId, fieldKey: "company_name", fieldDisplayName: "Company name", fieldDataType: "String", options: [] },
    { fieldDefinitionId: topicsId, fieldKey: "topics", fieldDisplayName: "Topics", fieldDataType: "Option", optionSelectionMode: "Multiple", options: [option("red", "Red", 1), option("green", "Green", 2), option("blue", "Blue", 3, false)] },
    { fieldDefinitionId: statusId, fieldKey: "status", fieldDisplayName: "Status", fieldDataType: "Option", options: [option("draft", "Draft", 1)] },
    { fieldDefinitionId: noteId, fieldKey: "note", fieldDisplayName: "Reference note", fieldDataType: "String", options: [] },
  ];
  const base = { required: false, layout: { columnSpan: 4 as const, startOnNewRow: false } };
  const richLayout = (): FormLayout => ({
    ...emptyLayout("Rich"),
    sections: [
      {
        sectionKey: "main",
        title: "Main",
        gridColumns: 4,
        elements: [
          { ...base, kind: "field", fieldDefinitionId: kindId, control: "select", label: "Kind", required: true },
          { ...base, kind: "field", fieldDefinitionId: companyId, control: "text", label: "Company name", visibleWhen: { op: "equals", fieldDefinitionId: kindId, value: "company" }, requiredWhen: { op: "isEmpty", fieldDefinitionId: kindId } },
          { ...base, kind: "field", fieldDefinitionId: topicsId, control: "multiselect", label: "Topics" },
          { ...base, kind: "field", fieldDefinitionId: statusId, control: "select", label: "Status", readOnly: true },
          { ...base, kind: "field", fieldDefinitionId: noteId, control: "text", label: "Reference note", showInForm: false },
        ],
      },
    ],
  });

  it("shows a field only while its visibility rule holds and omits the not-in-form field from the data-entry form", async () => {
    const onChange = vi.fn();
    const { rerender } = render(<FormRenderer layout={richLayout()} fields={richFields} values={{ kind: "person" }} onChange={onChange} breakpoint="desktop" />);
    expect(screen.queryByLabelText(/Company name/)).toBeNull();
    expect(screen.queryByLabelText(/Reference note/)).toBeNull();
    rerender(<FormRenderer layout={richLayout()} fields={richFields} values={{ kind: "company" }} onChange={onChange} breakpoint="desktop" />);
    expect(screen.getByLabelText(/Company name/)).toBeInTheDocument();
    // Without rules applied (a partial projection), the rule-hidden field is simply present; Show in form still only affects data entry.
    rerender(<FormRenderer layout={richLayout()} fields={richFields} values={{ kind: "person" }} applyRules={false} onChange={onChange} breakpoint="desktop" />);
    expect(screen.getByLabelText(/Company name/)).toBeInTheDocument();
  });

  it("shows every authorized field in the read-only record view, including the one left out of the data-entry form", () => {
    render(<FormRenderer layout={richLayout()} fields={richFields} values={{ kind: "company", company_name: "Acme", note: "ref-1", topics: "red,green" }} readOnly breakpoint="desktop" />);
    expect(screen.getByText("Acme")).toBeInTheDocument();
    expect(screen.getByText("ref-1")).toBeInTheDocument();
    expect(screen.getByText("Red, Green")).toBeInTheDocument();
  });

  it("renders a read-only field as a value with a Read-only marker and no input", () => {
    render(<FormRenderer layout={richLayout()} fields={richFields} values={{ status: "draft" }} breakpoint="desktop" />);
    expect(screen.getByText("Draft")).toBeInTheDocument();
    const locked = document.querySelector('[data-readonly-field="true"]')!;
    expect(locked).toHaveTextContent("Read-only");
    expect(locked.querySelector("input,select,button")).toBeNull();
  });

  it("makes a multi-select an accessible checkbox group that reports the selection in display order and keeps an unavailable selection uncheckable-visible", async () => {
    const onChange = vi.fn();
    render(<FormRenderer layout={richLayout()} fields={richFields} values={{ topics: "blue" }} onChange={onChange} breakpoint="desktop" />);
    const group = screen.getByRole("group", { name: /Topics/ });
    expect(group).toBeInTheDocument();
    // "Blue" is deactivated but selected: shown, marked, checked; "Green" is available; nothing else leaks in.
    expect(screen.getByRole("checkbox", { name: "Blue (no longer available)" })).toBeChecked();
    await userEvent.click(screen.getByRole("checkbox", { name: "Red" }));
    expect(onChange).toHaveBeenLastCalledWith("topics", "red,blue");
    await userEvent.click(screen.getByRole("checkbox", { name: "Blue (no longer available)" }));
    expect(onChange).toHaveBeenLastCalledWith("topics", "");
  });

  it("offers to clear an optional dropdown selection but never a required one", async () => {
    const onChange = vi.fn();
    const optional: FormLayout = { ...richLayout(), sections: [{ ...richLayout().sections[0]!, elements: [{ ...base, kind: "field", fieldDefinitionId: kindId, control: "select", label: "Kind" }] }] };
    const { rerender } = render(<FormRenderer layout={optional} fields={richFields} values={{ kind: "person" }} onChange={onChange} breakpoint="desktop" />);
    await userEvent.click(screen.getByRole("button", { name: "Clear Kind selection" }));
    expect(onChange).toHaveBeenLastCalledWith("kind", "");
    rerender(<FormRenderer layout={richLayout()} fields={richFields} values={{ kind: "person" }} onChange={onChange} breakpoint="desktop" />);
    expect(screen.queryByRole("button", { name: /Clear Kind selection/ })).toBeNull();
  });

  it("marks a field required only while its required-if rule holds", () => {
    const conditional: FormLayout = { ...richLayout(), sections: [{ ...richLayout().sections[0]!, elements: [
      { ...base, kind: "field", fieldDefinitionId: kindId, control: "select", label: "Kind" },
      { ...base, kind: "field", fieldDefinitionId: companyId, control: "text", label: "Company name", requiredWhen: { op: "equals", fieldDefinitionId: kindId, value: "company" } },
    ] }] };
    const { rerender } = render(<FormRenderer layout={conditional} fields={richFields} values={{ kind: "person" }} breakpoint="desktop" />);
    expect(screen.getByLabelText(/Company name/)).not.toBeRequired();
    rerender(<FormRenderer layout={conditional} fields={richFields} values={{ kind: "company" }} breakpoint="desktop" />);
    expect(screen.getByLabelText(/Company name/)).toBeRequired();
    // A partial projection evaluates no rules: requiredness falls back to the authored (static) flag.
    rerender(<FormRenderer layout={conditional} fields={richFields} values={{ kind: "company" }} applyRules={false} breakpoint="desktop" />);
    expect(screen.getByLabelText(/Company name/)).not.toBeRequired();
  });
});
