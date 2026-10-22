import type { GroupValueMap, ValueMap } from "@enterpriseforms/form-renderer";

/** What the editor knows about one version of the record's editable content. */
export interface EditSnapshot {
  values: ValueMap;
  groups: GroupValueMap;
}

/** A field both people changed to different values: the user must choose before saving. */
export interface FieldConflict {
  fieldKey: string;
  mine: string;
  theirs: string;
}

export interface MergeResult {
  values: ValueMap;
  groups: GroupValueMap;
  /** Field keys and repeating-section keys whose change by this user was carried onto the latest version. */
  reapplied: string[];
  /** Fields changed by both users differently; the user's own value is kept in the form until resolved. */
  conflicts: FieldConflict[];
  /** Repeating sections both users changed: the latest version is kept, the user's changes there are not reapplied. */
  sectionConflicts: string[];
}

const value = (map: ValueMap, key: string) => map[key] ?? "";

/** Order-independent comparison of a repeating section's entries (ids, order and values). */
function sectionSignature(groups: GroupValueMap, groupKey: string): string {
  const entries = groups[groupKey] ?? [];
  return JSON.stringify(
    entries.map((entry) => ({
      id: entry.formRecordGroupId,
      values: Object.keys(entry.values)
        .filter((key) => (entry.values[key] ?? "") !== "")
        .sort()
        .map((key) => [key, entry.values[key]]),
    })),
  );
}

export function sameValues(a: ValueMap, b: ValueMap): boolean {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
  for (const key of keys) {
    if (value(a, key) !== value(b, key)) {
      return false;
    }
  }
  return true;
}

export function sameGroups(a: GroupValueMap, b: GroupValueMap): boolean {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
  for (const key of keys) {
    if (sectionSignature(a, key) !== sectionSignature(b, key)) {
      return false;
    }
  }
  return true;
}

export function isDirty(base: EditSnapshot, current: EditSnapshot): boolean {
  return !sameValues(base.values, current.values) || !sameGroups(base.groups, current.groups);
}

/**
 * Three-way merge after a concurrent save (C-31): `base` is what this user started from, `mine` what they have now,
 * `theirs` the latest saved version. A field only one side changed takes that side's value; a field both changed to the
 * same value is not a conflict; a field both changed differently is a conflict the user resolves explicitly. Repeating
 * sections are merged as a whole (entry identities may differ), so a section changed by both keeps the latest version.
 * Nothing the other person saved is ever overwritten without the user seeing it.
 */
export function mergeEdits(base: EditSnapshot, mine: EditSnapshot, theirs: EditSnapshot): MergeResult {
  const values: ValueMap = { ...theirs.values };
  const reapplied: string[] = [];
  const conflicts: FieldConflict[] = [];
  const keys = [...new Set([...Object.keys(base.values), ...Object.keys(mine.values), ...Object.keys(theirs.values)])].sort();
  for (const key of keys) {
    const b = value(base.values, key);
    const m = value(mine.values, key);
    const t = value(theirs.values, key);
    if (m === b) {
      continue; // untouched by this user: the latest value stands
    }
    if (t === b || t === m) {
      values[key] = m;
      if (t === b) {
        reapplied.push(key);
      }
      continue;
    }
    values[key] = m;
    conflicts.push({ fieldKey: key, mine: m, theirs: t });
  }

  const groups: GroupValueMap = { ...theirs.groups };
  const sectionConflicts: string[] = [];
  const groupKeys = [...new Set([...Object.keys(base.groups), ...Object.keys(mine.groups), ...Object.keys(theirs.groups)])].sort();
  for (const groupKey of groupKeys) {
    const b = sectionSignature(base.groups, groupKey);
    const m = sectionSignature(mine.groups, groupKey);
    const t = sectionSignature(theirs.groups, groupKey);
    if (m === b) {
      continue;
    }
    if (t === b) {
      groups[groupKey] = mine.groups[groupKey] ?? [];
      reapplied.push(groupKey);
    } else if (t !== m) {
      sectionConflicts.push(groupKey);
    }
  }
  return { values, groups, reapplied, conflicts, sectionConflicts };
}
