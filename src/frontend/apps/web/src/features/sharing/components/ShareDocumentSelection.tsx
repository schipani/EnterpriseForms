import type { DocumentSummaryResponse } from "@enterpriseforms/api-client";
import { Checkbox, legendClass } from "@enterpriseforms/ui";

/** Selected documents of a share: attachment id → whether the recipient may download (viewing is always included). */
export type DocumentSelection = ReadonlyMap<string, { download: boolean }>;

/** The request form of a selection (C10 WP8: view always; the Authority binds each to its verified current version). */
export const toDocumentRequests = (selection: DocumentSelection) =>
  [...selection.entries()].map(([fileAttachmentId, flags]) => ({ fileAttachmentId, canDownloadDocument: flags.download }));

/**
 * C10 M3 / WP8 (WP-0 Section 3.6): choose which of the record's documents a share covers and whether each may be downloaded. A share
 * covers the version that is current now; versions added later stay hidden until the share is updated.
 */
export function ShareDocumentSelection({ documents, selection, onChange }: { documents: readonly DocumentSummaryResponse[]; selection: DocumentSelection; onChange: (next: DocumentSelection) => void }) {
  if (documents.length === 0) {
    return null;
  }
  const set = (id: string, value: { download: boolean } | null) => {
    const next = new Map(selection);
    if (value) {
      next.set(id, value);
    } else {
      next.delete(id);
    }
    onChange(next);
  };
  return (
    <fieldset className="flex flex-col gap-2 rounded-control border border-line p-3">
      <legend className={`px-1 ${legendClass}`}>Documents to share</legend>
      {documents.map((document) => {
        const chosen = selection.get(document.fileAttachmentId);
        return (
          <div key={document.fileAttachmentId} className="flex flex-wrap items-center gap-4">
            <Checkbox label={document.originalFileName} checked={!!chosen} onCheckedChange={(checked) => set(document.fileAttachmentId, checked === true ? { download: chosen?.download ?? false } : null)} />
            {chosen ? (
              <Checkbox label={`Allow download of ${document.originalFileName}`} checked={chosen.download} onCheckedChange={(checked) => set(document.fileAttachmentId, { download: checked === true })} />
            ) : null}
          </div>
        );
      })}
      <p className="text-sm text-muted">A shared document covers its current version. Versions added later are not included until the share is updated.</p>
    </fieldset>
  );
}
