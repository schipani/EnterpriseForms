import type { FormLayout, LayoutElement } from "@enterpriseforms/form-renderer";

/** Pure, immutable layout edits used by the designer canvas (unit-tested; no React). */

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

export function removeElement(layout: FormLayout, sectionIndex: number, index: number): FormLayout {
  const section = layout.sections[sectionIndex];
  if (!section) {
    return layout;
  }
  return replaceSection(layout, sectionIndex, { ...section, elements: section.elements.filter((_, i) => i !== index) });
}

export function appendElement(layout: FormLayout, sectionIndex: number, element: LayoutElement): FormLayout {
  const section = layout.sections[sectionIndex];
  if (!section) {
    return layout;
  }
  return replaceSection(layout, sectionIndex, { ...section, elements: [...section.elements, element] });
}

export function addSection(layout: FormLayout, sectionKey: string, title: string): FormLayout {
  if (layout.sections.some((section) => section.sectionKey === sectionKey)) {
    return layout;
  }
  return { ...layout, sections: [...layout.sections, { sectionKey, title, gridColumns: 4, elements: [] }] };
}

export function renameSection(layout: FormLayout, sectionIndex: number, title: string): FormLayout {
  const section = layout.sections[sectionIndex];
  return section ? replaceSection(layout, sectionIndex, { ...section, title }) : layout;
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
