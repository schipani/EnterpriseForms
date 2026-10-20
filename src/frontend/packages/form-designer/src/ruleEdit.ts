import { comparisonsOf, type FormLayout, type RuleCondition, type RuleField } from "@enterpriseforms/form-renderer";

/**
 * Pure, immutable edits and graph checks behind the Rules tab (unit-tested; no React). Rules are plain data in the
 * draft layout, so every edit here is undoable like any other layout change and nothing is stored until Save draft.
 */

/** Field id → ids of the fields its visibility or required-if rule reads. */
export function ruleDependencies(layout: FormLayout): Map<string, Set<string>> {
  const result = new Map<string, Set<string>>();
  for (const section of layout.sections) {
    for (const element of section.elements) {
      if (element.kind !== "field") {
        continue;
      }
      const sources = new Set([...comparisonsOf(element.visibleWhen), ...comparisonsOf(element.requiredWhen)].map((comparison) => comparison.fieldDefinitionId));
      if (sources.size > 0) {
        result.set(element.fieldDefinitionId, sources);
      }
    }
  }
  return result;
}

/** A cycle among the rules (first found), as the ordered field ids with the start repeated at the end; null when the rules are acyclic. */
export function findRuleCycle(layout: FormLayout): string[] | null {
  const dependencies = ruleDependencies(layout);
  const state = new Map<string, 1 | 2>();
  const path: string[] = [];
  const visit = (node: string): string[] | null => {
    if (state.get(node) === 2) {
      return null;
    }
    if (state.get(node) === 1) {
      return [...path.slice(path.indexOf(node)), node];
    }
    state.set(node, 1);
    path.push(node);
    for (const source of dependencies.get(node) ?? []) {
      const cycle = visit(source);
      if (cycle) {
        return cycle;
      }
    }
    path.pop();
    state.set(node, 2);
    return null;
  };
  for (const start of dependencies.keys()) {
    const cycle = visit(start);
    if (cycle) {
      return cycle;
    }
  }
  return null;
}

/** True when `fieldId` reads `otherId`, directly or through a chain of rules. */
export function dependsOn(layout: FormLayout, fieldId: string, otherId: string): boolean {
  const dependencies = ruleDependencies(layout);
  const seen = new Set<string>();
  const stack = [fieldId];
  while (stack.length > 0) {
    const current = stack.pop()!;
    for (const source of dependencies.get(current) ?? []) {
      if (source === otherId) {
        return true;
      }
      if (!seen.has(source)) {
        seen.add(source);
        stack.push(source);
      }
    }
  }
  return false;
}

/** Fields a rule on `targetId` may read: placed fields with a usable operator set that do not already depend on the target. */
export function candidateSources(layout: FormLayout, targetId: string, fields: readonly RuleField[], usable: (field: RuleField) => boolean): RuleField[] {
  const placed = new Set(layout.sections.flatMap((section) => section.elements).filter((element) => element.kind === "field").map((element) => (element as { fieldDefinitionId: string }).fieldDefinitionId));
  return fields.filter((field) => field.fieldDefinitionId !== targetId && placed.has(field.fieldDefinitionId) && usable(field) && !dependsOn(layout, field.fieldDefinitionId, targetId));
}

export type ConditionPath = number[];

export function getNode(root: RuleCondition, path: ConditionPath): RuleCondition | undefined {
  let node: RuleCondition | undefined = root;
  for (const index of path) {
    node = node && "conditions" in node ? node.conditions[index] : undefined;
  }
  return node;
}

/** Replaces the node at `path` (an empty path replaces the root). */
export function replaceNode(root: RuleCondition, path: ConditionPath, next: RuleCondition): RuleCondition {
  if (path.length === 0) {
    return next;
  }
  if (!("conditions" in root)) {
    return root;
  }
  const [head, ...rest] = path as [number, ...number[]];
  return { ...root, conditions: root.conditions.map((child, index) => (index === head ? replaceNode(child, rest, next) : child)) };
}

