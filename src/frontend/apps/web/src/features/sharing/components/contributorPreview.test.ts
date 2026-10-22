import type { RecordProjectionResponse } from "@enterpriseforms/api-client";
import { emptyLayout, serializeLayout, type FormLayout } from "@enterpriseforms/form-renderer";
import { describe, expect, it } from "vitest";
import { contributorViewOnlyReasons } from "./contributorPreview";

type Field = RecordProjectionResponse["fields"][number];

const ids = {
  kind: "0199a2b3-c4d5-7e6f-8123-456789abc001",
  company: "0199a2b3-c4d5-7e6f-8123-456789abc002",
  status: "0199a2b3-c4d5-7e6f-8123-456789abc003",
  tags: "0199a2b3-c4d5-7e6f-8123-456789abc004",
  given: "0199a2b3-c4d5-7e6f-8123-456789abc005",
  secret: "0199a2b3-c4d5-7e6f-8123-456789abc006",
};
const field = (id: string, key: string, type = "String", extra: Partial<Field> = {}): Field =>
  ({ fieldDefinitionId: id, fieldKey: key, fieldDisplayName: key, fieldDescription: null, fieldDataType: type, fieldClassification: "Internal", lifecycleStatus: "Available", options: [], fieldDefinitionRevision: 1, fieldProtectionMode: "Standard", protectionPolicyRevision: 1, optionSelectionMode: "Single", groupDefinitionId: null, ...extra }) as Field;
const fields: Field[] = [
  field(ids.kind, "kind", "Option"),
  field(ids.company, "company_name"),
  field(ids.status, "status", "Option"),
  field(ids.tags, "tags", "Option", { optionSelectionMode: "Multiple" }),
  field(ids.given, "given_name"),
  field(ids.secret, "secret_value", "String", { fieldClassification: "Restricted" }),
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
        { ...base, kind: "field", fieldDefinitionId: ids.tags, control: "multiselect", label: "Tags" },
        { ...base, kind: "field", fieldDefinitionId: ids.given, control: "text", label: "Given" },
        { ...base, kind: "field", fieldDefinitionId: ids.secret, control: "text", label: "Secret" },
      ],
    },
  ],
};
const json = serializeLayout(layout);

describe("contributor share preview (Phase C5, advisory mirror of the server eligibility)", () => {
  it("marks read-only, restricted and multiple-choice fields as view only", () => {
    const reasons = contributorViewOnlyReasons(json, fields, new Set(["status", "tags", "secret_value", "given_name"]));
    expect(reasons.get("status")).toBe("read-only in this form");
    expect(reasons.get("tags")).toBe("a multiple-choice field");
    expect(reasons.get("secret_value")).toBe("restricted information");
    expect(reasons.has("given_name")).toBe(false);
  });

  it("applies R1 and R2 against the selected field set", () => {
    // Company depends on Kind: without Kind in the share it is view only (R1); Kind drives Company, so Kind alone is view only (R2).
    expect(contributorViewOnlyReasons(json, fields, new Set(["company_name"])).get("company_name")).toBe("depends on fields not included in this share");
    expect(contributorViewOnlyReasons(json, fields, new Set(["kind"])).get("kind")).toBe("affects fields not included in this share");
    const both = contributorViewOnlyReasons(json, fields, new Set(["kind", "company_name"]));
    expect(both.has("kind")).toBe(false);
    expect(both.has("company_name")).toBe(false);
  });

  it("returns nothing for an unparsable layout rather than guessing", () => {
    expect(contributorViewOnlyReasons("{not json", fields, new Set(["given_name"])).size).toBe(0);
  });
});
