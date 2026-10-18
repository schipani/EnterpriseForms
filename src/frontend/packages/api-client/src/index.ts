/**
 * Generated contract types for the business API. The transport itself lives in the app-shell request
 * coordinator (packages/work-queue, ADR-PB-007); this package exports types and route constants only.
 */
export type { components as EnterpriseFormsV1Components, paths as EnterpriseFormsV1Paths } from "./generated/enterprise-forms-v1";

export const API_V1_PREFIX = "/api/v1" as const;
