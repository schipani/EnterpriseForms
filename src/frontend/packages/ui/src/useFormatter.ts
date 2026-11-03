import { useSyncExternalStore } from "react";
import { getActiveFormatter, getFormattingVersion, subscribeFormatting, type Formatter } from "./formatting";

/**
 * The session's formatter. Subscribes to the formatting store, so a component re-renders as soon as the user's or the
 * organization's regional settings change. Use this in components; never format dates or numbers with `toLocale*String`.
 */
export function useFormatter(): Formatter {
  useSyncExternalStore(subscribeFormatting, getFormattingVersion, getFormattingVersion);
  return getActiveFormatter();
}
