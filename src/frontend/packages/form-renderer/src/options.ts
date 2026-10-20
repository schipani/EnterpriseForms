import { splitSelection } from "./rules";
import type { FieldElement, FormLayout } from "./layout";
import type { RendererField } from "./validation";

export interface ChoiceOption {
  optionKey: string;
  label: string;
  /** Selectable now (the live catalog decides: deactivation takes effect immediately). */
  isActive: boolean;
}

/**
 * The choices a dropdown offers, in display order. A published version carries a snapshot of its options (ids, keys,
 * labels, order): the snapshot decides which options this version offers and how they read, the live catalog decides
 * whether an option is still selectable, so relabelling or adding options needs a publish while deactivation applies
 * at once. A version without a snapshot (published before snapshots existed) falls back to the catalog. A stored
 * selection that is no longer selectable stays visible, marked, but cannot be chosen again.
 */
export function choicesFor(element: Pick<FieldElement, "options"> | undefined, field: RendererField, selected: readonly string[] = []): ChoiceOption[] {
  const catalog = new Map(field.options.map((option) => [option.optionKey, option]));
  const snapshot = element?.options;
  const base: ChoiceOption[] = snapshot
    ? [...snapshot]
        .sort((a, b) => a.sortOrder - b.sortOrder || a.optionKey.localeCompare(b.optionKey))
        .map((option) => ({ optionKey: option.optionKey, label: option.label, isActive: catalog.has(option.optionKey) ? catalog.get(option.optionKey)!.isActive : option.isActive }))
    : [...field.options].sort((a, b) => a.optionSortOrder - b.optionSortOrder || a.optionKey.localeCompare(b.optionKey)).map((option) => ({ optionKey: option.optionKey, label: option.optionDisplayText, isActive: option.isActive }));
  const known = new Set(base.map((option) => option.optionKey));
  // A stored selection the version never offered (e.g. an option added after publishing) is still shown with its catalog label.
  for (const key of selected) {
    const option = catalog.get(key);
    if (!known.has(key) && option) {
      base.push({ optionKey: key, label: option.optionDisplayText, isActive: option.isActive });
    }
  }
  return base.filter((option) => option.isActive || selected.includes(option.optionKey));
}

/** Human-readable text of a stored dropdown value (a single key or a comma-joined multi-selection). */
export function describeSelection(element: Pick<FieldElement, "options"> | undefined, field: RendererField, value: string): string {
  const keys = splitSelection(value);
  const choices = choicesFor(element, field, keys);
  const position = (key: string) => {
    const index = choices.findIndex((candidate) => candidate.optionKey === key);
    return index < 0 ? Number.MAX_SAFE_INTEGER : index;
  };
  return [...keys]
    .sort((a, b) => position(a) - position(b))
    .map((key) => {
      const choice = choices.find((candidate) => candidate.optionKey === key);
      return choice ? (choice.isActive ? choice.label : `${choice.label} (no longer available)`) : key;
    })
    .join(", ");
}

/** The default selection for a new record: only options that are still selectable. */
export function defaultSelection(element: Pick<FieldElement, "options" | "defaultOptionKeys">, field: RendererField): string {
  const active = new Set(choicesFor(element, field).filter((choice) => choice.isActive).map((choice) => choice.optionKey));
  const order = new Map(choicesFor(element, field).map((choice, index) => [choice.optionKey, index]));
  return (element.defaultOptionKeys ?? [])
    .filter((key) => active.has(key))
    .sort((a, b) => (order.get(a) ?? 0) - (order.get(b) ?? 0))
    .join(",");
}

/**
 * Values a new record starts with: each dropdown's default selection (still selectable options only). A convenience for
 * the data-entry form — the server validates whatever is submitted and a default never overwrites a saved value.
 */
export function initialValues(layout: FormLayout, fields: readonly RendererField[]): Record<string, string> {
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  const values: Record<string, string> = {};
  for (const section of layout.sections) {
    for (const element of section.elements) {
      if (element.kind !== "field" || !element.defaultOptionKeys?.length) {
        continue;
      }
      const field = byId.get(element.fieldDefinitionId);
      const selection = field ? defaultSelection(element, field) : "";
      if (field && selection) {
        values[field.fieldKey] = selection;
      }
    }
  }
  return values;
}
