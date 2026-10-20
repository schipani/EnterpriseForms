import type { FormRecordResponse } from "@enterpriseforms/api-client";
import { emptyLayout, type FormLayout, type RendererField } from "@enterpriseforms/form-renderer";
import { describe, expect, it } from "vitest";
import { layoutValues, toValueMap } from "./recordModel";

const ids = { kind: "0199a2b3-c4d5-7e6f-8123-456789abf001", company: "0199a2b3-c4d5-7e6f-8123-456789abf002", status: "0199a2b3-c4d5-7e6f-8123-456789abf003", note: "0199a2b3-c4d5-7e6f-8123-456789abf004", given: "0199a2b3-c4d5-7e6f-8123-456789abf005" };
const fields: RendererField[] = [
  { fieldDefinitionId: ids.kind, fieldKey: "kind", fieldDisplayName: "Kind", fieldDataType: "Option", options: [] },
  { fieldDefinitionId: ids.company, fieldKey: "company_name", fieldDisplayName: "Company", fieldDataType: "String", options: [] },
  { fieldDefinitionId: ids.status, fieldKey: "status", fieldDisplayName: "Status", fieldDataType: "Option", options: [] },
  { fieldDefinitionId: ids.note, fieldKey: "note", fieldDisplayName: "Note", fieldDataType: "String", options: [] },
  { fieldDefinitionId: ids.given, fieldKey: "given_name", fieldDisplayName: "Given", fieldDataType: "String", options: [] },
];
const base = { required: false, layout: { columnSpan: 4 as const, startOnNewRow: false } };
const layout: FormLayout = {
  ...emptyLayout("T"),
  sections: [
    {
      sectionKey: "main",
      gridColumns: 4,
      elements: [
        { ...base, kind: "field", fieldDefinitionId: ids.kind, control: "select", label: "Kind" },
        { ...base, kind: "field", fieldDefinitionId: ids.company, control: "text", label: "Company", visibleWhen: { op: "equals", fieldDefinitionId: ids.kind, value: "company" } },
        { ...base, kind: "field", fieldDefinitionId: ids.status, control: "select", label: "Status", readOnly: true },
        { ...base, kind: "field", fieldDefinitionId: ids.note, control: "text", label: "Note", showInForm: false },
        { ...base, kind: "field", fieldDefinitionId: ids.given, control: "text", label: "Given" },
      ],
    },
  ],
};

describe("record form payloads", () => {
  it("submits typed edits but resubmits the starting value of fields the user cannot change here", () => {
    const initial = { kind: "company", company_name: "Acme", status: "draft", note: "api-set", given_name: "Ava" };
    // The user switched Kind to person (hiding Company), typed into Given, and (somehow) edited the locked/not-shown fields.
    const edited = { kind: "person", company_name: "Typed after hiding", status: "final", note: "changed", given_name: "Ava B" };
    expect(layoutValues(layout, fields, edited, initial)).toEqual({ kind: "person", company_name: "Acme", status: "draft", note: "api-set", given_name: "Ava B" });
  });

  it("sends nothing for fields that have no starting value on create", () => {
    expect(layoutValues(layout, fields, { kind: "person", company_name: "x", status: "y", note: "z" }, {})).toEqual({ kind: "person", company_name: "", status: "", note: "", given_name: "" });
  });

  it("submits a rule-hidden field normally when rules do not apply (partial projection)", () => {
    expect(layoutValues(layout, fields, { kind: "person", company_name: "kept" }, { company_name: "old" }, false).company_name).toBe("kept");
  });

  it("joins the per-selection values of a multi-select into the comma-separated wire form", () => {
    const record = { values: [{ fieldKey: "topics", stringValue: "red" }, { fieldKey: "topics", stringValue: "green" }, { fieldKey: "note", stringValue: "n" }] } as unknown as FormRecordResponse;
    expect(toValueMap(record)).toEqual({ topics: "red,green", note: "n" });
  });
});
