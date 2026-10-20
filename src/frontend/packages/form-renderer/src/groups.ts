import type { FieldElement, FormLayout, LayoutRepeat, LayoutSection } from "./layout";
import { cascadeDependentClears, defaultSelection } from "./options";
import { evaluateRuleStates, splitSelection, type FieldRuleState } from "./rules";
import { ruleFieldsOf, validateElement, valuesById, type ErrorMap, type RendererField, type ValueMap } from "./validation";

/**
 * Repeating sections (Phase C3) on the client: entry state, defaults, per-entry rules, validation, the request payload and
 * dependent-dropdown clearing. Everything here is a convenience mirror of the server (CandidateBuilder), which re-checks
 * every entry id, bound, rule and combination; nothing here grants or withholds anything.
 */

/** A repeating group of the form, as the API names it. */
export interface GroupInfo {
  groupDefinitionId: string;
  groupKey: string;
}

/** One entry being edited: its stable server id (null until saved), a client key for rendering, and its values by field key. */
export interface OccurrenceState {
  formRecordGroupId: string | null;
  clientKey: string;
  values: ValueMap;
}

/** Entries per repeating section, by group key, in display order. */
export type GroupValueMap = Record<string, OccurrenceState[]>;

export type RepeatingSection = LayoutSection & { repeat: LayoutRepeat };

let clientCounter = 0;

/** A render key for an entry that has no server id yet. */
export function newClientKey(): string {
  clientCounter += 1;
  return `new-entry-${clientCounter}`;
}

export function repeatingSections(layout: FormLayout): RepeatingSection[] {
  return layout.sections.filter((section): section is RepeatingSection => section.repeat !== undefined);
}

export function groupKeyFor(groups: readonly GroupInfo[], groupDefinitionId: string): string | undefined {
  return groups.find((group) => group.groupDefinitionId === groupDefinitionId)?.groupKey;
}

function fieldElementsOf(section: LayoutSection): FieldElement[] {
  return section.elements.filter((element): element is FieldElement => element.kind === "field");
}

/** Field elements outside repeating sections (the record's own fields). */
export function topLevelFieldElements(layout: FormLayout): FieldElement[] {
  return layout.sections.filter((section) => !section.repeat).flatMap(fieldElementsOf);
}

/** A new entry: each dropdown of the section starts with its default selection. */
export function newOccurrence(section: LayoutSection, fields: readonly RendererField[]): OccurrenceState {
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  const values: ValueMap = {};
  for (const element of fieldElementsOf(section)) {
    const field = byId.get(element.fieldDefinitionId);
    if (field && element.defaultOptionKeys?.length) {
      const selection = defaultSelection(element, field);
      if (selection) {
        values[field.fieldKey] = selection;
      }
    }
  }
  return { formRecordGroupId: null, clientKey: newClientKey(), values };
}

/** Entries a new record starts with: each repeating section's minimum number of entries. */
export function initialGroups(layout: FormLayout, fields: readonly RendererField[], groups: readonly GroupInfo[]): GroupValueMap {
  const result: GroupValueMap = {};
  for (const section of repeatingSections(layout)) {
    const key = groupKeyFor(groups, section.repeat.groupDefinitionId);
    if (key) {
      result[key] = Array.from({ length: section.repeat.minOccurrences }, () => newOccurrence(section, fields));
    }
  }
  return result;
}

/** Wire values of a record grouped into entries (the response lists each value with its entry id and each section's entries in order). */
export function toGroupValueMap(values: ReadonlyArray<{ fieldKey: string; stringValue: string; formRecordGroupId?: string | null | undefined }>, groups: ReadonlyArray<{ groupKey: string; occurrences: ReadonlyArray<{ formRecordGroupId: string }> }> | null | undefined): GroupValueMap {
  const result: GroupValueMap = {};
  const byOccurrence = new Map<string, OccurrenceState>();
  for (const group of groups ?? []) {
    result[group.groupKey] = group.occurrences.map((occurrence) => {
      const state: OccurrenceState = { formRecordGroupId: occurrence.formRecordGroupId, clientKey: occurrence.formRecordGroupId, values: {} };
      byOccurrence.set(occurrence.formRecordGroupId, state);
      return state;
    });
  }
  for (const value of values) {
    const state = value.formRecordGroupId ? byOccurrence.get(value.formRecordGroupId) : undefined;
    if (state) {
      const existing = state.values[value.fieldKey];
      state.values[value.fieldKey] = existing !== undefined ? [...splitSelection(existing), value.stringValue].join(",") : value.stringValue;
    }
  }
  return result;
}

