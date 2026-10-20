import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { FormRenderer } from "./FormRenderer";
import type { GroupValueMap } from "./groups";
import { formLayoutSchema, type FormLayout } from "./layout";
import type { RendererField, ValueMap } from "./validation";

const groupId = "0199a2b3-c4d5-7e6f-8123-000000000001";
const nameId = "0199a2b3-c4d5-7e6f-8123-000000000020";
const roleId = "0199a2b3-c4d5-7e6f-8123-000000000021";
const fields: RendererField[] = [
  { fieldDefinitionId: nameId, fieldKey: "person_name", fieldDisplayName: "Name", fieldDataType: "String", options: [], groupDefinitionId: groupId },
  { fieldDefinitionId: roleId, fieldKey: "person_role", fieldDisplayName: "Role", fieldDataType: "String", options: [], groupDefinitionId: groupId },
];
const layoutWith = (repeat: Partial<NonNullable<FormLayout["sections"][number]["repeat"]>> = {}): FormLayout =>
  formLayoutSchema.parse({
    layoutSchemaVersion: 2,
    title: "Case",
    sections: [
      {
        sectionKey: "people",
        title: "Person",
        repeat: { groupDefinitionId: groupId, minOccurrences: 1, maxOccurrences: 3, allowAdd: true, allowRemove: true, allowReorder: true, ...repeat },
        elements: [
          { kind: "field", fieldDefinitionId: nameId, control: "text", label: "Name", required: true, layout: { columnSpan: 2, startOnNewRow: false } },
          { kind: "field", fieldDefinitionId: roleId, control: "text", label: "Role", layout: { columnSpan: 2, startOnNewRow: false } },
        ],
      },
    ],
  });

function Harness({ layout, initial }: { layout: FormLayout; initial: GroupValueMap }) {
  const [groups, setGroups] = useState(initial);
  const values: ValueMap = {};
  return (
    <>
      <FormRenderer layout={layout} fields={fields} values={values} groups={groups} groupInfos={[{ groupDefinitionId: groupId, groupKey: "people" }]} onGroupsChange={setGroups} errors={{ "people[2].person_name": "Name is required (entry 2)" }} breakpoint="desktop" />
      <pre data-testid="state">{JSON.stringify(groups.people?.map((e) => [e.formRecordGroupId, e.values.person_name ?? ""]))}</pre>
    </>
  );
}

const state = () => JSON.parse(screen.getByTestId("state").textContent ?? "[]") as Array<[string | null, string]>;

describe("repeating section", () => {
  it("adds, edits, reorders and removes entries with labelled controls and announcements", async () => {
    const user = userEvent.setup();
    render(<Harness layout={layoutWith()} initial={{ people: [{ formRecordGroupId: "a", clientKey: "a", values: { person_name: "Ada" } }] }} />);
    const list = screen.getByRole("list", { name: "Person entries" });
    expect(within(list).getAllByRole("listitem")).toHaveLength(1);
    // A single entry cannot be removed below the minimum of 1, nor moved.
    expect(screen.queryByRole("button", { name: "Remove Person 1" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Move up, Person 1" })).toBeNull();

    await user.click(screen.getByRole("button", { name: "Add Person" }));
    expect(screen.getByRole("status")).toHaveTextContent("Person 2 added.");
    const second = screen.getByRole("region", { name: "Person, entry 2 of 2" });
    expect(second).toHaveFocus();
    await user.type(within(second).getByLabelText(/Name/), "Bo");
    expect(state()).toEqual([
      ["a", "Ada"],
      [null, "Bo"],
    ]);
    // Errors use the server's entry path and appear next to the right entry's control.
    expect(within(second).getByRole("alert")).toHaveTextContent("Name is required (entry 2)");

    await user.click(screen.getByRole("button", { name: "Move up, Person 2" }));
    expect(state()).toEqual([
      [null, "Bo"],
      ["a", "Ada"],
    ]);
    expect(screen.getByRole("status")).toHaveTextContent("moved to position 1");

    await user.click(screen.getByRole("button", { name: "Remove Person 1" }));
    expect(state()).toEqual([["a", "Ada"]]);
    expect(screen.getByRole("status")).toHaveTextContent("1 entry remain");
  });

  it("hides add, remove and reorder when the section does not allow them and stops adding at the maximum", async () => {
    const user = userEvent.setup();
    const { unmount } = render(<Harness layout={layoutWith({ allowAdd: false, allowRemove: false, allowReorder: false, minOccurrences: 2, maxOccurrences: 2 })} initial={{ people: [{ formRecordGroupId: "a", clientKey: "a", values: {} }, { formRecordGroupId: "b", clientKey: "b", values: {} }] }} />);
    expect(screen.queryByRole("button", { name: /Add Person/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /Remove/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /Move/ })).toBeNull();
    unmount();

    render(<Harness layout={layoutWith({ minOccurrences: 0, maxOccurrences: 2 })} initial={{ people: [] }} />);
    expect(screen.getByText("No entries yet.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Add Person" }));
    await user.click(screen.getByRole("button", { name: "Add Person" }));
    expect(screen.queryByRole("button", { name: "Add Person" })).toBeNull();
    expect(screen.getByText(/2 of up to 2 entries/)).toBeInTheDocument();
  });

  it("shows entries read-only without controls", () => {
    render(<FormRenderer layout={layoutWith()} fields={fields} values={{}} readOnly groups={{ people: [{ formRecordGroupId: "a", clientKey: "a", values: { person_name: "Ada", person_role: "Witness" } }] }} groupInfos={[{ groupDefinitionId: groupId, groupKey: "people" }]} breakpoint="phone" />);
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(screen.queryByRole("button")).toBeNull();
    expect(screen.getByText("Ada")).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Person, entry 1 of 1" })).toBeInTheDocument();
  });
});
