import { packElements, visibleColumns, type Breakpoint, type FormLayout, type LayoutElement, type LayoutSection, type RendererField } from "@enterpriseforms/form-renderer";
import { IconButton as SharedIconButton, OverflowMenu } from "@enterpriseforms/ui";
import { useEffect, useRef, useState, type DragEvent, type KeyboardEvent, type ReactNode } from "react";
import { duplicateSection, elementKey, findElement, moveElementTo, moveSection, newSectionKey, removeElement, sameScope, updateElement, updateSection } from "./layoutEdits";

export interface DesignerRelationship {
  formRelationshipDefinitionId: string;
  relationshipDisplayName: string;
  childFormDefinitionName: string;
}

/**
 * What the inspector shows: a placed element, a section, or an empty "+ Add Field" cell (exact insertion position).
 * A slot's `slotId` disambiguates several empty cells that share the same `insertIndex` — the four independent
 * columns "+ Add Row" creates all insert at the same position until one of them is filled, so `insertIndex` alone
 * cannot tell them apart (Phase C3 usability corrections: this was the root cause of all four columns highlighting
 * together, and of clicks on one column sometimes acting on another).
 */
export type DesignerSelection = { kind: "element"; key: string } | { kind: "section"; sectionKey: string } | { kind: "slot"; sectionKey: string; insertIndex: number; slotId: string; startOnNewRow?: boolean | undefined } | null;

export interface DesignerCanvasProps {
  layout: FormLayout;
  onChange: (layout: FormLayout) => void;
  fields: readonly RendererField[];
  relationships: readonly DesignerRelationship[];
  readOnly?: boolean | undefined;
  selection: DesignerSelection;
  onSelect: (selection: DesignerSelection) => void;
  /**
   * Additional empty placement cells per section, designer-only and never persisted, each with a stable `id` so it
   * can be independently selected, highlighted and later consumed by filling it with a field. "+ Add Column" adds
   * one 1-column cell at the target position. "+ Add Row" adds four independent 1-column cells at the end of the
   * target section — the same single-column building block "+ Add Column" uses, reusing the existing grid/packing
   * semantics rather than a separate structure — with only the first one marked `startOnNewRow: true` so the row
   * always starts fresh (the packer, `packElements`, then lays the other three immediately after it; together they
   * fill exactly one row of four).
   */
  extraSlots?: Readonly<Record<string, readonly { id: string; index: number; startOnNewRow?: boolean }[]>> | undefined;
  collapsed: ReadonlySet<string>;
  onToggleCollapse: (sectionKey: string) => void;
  /** Section whose title is being renamed inline (set right after "+ Add Section"). */
  renamingSectionKey: string | null;
  onRenamingSectionChange: (sectionKey: string | null) => void;
  /** Opens the explicit field-placement disposition dialog (never removes placements silently). */
  onRequestRemoveSection: (sectionIndex: number) => void;
  /** Opens the Configure Field modal directly for the given element (canvas tile's Settings icon). */
  onOpenConfig?: ((key: string) => void) | undefined;
  /** Polite status announcements (moves, renames) for assistive technology. */
  announce?: ((message: string) => void) | undefined;
}

const TYPE_LABEL: Record<string, string> = { String: "Text", Integer: "Whole number", Decimal: "Decimal", Date: "Date", DateTime: "Date and time", Guid: "Identifier", Boolean: "Yes/No toggle", Option: "Dropdown", File: "Document collection" };

/**
 * Designer canvas (PG-033, C1 designer rebuild): the form as a stack of titled, collapsible sections, each with its own
 * four-column grid of compact field previews. A tile is selected by clicking (or Enter/Space on its name); the
 * inspector — not the tile — owns property editing. Double-click a name to rename the display label inline. Empty
 * "+ Add Field" cells mark exact insertion positions. Tiles move by drag-and-drop between and within sections, or
 * without a pointer through Alt+Arrow keys and the tile's "…" menu. Previews are inert pictures: there is no record
 * data entry in Design mode, and nothing here can change a FieldDefinitionId, field key, storage type or stored value.
 */
