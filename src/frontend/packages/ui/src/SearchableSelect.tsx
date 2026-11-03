import { useId, useState } from "react";
import { TextInput } from "./Field";
import { Select, type SelectOption, type SelectProps } from "./Select";

export interface SearchableSelectProps extends Omit<SelectProps, "actions"> {
  /** Label of the filter box; it narrows the list without removing the current selection. */
  filterLabel: string;
  /** Shown when the filter matches nothing. */
  noMatchText?: string;
}

/**
 * A Select with a filter box above it, for long lists (time zones). The selected option always stays in the list so the
 * control never shows an empty value because of a filter; matching is case-insensitive on the visible label and the value.
 */
export function SearchableSelect({ filterLabel, noMatchText = "No match. Clear the filter to see every option.", options, value, ...rest }: SearchableSelectProps) {
  const id = useId();
  const [filter, setFilter] = useState("");
  const needle = filter.trim().toLowerCase();
  const visible: SelectOption[] = needle ? options.filter((o) => o.value === value || o.label.toLowerCase().includes(needle) || o.value.toLowerCase().includes(needle)) : options;
  const matches = needle ? options.filter((o) => o.label.toLowerCase().includes(needle) || o.value.toLowerCase().includes(needle)).length : options.length;
  return (
    <div className="flex min-w-0 flex-col gap-2">
      <TextInput label={filterLabel} value={filter} onChange={(event) => setFilter(event.target.value)} aria-describedby={`${id}-matches`} autoComplete="off" />
      <p id={`${id}-matches`} className="text-help text-muted" role="status">
        {matches === 0 ? noMatchText : `${matches} ${matches === 1 ? "choice" : "choices"}`}
      </p>
      <Select {...rest} value={value} options={visible} />
    </div>
  );
}
