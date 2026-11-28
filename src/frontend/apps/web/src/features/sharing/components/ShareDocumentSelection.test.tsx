import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ShareDocumentSelection, toDocumentRequests, type DocumentSelection } from "./ShareDocumentSelection";

/** C10 WP8-B: the replace option is offered only where the owner grants the share directly, and is sent explicitly when chosen. */

const documents = [{ fileAttachmentId: "doc-1", originalFileName: "statement.pdf" }] as never;

describe("ShareDocumentSelection", () => {
  it("offers replacing only when allowed and records the choice", () => {
    const onChange = vi.fn();
    const selection: DocumentSelection = new Map([["doc-1", { download: false }]]);
    const { rerender } = render(<ShareDocumentSelection documents={documents} selection={selection} onChange={onChange} />);
    expect(screen.queryByRole("checkbox", { name: "Allow replacing statement.pdf" })).toBeNull();

    rerender(<ShareDocumentSelection documents={documents} selection={selection} onChange={onChange} allowReplace />);
    fireEvent.click(screen.getByRole("checkbox", { name: "Allow replacing statement.pdf" }));

    const next = onChange.mock.calls[0]![0] as DocumentSelection;
    expect(toDocumentRequests(next)).toEqual([{ fileAttachmentId: "doc-1", canDownloadDocument: false, canReplaceDocument: true }]);
    expect(toDocumentRequests(selection)).toEqual([{ fileAttachmentId: "doc-1", canDownloadDocument: false }]);
  });
});
