import { describe, expect, it } from "vitest";
import { emptyLayout, type FormLayout } from "./layout";
import { validateValues, type RendererField } from "./validation";

const fieldId = "0199a2b3-c4d5-7e6f-8123-456789abcdef";
const statusId = "0199a2b3-c4d5-7e6f-8123-456789abcdf0";
const fields: RendererField[] = [
  { fieldDefinitionId: fieldId, fieldKey: "given_name", fieldDisplayName: "Given name", fieldDataType: "String", options: [] },
  { fieldDefinitionId: statusId, fieldKey: "status", fieldDisplayName: "Status", fieldDataType: "Option", options: [{ fieldOptionId: "a", optionKey: "active", optionDisplayText: "Active", optionSortOrder: 1, isActive: true }, { fieldOptionId: "b", optionKey: "retired", optionDisplayText: "Retired", optionSortOrder: 2, isActive: false }] },
];
const layout: FormLayout = {
  ...emptyLayout("Accused"),
  sections: [
    {
      sectionKey: "main",
      gridColumns: 4,
      elements: [
        { kind: "field", fieldDefinitionId: fieldId, control: "text", label: "Given name", required: true, layout: { columnSpan: 2, startOnNewRow: false }, validation: { maxLength: 5 } },
        { kind: "field", fieldDefinitionId: statusId, control: "select", label: "Status", required: false, layout: { columnSpan: 2, startOnNewRow: false } },
      ],
    },
  ],
};

describe("client validation mirrors the server rules", () => {
  it("reports required and max-length violations", () => {
    expect(validateValues(layout, fields, {})).toEqual({ given_name: "Given name is required" });
    expect(validateValues(layout, fields, { given_name: "Averyyy" })).toEqual({ given_name: "Given name must be at most 5 characters" });
  });

  it("rejects deactivated options but keeps them displayable", () => {
    expect(validateValues(layout, fields, { given_name: "Ava", status: "retired" })).toEqual({ status: "Status: the selected option is no longer available; choose another" });
    expect(validateValues(layout, fields, { given_name: "Ava", status: "active" })).toEqual({});
  });

  it("does not demand untouched required fields in edit scope", () => {
    expect(validateValues(layout, fields, {}, "edit")).toEqual({});
  });
});
