import { DescriptionList } from "./Surfaces";
import { createFormatter, zoneOffsetLabel, type FormattingSettings } from "./formatting";

/** A fixed instant just after the 2026 spring-forward change in North America, so a preview shows the daylight offset in force. */
const SAMPLE_INSTANT = "2026-03-08T07:30:00Z";
const SAMPLE_DATE = "2026-03-08";

/**
 * Live preview of a set of regional settings (C8 M3): the same fixed instant, date-only value and numbers formatted exactly as
 * the application will format them. Pure display from the settings passed in; it never reads or changes any stored data.
 */
export function FormattingPreview({ settings }: { settings: FormattingSettings }) {
  const formatter = createFormatter(settings);
  return (
    <DescriptionList
      items={[
        { term: "Date and time", detail: <span data-testid="preview-instant">{formatter.instant(SAMPLE_INSTANT)}</span> },
        { term: "Time zone", detail: <span data-testid="preview-zone">{`${settings.timeZone} (${zoneOffsetLabel(settings.timeZone, new Date(SAMPLE_INSTANT))} on the sample date)`}</span> },
        { term: "Date only", detail: <span data-testid="preview-date-only">{formatter.dateOnly(SAMPLE_DATE)}</span> },
        { term: "Numbers", detail: <span data-testid="preview-numbers">{`${formatter.number(1234567.891, { maximumFractionDigits: 2 })} · ${formatter.number(-1234.5, { minimumFractionDigits: 2 })} · ${formatter.number(0.5)}`}</span> },
        { term: "Language", detail: <span data-testid="preview-language">{settings.language}</span> },
      ]}
    />
  );
}
