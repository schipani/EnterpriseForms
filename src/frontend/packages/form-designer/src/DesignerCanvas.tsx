import { packElements, visibleColumns, type Breakpoint, type FormLayout, type LayoutElement, type LayoutSection, type RendererField } from "@enterpriseforms/form-renderer";
import { IconButton as SharedIconButton, OverflowMenu } from "@enterpriseforms/ui";
import { useEffect, useRef, useState, type DragEvent, type KeyboardEvent, type ReactNode } from "react";
import { appendRow, emptyCells, fieldCells, isEmptyRow, maxSpanFor, moveByColumn, moveToCell, moveToNewRow, neighbourKey, placementProblem, refusalMessage, removeFromGrid, sectionGrid, setSpan, swapElements, type GridEditResult } from "./gridEdits";
import { duplicateSection, elementKey, findElement, moveSection, newSectionKey, sameScope, updateElement, updateSection } from "./layoutEdits";
import type { GridOperation } from "./rowColumnEdits";

export interface DesignerRelationship {
  formRelationshipDefinitionId: string;
  relationshipDisplayName: string;
  childFormDefinitionName: string;
}

/**
 * What the inspector shows: a placed element, a section, or an empty "+ Add Field" cell. FD-01: a cell is an exact desktop
 * grid position (`row`, `column`) of its section — an empty cell of a row with fields, or a cell of an empty row ("+ Add
 * Row", "Clear row") — and `slotId` identifies it uniquely (so several empty cells of one row are independently
 * selectable, Phase C3 usability corrections).
 */
export type DesignerSelection = { kind: "element"; key: string } | { kind: "section"; sectionKey: string } | { kind: "slot"; sectionKey: string; slotId: string; row: number; column: number } | null;

export interface DesignerCanvasProps {
  layout: FormLayout;
  onChange: (layout: FormLayout) => void;
  fields: readonly RendererField[];
  relationships: readonly DesignerRelationship[];
  readOnly?: boolean | undefined;
  selection: DesignerSelection;
  onSelect: (selection: DesignerSelection) => void;
  /** A placement edit was refused (occupied cell, not enough room); the page shows the message. */
  onRefused?: ((message: string) => void) | undefined;
  /** Clear/Delete row or column (FD-01 row management); the page confirms before any field is removed. */
  onRequestGridOperation?: ((operation: GridOperation) => void) | undefined;
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
  const [dropTarget, setDropTarget] = useState<DropTarget | null>(null);
  const fieldById = new Map(props.fields.map((field) => [field.fieldDefinitionId, field]));
  const relationshipById = new Map(props.relationships.map((relationship) => [relationship.formRelationshipDefinitionId, relationship]));

  // Applies a grid edit; a refusal changes nothing and is reported (never a silent overwrite or displacement).
  const apply: Apply = (result, key, message) => {
    if (!result.ok) {
      const text = refusalMessage(result.reason);
      props.onRefused?.(text);
      props.announce?.(text);
      return;
    }
    if (result.layout !== layout) {
      props.onChange(result.layout);
      props.onSelect({ kind: "element", key });
      props.announce?.(message);
    }
  };
  const draggedElement = () => (dragKey.current ? findElement(layout, dragKey.current) : null);

