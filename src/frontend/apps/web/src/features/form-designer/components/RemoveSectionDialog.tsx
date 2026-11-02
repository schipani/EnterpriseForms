import { removeSection, sameScope, type SectionRemoval } from "@enterpriseforms/form-designer";
import type { FormLayout } from "@enterpriseforms/form-renderer";
import { Button, Dialog, RadioGroup, Select } from "@enterpriseforms/ui";
import { useId, useState } from "react";

/**
 * Removing a section (UX-08): an empty section is removed after confirmation; a section that still holds placements
 * requires an explicit choice — move them into another section, or un-place them from this draft layout. Un-placing is
 * presentation only: field definitions, stored values, child-record links and documents are untouched, and the items
 * stay available to place again. Nothing is deleted silently, and the change is undoable until the draft is saved.
 */
export function RemoveSectionDialog({ layout, sectionIndex, onConfirm, onClose }: { layout: FormLayout; sectionIndex: number; onConfirm: (next: FormLayout, message: string) => void; onClose: () => void }) {
  const section = layout.sections[sectionIndex];
  // Placements can only move to a section of the same scope (a section that allows multiple entries keeps its group's fields).
  const others = layout.sections.map((candidate, index) => ({ candidate, index })).filter(({ candidate, index }) => index !== sectionIndex && (!section || sameScope(candidate, section)));
  const [choice, setChoice] = useState<"move" | "unplace" | null>(null);
  const [target, setTarget] = useState<string | undefined>(others[Math.max(0, sectionIndex - 1)]?.candidate.sectionKey ?? others[0]?.candidate.sectionKey);
  const groupName = useId();
  if (!section) {
    return null;
  }
  const title = section.title?.trim() || section.sectionKey;
  const count = section.elements.length;
  const empty = count === 0;
  const items = count === 1 ? "1 item" : `${count} items`;
  const targetIndex = layout.sections.findIndex((candidate) => candidate.sectionKey === target);
  const disposition: SectionRemoval | null = empty ? { kind: "empty" } : choice === "move" && targetIndex >= 0 ? { kind: "moveTo", targetSectionIndex: targetIndex } : choice === "unplace" ? { kind: "unplace" } : null;
  return (
    <Dialog
      open
      onOpenChange={(open) => (open ? undefined : onClose())}
      title={`Remove section “${title}”?`}
      description={empty ? "This section is empty. Removing it changes only the draft layout." : `This section contains ${items}. Choose what happens to them before the section is removed. Field definitions, stored values, child records and documents are never deleted by a layout change.`}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="danger"
            disabled={!disposition}
            onClick={() => {
              if (!disposition) {
                return;
              }
              const message = disposition.kind === "moveTo" ? `Section ${title} removed; ${items} moved to ${layout.sections[targetIndex]?.title ?? ""}` : disposition.kind === "unplace" ? `Section ${title} removed; ${items} un-placed and kept in the field catalog` : `Empty section ${title} removed`;
              onConfirm(removeSection(layout, sectionIndex, disposition), message);
            }}
          >
            {empty ? "Remove empty section" : "Remove section"}
          </Button>
        </>
      }
    >
      {empty ? null : (
        <RadioGroup
          legend={`What should happen to the ${items}?`}
          name={groupName}
          value={choice ?? undefined}
          onValueChange={(value) => setChoice(value as typeof choice)}
          options={[
            {
              value: "move",
              disabled: others.length === 0,
              label: `Move the ${items} to another section${others.length === 0 ? " (no compatible section: fields of a section that allows multiple entries can only be un-placed)" : ""}`,
              details: <Select label="Move to section" value={target} onValueChange={setTarget} options={others.map(({ candidate }) => ({ value: candidate.sectionKey, label: candidate.title?.trim() || candidate.sectionKey }))} />,
            },
            { value: "unplace", label: `Remove the ${items} from the layout only — they stay in the field catalog with their values and documents, ready to place again` },
          ]}
        />
      )}
    </Dialog>
  );
}
