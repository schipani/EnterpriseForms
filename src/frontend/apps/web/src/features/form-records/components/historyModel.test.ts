import type { FieldDefinitionResponse, FieldValueResponse } from "@enterpriseforms/api-client";
import type { FormLayout } from "@enterpriseforms/form-renderer";
import { describe, expect, it } from "vitest";
import { actorText, changeText, otherRecordedValues, placedFieldIds } from "./historyModel";

describe("record history presentation (Phase C6 M2)", () => {
  it("shows Authority change labels plainly, activity-log labels with their source, and never invents one", () => {
    expect(changeText({ actionLabel: "Created", actionSource: "Authority" })).toEqual({ text: "Created" });
    expect(changeText({ actionLabel: "Submitted", actionSource: "ActivityLog" })).toEqual({ text: "Submitted", note: "from the activity log" });
    expect(changeText({ actionLabel: null, actionSource: "None" })).toEqual({ text: "Not recorded" });
    expect(changeText({ actionLabel: "Edited", actionSource: "None" })).toEqual({ text: "Not recorded" });
  });

  it("names the person, the system migration or 'Not known'", () => {
    expect(actorText({ actorKind: "User", actorDisplayName: "Avery Owner" })).toBe("Avery Owner");
    expect(actorText({ actorKind: "User", actorDisplayName: null })).toBe("Unknown user");
    expect(actorText({ actorKind: "ProtectionMigration", actorDisplayName: null })).toBe("System (field protection update)");
    expect(actorText({ actorKind: "Unknown", actorDisplayName: null })).toBe("Not known");
  });

  it("lists values of fields the current form no longer places, with their current name", () => {
    const layout = {
      layoutSchemaVersion: 2,
      title: "Case",
      sections: [
        { sectionKey: "main", title: "Main", gridColumns: 4, elements: [{ kind: "field", fieldDefinitionId: "f-title", control: "text", label: "Title", layout: { columnSpan: 2, startOnNewRow: false } }] },
        { sectionKey: "docs", title: "Docs", gridColumns: 4, elements: [{ kind: "documentCollection", fieldDefinitionId: "f-docs", label: "Docs", layout: { columnSpan: 4, startOnNewRow: true } }] },
      ],
    } as unknown as FormLayout;
    expect([...placedFieldIds(layout)].sort()).toEqual(["f-docs", "f-title"]);

    const values = [
      { fieldDefinitionId: "f-title", fieldKey: "title", stringValue: "A" },
      { fieldDefinitionId: "f-ref", fieldKey: "external_reference", stringValue: "0199" },
      { fieldDefinitionId: "f-old", fieldKey: "old_code", stringValue: "X1" },
    ] as unknown as FieldValueResponse[];
    const fields = [
      { fieldDefinitionId: "f-ref", fieldDisplayName: "External reference", lifecycleStatus: "Available" },
      { fieldDefinitionId: "f-old", fieldDisplayName: "Old code", lifecycleStatus: "Retired" },
    ] as unknown as FieldDefinitionResponse[];
    expect(otherRecordedValues(layout, values, fields)).toEqual([
      { fieldDefinitionId: "f-ref", label: "External reference", value: "0199", retired: false },
      { fieldDefinitionId: "f-old", label: "Old code", value: "X1", retired: true },
    ]);
  });
});