  const drag: Drag = {
    start: (key: string) => (event: DragEvent) => {
      dragKey.current = key;
      event.dataTransfer.effectAllowed = "move";
      event.dataTransfer.setData("text/plain", key);
    },
    // Only valid targets accept the drop (preventDefault); an invalid one shows a "no drop" cursor and a distinct outline.
    over: (target, valid) => (event: DragEvent) => {
      const dragged = draggedElement();
      if (!dragged || !dragKey.current) {
        return;
      }
      event.stopPropagation();
      const ok = valid({ key: dragKey.current, sectionIndex: dragged.sectionIndex, span: dragged.element.layout.columnSpan });
      if (ok) {
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
      } else {
        event.dataTransfer.dropEffect = "none";
      }
      setDropTarget((current) => (current?.id === target && current.valid === ok ? current : { id: target, valid: ok }));
    },
    drop: (run) => (event: DragEvent) => {
      event.preventDefault();
      event.stopPropagation();
      const key = dragKey.current;
      dragKey.current = null;
      setDropTarget(null);
      if (key) {
        run(key);
      }
    },
    end: () => {
      dragKey.current = null;
      setDropTarget(null);
    },
    toCell: (sectionIndex, row, column) => (key) => apply(moveToCell(layout, key, sectionIndex, row, column), key, `Moved to ${sectionTitle(layout.sections[sectionIndex]!)}, row ${row}, column ${column}`),
    toSection: (sectionIndex) => (key) => apply(moveToNewRow(layout, key, sectionIndex), key, `Moved to a new row in ${sectionTitle(layout.sections[sectionIndex]!)}`),
    onto: (targetKey) => (key) => (key === targetKey ? undefined : apply(swapElements(layout, key, targetKey), key, "Fields swapped")),
  };

  return (
    <div className="flex min-w-0 flex-col gap-4" onDragEnd={drag.end}>
      {layout.sections.map((section, sectionIndex) => (
        <SectionCard key={section.sectionKey} section={section} sectionIndex={sectionIndex} props={props} focusKey={focusKey} fieldById={fieldById} relationshipById={relationshipById} readOnly={readOnly} drag={drag} dropTarget={dropTarget} apply={apply} />
      ))}
    </div>
  );
}

type DraggedInfo = { key: string; sectionIndex: number; span: number };
type DropTarget = { id: string; valid: boolean };
type Apply = (result: GridEditResult, key: string, message: string) => void;
type Drag = {
  start: (key: string) => (event: DragEvent) => void;
  over: (target: string, valid: (dragged: DraggedInfo) => boolean) => (event: DragEvent) => void;
  drop: (run: (key: string) => void) => (event: DragEvent) => void;
  end: () => void;
  toCell: (sectionIndex: number, row: number, column: number) => (key: string) => void;
  toSection: (sectionIndex: number) => (key: string) => void;
  onto: (targetKey: string) => (key: string) => void;
};

/** A drawn grid item: a placed element, or an empty cell at an exact desktop (row, column) of the section. */
type Item = { type: "element"; element: LayoutElement; index: number; row: number; column: number; span: number } | { type: "slot"; slotId: string; row: number; column: number };

function dropOutline(target: DropTarget | null, id: string): string {
  if (target?.id !== id) {
    return "";
  }
  return target.valid ? "outline-2 outline-dashed outline-focus" : "outline-2 outline-dashed outline-danger";
}

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

