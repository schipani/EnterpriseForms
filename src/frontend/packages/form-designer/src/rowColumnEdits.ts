import type { FormLayout, LayoutElement } from "@enterpriseforms/form-renderer";
import { emptyRowPlacement, fieldCells, GRID_COLUMNS, rebuildSection, replaceSection, sectionGrid, stationary, type GridCell, type Placement } from "./gridEdits";
import { elementKey, sectionIndexOf } from "./layoutEdits";
import { optionFilterParents, ruleDependencies } from "./ruleEdit";

/**
 * FD-01 row and column management (pure, immutable, unit-tested; no React). Each operation works on one desktop row of
 * one section, through the canonical packer, and writes the section back with `rebuildSection`:
 *
 * - **Clear row** removes the row's fields from the layout and keeps the row as a designer-only empty row of four cells.
 * - **Delete row** removes the row and its fields; the rows below move up, and every other field keeps its columns.
 * - **Clear column** removes the one field occupying that cell (a field spanning several columns is removed as a whole);
 *   every other field stays where it is, and a row left without fields stays as an empty row.
 * - **Delete column** removes that column position from the row: the field occupying it (if any) is removed and the
 *   fields to its right move one column left. The grid stays four columns wide and other rows never move. It is refused
 *   when the column is part of a field spanning several columns, because it would have to resize or remove that field.
 *
 * "Removing" a field always means un-placing it from this draft layout: field definitions, stable ids, stored values,
 * documents and child records are untouched, and the field can be placed again. An operation is refused when a field that
 * stays in the layout has a rule or dependent dropdown that reads a field the operation would remove — the server rejects
 * such a layout — so no reference is ever left dangling and the existing dependency protection is never bypassed.
 */

export type GridOperationKind = "clearRow" | "deleteRow" | "clearColumn" | "deleteColumn";

export interface GridOperation {
  kind: GridOperationKind;
  sectionKey: string;
  row: number;
  /** Required for the column operations. */
  column?: number | undefined;
}

/** A field that stays in the layout but reads a field the operation would remove. */
export interface RemovalDependency {
  /** The field that would be left with a dangling reference. */
  dependentFieldDefinitionId: string;
  /** The removed field it reads. */
  sourceFieldDefinitionId: string;
  reference: "rule" | "choices";
}

export type GridOperationPlan =
  | {
      ok: true;
      operation: GridOperation;
      /** The layout after the operation (apply only after confirmation when `removed` is not empty). */
      layout: FormLayout;
      /** Elements the operation removes from the layout, in reading order. */
      removed: LayoutElement[];
      /** Fields that move one column left (Delete column). */
      shifted: number;
      /** What is affected, for the confirmation and announcements, e.g. "row 2" or "row 2, columns 1–3". */
      affected: string;
      /** Non-empty: the operation must not be applied (it would leave dangling references). */
      dependencies: RemovalDependency[];
    }
  | { ok: false; operation: GridOperation; message: string };

export const GRID_OPERATION_LABEL: Record<GridOperationKind, string> = { clearRow: "Clear row", deleteRow: "Delete row", clearColumn: "Clear column", deleteColumn: "Delete column" };

function columnText(column: number, span: number): string {
  return span > 1 ? `columns ${column}–${column + span - 1}` : `column ${column}`;
}

