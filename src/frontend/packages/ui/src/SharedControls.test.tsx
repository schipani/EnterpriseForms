// Testing Library matchers (the app's vitest-env.d.ts is outside this package's type scope).
import "@testing-library/jest-dom/vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { buttonClass, IconButton } from "./Button";
import { Checkbox } from "./Checkbox";
import { TextInput } from "./Field";
import { RadioGroup } from "./RadioGroup";

describe("UI consistency standard: shared controls", () => {
  it("TextInput uses the shared control geometry and ties help and error to the control", () => {
    render(<TextInput label="Given name" required helpText="As on the warrant." error="Enter a given name." />);
    const input = screen.getByRole("textbox", { name: /Given name/ });
    expect(input.className).toContain("min-h-control");
    expect(input.className).toContain("rounded-control");
    expect(input.className).toContain("text-control");
    expect(input).toHaveAttribute("aria-invalid", "true");
    expect(input.getAttribute("aria-describedby")?.split(" ")).toHaveLength(2);
    expect(screen.getByRole("alert")).toHaveClass("text-help", "text-danger");
    expect(screen.getByText("(required)")).toHaveClass("sr-only");
  });

  it("Checkbox labels use the shared choice-label style", () => {
    render(<Checkbox label="In custody" checked={false} onCheckedChange={() => undefined} />);
    expect(screen.getByText("In custody")).toHaveClass("text-label", "font-medium");
  });

  it("RadioGroup is a named group of native radios; choosing one reports it and shows its details only while selected", () => {
    const onValueChange = vi.fn();
    const { rerender } = render(
      <RadioGroup legend="What should happen to the 2 items?" value={undefined} onValueChange={onValueChange} options={[{ value: "move", label: "Move them", details: <p>Target section</p> }, { value: "unplace", label: "Un-place them" }, { value: "keep", label: "Keep them", disabled: true }]} />,
    );
    expect(screen.getByRole("group", { name: "What should happen to the 2 items?" })).toBeInTheDocument();
    expect(screen.getAllByRole("radio")).toHaveLength(3);
    expect(screen.getByRole("radio", { name: "Keep them" })).toBeDisabled();
    expect(screen.queryByText("Target section")).toBeNull();
    fireEvent.click(screen.getByRole("radio", { name: "Move them" }));
    expect(onValueChange).toHaveBeenCalledWith("move");
    rerender(<RadioGroup legend="What should happen to the 2 items?" value="move" onValueChange={onValueChange} options={[{ value: "move", label: "Move them", details: <p>Target section</p> }, { value: "unplace", label: "Un-place them" }]} />);
    expect(screen.getByRole("radio", { name: "Move them" })).toBeChecked();
    expect(screen.getByText("Target section")).toBeInTheDocument();
  });

  it("RadioGroup announces an error and marks a required group", () => {
    render(<RadioGroup legend="Disposition" required error="Choose what happens to the items." value={undefined} onValueChange={() => undefined} options={[{ value: "a", label: "A" }]} />);
    expect(screen.getByRole("alert")).toHaveTextContent("Choose what happens to the items.");
    expect(screen.getByRole("group", { name: /Disposition/ })).toHaveAttribute("aria-invalid", "true");
  });

  it("IconButton takes its accessible name and tooltip from its label and uses the control size", () => {
    const onClick = vi.fn();
    render(
      <IconButton label="Close properties" onClick={onClick}>
        <svg aria-hidden="true" />
      </IconButton>,
    );
    const button = screen.getByRole("button", { name: "Close properties" });
    expect(button).toHaveAttribute("title", "Close properties");
    expect(button).toHaveAttribute("type", "button");
    expect(button.className).toContain("h-control");
    fireEvent.click(button);
    expect(onClick).toHaveBeenCalledOnce();
  });

  it("buttonClass gives links the same look as the matching Button variant and size", () => {
    expect(buttonClass("secondary", "sm")).toContain("min-h-control-compact");
    expect(buttonClass("secondary", "sm")).toContain("rounded-control");
    expect(buttonClass("primary")).toContain("min-h-control ");
  });
});
