import { placeAtCell, planGridOperation, type GridOperation } from "@enterpriseforms/form-designer";
import { emptyLayout, type FormLayout, type LayoutElement } from "@enterpriseforms/form-renderer";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { GridOperationDialog } from "./GridOperationDialog";

/** FD-01 row management: the confirmation shown before Clear/Delete row or column removes fields from the layout. */

const id = (n: number) => `0199a2b3-c4d5-7e6f-8123-${n.toString().padStart(12, "0")}`;
const field = (n: number, label: string, extra: Partial<LayoutElement> = {}): LayoutElement => ({ kind: "field", fieldDefinitionId: id(n), control: "text", label, required: false, layout: { columnSpan: 1, startOnNewRow: false }, ...extra }) as LayoutElement;
const names: Record<string, string> = { [id(1)]: "Toggle", [id(2)]: "Note", [id(3)]: "Wide note" };

function layoutOf(placements: Array<[LayoutElement, number, number]>): FormLayout {
  return placements.reduce((layout, [element, row, column]) => placeAtCell(layout, 0, element, row, column).layout, emptyLayout("Intake"));
}

function show(layout: FormLayout, operation: Omit<GridOperation, "sectionKey">) {
  const plan = planGridOperation(layout, { sectionKey: "main", ...operation });
  if (!plan.ok) {
    throw new Error(plan.message);
  }
  const onConfirm = vi.fn();
  const onClose = vi.fn();
  render(<GridOperationDialog plan={plan} sectionTitle="Intake" fieldName={(fieldId) => names[fieldId] ?? "Unknown"} onConfirm={onConfirm} onClose={onClose} />);
  return { onConfirm, onClose };
}

describe("FD-01 Clear/Delete row and column confirmation", () => {
  it("8: names the row, counts and lists the fields, and distinguishes clearing from deleting", () => {
    const layout = layoutOf([
      [field(1, "Toggle"), 1, 1],
      [field(2, "Note"), 1, 3],
    ]);
    const { onConfirm, onClose } = show(layout, { kind: "clearRow", row: 1 });
    const dialog = screen.getByRole("dialog", { name: /Clear row — Row 1 of “Intake”\?/ });
    expect(dialog).toHaveTextContent("2 fields will be removed from the layout");
    expect(dialog).toHaveTextContent("Toggle");
    expect(dialog).toHaveTextContent("Note");
    expect(dialog).toHaveTextContent(/keeps the row as four empty cells/);
    expect(dialog).toHaveTextContent(/stored values/);
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onClose).toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Clear row" }));
    expect(onConfirm).toHaveBeenCalledTimes(1);
  });

  it("8: Delete row explains that the rows below move up", () => {
    show(layoutOf([[field(1, "Toggle"), 1, 1]]), { kind: "deleteRow", row: 1 });
    expect(screen.getByRole("dialog")).toHaveTextContent(/rows below move up/);
    expect(screen.getByRole("button", { name: "Delete row" })).toBeEnabled();
  });

  it("identifies the whole span of a wider field being cleared", () => {
    show(layoutOf([[field(3, "Wide note", { layout: { columnSpan: 3, startOnNewRow: false } } as Partial<LayoutElement>), 1, 2]]), { kind: "clearColumn", row: 1, column: 3 });
    const dialog = screen.getByRole("dialog", { name: /Row 1, columns 2–4/ });
    expect(dialog).toHaveTextContent("1 field will be removed");
    expect(dialog).toHaveTextContent("spans 3 columns");
  });

  it("10: blocks the operation while a remaining field's rule reads a field it would remove", () => {
    const layout = layoutOf([
      [field(1, "Toggle"), 1, 1],
      [field(2, "Note", { visibleWhen: { op: "isNotEmpty", fieldDefinitionId: id(1) } } as Partial<LayoutElement>), 2, 1],
    ]);
    const { onConfirm } = show(layout, { kind: "clearColumn", row: 1, column: 1 });
    expect(screen.getByRole("alert")).toHaveTextContent("“Note” has a rule that reads “Toggle”.");
    const confirm = screen.getByRole("button", { name: "Clear column" });
    expect(confirm).toBeDisabled();
    fireEvent.click(confirm);
    expect(onConfirm).not.toHaveBeenCalled();
  });
});
