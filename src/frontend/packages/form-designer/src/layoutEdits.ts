import type { FormLayout, LayoutElement, LayoutRepeat, LayoutSection } from "@enterpriseforms/form-renderer";

/**
 * Pure, immutable layout edits used by the designer canvas and inspector (unit-tested; no React).
 *
 * C1 designer rebuild: a "row" in the designer toolbar is a titled **section** with its own four-column grid
 * (`LayoutSection` in the existing schema-v2 layout — not a new entity and not the per-element `startOnNewRow` flag).
 * Every edit here changes presentation only: field definitions, stable ids, stored values, child links and documents
 * are never touched, and a field remains placeable at most once across all sections (server rule).
 */

/** Stable identity of a placed element within one layout (a field, document collection or related list is placed once). */
export function elementKey(element: LayoutElement): string {
  return element.kind === "relatedFormList" ? `rel:${element.formRelationshipDefinitionId}` : `${element.kind}:${element.fieldDefinitionId}`;
}

export interface ElementPosition {
  sectionIndex: number;
  index: number;
}

export function findElement(layout: FormLayout, key: string): (ElementPosition & { element: LayoutElement }) | null {
  for (let sectionIndex = 0; sectionIndex < layout.sections.length; sectionIndex++) {
    const elements = layout.sections[sectionIndex]!.elements;
    const index = elements.findIndex((candidate) => elementKey(candidate) === key);
    if (index >= 0) {
      return { sectionIndex, index, element: elements[index]! };
    }
  }
  return null;
}

export function sectionIndexOf(layout: FormLayout, sectionKey: string): number {
  return layout.sections.findIndex((section) => section.sectionKey === sectionKey);
}

export function moveElement(layout: FormLayout, sectionIndex: number, from: number, to: number): FormLayout {
  const section = layout.sections[sectionIndex];
  if (!section || from === to || to < 0 || to >= section.elements.length) {
    return layout;
  }
  const elements = [...section.elements];
  const [moved] = elements.splice(from, 1);
  elements.splice(to, 0, moved!);
  return replaceSection(layout, sectionIndex, { ...section, elements });
}

/**
 * Moves a placed element to `targetIndex` (an insertion index counted *before* removal) of another or the same section.
 * Used by drag-and-drop and by the keyboard/menu "Move to section" alternative; never duplicates or drops an element.
 */
export function moveElementTo(layout: FormLayout, key: string, targetSectionIndex: number, targetIndex: number): FormLayout {
  const source = findElement(layout, key);
  const target = layout.sections[targetSectionIndex];
  if (!source || !target || !sameScope(layout.sections[source.sectionIndex]!, target)) {
    return layout;
  }
  let insertAt = Math.max(0, Math.min(targetIndex, target.elements.length));
  if (source.sectionIndex === targetSectionIndex) {
    if (insertAt > source.index) {
      insertAt -= 1;
    }
    if (insertAt === source.index) {
      return layout;
    }
    return moveElement(layout, source.sectionIndex, source.index, insertAt);
  }
  const without = removeElement(layout, source.sectionIndex, source.index);
  return insertElement(without, targetSectionIndex, insertAt, source.element);
}

/**
 * Whether elements can move between two sections (Phase C3). A field of a repeating section belongs to its group for life
 * and has one value per entry, so it stays in that group's section; record-level fields, document collections and related
 * lists never enter a repeating section. Moves are allowed between sections of the same scope only.
 */
export function sameScope(a: LayoutSection, b: LayoutSection): boolean {
  return (a.repeat?.groupDefinitionId ?? null) === (b.repeat?.groupDefinitionId ?? null);
}

/**
 * "Allow multiple entries": turns an empty section into the placement of a repeating group (or back), or changes the entry
 * settings of a section that already allows multiple entries. Turning it on or off needs an empty section, because field
 * membership in a group is permanent.
 */
export function setSectionRepeat(layout: FormLayout, sectionIndex: number, repeat: LayoutRepeat | undefined): FormLayout {
  const section = layout.sections[sectionIndex];
  if (!section) {
    return layout;
  }
  const switching = (section.repeat?.groupDefinitionId ?? null) !== (repeat?.groupDefinitionId ?? null);
  if (switching && section.elements.length > 0) {
    return layout;
  }
  if (repeat && layout.sections.some((candidate, index) => index !== sectionIndex && candidate.repeat?.groupDefinitionId === repeat.groupDefinitionId)) {
    return layout;
  }
  const next: LayoutSection = { ...section };
  if (repeat) {
    next.repeat = repeat;
  } else {
    delete next.repeat;
  }
  return replaceSection(layout, sectionIndex, next);
}

export function updateElement(layout: FormLayout, sectionIndex: number, index: number, patch: (element: LayoutElement) => LayoutElement): FormLayout {
  const section = layout.sections[sectionIndex];
  const current = section?.elements[index];
  if (!section || !current) {
    return layout;
  }
  const elements = [...section.elements];
  elements[index] = patch(current);
  return replaceSection(layout, sectionIndex, { ...section, elements });
}

export function updateElementByKey(layout: FormLayout, key: string, patch: (element: LayoutElement) => LayoutElement): FormLayout {
  const position = findElement(layout, key);
  return position ? updateElement(layout, position.sectionIndex, position.index, patch) : layout;
}

export function removeElement(layout: FormLayout, sectionIndex: number, index: number): FormLayout {
  const section = layout.sections[sectionIndex];
  if (!section) {
    return layout;
  }
  return replaceSection(layout, sectionIndex, { ...section, elements: section.elements.filter((_, i) => i !== index) });
}

