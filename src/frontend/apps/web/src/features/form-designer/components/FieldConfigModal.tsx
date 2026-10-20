import { findElement } from "@enterpriseforms/form-designer";
import { serializeLayout, type FormLayout } from "@enterpriseforms/form-renderer";
import { Button, Dialog } from "@enterpriseforms/ui";
import { useState } from "react";
import { FieldProperties, RelatedListProperties, type DesignerInspectorProps } from "./DesignerInspector";

export interface FieldConfigModalProps extends DesignerInspectorProps {
  open: boolean;
  /** The element key being configured; the component is only ever mounted while `open` is true. */
  configKey: string;
  initialTab: string;
  onClose: () => void;
}

/**
 * Configure Field (Phase C3 designer UX refinement): the full General / Validation / Rules / Security & Encryption /
 * Advanced workspace that used to live inline in the right-hand inspector, now in a large, pinned-header-and-footer
 * modal with one scroll surface. Presentation edits (General/Validation/Rules placement, labels, widths, rule
 * conditions) are held in a local draft layout and committed to the real draft — one undo entry — only on **Apply
 * Changes**; **Cancel**, Escape, the overlay and the close button all discard that local draft the same way. This is
 * new behaviour versus the old inline inspector (which applied every keystroke immediately); it is what lets Cancel
 * mean something. Security & Encryption (Authority-backed) and "Edit catalog name and description…" are unaffected:
 * they act immediately through their own server calls exactly as they always have — Cancel never rolls those back,
 * since they were never part of the draft layout.
 */
export function FieldConfigModal({ open, configKey, initialTab, onClose, ...rest }: FieldConfigModalProps) {
  const [draftLayout, setDraftLayout] = useState<FormLayout>(rest.layout);
  const [tab, setTab] = useState(initialTab);
  const position = findElement(draftLayout, configKey);
  const dirty = serializeLayout(draftLayout) !== serializeLayout(rest.layout);

  const close = () => onClose();
  const apply = () => {
    if (dirty) {
      rest.onChange(draftLayout);
      rest.announce("Field configuration applied");
    }
    onClose();
  };

  if (!position) {
    // The field left the layout while the modal was open (e.g. undo elsewhere, or removed from another tab/window).
    return (
      <Dialog open={open} onOpenChange={(next) => (next ? undefined : close())} title="Configure field" size="large" footer={<Button onClick={close}>Close</Button>}>
        <p className="text-sm text-muted">This field is no longer on the layout.</p>
      </Dialog>
    );
  }

  const element = position.element;
  const field = element.kind === "relatedFormList" ? undefined : rest.fields.find((candidate) => candidate.fieldDefinitionId === element.fieldDefinitionId);
  const label = element.kind === "relatedFormList" ? element.label || "related list" : field?.fieldDisplayName ?? "field";
  const scoped: DesignerInspectorProps = { ...rest, layout: draftLayout, onChange: setDraftLayout };

  const body =
    element.kind === "relatedFormList" ? (
      <RelatedListProperties {...scoped} element={element} sectionIndex={position.sectionIndex} index={position.index} tab={tab} setTab={setTab} />
    ) : field ? (
      <FieldProperties {...scoped} element={element} field={field} sectionIndex={position.sectionIndex} index={position.index} tab={tab} setTab={setTab} />
    ) : (
      <p className="text-sm text-muted">This placement refers to a field that is not in this form's catalog. Remove it from the layout.</p>
    );

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => (next ? undefined : close())}
      title={`Configure ${label}`}
      description="Apply Changes saves your edits to the draft layout; Escape, the close button and Cancel discard them. Security & Encryption and catalog-name changes save immediately and are not affected by Cancel."
      size="large"
      footer={
        <>
          <Button variant="secondary" onClick={close}>
            Cancel
          </Button>
          <Button onClick={apply} disabled={!rest.canDesign}>
            Apply Changes
          </Button>
        </>
      }
    >
      {body}
    </Dialog>
  );
}
