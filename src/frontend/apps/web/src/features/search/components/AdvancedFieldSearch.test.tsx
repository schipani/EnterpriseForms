import type { FieldSearchCapabilitiesResponse } from "@enterpriseforms/api-client";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { AdvancedFieldSearch } from "./AdvancedFieldSearch";

const unavailable: FieldSearchCapabilitiesResponse = {
  formDefinitionId: "0199a2b3-c4d5-7e6f-8123-456789abcdf9",
  fieldValueSearchAvailable: false,
  unavailableReasonCode: "TRUSTED_SEARCH_NOT_ENABLED",
  maximumConditions: 5,
  conditionJoin: "And",
  policyRevision: null,
  fields: [],
  notSearchableFields: [],
};
const available: FieldSearchCapabilitiesResponse = {
  ...unavailable,
  fieldValueSearchAvailable: true,
  unavailableReasonCode: null,
  fields: [{ fieldDefinitionId: "0199a2b3-c4d5-7e6f-8123-456789abcde1", displayLabel: "Given name", valueType: "String", operators: ["equals"], options: [], minimumTextLength: 3, maximumTextLength: 200 }],
  notSearchableFields: [{ fieldDefinitionId: "0199a2b3-c4d5-7e6f-8123-456789abcde9", displayLabel: "Case notes", reasonCode: "ENCRYPTED" }],
};

describe("AdvancedFieldSearch", () => {
  it("is collapsed by default behind an accessible disclosure", async () => {
    render(<AdvancedFieldSearch formSelected capabilities={unavailable} />);
    const toggle = screen.getByRole("button", { name: /Advanced Search/ });
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("group", { name: "Condition 1" })).toBeNull();
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("group", { name: "Condition 1" })).toBeVisible();
  });

  it("when the server reports search unavailable, states it and disables every control", () => {
    const onSearch = vi.fn();
    render(<AdvancedFieldSearch formSelected capabilities={unavailable} defaultExpanded onSearch={onSearch} />);
    expect(screen.getByText("Not available yet")).toBeInTheDocument();
    expect(screen.getByText("Advanced field search is not available yet")).toBeInTheDocument();
    const condition = screen.getByRole("group", { name: "Condition 1" });
    expect(within(condition).getByRole("combobox", { name: "Field" })).toBeDisabled();
    expect(within(condition).getByRole("combobox", { name: "Condition" })).toBeDisabled();
    expect(within(condition).getByRole("textbox", { name: "Value" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Add condition" })).toBeDisabled();
    const run = screen.getByRole("button", { name: "Search records" });
    expect(run).toBeDisabled();
    // Even a forced submit does not run anything while unavailable.
    run.closest("form")!.requestSubmit();
    expect(onSearch).not.toHaveBeenCalled();
  });

  it("with approved fields but execution off, lets conditions be composed from the server's fields and never runs them", async () => {
    const onSearch = vi.fn();
    const approvedButOff: FieldSearchCapabilitiesResponse = { ...unavailable, fields: available.fields };
    render(<AdvancedFieldSearch formSelected capabilities={approvedButOff} defaultExpanded onSearch={onSearch} />);
    expect(screen.getByText("Advanced field search is not available yet")).toBeInTheDocument();
    const condition = screen.getByRole("group", { name: "Condition 1" });
    // The Field control is live (its choices are exactly the server's approved fields; the browser test opens it).
    expect(within(condition).getByRole("combobox", { name: "Field" })).toBeEnabled();
    expect(within(condition).getByRole("combobox", { name: "Condition" })).toBeDisabled();
    await userEvent.click(screen.getByRole("button", { name: "Add condition" }));
    expect(screen.getAllByRole("group", { name: /^Condition \d$/ })).toHaveLength(2);
    expect(screen.getByText(/nothing is searched until trusted search is turned on/)).toBeInTheDocument();
    const run = screen.getByRole("button", { name: "Search records" });
    expect(run).toBeDisabled();
    run.closest("form")!.requestSubmit();
    expect(onSearch).not.toHaveBeenCalled();
  });

  it("without a selected form asks for one and offers nothing", () => {
    render(<AdvancedFieldSearch formSelected={false} capabilities={undefined} defaultExpanded />);
    expect(screen.getByText("Choose a form first")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Search records" })).toBeDisabled();
  });

  it("does not run without onSearch even if capabilities say available", () => {
    render(<AdvancedFieldSearch formSelected capabilities={available} defaultExpanded />);
    expect(screen.getByRole("button", { name: "Search records" })).toBeDisabled();
  });

  it("associates Field, Condition and Value labels with their controls inside each condition group, up to five groups", async () => {
    render(<AdvancedFieldSearch formSelected capabilities={available} defaultExpanded onSearch={vi.fn()} />);
    const add = screen.getByRole("button", { name: "Add condition" });
    for (let i = 0; i < 6; i++) {
      if (!(add as HTMLButtonElement).disabled) {
        await userEvent.click(add);
      }
    }
    expect(screen.getAllByRole("group", { name: /^Condition \d$/ })).toHaveLength(5);
    expect(add).toBeDisabled();
    expect(screen.getByText("You can use up to 5 conditions.")).toBeInTheDocument();
    for (let n = 1; n <= 5; n++) {
      const group = screen.getByRole("group", { name: `Condition ${n}` });
      expect(within(group).getByRole("combobox", { name: "Field" })).toBeEnabled();
      expect(within(group).getByRole("combobox", { name: "Condition" })).toBeDisabled();
      expect(within(group).getByRole("textbox", { name: "Value" })).toBeInTheDocument();
      expect(within(group).getByRole("button", { name: `Remove condition ${n}` })).toBeInTheDocument();
    }
    await userEvent.click(screen.getByRole("button", { name: "Remove condition 3" }));
    expect(screen.getAllByRole("group", { name: /^Condition \d$/ })).toHaveLength(4);
    expect(add).toBeEnabled();
  });

  it("validates before running and does not call onSearch with incomplete conditions", async () => {
    const onSearch = vi.fn();
    render(<AdvancedFieldSearch formSelected capabilities={available} defaultExpanded onSearch={onSearch} />);
    await userEvent.click(screen.getByRole("button", { name: "Search records" }));
    expect(onSearch).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("Choose a field.");
  });

  it("lists fields that cannot be searched with the approved wording", () => {
    render(<AdvancedFieldSearch formSelected capabilities={available} defaultExpanded onSearch={vi.fn()} />);
    expect(screen.getByText(/Encrypted for privacy — not searchable: Case notes\./)).toBeInTheDocument();
  });
});
