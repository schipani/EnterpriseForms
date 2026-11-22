import { useEffect, useRef } from "react";

const focusLost = () => document.activeElement === null || document.activeElement === document.body;

/**
 * Keyboard focus for a paged audit list (explorer, security-command timeline) whose rows and paging controls are replaced by the
 * error panel when a page read fails, which drops the focus of the Next or Previous control that was used to the document body.
 * After a failed page move the error container receives focus, once per failure; after "Try again" the page navigation receives
 * it (the retried page is announced by the navigation's live region; Next or Previous may still be unavailable, so neither is
 * chosen). Focus is moved only when it was lost, so it is never taken from where the user has put it since, and a failed first
 * load or filter change is left as it is.
 */
export function usePagingErrorFocus(query: { isError: boolean; errorUpdatedAt: number; isSuccess: boolean; isPlaceholderData: boolean; dataUpdatedAt: number }) {
  const errorTarget = useRef<HTMLDivElement>(null);
  const pagingTarget = useRef<HTMLElement>(null);
  const pageMove = useRef(false);
  const retrying = useRef(false);
  const { isError, errorUpdatedAt, isSuccess, isPlaceholderData, dataUpdatedAt } = query;

  useEffect(() => {
    if (isError && pageMove.current && focusLost()) {
      errorTarget.current?.focus();
    }
  }, [isError, errorUpdatedAt]);

  useEffect(() => {
    if (!isError && retrying.current && pagingTarget.current) {
      retrying.current = false;
      if (focusLost()) {
        pagingTarget.current.focus();
      }
    }
  }, [isError]);

  // A page move ends when its page has been shown.
  useEffect(() => {
    if (isSuccess && !isPlaceholderData) {
      pageMove.current = false;
    }
  }, [isSuccess, isPlaceholderData, dataUpdatedAt]);

  return {
    errorTarget,
    pagingTarget,
    /** A paging control has started a page move. */
    pageMoveStarted: () => {
      pageMove.current = true;
    },
    /** The error panel's "Try again" was used; focus follows to the page navigation only if a page move had failed. */
    retryStarted: () => {
      retrying.current = pageMove.current;
    },
  };
}
