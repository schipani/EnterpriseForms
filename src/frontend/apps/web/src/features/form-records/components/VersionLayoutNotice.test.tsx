import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { EditLayoutNotice, ViewLayoutNotice, layoutBadgeText } from "./VersionLayoutNotice";

/** Phase C9: the record page and editor say which published layout they use; the server chooses it. */

function projection(overrides: Record<string, unknown>) {
  return {
    layoutVersion: { formVersionId: "v1", versionNumber: 1 },
    versionBehavior: "Latest",
    layoutPurpose: "View",
    createdFormVersionNumber: 1,
    editLayoutVersionId: "v1",
    editLayoutVersionNumber: 1,
    ...overrides,
  } as unknown as Parameters<typeof layoutBadgeText>[0];
}

describe("layoutBadgeText", () => {
  it("names the created version only when it differs", () => {
    expect(layoutBadgeText(projection({}))).toBe("Layout v1");
    expect(layoutBadgeText(projection({ layoutVersion: { formVersionId: "v3", versionNumber: 3 } }))).toBe("Layout v3 (created with v1)");
  });
});

describe("ViewLayoutNotice", () => {
  it("explains a Hybrid record shown with its created version and edited with the current one", () => {
    render(<ViewLayoutNotice projection={projection({ versionBehavior: "Hybrid", editLayoutVersionId: "v3", editLayoutVersionNumber: 3 })} canEdit />);
    expect(screen.getByText("Shown with layout v1; edits use layout v3")).toBeInTheDocument();
  });

  it("warns an editor when no edit layout is available, and says nothing to readers", () => {
    const { rerender, container } = render(<ViewLayoutNotice projection={projection({ editLayoutVersionId: null, editLayoutVersionNumber: null })} canEdit />);
    expect(screen.getByText("This record can't be edited right now")).toBeInTheDocument();
    rerender(<ViewLayoutNotice projection={projection({ editLayoutVersionId: null, editLayoutVersionNumber: null })} canEdit={false} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("says nothing when the record is shown and edited with the same layout", () => {
    const { container } = render(<ViewLayoutNotice projection={projection({ versionBehavior: "Historical" })} canEdit />);
    expect(container).toBeEmptyDOMElement();
  });
});

describe("EditLayoutNotice", () => {
  it("explains Historical editing with the created version", () => {
    render(<EditLayoutNotice projection={projection({ versionBehavior: "Historical", layoutPurpose: "Edit" })} />);
    expect(screen.getByText("Editing with layout v1, the version this record was created with")).toBeInTheDocument();
  });

  it("explains editing an older record with the current layout and that removed fields keep their values", () => {
    render(<EditLayoutNotice projection={projection({ versionBehavior: "Hybrid", layoutPurpose: "Edit", layoutVersion: { formVersionId: "v3", versionNumber: 3 } })} />);
    expect(screen.getByText("Editing with the current layout v3")).toBeInTheDocument();
    expect(screen.getByText(/created with layout v1 and is shown with it/)).toBeInTheDocument();
    expect(screen.getByText(/no longer part of the form are kept unchanged/)).toBeInTheDocument();
  });

  it("says nothing for a Latest record created with the current version", () => {
    const { container } = render(<EditLayoutNotice projection={projection({ layoutPurpose: "Edit" })} />);
    expect(container).toBeEmptyDOMElement();
  });
});