/** Values one entry's rules read: the record's values plus the entry's own (grouped field keys never collide with the record's). */
export function occurrenceScopeValues(topValues: ValueMap, occurrence: OccurrenceState): ValueMap {
  return { ...topValues, ...occurrence.values };
}

/** Rule state of the section's fields for one entry (rules may read the entry and the record, as on the server). */
export function occurrenceRuleStates(layout: FormLayout, section: LayoutSection, fields: readonly RendererField[], topValues: ValueMap, occurrence: OccurrenceState, applyRules = true): Map<string, FieldRuleState> {
  const elements = fieldElementsOf(section);
  if (!applyRules) {
    return new Map(elements.map((element) => [element.fieldDefinitionId, { visible: true, required: element.required === true }]));
  }
  return evaluateRuleStates([...topLevelFieldElements(layout), ...elements], ruleFieldsOf(fields), valuesById(fields, occurrenceScopeValues(topValues, occurrence)));
}

/** Error key of a field inside an entry: the server's path form, e.g. <c>people[2].person_name</c> (1-based). */
export function occurrenceErrorKey(groupKey: string, index: number, fieldKey: string): string {
  return `${groupKey}[${index + 1}].${fieldKey}`;
}

/** Entry counts and every entry's fields, keyed like the server (section errors under the group key). */
export function validateGroups(layout: FormLayout, fields: readonly RendererField[], topValues: ValueMap, groups: GroupValueMap, infos: readonly GroupInfo[], scope: "create" | "edit" = "create", applyRules = true): ErrorMap {
  const errors: ErrorMap = {};
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  for (const section of repeatingSections(layout)) {
    const groupKey = groupKeyFor(infos, section.repeat.groupDefinitionId);
    if (!groupKey) {
      continue;
    }
    const entries = groups[groupKey] ?? [];
    const title = section.title ?? groupKey;
    if (entries.length < section.repeat.minOccurrences) {
      errors[groupKey] = `${title} needs at least ${section.repeat.minOccurrences} ${section.repeat.minOccurrences === 1 ? "entry" : "entries"}`;
    } else if (entries.length > section.repeat.maxOccurrences) {
      errors[groupKey] = `${title} allows at most ${section.repeat.maxOccurrences} ${section.repeat.maxOccurrences === 1 ? "entry" : "entries"}`;
    }
    entries.forEach((entry, index) => {
      const states = occurrenceRuleStates(layout, section, fields, topValues, entry, applyRules);
      const scopeValues = occurrenceScopeValues(topValues, entry);
      for (const element of fieldElementsOf(section)) {
        const field = byId.get(element.fieldDefinitionId);
        const requireMissing = scope === "create" || entry.formRecordGroupId === null || (field !== undefined && field.fieldKey in entry.values);
        const message = field ? validateElement(element, field, scopeValues, states.get(element.fieldDefinitionId), requireMissing, applyRules, byId) : undefined;
        if (message && field) {
          errors[occurrenceErrorKey(groupKey, index, field.fieldKey)] = `${message} (entry ${index + 1})`;
        }
      }
    });
  }
  return errors;
}

export interface RecordGroupPayload {
  groupKey: string;
  occurrences: Array<{ formRecordGroupId: string | null; values: Record<string, string> }>;
}

/**
 * The complete entry list of every repeating section, in display order. An entry sends only the fields a person can change
 * in it (shown, not read-only, not hidden by a rule); everything else is omitted, which the server keeps unchanged (or, for
 * a new entry, fills with the read-only default).
 */