function SectionCard({ section, sectionIndex, props, focusKey, fieldById, relationshipById, readOnly, drag, dropTarget, apply }: { section: LayoutSection; sectionIndex: number; props: DesignerCanvasProps; focusKey: React.RefObject<string | null>; fieldById: Map<string, RendererField>; relationshipById: Map<string, DesignerRelationship>; readOnly: boolean; drag: Drag; dropTarget: DropTarget | null; apply: Apply }) {
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

  // FD-01: the canvas draws the section's canonical desktop grid (the same packer Preview and the published renderer use)
  // with every element at its exact (row, column). Empty "+ Add Field" cells are the empty cells of each row, including all
  // four cells of a designer-only empty row ("+ Add Row", "Clear row"); there is no separate trailing cell — "+ Add Row"
  // adds exactly one row, and filling a cell of an empty row turns it into a row of the section.
  const cells = sectionGrid(section);
  const desktop = breakpoint === "desktop";
  const elementItems: Item[] = fieldCells(cells).map((cell) => ({ type: "element", element: cell.element, index: cell.index, row: cell.row, column: cell.column, span: cell.span }));
  const slots: Item[] = readOnly ? [] : emptyCells(cells).filter(({ row }) => desktop || isEmptyRow(cells, row)).map(({ row, column }): Item => ({ type: "slot", slotId: `cell-${row}-${column}`, row, column }));
  // Reading/tab order is the visual order: row by row, column by column.
  const ordered = [...elementItems, ...slots].sort((a, b) => a.row - b.row || a.column - b.column);
  // Narrower canvases (tablet, phone, zoom) draw the renderer's reflow of the same elements (no empty desktop cells of rows
  // with fields), keeping the cells of empty rows in place; their placement targets are still exact desktop cells.
  const packed = desktop
    ? ordered.map((item) => ({ element: item, row: item.row, column: item.column, span: item.type === "element" ? item.span : 1 }))
    : packElements(
        ordered.map((item) => ({ item, layout: item.type === "element" ? item.element.layout : { columnSpan: 1, startOnNewRow: item.column === 1 } })),
        breakpoint,
      ).map((cell) => ({ element: cell.element.item, row: cell.row, column: cell.column, span: cell.span }));
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
      className={`min-w-0 rounded-panel border bg-surface ${selected ? "border-focus ring-2 ring-focus" : containsSelection ? "border-focus" : "border-line"} ${dropOutline(dropTarget, `section:${section.sectionKey}`)}`}
      onDragOver={readOnly ? undefined : drag.over(`section:${section.sectionKey}`, (dragged) => sameScope(layout.sections[dragged.sectionIndex]!, section))}
      onDrop={readOnly ? undefined : drag.drop(drag.toSection(sectionIndex))}
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
              const fits = (dragged: DraggedInfo) => sameScope(layout.sections[dragged.sectionIndex]!, section) && placementProblem(cells, slot.row, slot.column, dragged.span, dragged.key) === null;
              const dropState = dropTarget?.id === target ? (dropTarget.valid ? "valid" : "invalid") : null;
              return (
                <li key={slot.slotId} style={style} className="min-w-0" data-grid-row={slot.row} data-grid-column={slot.column} data-empty-row={isEmptyRow(cells, slot.row) ? "true" : undefined} onDragOver={drag.over(target, fits)} onDrop={drag.drop(drag.toCell(sectionIndex, slot.row, slot.column))}>
                  <button
                    type="button"
                    aria-pressed={isSelected}
                    aria-label={`Add Field to ${title}, row ${slot.row}, column ${slot.column}`}
                    onClick={(event) => {
                      // A parent section/row selection must never intercept a column's own click (Phase C3 usability
                      // corrections): stop here even though the section-body handler already skips buttons.
                      event.stopPropagation();
                      props.onSelect({ kind: "slot", sectionKey: section.sectionKey, slotId: slot.slotId, row: slot.row, column: slot.column });
                    }}
                    className={`flex h-full min-h-20 w-full flex-col items-center justify-center gap-1 rounded-control border-2 border-dashed text-sm font-medium text-body hover:bg-page ${isSelected || dropState === "valid" ? "border-focus bg-page" : dropState === "invalid" ? "border-danger" : "border-input"}`}
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
                position={{ row: cell.element.row, column: cell.element.column }}
                section={section}
                sectionIndex={sectionIndex}
                field={element.kind === "relatedFormList" ? undefined : fieldById.get(element.fieldDefinitionId)}
                relationship={element.kind === "relatedFormList" ? relationshipById.get(element.formRelationshipDefinitionId) : undefined}
                selected={selection?.kind === "element" && selection.key === key}
                dropOutline={dropOutline(dropTarget, `element:${key}`)}
                props={props}
                focusKey={focusKey}
                readOnly={readOnly}
                drag={drag}
                apply={apply}
              />
            );
          })}
        </ol>
      </div>
    </section>
  );
}