export function DesignerCanvas(props: DesignerCanvasProps) {
  const { layout, readOnly = false } = props;
  const dragKey = useRef<string | null>(null);
  // A tile moved with the keyboard into another section remounts; this tells the new instance to take focus back.
  const focusKey = useRef<string | null>(null);
  const [dropTarget, setDropTarget] = useState<string | null>(null);
  const fieldById = new Map(props.fields.map((field) => [field.fieldDefinitionId, field]));
  const relationshipById = new Map(props.relationships.map((relationship) => [relationship.formRelationshipDefinitionId, relationship]));

  const drag = {
    start: (key: string) => (event: DragEvent) => {
      dragKey.current = key;
      event.dataTransfer.effectAllowed = "move";
      event.dataTransfer.setData("text/plain", key);
    },
    over: (target: string) => (event: DragEvent) => {
      if (dragKey.current) {
        event.preventDefault();
        event.stopPropagation();
        event.dataTransfer.dropEffect = "move";
        setDropTarget(target);
      }
    },
    drop: (sectionIndex: number, insertIndex: number) => (event: DragEvent) => {
      event.preventDefault();
      event.stopPropagation();
      const key = dragKey.current;
      dragKey.current = null;
      setDropTarget(null);
      if (!key) {
        return;
      }
      const next = moveElementTo(layout, key, sectionIndex, insertIndex);
      if (next !== layout) {
        props.onChange(next);
        props.onSelect({ kind: "element", key });
        props.announce?.(`Moved to ${sectionTitle(layout.sections[sectionIndex]!)}`);
      }
    },
    end: () => {
      dragKey.current = null;
      setDropTarget(null);
    },
  };

  return (
    <div className="flex min-w-0 flex-col gap-4" onDragEnd={drag.end}>
      {layout.sections.map((section, sectionIndex) => (
        <SectionCard key={section.sectionKey} section={section} sectionIndex={sectionIndex} props={props} focusKey={focusKey} fieldById={fieldById} relationshipById={relationshipById} readOnly={readOnly} drag={drag} dropTarget={dropTarget} />
      ))}
    </div>
  );
}

type Drag = {
  start: (key: string) => (event: DragEvent) => void;
  over: (target: string) => (event: DragEvent) => void;
  drop: (sectionIndex: number, insertIndex: number) => (event: DragEvent) => void;
};

type Item = { type: "element"; element: LayoutElement; index: number; layout: LayoutElement["layout"] } | { type: "slot"; insertIndex: number; slotId: string; startOnNewRow?: boolean; layout: LayoutElement["layout"] };

/**
 * The canvas shows the authored four-column desktop arrangement whenever a section is wide enough to draw four usable
 * tiles (≥ 560 px), even though that is narrower than the record renderer's 1024 px desktop breakpoint: the designer
 * edits the desktop layout. Narrower canvases (tablet, phone, zoom) fall back to the renderer's 2- and 1-column
 * mapping, so what is drawn is always a real packing of the same deterministic packer.
 */
function useCanvasBreakpoint(): [React.RefObject<HTMLOListElement | null>, Breakpoint] {
  const ref = useRef<HTMLOListElement | null>(null);
  const [breakpoint, setBreakpoint] = useState<Breakpoint>("desktop");
  useEffect(() => {
    if (!ref.current || typeof ResizeObserver === "undefined") {
      return;
    }
    const observer = new ResizeObserver((entries) => {
      const width = entries[0]?.contentRect.width ?? 1024;
      setBreakpoint(width >= 560 ? "desktop" : width >= 360 ? "tablet" : "phone");
    });
    observer.observe(ref.current);
    return () => observer.disconnect();
  }, []);
  return [ref, breakpoint];
}

function sectionTitle(section: LayoutSection): string {
  return section.title?.trim() || section.sectionKey;
}