/** Removes the node at `path`; a group left empty disappears, and a root that disappears yields undefined. */
export function removeNode(root: RuleCondition, path: ConditionPath): RuleCondition | undefined {
  if (path.length === 0) {
    return undefined;
  }
  if (!("conditions" in root)) {
    return root;
  }
  const [head, ...rest] = path as [number, ...number[]];
  const conditions: RuleCondition[] = [];
  root.conditions.forEach((child, index) => {
    if (index !== head) {
      conditions.push(child);
      return;
    }
    const reduced = rest.length === 0 ? undefined : removeNode(child, rest);
    if (reduced) {
      conditions.push(reduced);
    }
  });
  return conditions.length === 0 ? undefined : { ...root, conditions };
}

/** Appends a condition to the group at `path`. A comparison at that path is wrapped into an `and` group first. */
export function appendCondition(root: RuleCondition, path: ConditionPath, added: RuleCondition): RuleCondition {
  const target = getNode(root, path);
  if (!target) {
    return root;
  }
  if (!("conditions" in target)) {
    return replaceNode(root, path, { op: "and", conditions: [target, added] });
  }
  return replaceNode(root, path, { ...target, conditions: [...target.conditions, added] });
}

/** Normalizes a possibly bare comparison (as an API client may have written) to an editable `and` group. */
export function asGroup(condition: RuleCondition): Extract<RuleCondition, { conditions: RuleCondition[] }> {
  return "conditions" in condition ? condition : { op: "and", conditions: [condition] };
}

/** A fresh comparison for a field: `isNotEmpty` is valid for every type that supports presence checks, otherwise `equals` with an empty operand to be filled in. */
export function newComparison(fieldDefinitionId: string, operators: readonly string[]): RuleCondition {
  if (operators.includes("isNotEmpty")) {
    return { op: "isNotEmpty", fieldDefinitionId };
  }
  return { op: "equals", fieldDefinitionId, value: "true" };
}

/** What an operand is allowed to be, for early feedback (the server re-validates everything). Returns a message or null. */
export function operandProblem(condition: RuleCondition, field: RuleField | undefined): string | null {
  if ("conditions" in condition) {
    return null;
  }
  if (!field) {
    return "The field this rule reads is not in the form";
  }
  const operands = condition.values ?? (condition.value !== undefined ? [condition.value] : []);
  if (condition.op === "isEmpty" || condition.op === "isNotEmpty") {
    return null;
  }
  if (operands.length === 0 || operands.some((operand) => operand.trim() === "" && field.fieldDataType !== "String")) {
    return "Enter a value";
  }
  for (const operand of operands) {
    switch (field.fieldDataType) {
      case "Integer":
        if (!/^-?\d+$/.test(operand)) return "Enter a whole number";
        break;
      case "Decimal":
        if (!/^-?\d+(\.\d+)?$/.test(operand)) return "Enter a number";
        break;
      case "Date":
        if (!/^\d{4}-\d{2}-\d{2}$/.test(operand)) return "Enter a date";
        break;
      case "DateTime":
        if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(Z|[+-]\d{2}:\d{2})$/.test(operand)) return "Enter a date and time";
        break;
      case "Boolean":
        if (operand !== "true" && operand !== "false") return "Choose true or false";
        break;
      case "Guid":
        if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(operand)) return "Enter a UUID";
        break;
      default:
        break;
    }
  }
  return null;
}

/** First problem anywhere in the tree, or null. */
export function conditionProblem(condition: RuleCondition, fieldsById: ReadonlyMap<string, RuleField>): string | null {
  if ("conditions" in condition) {
    for (const child of condition.conditions) {
      const problem = conditionProblem(child, fieldsById);
      if (problem) {
        return problem;
      }
    }
    return null;
  }
  return operandProblem(condition, fieldsById.get(condition.fieldDefinitionId));
}