function FieldTile({ style, element, index, position, section, sectionIndex, field, relationship, selected, dropOutline: dropClass, props, focusKey, readOnly, drag, apply }: { style: React.CSSProperties; element: LayoutElement; index: number; position: { row: number; column: number }; section: LayoutSection; sectionIndex: number; field: RendererField | undefined; relationship: DesignerRelationship | undefined; selected: boolean; dropOutline: string; props: DesignerCanvasProps; focusKey: React.RefObject<string | null>; readOnly: boolean; drag: Drag; apply: Apply }) {
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
  // FD-01 grid moves. Earlier/later exchange places with the neighbouring field in reading order (crossing into the
  // previous/next section of the same scope at the ends); left/right move one column within the row. A move that would
  // overlap or leave the grid is refused and announced — nothing else moves.
  const previous = neighbourKey(layout, key, -1);
  const following = neighbourKey(layout, key, 1);
  const adjacentSection = (delta: -1 | 1) => {
    for (let candidate = sectionIndex + delta; candidate >= 0 && candidate < layout.sections.length; candidate += delta) {
      if (sameScope(layout.sections[candidate]!, section)) {
        return candidate;
      }
    }
    return null;
  };
  const moveEarlier = () => {
    if (previous) {
      apply(swapElements(layout, key, previous), key, `${label} moved earlier`);
      return;
    }
    const target = adjacentSection(-1);
    if (target !== null) {
      apply(moveToNewRow(layout, key, target), key, `${label} moved to ${sectionTitle(layout.sections[target]!)}`);
    }
  };
  const moveLater = () => {
    if (following) {
      apply(swapElements(layout, key, following), key, `${label} moved later`);
      return;
    }
    const target = adjacentSection(1);
    if (target !== null) {
      apply(moveToNewRow(layout, key, target, true), key, `${label} moved to ${sectionTitle(layout.sections[target]!)}`);
    }
  };
  const moveColumn = (delta: -1 | 1) => apply(moveByColumn(layout, key, delta), key, `${label} moved to column ${position.column + delta}`);
  const setWidth = (value: number) => {
    const columnSpan = Math.min(4, Math.max(1, value)) as 1 | 2 | 3 | 4;
    apply(setSpan(layout, key, columnSpan), key, `${label} width ${columnSpan} of 4 columns`);
  };
  const onKeyDown = (event: KeyboardEvent) => {
    if (readOnly || !event.altKey) {
      return;
    }
    focusKey.current = key;
    if (event.key === "ArrowUp") {
      event.preventDefault();
      moveEarlier();
    } else if (event.key === "ArrowDown") {
      event.preventDefault();
      moveLater();
    } else if (event.key === "ArrowLeft") {
      event.preventDefault();
      moveColumn(-1);
    } else if (event.key === "ArrowRight") {
      event.preventDefault();
      moveColumn(1);
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
        { key: "earlier", label: "Move earlier", disabled: previous === null, run: moveEarlier },
        { key: "later", label: "Move later", disabled: following === null, run: moveLater },
        { key: "left", label: "Move one column left", disabled: position.column <= 1, run: () => moveColumn(-1) },
        { key: "right", label: "Move one column right", disabled: position.column + span > 4, run: () => moveColumn(1) },
        ...layout.sections
          .map((candidate, candidateIndex) => ({ candidate, candidateIndex }))
          .filter(({ candidate, candidateIndex }) => candidateIndex !== sectionIndex && sameScope(candidate, section))
          .map(({ candidate, candidateIndex }) => ({ key: `to-${candidate.sectionKey}`, label: `Move to ${sectionTitle(candidate)}`, run: () => apply(moveToNewRow(layout, key, candidateIndex), key, `${label} moved to ${sectionTitle(candidate)}`) })),
        { key: "narrower", label: "Narrower", disabled: span <= 1, run: () => setWidth(span - 1) },
        { key: "wider", label: "Wider", disabled: span >= 4 || maxSpanFor(layout, key) <= span, run: () => setWidth(span + 1) },
        { key: "rename", label: "Rename label", run: () => setRenaming(true) },
        { key: "remove", label: "Remove from layout", run: () => removeFromLayout() },
        ...(props.onRequestGridOperation
          ? (
              [
                ["clearColumn", span > 1 ? `Clear columns ${position.column}–${position.column + span - 1}…` : `Clear column ${position.column}…`],
                ["deleteColumn", `Delete column ${position.column}…`],
                ["clearRow", `Clear row ${position.row}…`],
                ["deleteRow", `Delete row ${position.row}…`],
              ] as const
            ).map(([kind, text]) => ({ key: kind, label: text, run: () => props.onRequestGridOperation!({ kind, sectionKey: section.sectionKey, row: position.row, column: position.column }) }))
          : []),
      ];
  function removeFromLayout() {
    props.onChange(removeFromGrid(layout, key));
    props.onSelect(null);
    props.announce?.(`${label} removed from the layout. The field and its stored values are kept.`);
  }

  return (
    <li
      style={style}
      className="min-w-0"
      draggable={!readOnly && !renaming}
      data-grid-row={position.row}
      data-grid-column={position.column}
      data-grid-span={span}
      onDragStart={readOnly ? undefined : drag.start(key)}
      onDragOver={readOnly ? undefined : drag.over(`element:${key}`, (dragged) => dragged.key !== key && sameScope(layout.sections[dragged.sectionIndex]!, section))}
      onDrop={readOnly ? undefined : drag.drop(drag.onto(key))}
    >
      <div
        className={`relative flex h-full min-h-20 flex-col gap-2 rounded-control border bg-surface p-3 ${selected ? "border-focus ring-2 ring-focus" : "border-line hover:border-input"} ${dropClass} ${readOnly ? "" : "cursor-grab"}`}
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
              aria-keyshortcuts={readOnly ? undefined : "Alt+ArrowUp Alt+ArrowDown Alt+ArrowLeft Alt+ArrowRight"}
              className="min-w-0 flex-1 truncate rounded-control text-left text-sm font-semibold text-body"
              onClick={select}
              onDoubleClick={() => !readOnly && setRenaming(true)}
              onKeyDown={onKeyDown}
              title={readOnly ? undefined : "Click to select; double-click to rename; drag to an empty cell (or onto a field to swap), or Alt+Arrow keys to move"}
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
          {kind}, {span} of 4 columns, row {position.row}, column {position.column}{element.kind !== "relatedFormList" && element.required ? ", required" : ""}
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

/**
 * Pure helper for the toolbar ("New field…"): the exact desktop cell a new field goes to for the current selection — the
 * first empty cell to the right of a selected field in its row, the selected empty cell itself, or otherwise the first
 * trailing empty row (or a new row) at the end of the selected (or last) section.
 */
export function columnSlotFor(layout: FormLayout, selection: DesignerSelection): { sectionKey: string; slotId: string; row: number; column: number } | null {
  // The first of the section's trailing empty rows (consumed when the field is placed), otherwise a new row at the end.
  const trailing = (section: LayoutSection) => {
    const row = appendRow(sectionGrid(section));
    return { sectionKey: section.sectionKey, slotId: `cell-${row}-1`, row, column: 1 };
  };
  if (selection?.kind === "element") {
    const position = findElement(layout, selection.key);
    if (!position) {
      return null;
    }
    const section = layout.sections[position.sectionIndex]!;
    const cells = sectionGrid(section);
    const cell = cells.find((candidate) => candidate.key === selection.key)!;
    const gap = emptyCells(cells).find((candidate) => candidate.row === cell.row && candidate.column > cell.column);
    return gap ? { sectionKey: section.sectionKey, slotId: `cell-${gap.row}-${gap.column}`, row: gap.row, column: gap.column } : trailing(section);
  }
  if (selection?.kind === "slot") {
    return { sectionKey: selection.sectionKey, slotId: selection.slotId, row: selection.row, column: selection.column };
  }
  if (selection?.kind === "section") {
    const section = layout.sections.find((candidate) => candidate.sectionKey === selection.sectionKey);
    return section ? trailing(section) : null;
  }
  const last = layout.sections.at(-1);
  return last ? trailing(last) : null;
}
