import { z } from "zod";
import { ruleConditionSchema, ruleShapeProblem } from "./rules";

/**
 * TypeScript mirror of the server layout contract (Application/FormDefinitions/Layout/FormLayout.cs, schema v2).
 * The server remains authoritative: this schema exists so the designer and renderer can parse/produce the same
 * document and give early feedback. Unknown properties are rejected so nothing executable can ride along.
 */
const placement = z.object({ columnSpan: z.number().int().min(1).max(4), startOnNewRow: z.boolean() }).strict();
const validation = z
  .object({
    minLength: z.number().int().min(0).optional(),
    maxLength: z.number().int().min(1).max(4000).optional(),
    pattern: z.string().max(200).optional(),
    minimum: z.number().optional(),
    maximum: z.number().optional(),
    minDate: z.string().regex(/^\d{4}-\d{2}-\d{2}$/).optional(),
    maxDate: z.string().regex(/^\d{4}-\d{2}-\d{2}$/).optional(),
  })
  .strict();
const upload = z
  .object({
    allowMultiple: z.boolean().optional(),
    maxFiles: z.number().int().min(1).max(100).optional(),
    maxFileSizeBytes: z.number().int().min(1).max(500 * 1024 * 1024).optional(),
    allowedContentTypes: z.array(z.string().max(100)).max(50).optional(),
  })
  .strict();

/** Published declaration of a field's classification and storage protection (ADR-PB-004); informational for the renderer. */
const protection = z.object({ classification: z.enum(["Public", "Internal", "Confidential", "Restricted"]), protectionMode: z.enum(["Standard", "ApplicationEncrypted"]) }).strict();

/** Option as published (snapshot): stable id and key, the label and order shown at publish time, and whether it was selectable. */
const optionSnapshot = z.object({ fieldOptionId: z.string().uuid(), optionKey: z.string().regex(/^[a-z][a-z0-9_]{0,63}$/), label: z.string().max(200), sortOrder: z.number().int(), isActive: z.boolean() }).strict();

const ruleSchema = ruleConditionSchema.refine((condition) => ruleShapeProblem(condition) === null, "The rule is nested too deeply or has too many conditions");

const safeText = (max: number) => z.string().max(max).refine((text) => !/[<>]/.test(text), "Angle brackets are not permitted in layout text");

const fieldElement = z
  .object({
    kind: z.literal("field"),
    fieldDefinitionId: z.string().uuid(),
    control: z.enum(["text", "textarea", "number", "decimal", "date", "dateTime", "guid", "checkbox", "select", "multiselect"]).default("text"),
    label: safeText(200).default(""),
    helpText: safeText(1000).optional(),
    /** Optional input hint shown inside empty text-like controls (presentation only; never a default value). */
    placeholder: safeText(200).optional(),
    required: z.boolean().default(false),
    layout: placement.default({ columnSpan: 4, startOnNewRow: false }),
    validation: validation.optional(),
    protection: protection.optional(),
    /** Dropdowns: the options as published (snapshot; the designer's draft does not carry it, publishing adds it). */
    options: z.array(optionSnapshot).max(500).optional(),
    /** Dropdowns: option keys pre-selected for a new record (a convenience, validated by the server; never a stored value by itself). */
    defaultOptionKeys: z.array(z.string().regex(/^[a-z][a-z0-9_]{0,63}$/)).max(100).optional(),
    /** Presentation only: false removes the field from the data-entry form. It is never an authorization decision. */
    showInForm: z.boolean().optional(),
    /** Enforced by the server on every user write. */
    readOnly: z.boolean().optional(),
    /** Opt-in: clear the stored value when a visibility rule hides the field (default: retain it). */
    clearWhenHidden: z.boolean().optional(),
    visibleWhen: ruleSchema.optional(),
    requiredWhen: ruleSchema.optional(),
  })
  .strict();