export function appendElement(layout: FormLayout, sectionIndex: number, element: LayoutElement): FormLayout {
  const section = layout.sections[sectionIndex];
  return section ? insertElement(layout, sectionIndex, section.elements.length, element) : layout;
}

/** Inserts an element at an exact position of a section (the "+ Add Field" cell the designer clicked). Rejects a second placement of the same identity. */
export function insertElement(layout: FormLayout, sectionIndex: number, index: number, element: LayoutElement): FormLayout {
  const section = layout.sections[sectionIndex];
  if (!section || findElement(layout, elementKey(element))) {
    return layout;
  }
  const elements = [...section.elements];
  elements.splice(Math.max(0, Math.min(index, elements.length)), 0, element);
  return replaceSection(layout, sectionIndex, { ...section, elements });
}

/** A new section key that matches the server pattern `^[a-z][a-z0-9_]{0,63}$` and is unique within the layout. */
export function newSectionKey(layout: FormLayout, random: () => string = randomSuffix): string {
  for (;;) {
    const key = `section_${random()}`;
    if (!layout.sections.some((section) => section.sectionKey === key)) {
      return key;
    }
  }
}

function randomSuffix(): string {
  const bytes = new Uint8Array(5);
  globalThis.crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

/** "+ Add Row": inserts an empty titled section after `afterIndex` (or at the end when null). */
export function insertSection(layout: FormLayout, afterIndex: number | null, sectionKey: string, title: string): FormLayout {
  if (layout.sections.length >= 50 || layout.sections.some((section) => section.sectionKey === sectionKey)) {
    return layout;
  }
  const at = afterIndex === null ? layout.sections.length : Math.max(0, Math.min(afterIndex + 1, layout.sections.length));
  const sections = [...layout.sections];
  sections.splice(at, 0, { sectionKey, title, gridColumns: 4, elements: [] });
  return { ...layout, sections };
}

/** Kept for existing callers: appends an empty titled section. */
export function addSection(layout: FormLayout, sectionKey: string, title: string): FormLayout {
  return insertSection(layout, null, sectionKey, title);
}

export function renameSection(layout: FormLayout, sectionIndex: number, title: string): FormLayout {
  const section = layout.sections[sectionIndex];
  return section ? replaceSection(layout, sectionIndex, { ...section, title }) : layout;
}

export function updateSection(layout: FormLayout, sectionIndex: number, patch: { title?: string; description?: string }): FormLayout {
  const section = layout.sections[sectionIndex];
  if (!section) {
    return layout;
  }
  const next: LayoutSection = { ...section };
  if (patch.title !== undefined) {
    next.title = patch.title;
  }
  if (patch.description !== undefined) {
    if (patch.description.trim()) {
      next.description = patch.description;
    } else {
      delete next.description;
    }
  }
  return replaceSection(layout, sectionIndex, next);
}

export function moveSection(layout: FormLayout, from: number, to: number): FormLayout {
  if (from === to || from < 0 || to < 0 || from >= layout.sections.length || to >= layout.sections.length) {
    return layout;
  }
  const sections = [...layout.sections];
  const [moved] = sections.splice(from, 1);
  sections.splice(to, 0, moved!);
  return { ...layout, sections };
}

/** Duplicates a section's title/description as a new empty section (fields can be placed only once, so placements are not copied). */
export function duplicateSection(layout: FormLayout, sectionIndex: number, sectionKey: string): FormLayout {
  const section = layout.sections[sectionIndex];
  if (!section) {
    return layout;
  }
  const inserted = insertSection(layout, sectionIndex, sectionKey, `${section.title ?? section.sectionKey} (copy)`.slice(0, 200));
  return section.description ? updateSection(inserted, sectionIndex + 1, { description: section.description }) : inserted;
}

/**
 * Explicit disposition for removing a section (contract §3.6 / UX-08): an empty section is simply removed; a section
 * with placements requires either moving them into another section, or explicitly un-placing them from the layout.
 * Un-placing removes presentation only — field definitions, stored values, child links and files are untouched and
 * the fields can be placed again later. The last remaining section can never be removed (server requires ≥ 1).
 */
export type SectionRemoval = { kind: "empty" } | { kind: "moveTo"; targetSectionIndex: number } | { kind: "unplace" };

export function removeSection(layout: FormLayout, sectionIndex: number, disposition: SectionRemoval): FormLayout {
  const section = layout.sections[sectionIndex];
  if (!section || layout.sections.length <= 1) {
    return layout;
  }
  if (disposition.kind === "empty" && section.elements.length > 0) {
    return layout;
  }
  let sections = [...layout.sections];
  if (disposition.kind === "moveTo") {
    const target = sections[disposition.targetSectionIndex];
    if (!target || disposition.targetSectionIndex === sectionIndex || !sameScope(section, target)) {
      return layout;
    }
    sections[disposition.targetSectionIndex] = { ...target, elements: [...target.elements, ...section.elements] };
  }
  sections = sections.filter((_, index) => index !== sectionIndex);
  return { ...layout, sections };
}

export function placedIds(layout: FormLayout): { fieldIds: Set<string>; relationshipIds: Set<string> } {
  const fieldIds = new Set<string>();
  const relationshipIds = new Set<string>();
  for (const section of layout.sections) {
    for (const element of section.elements) {
      if (element.kind === "relatedFormList") {
        relationshipIds.add(element.formRelationshipDefinitionId);
      } else {
        fieldIds.add(element.fieldDefinitionId);
      }
    }
  }
  return { fieldIds, relationshipIds };
}

function replaceSection(layout: FormLayout, index: number, section: FormLayout["sections"][number]): FormLayout {
  const sections = [...layout.sections];
  sections[index] = section;
  return { ...layout, sections };
}