/** Plans an operation without applying it. */
export function planGridOperation(layout: FormLayout, operation: GridOperation): GridOperationPlan {
  const refuse = (message: string): GridOperationPlan => ({ ok: false, operation, message });
  const sectionIndex = sectionIndexOf(layout, operation.sectionKey);
  const section = layout.sections[sectionIndex];
  if (!section) {
    return refuse("That section is no longer in the layout.");
  }
  const cells = sectionGrid(section);
  const { row } = operation;
  const rowExists = cells.some((cell) => cell.row === row);
  if (!rowExists) {
    return refuse(`Row ${row} is no longer in this section.`);
  }
  const inRow = fieldCells(cells).filter((cell) => cell.row === row);
  const others = cells.filter((cell) => cell.row !== row);
  const result = (placements: Placement[], removed: GridCell[], affected: string, shifted = 0): GridOperationPlan => {
    const next = replaceSection(layout, sectionIndex, rebuildSection(section, placements));
    const removedElements = removed.map((cell) => cell.element);
    return { ok: true, operation, layout: next, removed: removedElements, shifted, affected, dependencies: removalDependencies(next, removedElements) };
  };

  switch (operation.kind) {
    case "clearRow":
      if (inRow.length === 0) {
        return refuse(`Row ${row} is already empty.`);
      }
      return result([...stationary(others), emptyRowPlacement(row)], inRow, `row ${row}`);
    case "deleteRow":
      if (inRow.length === 0 && others.length === 0) {
        return refuse("This is the section's only row. An empty section always keeps one row to place fields in.");
      }
      return result(stationary(others), inRow, `row ${row}`);
    case "clearColumn":
    case "deleteColumn": {
      const column = operation.column ?? 0;
      if (column < 1 || column > GRID_COLUMNS) {
        return refuse("Choose a column from 1 to 4.");
      }
      const occupant = inRow.find((cell) => cell.column <= column && column < cell.column + cell.span);
      const label = occupant ? displayName(occupant.element) : "";
      if (operation.kind === "clearColumn") {
        if (!occupant) {
          return refuse(`Row ${row}, column ${column} is already empty.`);
        }
        const rest = inRow.filter((cell) => cell !== occupant);
        return result([...stationary(others), ...stationary(rest), ...(rest.length === 0 ? [emptyRowPlacement(row)] : [])], [occupant], `row ${row}, ${columnText(occupant.column, occupant.span)}`);
      }
      if (occupant && occupant.span > 1) {
        return refuse(`Column ${column} of row ${row} is part of “${label}”, which spans ${columnText(occupant.column, occupant.span)}. Deleting one column would have to resize or remove that field, so nothing was changed. Make the field narrower first, or use Clear column to remove the whole field.`);
      }
      const after = inRow.filter((cell) => cell !== occupant && cell.column > column);
      if (!occupant && after.length === 0) {
        return refuse(`Row ${row} has no field in or after column ${column}, so there is nothing to delete.`);
      }
      const kept = inRow.filter((cell) => cell !== occupant);
      const placements = kept.map((cell) => ({ element: cell.element, row, column: cell.column > column ? cell.column - 1 : cell.column }));
      return result([...stationary(others), ...placements, ...(kept.length === 0 ? [emptyRowPlacement(row)] : [])], occupant ? [occupant] : [], `row ${row}, column ${column}`, after.length);
    }
  }
}

function displayName(element: LayoutElement): string {
  return element.label.trim() || "this field";
}

/** Fields remaining in `layout` whose rules or dependent dropdown choices read one of the `removed` fields. */
export function removalDependencies(layout: FormLayout, removed: readonly LayoutElement[]): RemovalDependency[] {
  const removedIds = new Set(removed.flatMap((element) => (element.kind === "relatedFormList" ? [] : [element.fieldDefinitionId])));
  if (removedIds.size === 0) {
    return [];
  }
  const dependencies: RemovalDependency[] = [];
  for (const [dependent, sources] of ruleDependencies(layout)) {
    for (const source of sources) {
      if (removedIds.has(source)) {
        dependencies.push({ dependentFieldDefinitionId: dependent, sourceFieldDefinitionId: source, reference: "rule" });
      }
    }
  }
  for (const [dependent, parent] of optionFilterParents(layout)) {
    if (removedIds.has(parent)) {
      dependencies.push({ dependentFieldDefinitionId: dependent, sourceFieldDefinitionId: parent, reference: "choices" });
    }
  }
  return dependencies;
}

/** The selection that should follow an applied operation: the affected cell (or the section after Delete row). */
export function selectionAfter(layout: FormLayout, operation: GridOperation): { kind: "slot"; sectionKey: string; slotId: string; row: number; column: number } | { kind: "element"; key: string } | { kind: "section"; sectionKey: string } {
  if (operation.kind === "deleteRow") {
    return { kind: "section", sectionKey: operation.sectionKey };
  }
  const section = layout.sections[sectionIndexOf(layout, operation.sectionKey)];
  const column = operation.kind === "clearRow" ? 1 : (operation.column ?? 1);
  const occupant = section ? fieldCells(sectionGrid(section)).find((cell) => cell.row === operation.row && cell.column <= column && column < cell.column + cell.span) : undefined;
  if (occupant) {
    return { kind: "element", key: elementKey(occupant.element) };
  }
  return { kind: "slot", sectionKey: operation.sectionKey, slotId: `cell-${operation.row}-${column}`, row: operation.row, column };
}
