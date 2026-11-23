import type { LayoutChangeDetailResponse, LayoutChangeResponse, PublishImpactNoticeResponse } from "@enterpriseforms/api-client";

/*
 * Phase C9 M2: plain-language wording of the server's semantic layout comparison and publish-impact notices. The server
 * decides what changed (by stable ids); this module only words it. Nothing here says data is deleted, because publishing
 * never deletes or changes stored values.
 */

const TARGET_NAMES: Record<string, string> = {
  Form: "Form",
  Section: "Section",
  Field: "Field",
  DocumentCollection: "Document collection",
  RelatedFormList: "Related list",
};

export function targetName(target: string): string {
  return TARGET_NAMES[target] ?? target;
}

const ASPECT_NAMES: Record<string, string> = {
  Title: "Title",
  Description: "Description",
  Label: "Label",
  HelpText: "Help text",
  Placeholder: "Placeholder",
  Control: "Control",
  Placement: "Width and position on the grid",
  MinLength: "Minimum length",
  MaxLength: "Maximum length",
  Pattern: "Required format",
  Minimum: "Minimum value",
  Maximum: "Maximum value",
  MinDate: "Earliest date",
  MaxDate: "Latest date",
  DefaultOptions: "Default selection",
  Protection: "Classification and protection",
  UploadPolicy: "Upload limits",
  SummaryFields: "Summary columns",
  RepeatLimits: "Entry limits",
  OptionRelabelled: "Option label",
};

const quote = (value: string | null | undefined) => (value === null || value === undefined || value === "" ? "none" : `“${value}”`);

/** One sentence per changed aspect. */
export function describeDetail(detail: LayoutChangeDetailResponse): string {
  switch (detail.aspect) {
    case "Position":
      return "Order changed";
    case "Section":
      return `Moved from section ${quote(detail.before)} to ${quote(detail.after)}`;
    case "Required":
      return detail.after === "yes" ? "Now required" : "No longer required";
    case "ReadOnly":
      return detail.after === "yes" ? "Now read-only" : "No longer read-only";
    case "ShowInForm":
      return detail.after === "yes" ? "Now shown in the form" : "No longer shown in the form (its stored values are kept)";
    case "ClearWhenHidden":
      return detail.after === "yes" ? "Now cleared when a rule hides it" : "No longer cleared when a rule hides it";
    case "VisibleWhen":
      return ruleSentence("Visibility rule", detail);
    case "RequiredWhen":
      return ruleSentence("Conditional requirement", detail);
    case "OptionFilter":
      return ruleSentence("Dependent-choice filter", detail);
    case "OptionAdded":
      return `Option ${quote(detail.subject)} added`;
    case "OptionRemoved":
      return `Option ${quote(detail.subject)} no longer offered (existing selections are kept)`;
    case "OptionDeactivated":
      return `Option ${quote(detail.subject)} no longer available for new selections (existing selections are kept)`;
    case "OptionReactivated":
      return `Option ${quote(detail.subject)} available again`;
    case "OptionOrder":
      return "Order of the options changed";
    case "Repeat":
      return detail.after ? `Now allows multiple entries (${detail.after})` : "No longer allows multiple entries";
    default: {
      const name = ASPECT_NAMES[detail.aspect] ?? detail.aspect;
      return `${name}: ${quote(detail.before)} → ${quote(detail.after)}${detail.tightened ? " (stricter)" : ""}`;
    }
  }
}

function ruleSentence(name: string, detail: LayoutChangeDetailResponse): string {
  if (detail.before === "none") {
    return `${name} added`;
  }
  if (detail.after === "none") {
    return `${name} removed`;
  }
  return `${name} changed`;
}

export type ChangeGroup = "added" | "removed" | "behaviour" | "presentation";

/** Where a change is listed: additions, removals (values kept), behaviour changes, presentation-only changes. */
export function changeGroup(change: LayoutChangeResponse): ChangeGroup {
  if (change.changeType === "Added") {
    return "added";
  }
  if (change.changeType === "Removed") {
    return "removed";
  }
  return change.significance === "Presentation" ? "presentation" : "behaviour";
}

export const GROUP_HEADINGS: Record<ChangeGroup, string> = {
  added: "Added",
  removed: "Removed from the layout",
  behaviour: "Changes to how the form behaves",
  presentation: "Presentation-only changes",
};

export const GROUP_EXPLANATIONS: Record<ChangeGroup, string> = {
  added: "New in the later layout.",
  removed: "No longer shown or edited with the later layout. Stored values are kept and stay in each record's history.",
  behaviour: "Validation, rules, read-only settings, options, limits or protection. Existing values are kept; records are checked against these rules when they are next edited with this version.",
  presentation: "Wording, width, position or order only. Nothing a person enters is checked differently.",
};

const NOTICE_TEXT: Record<string, { title: string; body: string }> = {
  NEW_REQUIRED_FIELDS: { title: "Newly required fields", body: "Records edited with the new version will need a value for these fields." },
  VALIDATION_TIGHTENED: { title: "Stricter validation", body: "Existing values are kept, but records edited with the new version must meet the new rules for these fields." },
  OPTIONS_NO_LONGER_AVAILABLE: { title: "Options no longer available", body: "Records that already hold these choices keep them; a record that still holds one can't be saved until it is changed." },
  LIMITS_TIGHTENED: { title: "Tighter limits", body: "New uploads or entries must fit the new limits; existing documents and entries are kept." },
  ELEMENTS_REMOVED: { title: "Removed from the layout", body: "Their stored values and documents are kept and stay in record history; they are no longer shown or edited with the new version." },
  READ_ONLY_OR_HIDDEN: { title: "Read-only or hidden", body: "These can no longer be changed in the form; their stored values are kept." },
  PROTECTION_CHANGED: { title: "Protection recorded with this version", body: "The version records each field's current classification and protection (changed through the field's protection settings)." },
  PRESENTATION_ONLY: { title: "Presentation changes only", body: "Nothing a person enters is checked differently." },
  NO_CHANGES: { title: "No changes", body: "The draft is the same as the current version." },
};

export function noticeText(notice: PublishImpactNoticeResponse): { title: string; body: string } {
  return NOTICE_TEXT[notice.code] ?? { title: notice.code, body: "" };
}

export function comparisonSummary(counts: { addedCount: number | string; removedCount: number | string; behaviourChangeCount: number | string; presentationChangeCount: number | string; hasChanges: boolean }): string {
  if (!counts.hasChanges) {
    return "No differences.";
  }
  const parts: string[] = [];
  const add = (n: number, one: string, many: string) => {
    if (n > 0) {
      parts.push(n === 1 ? one : `${n} ${many}`);
    }
  };
  add(Number(counts.addedCount), "1 added", "added");
  add(Number(counts.removedCount), "1 removed from the layout", "removed from the layout");
  add(Number(counts.behaviourChangeCount), "1 behaviour change", "behaviour changes");
  add(Number(counts.presentationChangeCount), "1 presentation-only change", "presentation-only changes");
  return parts.length === 0 ? "Only the form title changed." : `${parts.join(", ")}.`;
}