const documentElement = z
  .object({
    kind: z.literal("documentCollection"),
    fieldDefinitionId: z.string().uuid(),
    /** The server serializes the File control name; it is implied by the kind. */
    control: z.literal("fileUpload").optional(),
    label: safeText(200).default(""),
    helpText: safeText(1000).optional(),
    required: z.boolean().default(false),
    layout: placement.default({ columnSpan: 4, startOnNewRow: true }),
    validation: validation.optional(),
    upload: upload.optional(),
    protection: protection.optional(),
  })
  .strict();
const relatedElement = z
  .object({
    kind: z.literal("relatedFormList"),
    formRelationshipDefinitionId: z.string().uuid(),
    label: safeText(200).default(""),
    helpText: safeText(1000).optional(),
    layout: placement.default({ columnSpan: 4, startOnNewRow: true }),
    required: z.boolean().optional(),
    validation: validation.optional(),
    summaryFieldKeys: z.array(z.string().regex(/^[a-z][a-z0-9_]{0,63}$/)).max(6).default([]),
  })
  .strict();

export const layoutElementSchema = z.discriminatedUnion("kind", [fieldElement, documentElement, relatedElement]);
export const layoutSectionSchema = z
  .object({
    sectionKey: z.string().regex(/^[a-z][a-z0-9_]{0,63}$/),
    title: safeText(200).optional(),
    /** Optional section description (C1 designer rebuild); omitted when absent so older layouts are unchanged. */
    description: safeText(1000).optional(),
    gridColumns: z.literal(4).default(4),
    layout: z.object({ columnGap: z.enum(["small", "medium", "large"]).optional(), rowGap: z.enum(["small", "medium", "large"]).optional() }).strict().optional(),
    elements: z.array(layoutElementSchema).max(250),
  })
  .strict();
export const formLayoutSchema = z
  .object({
    /** Schema 1 (Stage A) layouts are readable; the designer always writes schema 2. */
    layoutSchemaVersion: z.union([z.literal(1), z.literal(2)]),
    title: safeText(200).default(""),
    sections: z.array(layoutSectionSchema).min(1).max(50),
  })
  .strict();

export type LayoutPlacement = z.infer<typeof placement>;
export type LayoutValidation = z.infer<typeof validation>;
export type LayoutUploadPolicy = z.infer<typeof upload>;
export type FieldElement = z.infer<typeof fieldElement>;
export type DocumentElement = z.infer<typeof documentElement>;
export type RelatedElement = z.infer<typeof relatedElement>;
export type LayoutElement = z.infer<typeof layoutElementSchema>;
export type LayoutSection = z.infer<typeof layoutSectionSchema>;
export type FormLayout = z.infer<typeof formLayoutSchema>;

export function parseLayout(json: string): FormLayout {
  return formLayoutSchema.parse(JSON.parse(json));
}

export function tryParseLayout(json: string): { ok: true; layout: FormLayout } | { ok: false; error: string } {
  try {
    return { ok: true, layout: parseLayout(json) };
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
}

/** Deterministic serialization (stable key order) so the designer's "dirty" check and the server's canonical form agree. */
export function serializeLayout(layout: FormLayout): string {
  return JSON.stringify(formLayoutSchema.parse({ ...layout, layoutSchemaVersion: 2 }), null, 2);
}

export function emptyLayout(title: string): FormLayout {
  return { layoutSchemaVersion: 2, title, sections: [{ sectionKey: "main", title, gridColumns: 4, elements: [] }] };
}

export const FIELD_DATA_TYPES = ["String", "Integer", "Decimal", "Date", "DateTime", "Guid", "Boolean", "Option", "File"] as const;
export type FieldDataType = (typeof FIELD_DATA_TYPES)[number];

/** Controls permitted per storage type (server rule in FormDesignerHandlers.ValidateLayout). */
export const CONTROLS_BY_TYPE: Record<FieldDataType, FieldElement["control"][]> = {
  String: ["text", "textarea"],
  Integer: ["number"],
  Decimal: ["decimal"],
  Date: ["date"],
  DateTime: ["dateTime"],
  Guid: ["guid"],
  Boolean: ["checkbox"],
  Option: ["select"],
  File: [],
};
