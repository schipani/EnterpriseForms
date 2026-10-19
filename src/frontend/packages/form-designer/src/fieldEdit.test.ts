import { emptyLayout, type FormLayout } from "@enterpriseforms/form-renderer";
import { describe, expect, it } from "vitest";
import { applyEditValues, locateField, toEditValues, validateEditValues } from "./fieldEdit";

const FIELD = "0199a2b3-c4d5-7e6f-8123-456789abcdef";
const DOCS = "0199a2b3-c4d5-7e6f-8123-456789abcdf0";

function layout(): FormLayout {
  const base = emptyLayout("Accused");
  base.sections[0]!.elements.push(
    { kind: "field", fieldDefinitionId: FIELD, control: "text", label: "Given name", required: false, layout: { columnSpan: 2, startOnNewRow: false }, validation: { maxLength: 100, pattern: "[A-Z].*" } },
    { kind: "documentCollection", fieldDefinitionId: DOCS, control: "fileUpload", label: "Case documents", required: false, layout: { columnSpan: 4, startOnNewRow: true }, upload: { allowMultiple: true, maxFiles: 10, maxFileSizeBytes: 20 * 1024 * 1024, allowedContentTypes: ["application/pdf"] } },
  );
  return base;
}

describe("field edit model", () => {
  it("locates a placed field and reports an unplaced one as null", () => {
    expect(locateField(layout(), FIELD)).toMatchObject({ sectionIndex: 0, index: 0 });
    expect(locateField(layout(), DOCS)?.element.kind).toBe("documentCollection");
    expect(locateField(layout(), "0199a2b3-c4d5-7e6f-8123-000000000000")).toBeNull();
  });

  it("round-trips a label, help text and rules without touching identity or layout placement", () => {
    const location = locateField(layout(), FIELD)!;
    const values = { ...toEditValues(location.element, "Given name"), label: "Forename", helpText: "As on the charge sheet", required: true, maxLength: "80" };
    const updated = applyEditValues(location.element, values, "String");
    expect(updated).toMatchObject({ kind: "field", fieldDefinitionId: FIELD, control: "text", label: "Forename", helpText: "As on the charge sheet", required: true, layout: { columnSpan: 2, startOnNewRow: false }, validation: { maxLength: 80, pattern: "[A-Z].*" } });
  });

  it("keeps a document collection bound to its field and carries the upload restrictions", () => {
    const location = locateField(layout(), DOCS)!;
    const values = { ...toEditValues(location.element, "Case documents"), label: "Evidence", maxFiles: "5", maxFileSizeMb: "8", allowedContentTypes: "application/pdf, text/plain" };
    expect(validateEditValues(values, "File")).toEqual({});
    const updated = applyEditValues(location.element, values, "File");
    expect(updated).toMatchObject({ kind: "documentCollection", fieldDefinitionId: DOCS, label: "Evidence", upload: { maxFiles: 5, maxFileSizeBytes: 8 * 1024 * 1024, allowedContentTypes: ["application/pdf", "text/plain"] } });
  });

  it("falls back to the catalog name for an unlabelled element and never writes rules that do not fit the type", () => {
    const empty = toEditValues({ kind: "field", fieldDefinitionId: FIELD, control: "number", label: "", required: false, layout: { columnSpan: 4, startOnNewRow: false } }, "Age");
    expect(empty.label).toBe("Age");
    const updated = applyEditValues({ kind: "field", fieldDefinitionId: FIELD, control: "number", label: "Age", required: false, layout: { columnSpan: 4, startOnNewRow: false } }, { ...empty, maxLength: "50", minimum: "0", maximum: "120" }, "Integer");
    expect(updated).toMatchObject({ validation: { minimum: 0, maximum: 120 } });
    expect((updated as { validation: Record<string, unknown> }).validation["maxLength"]).toBeUndefined();
  });

  it("explains incompatible values before the server does", () => {
    const base = toEditValues(null, "x");
    expect(validateEditValues({ ...base, label: "" }, "String").label).toMatch(/1 to 200/);
    expect(validateEditValues({ ...base, minLength: "10", maxLength: "5" }, "String").minLength).toMatch(/cannot exceed/);
    expect(validateEditValues({ ...base, minimum: "5", maximum: "1" }, "Integer").minimum).toMatch(/cannot exceed/);
    expect(validateEditValues({ ...base, minimum: "1.5" }, "Integer").minimum).toMatch(/whole number/);
    expect(validateEditValues({ ...base, minDate: "2026-02-01", maxDate: "2026-01-01" }, "Date").minDate).toMatch(/after/);
    expect(validateEditValues({ ...base, maxFiles: "0", maxFileSizeMb: "900" }, "File")).toMatchObject({ maxFiles: expect.any(String), maxFileSizeMb: expect.any(String) });
    expect(validateEditValues({ ...base, label: "<b>x</b>" }, "String").helpText).toMatch(/Angle/);
  });
});
