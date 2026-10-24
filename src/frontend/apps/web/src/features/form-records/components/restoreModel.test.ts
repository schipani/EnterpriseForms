import type { FieldDefinitionResponse, RecordProjectionResponse } from "@enterpriseforms/api-client";
import { describe, expect, it } from "vitest";
import { canConfirmRestore, changedFields, displayValue, exclusions, reasonIsValid, restoreEligibility } from "./restoreModel";

function projection(status: string, permissions: Partial<RecordProjectionResponse["permissions"]> = {}, contentEditable = status === "Draft"): RecordProjectionResponse {
  return {
    record: { recordStatus: status },
    lifecycle: { status, submittedOn: null, allowedTransitions: [], contentEditable },
    permissions: { canRead: true, canEdit: true, canManageAccess: true, canTransferOwnership: true, isCompleteProjection: true, canEditSharedFields: false, contributorWritesEnabled: false, ...permissions },
  } as unknown as RecordProjectionResponse;
}

function field(partial: Partial<FieldDefinitionResponse>): FieldDefinitionResponse {
  return { fieldDefinitionId: "f", fieldKey: "k", fieldDisplayName: "K", fieldDescription: "", fieldDataType: "String", fieldClassification: "Internal", lifecycleStatus: "Available", options: [], ...partial } as FieldDefinitionResponse;
}

describe("restore presentation (Phase C6 M3)", () => {
  it("offers Restore only to the owner of a Draft record viewing an earlier version", () => {
    expect(restoreEligibility(false, projection("Draft"))).toEqual({ kind: "eligible" });
    expect(restoreEligibility(true, projection("Draft"))).toEqual({ kind: "hidden" });
    expect(restoreEligibility(false, undefined)).toEqual({ kind: "hidden" });
    // Not the owner (a field-share contributor or a reader): no action at all.
    expect(restoreEligibility(false, projection("Draft", { canTransferOwnership: false }))).toEqual({ kind: "hidden" });
    expect(restoreEligibility(false, projection("Draft", { isCompleteProjection: false }))).toEqual({ kind: "hidden" });
    expect(restoreEligibility(false, projection("Draft", { canEdit: false }))).toEqual({ kind: "hidden" });
  });

  it("explains, without an actionable button, why a Submitted or Archived record can't be restored", () => {
    expect(restoreEligibility(false, projection("Submitted")).kind).toBe("explain");
    expect(restoreEligibility(false, projection("Archived")).kind).toBe("explain");
    expect(restoreEligibility(false, projection("Draft", {}, false))).toEqual({ kind: "hidden" });
  });

  it("lists only fields that change and shows readable values", () => {
    const fields = [
      { fieldKey: "a", change: "Unchanged" },
      { fieldKey: "b", change: "Changed" },
      { fieldKey: "c", change: "Added" },
      { fieldKey: "d", change: "Cleared" },
    ] as never;
    expect(changedFields({ fields }).map((f) => f.fieldKey)).toEqual(["b", "c", "d"]);
    const options = field({ fieldDataType: "Option", options: [{ fieldOptionId: "1", optionKey: "open", optionDisplayText: "Open", optionSortOrder: 1, isActive: true }, { fieldOptionId: "2", optionKey: "closed", optionDisplayText: "Closed", optionSortOrder: 2, isActive: true }] });
    expect(displayValue("open,closed", options)).toBe("Open, Closed");
    expect(displayValue("gone", options)).toBe("gone");
    expect(displayValue("true", field({ fieldDataType: "Boolean" }))).toBe("Yes");
    expect(displayValue("", field({}))).toBe("Empty");
    expect(displayValue("raw", undefined)).toBe("raw");
  });

  it("requires a reason, and the acknowledgement when values would lose encryption", () => {
    expect(reasonIsValid("   ")).toBe(false);
    expect(reasonIsValid("x".repeat(501))).toBe(false);
    expect(reasonIsValid(" a reason ")).toBe(true);
    const plain = { canRestore: true, plaintextAcknowledgementFieldKeys: [] };
    const downgrade = { canRestore: true, plaintextAcknowledgementFieldKeys: ["notes"] };
    expect(canConfirmRestore(plain, "because", false)).toBe(true);
    expect(canConfirmRestore(plain, "", false)).toBe(false);
    expect(canConfirmRestore(downgrade, "because", false)).toBe(false);
    expect(canConfirmRestore(downgrade, "because", true)).toBe(true);
    expect(canConfirmRestore({ ...plain, canRestore: false }, "because", true)).toBe(false);
  });

  it("always states what is not restored", () => {
    const lines = exclusions({ unchangedDocumentCount: 2, formHasRelatedRecords: true });
    expect(lines[0]).toContain("2 current documents stay");
    expect(lines.join(" ")).toMatch(/Related records are not restored/);
    expect(lines.join(" ")).toMatch(/Sharing, access and ownership/);
    expect(lines.join(" ")).toMatch(/current protection settings/);
  });
});