export function groupPayload(layout: FormLayout, fields: readonly RendererField[], topValues: ValueMap, groups: GroupValueMap, infos: readonly GroupInfo[], applyRules = true): RecordGroupPayload[] {
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  const result: RecordGroupPayload[] = [];
  for (const section of repeatingSections(layout)) {
    const groupKey = groupKeyFor(infos, section.repeat.groupDefinitionId);
    if (!groupKey) {
      continue;
    }
    result.push({
      groupKey,
      occurrences: (groups[groupKey] ?? []).map((entry) => {
        const states = occurrenceRuleStates(layout, section, fields, topValues, entry, applyRules);
        const values: Record<string, string> = {};
        for (const element of fieldElementsOf(section)) {
          const field = byId.get(element.fieldDefinitionId);
          const editable = field && !element.readOnly && element.showInForm !== false && states.get(element.fieldDefinitionId)?.visible !== false;
          if (field && editable && (entry.formRecordGroupId === null ? (entry.values[field.fieldKey] ?? "") !== "" : field.fieldKey in entry.values)) {
            values[field.fieldKey] = entry.values[field.fieldKey] ?? "";
          }
        }
        return { formRecordGroupId: entry.formRecordGroupId, values };
      }),
    });
  }
  return result;
}

/** Result of a change with the dependent selections it cleared (field keys; entry fields as server paths) for announcement. */
export interface ChangeResult {
  values: ValueMap;
  groups: GroupValueMap;
  clearedFieldKeys: string[];
}

/** A record-level field changed: clears dependent selections at the record level and in every entry that depends on it. */
export function changeTopLevel(layout: FormLayout, fields: readonly RendererField[], values: ValueMap, groups: GroupValueMap, infos: readonly GroupInfo[], fieldKey: string, value: string): ChangeResult {
  const top = cascadeDependentClears(topLevelFieldElements(layout), fields, { ...values, [fieldKey]: value }, [fieldKey]);
  const cleared = [...top.clearedFieldKeys];
  const nextGroups: GroupValueMap = { ...groups };
  for (const section of repeatingSections(layout)) {
    const groupKey = groupKeyFor(infos, section.repeat.groupDefinitionId);
    if (!groupKey || !groups[groupKey]) {
      continue;
    }
    nextGroups[groupKey] = groups[groupKey]!.map((entry, index) => {
      const scoped = cascadeDependentClears(fieldElementsOf(section), fields, occurrenceScopeValues(top.values, entry), [fieldKey, ...top.clearedFieldKeys]);
      if (scoped.clearedFieldKeys.length === 0) {
        return entry;
      }
      cleared.push(...scoped.clearedFieldKeys.map((key) => occurrenceErrorKey(groupKey, index, key)));
      const ownKeys = new Set([...Object.keys(entry.values), ...scoped.clearedFieldKeys]);
      return { ...entry, values: Object.fromEntries([...ownKeys].map((key) => [key, scoped.values[key] ?? ""])) };
    });
  }
  return { values: top.values, groups: nextGroups, clearedFieldKeys: cleared };
}

/** A field inside one entry changed: clears dependent selections of that entry only. */
export function changeInOccurrence(layout: FormLayout, section: LayoutSection, fields: readonly RendererField[], values: ValueMap, groups: GroupValueMap, groupKey: string, index: number, fieldKey: string, value: string): ChangeResult {
  const entries = groups[groupKey] ?? [];
  const entry = entries[index];
  if (!entry) {
    return { values, groups, clearedFieldKeys: [] };
  }
  const scoped = cascadeDependentClears(fieldElementsOf(section), fields, occurrenceScopeValues(values, { ...entry, values: { ...entry.values, [fieldKey]: value } }), [fieldKey]);
  const ownKeys = new Set([...Object.keys(entry.values), fieldKey, ...scoped.clearedFieldKeys]);
  const nextEntry: OccurrenceState = { ...entry, values: Object.fromEntries([...ownKeys].map((key) => [key, scoped.values[key] ?? ""])) };
  return { values, groups: { ...groups, [groupKey]: entries.map((candidate, i) => (i === index ? nextEntry : candidate)) }, clearedFieldKeys: scoped.clearedFieldKeys.map((key) => occurrenceErrorKey(groupKey, index, key)) };
}

/** Moves an entry one place up (-1) or down (+1); identity is kept, only the order changes. */
export function moveOccurrence(entries: readonly OccurrenceState[], index: number, delta: -1 | 1): OccurrenceState[] {
  const target = index + delta;
  if (target < 0 || target >= entries.length) {
    return [...entries];
  }
  const next = [...entries];
  [next[index], next[target]] = [next[target]!, next[index]!];
  return next;
}
