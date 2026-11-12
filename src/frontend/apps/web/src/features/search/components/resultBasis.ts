/**
 * IX-4 (indexed search design §6.6): how a field-search result is described, by the engine that produced it (`resultBasis`).
 * An indexed result (`AuthorityAcceptedContent`) was evaluated on the content the Authorization Authority last accepted; it is never
 * called "verified" and never implies the business copy is intact — that copy is verified when a record is opened. A verified scan
 * (`VerifiedScan`, or an older server without the field) read and verified every record in scope.
 */
export type ResultBasis = "AuthorityAcceptedContent" | "VerifiedScan";

export function resultBasisOf(value: string | null | undefined): ResultBasis {
  return value === "AuthorityAcceptedContent" ? "AuthorityAcceptedContent" : "VerifiedScan";
}

export interface ResultBasisCopy {
  /** Body of the empty state when the complete search matched nothing. */
  emptyMessage: string;
}

export function resultBasisCopy(value: string | null | undefined): ResultBasisCopy {
  return resultBasisOf(value) === "AuthorityAcceptedContent"
    ? { emptyMessage: "The search was complete for the records you can access, evaluated on the content the Authorization Authority last accepted for each record." }
    : { emptyMessage: "The search was complete and verified for the records you can access." };
}