function SectionCard({ section, sectionIndex, props, focusKey, fieldById, relationshipById, readOnly, drag, dropTarget }: { section: LayoutSection; sectionIndex: number; props: DesignerCanvasProps; focusKey: React.RefObject<string | null>; fieldById: Map<string, RendererField>; relationshipById: Map<string, DesignerRelationship>; readOnly: boolean; drag: Drag; dropTarget: string | null }) {
  const { layout, selection } = props;
  const [gridRef, breakpoint] = useCanvasBreakpoint();
  const title = sectionTitle(section);
  const collapsed = props.collapsed.has(section.sectionKey);
  const renaming = props.renamingSectionKey === section.sectionKey;
  const selected = selection?.kind === "section" && selection.sectionKey === section.sectionKey;
  // A field or empty column selected inside this section gets its own (stronger) outline; the containing section
  // still shows a visually distinct, lighter outline so its boundary stays clear without implying it is itself the
  // selected object (Phase C3 usability corrections).
  const containsSelection = (selection?.kind === "element" && findElement(layout, selection.key)?.sectionIndex === sectionIndex) || (selection?.kind === "slot" && selection.sectionKey === section.sectionKey);
  const headingId = `designer-section-${section.sectionKey}`;
  const bodyId = `${headingId}-body`;

  // Placement cells: the trailing "+ Add Field" cell (always 1 column, stable id "baseline") plus any extra cells
  // added with "+ Add Column" (one 1-column cell) or "+ Add Row" (four independent 1-column cells), in element order.
  const slots = readOnly
    ? []
    : [...(props.extraSlots?.[section.sectionKey] ?? []).map((slot) => ({ id: slot.id, index: Math.min(slot.index, section.elements.length), startOnNewRow: slot.startOnNewRow === true })), { id: "baseline", index: section.elements.length, startOnNewRow: false }].sort((a, b) => a.index - b.index);
  const items: Item[] = [];
  const pushSlots = (index: number) => {
    for (const slot of slots.filter((candidate) => candidate.index === index)) {
      items.push({ type: "slot", insertIndex: slot.index, slotId: slot.id, startOnNewRow: slot.startOnNewRow, layout: { columnSpan: 1, startOnNewRow: slot.startOnNewRow } });
    }
  };
  section.elements.forEach((element, index) => {
    pushSlots(index);
    items.push({ type: "element", element, index, layout: element.layout });
  });
  pushSlots(section.elements.length);
  const packed = packElements(items, breakpoint);
  const columns = visibleColumns(breakpoint);

  const sectionMenu = readOnly
    ? []
    : [
        { key: "rename", label: "Rename section", run: () => props.onRenamingSectionChange(section.sectionKey) },
        { key: "up", label: "Move section up", disabled: sectionIndex === 0, run: () => props.onChange(moveSection(layout, sectionIndex, sectionIndex - 1)) },
        { key: "down", label: "Move section down", disabled: sectionIndex === layout.sections.length - 1, run: () => props.onChange(moveSection(layout, sectionIndex, sectionIndex + 1)) },
        {
          key: "duplicate",
          label: "Duplicate section (without fields)",
          run: () => {
            const key = newSectionKey(layout);
            props.onChange(duplicateSection(layout, sectionIndex, key));
            props.onSelect({ kind: "section", sectionKey: key });
          },
        },
        { key: "remove", label: "Remove section…", disabled: layout.sections.length <= 1, run: () => props.onRequestRemoveSection(sectionIndex) },
      ];

  return (
    <section
      aria-labelledby={headingId}
      className={`min-w-0 rounded-panel border bg-surface ${selected ? "border-focus ring-2 ring-focus" : containsSelection ? "border-focus" : "border-line"} ${dropTarget === `section:${section.sectionKey}` ? "outline-2 outline-dashed outline-focus" : ""}`}
      onDragOver={readOnly ? undefined : drag.over(`section:${section.sectionKey}`)}
      onDrop={readOnly ? undefined : drag.drop(sectionIndex, section.elements.length)}
    >
      <div className="flex min-h-12 items-center gap-1 border-b border-line px-2 py-1">
        <SharedIconButton
          label={`${collapsed ? "Expand" : "Collapse"} ${title}`}
          aria-expanded={!collapsed}
          aria-controls={bodyId}
          onClick={() => props.onToggleCollapse(section.sectionKey)}
        >
          <Chevron open={!collapsed} />
        </SharedIconButton>
        <h2 id={headingId} className="min-w-0 flex-1 text-lg font-semibold">
          {renaming ? (
            <InlineText
              label="Rename section"
              initial={section.title ?? ""}
              maxLength={200}
              selectAll
              onCommit={(value) => {
                props.onRenamingSectionChange(null);
                if (value && value !== section.title) {
                  props.onChange(updateSection(layout, sectionIndex, { title: value }));
                  props.announce?.(`Section renamed to ${value}`);
                }
              }}
              onCancel={() => props.onRenamingSectionChange(null)}
            />
          ) : (
            <button
              type="button"
              className="max-w-full truncate rounded-control px-1 text-left hover:bg-page"
              aria-pressed={selected}
              onClick={(event) => {
                event.stopPropagation();
                props.onSelect({ kind: "section", sectionKey: section.sectionKey });
              }}
              onDoubleClick={() => !readOnly && props.onRenamingSectionChange(section.sectionKey)}
              title={readOnly ? undefined : "Select the section; double-click to rename"}
            >
              {title}
            </button>
          )}
        </h2>
        {section.repeat ? (
          <span className="shrink-0 rounded-control border border-line px-1.5 py-0.5 text-xs" data-repeat-badge title="People can add several entries to this section; each entry has its own values.">
            Multiple entries · {section.repeat.minOccurrences}–{section.repeat.maxOccurrences}
          </span>
        ) : null}
        {collapsed ? <span className="shrink-0 text-sm text-muted">{section.elements.length === 1 ? "1 item" : `${section.elements.length} items`}</span> : null}
        {sectionMenu.length > 0 ? <MenuButton label={`Section actions for ${title}`} items={sectionMenu} /> : null}
      </div>
      <div
        id={bodyId}
        data-section-body="true"
        hidden={collapsed}
        className="p-3"
        onClick={(event) => {
          // Clicking empty section area (not a field tile, slot cell or other control) selects the section, the
          // same as clicking its header — both become the target for "+ Add Row".
          if (!(event.target as HTMLElement).closest("button,input,[role=menu]")) {
            props.onSelect({ kind: "section", sectionKey: section.sectionKey });
          }
        }}
      >
        {section.description ? <p className="mb-3 text-sm text-muted">{section.description}</p> : null}
        {section.elements.length === 0 && readOnly ? <p className="text-sm text-muted">No fields in this section.</p> : null}
        <ol ref={gridRef} aria-label={`Fields in ${title}`} className="grid gap-3" style={{ gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))` }} data-breakpoint={breakpoint}>
          {packed.map((cell) => {
            const style = { gridColumn: `${cell.column} / span ${cell.span}`, gridRow: cell.row };
            if (cell.element.type === "slot") {
              const slot = cell.element;
              const isSelected = selection?.kind === "slot" && selection.sectionKey === section.sectionKey && selection.slotId === slot.slotId;
              const target = `slot:${section.sectionKey}:${slot.slotId}`;
              return (
                <li key={slot.slotId} style={style} className="min-w-0" onDragOver={drag.over(target)} onDrop={drag.drop(sectionIndex, slot.insertIndex)}>
                  <button
                    type="button"
                    aria-pressed={isSelected}
                    aria-label={`Add Field to ${title}, position ${slot.insertIndex + 1}`}
                    onClick={(event) => {
                      // A parent section/row selection must never intercept a column's own click (Phase C3 usability
                      // corrections): stop here even though the section-body handler already skips buttons.
                      event.stopPropagation();
                      props.onSelect({ kind: "slot", sectionKey: section.sectionKey, insertIndex: slot.insertIndex, slotId: slot.slotId, startOnNewRow: slot.startOnNewRow });
                    }}
                    className={`flex h-full min-h-20 w-full flex-col items-center justify-center gap-1 rounded-control border-2 border-dashed text-sm font-medium text-body hover:bg-page ${isSelected || dropTarget === target ? "border-focus bg-page" : "border-input"}`}
                  >
                    <span aria-hidden="true" className="text-xl leading-none">
                      +
                    </span>
                    <span>Add Field</span>
                  </button>
                </li>
              );
            }
            const { element, index } = cell.element;
            const key = elementKey(element);
            return (
              <FieldTile
                key={key}
                style={style}
                element={element}
                index={index}
                section={section}
                sectionIndex={sectionIndex}
                field={element.kind === "relatedFormList" ? undefined : fieldById.get(element.fieldDefinitionId)}
                relationship={element.kind === "relatedFormList" ? relationshipById.get(element.formRelationshipDefinitionId) : undefined}
                selected={selection?.kind === "element" && selection.key === key}
                dropActive={dropTarget === `element:${key}`}
                props={props}
                focusKey={focusKey}
                readOnly={readOnly}
                drag={drag}
              />
            );
          })}
        </ol>
      </div>
    </section>
  );
}

function FieldTile({ style, element, index, section, sectionIndex, field, relationship, selected, dropActive, props, focusKey, readOnly, drag }: { style: React.CSSProperties; element: LayoutElement; index: number; section: LayoutSection; sectionIndex: number; field: RendererField | undefined; relationship: DesignerRelationship | undefined; selected: boolean; dropActive: boolean; props: DesignerCanvasProps; focusKey: React.RefObject<string | null>; readOnly: boolean; drag: Drag }) {
  const { layout } = props;
  const key = elementKey(element);
  const [renaming, setRenaming] = useState(false);
  const label = displayLabel(element, field, relationship);
  const kind = element.kind === "relatedFormList" ? "Related list" : element.kind === "documentCollection" ? "Document collection" : `${TYPE_LABEL[field?.fieldDataType ?? ""] ?? "Field"} field`;
  const span = element.layout.columnSpan;
  const select = (event?: { stopPropagation: () => void }) => {
    event?.stopPropagation(); // a field's own click must never also select its containing section (Phase C3 usability corrections)
    props.onSelect({ kind: "element", key });
  };
  const move = (targetSectionIndex: number, insertIndex: number, message: string) => {
    const next = moveElementTo(layout, key, targetSectionIndex, insertIndex);
    if (next !== layout) {
      props.onChange(next);
      props.onSelect({ kind: "element", key });
      props.announce?.(message);
    }
  };
  const setSpan = (value: number) => {
    const columnSpan = Math.min(4, Math.max(1, value)) as 1 | 2 | 3 | 4;
    props.onChange(updateElement(layout, sectionIndex, index, (current) => ({ ...current, layout: { ...current.layout, columnSpan } })));
    props.announce?.(`${label} width ${columnSpan} of 4 columns`);
  };
  const onKeyDown = (event: KeyboardEvent) => {
    if (readOnly || !event.altKey) {
      return;
    }
    focusKey.current = key;
    if (event.key === "ArrowUp" || event.key === "ArrowLeft") {
      event.preventDefault();
      if (index > 0) {
        move(sectionIndex, index - 1, `${label} moved earlier`);
      } else if (sectionIndex > 0) {
        move(sectionIndex - 1, layout.sections[sectionIndex - 1]!.elements.length, `${label} moved to ${sectionTitle(layout.sections[sectionIndex - 1]!)}`);
      }
    } else if (event.key === "ArrowDown" || event.key === "ArrowRight") {
      event.preventDefault();
      if (index < section.elements.length - 1) {
        move(sectionIndex, index + 2, `${label} moved later`);
      } else if (sectionIndex < layout.sections.length - 1) {
        move(sectionIndex + 1, 0, `${label} moved to ${sectionTitle(layout.sections[sectionIndex + 1]!)}`);
      }
    }
  };
  // Keep focus on a tile moved with Alt+Arrow so repeated presses keep working, even across sections.
  const nameRef = useRef<HTMLButtonElement | null>(null);
  useEffect(() => {
    if (focusKey.current === key && document.activeElement !== nameRef.current) {
      focusKey.current = null;
      nameRef.current?.focus();
    } else if (focusKey.current === key) {
      focusKey.current = null;
    }
  });

  const menu = readOnly
    ? []
    : [
        { key: "earlier", label: "Move earlier", disabled: index === 0, run: () => move(sectionIndex, index - 1, `${label} moved earlier`) },
        { key: "later", label: "Move later", disabled: index === section.elements.length - 1, run: () => move(sectionIndex, index + 2, `${label} moved later`) },
        ...layout.sections
          .map((candidate, candidateIndex) => ({ candidate, candidateIndex }))
          .filter(({ candidate, candidateIndex }) => candidateIndex !== sectionIndex && sameScope(candidate, section))
          .map(({ candidate, candidateIndex }) => ({ key: `to-${candidate.sectionKey}`, label: `Move to ${sectionTitle(candidate)}`, run: () => move(candidateIndex, candidate.elements.length, `${label} moved to ${sectionTitle(candidate)}`) })),
        { key: "narrower", label: "Narrower", disabled: span <= 1, run: () => setSpan(span - 1) },
        { key: "wider", label: "Wider", disabled: span >= 4, run: () => setSpan(span + 1) },
        { key: "rename", label: "Rename label", run: () => setRenaming(true) },
        { key: "remove", label: "Remove from layout", run: () => removeFromLayout() },
      ];
  function removeFromLayout() {
    props.onChange(removeElement(layout, sectionIndex, index));
    props.onSelect(null);
    props.announce?.(`${label} removed from the layout. The field and its stored values are kept.`);
  }

  return (
    <li
      style={style}
      className="min-w-0"
      draggable={!readOnly && !renaming}
      onDragStart={readOnly ? undefined : drag.start(key)}
      onDragOver={readOnly ? undefined : drag.over(`element:${key}`)}
      onDrop={readOnly ? undefined : drag.drop(sectionIndex, index)}
    >
      <div
        className={`relative flex h-full min-h-20 flex-col gap-2 rounded-control border bg-surface p-3 ${selected ? "border-focus ring-2 ring-focus" : "border-line hover:border-input"} ${dropActive ? "outline-2 outline-dashed outline-focus" : ""} ${readOnly ? "" : "cursor-grab"}`}
        onClick={(event) => {
          if (!(event.target as HTMLElement).closest("button,input,[role=menu]")) {
            select();
          }
        }}
      >
        <div className="flex min-w-0 items-start gap-1">
          {renaming ? (
            <InlineText
              label="Rename field label"
              initial={element.label || label}
              maxLength={200}
              selectAll
              onCommit={(value) => {
                setRenaming(false);
                if (value && value !== element.label) {
                  props.onChange(updateElement(layout, sectionIndex, index, (current) => ({ ...current, label: value })));
                  props.announce?.(`Label changed to ${value}`);
                }
              }}
              onCancel={() => setRenaming(false)}
            />
          ) : (
            <button
              ref={nameRef}
              type="button"
              aria-pressed={selected}
              aria-describedby={`${key}-desc`}
              aria-keyshortcuts={readOnly ? undefined : "Alt+ArrowUp Alt+ArrowDown"}
              className="min-w-0 flex-1 truncate rounded-control text-left text-sm font-semibold text-body"
              onClick={select}
              onDoubleClick={() => !readOnly && setRenaming(true)}
              onKeyDown={onKeyDown}
              title={readOnly ? undefined : "Click to select; double-click to rename; drag or Alt+Arrow keys to move"}
            >
              {label}
              {element.kind !== "relatedFormList" && element.required ? (
                <span aria-hidden="true" className="text-danger">
                  {" "}
                  *
                </span>
              ) : null}
            </button>
          )}
          {selected && !readOnly && !renaming ? (
            <div role="group" aria-label={`Actions for ${label}`} className="absolute -top-4 right-2 z-10 flex items-center gap-0.5 rounded-control border border-line bg-surface px-0.5 shadow-sm">
              {props.onOpenConfig ? (
                <IconButton label={`Configure ${label}`} onClick={() => props.onOpenConfig!(key)}>
                  <GearIcon />
                </IconButton>
              ) : null}
              <MenuButton label={`More actions for ${label}`} items={menu} compact />
              <IconButton label={`Remove ${label} from layout`} onClick={removeFromLayout} danger>
                <TrashIcon />
              </IconButton>
            </div>
          ) : null}
        </div>
        <span id={`${key}-desc`} className="sr-only">
          {kind}, {span} of 4 columns{element.kind !== "relatedFormList" && element.required ? ", required" : ""}
          {behaviorFlags(element, field).map((flag) => `, ${flag.description}`).join("")}, in {sectionTitle(section)}
        </span>
        {/* The preview resembles the actual record form (control first); badges are designer-only metadata, shown
            below the control like Required's "*", never mistaken for a form value. */}
        <TilePreview element={element} field={field} relationship={relationship} />
        <TileBadges element={element} field={field} />
      </div>
    </li>
  );
}

interface BehaviorFlag {
  key: string;
  label: string;
  description: string;
  /** Success (green) styling for the Encrypted badge; other flags keep the neutral style. */
  tone?: "success";
}

/**
 * Rule, behavior and protection settings that change how a field acts (or is protected) in the data-entry form, as
 * visible (never color-only) text below the control. `field` carries the catalog's current protection mode
 * (`RendererField.applicationEncrypted`, the same source the Fields catalog table's "Protection" column and the
 * Configure Field modal's Security & Encryption tab read) — this is the field's actual current protection mode, not
 * a claim that any particular stored record has finished an encryption migration (G19/COMPLETENESS_NOTE); it works
 * the same for a field placed inside a repeating section, since the canvas shows the field's placement once
 * regardless of how many entries a published record ends up with.
 */
function behaviorFlags(element: LayoutElement, field: RendererField | undefined): BehaviorFlag[] {
  if (element.kind === "relatedFormList") {
    return [];
  }
  const flags: BehaviorFlag[] = [];
  if (element.kind === "field") {
    if (element.visibleWhen) {
      flags.push({ key: "visible-rule", label: "Conditional", description: "shown only when a visibility rule holds" });
    }
    if (element.requiredWhen) {
      flags.push({ key: "required-rule", label: "Required if", description: "required when a rule holds" });
    }
    if (element.showInForm === false) {
      flags.push({ key: "not-in-form", label: "Not in form", description: "not shown in the data-entry form" });
    }
    if (element.readOnly) {
      flags.push({ key: "read-only", label: "Read-only", description: "read-only" });
    }
  }
  if (field?.applicationEncrypted === true) {
    flags.push({ key: "encrypted", label: "Encrypted", description: "application-encrypted: protected at rest, not searchable", tone: "success" });
  }
  return flags;
}

function TileBadges({ element, field }: { element: LayoutElement; field: RendererField | undefined }) {
  const flags = behaviorFlags(element, field);
  if (flags.length === 0) {
    return null;
  }
  return (
    <ul aria-hidden="true" className="flex flex-wrap gap-1" data-tile-flags="true">
      {flags.map((flag) => (
        <li
          key={flag.key}
          data-flag={flag.key}
          title={flag.description}
          className={`flex items-center gap-1 rounded-control border px-1.5 py-0.5 text-xs ${flag.tone === "success" ? "border-[#166534] bg-[#f0fdf4] text-[#14532d] font-medium" : "border-line bg-page text-body"}`}
        >
          {flag.key === "encrypted" ? <LockIcon /> : null}
          {flag.label}
        </li>
      ))}
    </ul>
  );
}

function displayLabel(element: LayoutElement, field: RendererField | undefined, relationship: DesignerRelationship | undefined): string {
  const catalogName = element.kind === "relatedFormList" ? relationship?.relationshipDisplayName ?? "Related list" : field?.fieldDisplayName ?? "Unknown field";
  return element.label.trim() || catalogName;
}

/** Inert, decorative picture of the control (aria-hidden): never a real input, so Design mode cannot capture record data. */
function TilePreview({ element, field, relationship }: { element: LayoutElement; field: RendererField | undefined; relationship: DesignerRelationship | undefined }) {
  const box = "flex min-h-9 w-full items-center justify-between gap-2 rounded-control border border-input bg-surface px-2.5 text-sm text-muted";
  let body: ReactNode;
  if (element.kind === "relatedFormList") {
    body = (
      <div className="flex flex-col gap-1 rounded-control border border-dashed border-input p-2 text-xs text-muted">
        <span>Child records: {relationship?.childFormDefinitionName ?? "child form"}</span>
        <span className="h-1.5 w-3/4 rounded-control bg-page" />
        <span className="h-1.5 w-1/2 rounded-control bg-page" />
      </div>
    );
  } else if (element.kind === "documentCollection") {
    body = (
      <div className="flex min-h-9 items-center gap-2 rounded-control border border-dashed border-input px-2.5 text-sm text-muted">
        <UploadIcon />
        <span className="truncate">Up to {element.upload?.maxFiles ?? 20} documents</span>
      </div>
    );
  } else {
    const placeholder = element.placeholder;
    switch (field?.fieldDataType) {
      case "Boolean":
        body = (
          <span className="inline-flex h-6 w-11 items-center rounded-full bg-primary p-0.5">
            <span className="ml-auto h-5 w-5 rounded-full bg-primary-foreground" />
          </span>
        );
        break;
      case "Option":
        body = (
          <span className={box}>
            <span className="truncate">{placeholder || (field.optionSelectionMode === "Multiple" ? "Select several…" : "Select…")}</span>
            <Chevron open />
          </span>
        );
        break;
      case "Date":
      case "DateTime":
        body = (
          <span className={box}>
            <span className="truncate">{field.fieldDataType === "Date" ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm"}</span>
            <CalendarIcon />
          </span>
        );
        break;
      default:
        body = <span className={`${box} ${element.kind === "field" && element.control === "textarea" ? "min-h-16 items-start pt-1.5" : ""}`}>{placeholder ? <span className="truncate">{placeholder}</span> : null}</span>;
    }
  }
  return (
    <div aria-hidden="true" className="pointer-events-none select-none">
      {body}
    </div>
  );
}

function InlineText({ label, initial, maxLength, selectAll, onCommit, onCancel }: { label: string; initial: string; maxLength: number; selectAll?: boolean; onCommit: (value: string) => void; onCancel: () => void }) {
  const [value, setValue] = useState(initial);
  const ref = useRef<HTMLInputElement | null>(null);
  const done = useRef(false);
  useEffect(() => {
    ref.current?.focus();
    if (selectAll) {
      ref.current?.select();
    }
  }, [selectAll]);
  const commit = () => {
    if (!done.current) {
      done.current = true;
      onCommit(value.trim());
    }
  };
  return (
    <input
      ref={ref}
      aria-label={label}
      value={value}
      maxLength={maxLength}
      className="min-h-control-compact w-full min-w-0 rounded-control border border-input bg-surface px-2 text-base font-semibold text-body"
      onChange={(event) => setValue(event.target.value.replace(/[<>]/g, ""))}
      onKeyDown={(event) => {
        if (event.key === "Enter") {
          event.preventDefault();
          commit();
        } else if (event.key === "Escape") {
          event.preventDefault();
          done.current = true;
          onCancel();
        }
      }}
      onBlur={commit}
    />
  );
}

function MenuButton({ label, items, compact = false }: { label: string; items: Array<{ key: string; label: string; disabled?: boolean; run: () => void }>; compact?: boolean }) {
  return (
    <span className={compact ? "[&_button]:min-h-8 [&_button]:border-transparent [&_button]:px-2" : undefined}>
      <OverflowMenu
        label={label}
        items={items.map((item) => ({
          key: item.key,
          content: (
            <button type="button" disabled={item.disabled} aria-disabled={item.disabled} onClick={item.run} className="disabled:opacity-50">
              {item.label}
            </button>
          ),
        }))}
      />
    </span>
  );
}

/** The shared compact icon button (packages/ui); kept as a local adapter for the tile toolbar's call sites. */
function IconButton({ label, onClick, children, danger = false }: { label: string; onClick: () => void; children: ReactNode; danger?: boolean }) {
  return (
    <SharedIconButton label={label} size="sm" tone={danger ? "danger" : "default"} onClick={onClick}>
      {children}
    </SharedIconButton>
  );
}

const svg = { width: 16, height: 16, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round", strokeLinejoin: "round", "aria-hidden": true } as const;

function Chevron({ open }: { open: boolean }) {
  return (
    <svg {...svg} className={open ? "" : "-rotate-90"}>
      <path d="M6 9l6 6 6-6" />
    </svg>
  );
}

function GearIcon() {
  return (
    <svg {...svg}>
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68 1.65 1.65 0 0 0 10 3.17V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
    </svg>
  );
}

function TrashIcon() {
  return (
    <svg {...svg}>
      <path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6M10 11v6M14 11v6" />
    </svg>
  );
}

function LockIcon() {
  return (
    <svg {...svg} width={12} height={12}>
      <rect x="4" y="11" width="16" height="9" rx="1.5" />
      <path d="M7 11V7a5 5 0 0 1 10 0v4" />
    </svg>
  );
}

function CalendarIcon() {
  return (
    <svg {...svg}>
      <rect x="3" y="4" width="18" height="18" rx="2" />
      <path d="M16 2v4M8 2v4M3 10h18" />
    </svg>
  );
}

function UploadIcon() {
  return (
    <svg {...svg}>
      <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12" />
    </svg>
  );
}

/** Pure helper for the toolbar: where a new "+ Add Column" cell goes for the current selection. */
export function columnSlotFor(layout: FormLayout, selection: DesignerSelection): { sectionKey: string; insertIndex: number } | null {
  if (selection?.kind === "element") {
    const position = findElement(layout, selection.key);
    return position ? { sectionKey: layout.sections[position.sectionIndex]!.sectionKey, insertIndex: position.index + 1 } : null;
  }
  if (selection?.kind === "slot") {
    return { sectionKey: selection.sectionKey, insertIndex: selection.insertIndex };
  }
  if (selection?.kind === "section") {
    const section = layout.sections.find((candidate) => candidate.sectionKey === selection.sectionKey);
    return section ? { sectionKey: section.sectionKey, insertIndex: section.elements.length } : null;
  }
  const last = layout.sections.at(-1);
  return last ? { sectionKey: last.sectionKey, insertIndex: last.elements.length } : null;
}
