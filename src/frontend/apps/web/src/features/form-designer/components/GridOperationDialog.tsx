import { GRID_OPERATION_LABEL, type GridOperationPlan } from "@enterpriseforms/form-designer";
import type { LayoutElement } from "@enterpriseforms/form-renderer";
import { ActionIcons, Button, Dialog, StatusMessage } from "@enterpriseforms/ui";

type ReadyPlan = Extract<GridOperationPlan, { ok: true }>;

/**
 * Confirmation for Clear/Delete row and Clear/Delete column (FD-01 row management) whenever the operation would remove a
 * field from the layout. It names the affected row or column (and the full span of a wider field), says how many fields
 * are removed and which, and states the difference between clearing (the empty cells stay) and deleting (the row or column
 * position goes). Removing only un-places fields from this draft — definitions, stored values and documents are kept —
 * and the change can be undone. When a remaining field still reads a removed field (a rule or dependent dropdown), the
 * operation is blocked here, exactly as the server would reject the saved layout.
 */
export function GridOperationDialog({ plan, sectionTitle, fieldName, onConfirm, onClose }: { plan: ReadyPlan; sectionTitle: string; fieldName: (fieldDefinitionId: string) => string; onConfirm: () => void; onClose: () => void }) {
  const { operation, removed, affected, shifted, dependencies } = plan;
  const label = GRID_OPERATION_LABEL[operation.kind];
  const count = removed.length === 1 ? "1 field" : `${removed.length} fields`;
  const blocked = dependencies.length > 0;
  const explanation = {
    clearRow: `Clearing removes the fields from ${affected} and keeps the row as four empty cells, ready for new fields or for fields dragged into it.`,
    deleteRow: `Deleting removes ${affected} and its fields. The rows below move up one row; every other field keeps its columns.`,
    clearColumn: `Clearing removes the field in ${affected} and leaves ${removed[0] && removed[0].layout.columnSpan > 1 ? "those cells" : "that cell"} empty. Every other field stays where it is.`,
    deleteColumn: `Deleting removes ${affected} from this row: the field in it is removed${shifted > 0 ? ` and ${shifted === 1 ? "the field to its right moves" : `the ${shifted} fields to its right move`} one column left` : ""}. Other rows do not change, and the grid stays four columns wide.`,
  }[operation.kind];
  return (
    <Dialog
      open
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={`${label} — ${capitalize(affected)} of “${sectionTitle}”?`}
      description={explanation}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="danger" icon={operation.kind.startsWith("clear") ? ActionIcons.clear : ActionIcons.delete} disabled={blocked} onClick={onConfirm}>
            {label}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3 text-sm">
        <p className="font-medium" data-testid="grid-operation-count">
          {count} will be removed from the layout:
        </p>
        <ul className="list-disc pl-5">
          {removed.map((element) => (
            <li key={key(element)}>
              {nameOf(element, fieldName)}
              {element.layout.columnSpan > 1 ? <span className="text-muted"> (spans {element.layout.columnSpan} columns)</span> : null}
            </li>
          ))}
        </ul>
        {blocked ? (
          <StatusMessage tone="danger" title={`${label} is not available yet`}>
            <p>Other fields still depend on {removed.length === 1 ? "this field" : "these fields"}. Change or remove these first:</p>
            <ul className="mt-1 list-disc pl-5">
              {dependencies.map((dependency) => (
                <li key={`${dependency.dependentFieldDefinitionId}:${dependency.sourceFieldDefinitionId}:${dependency.reference}`}>
                  {dependency.reference === "rule"
                    ? `“${fieldName(dependency.dependentFieldDefinitionId)}” has a rule that reads “${fieldName(dependency.sourceFieldDefinitionId)}”.`
                    : `The choices of “${fieldName(dependency.dependentFieldDefinitionId)}” depend on “${fieldName(dependency.sourceFieldDefinitionId)}”.`}
                </li>
              ))}
            </ul>
          </StatusMessage>
        ) : (
          <p className="text-muted">Removing a field from the layout keeps the field, its stored values and its documents; you can place it again. Undo (Ctrl+Z) puts the fields back in their original positions.</p>
        )}
      </div>
    </Dialog>
  );
}

function key(element: LayoutElement): string {
  return element.kind === "relatedFormList" ? element.formRelationshipDefinitionId : element.fieldDefinitionId;
}

function nameOf(element: LayoutElement, fieldName: (id: string) => string): string {
  if (element.label.trim()) {
    return element.label.trim();
  }
  return element.kind === "relatedFormList" ? "Related list" : fieldName(element.fieldDefinitionId);
}

function capitalize(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}
