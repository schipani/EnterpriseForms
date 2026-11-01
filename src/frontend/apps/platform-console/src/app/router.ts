import { useEffect, useState } from "react";

/** Minimal History-API router: three routes, no dependency. Paths live under /platform so the console is a distinct site section. */
export type Route = { name: "sign-in" } | { name: "approvals" } | { name: "review"; organizationId: string; requestId: string } | { name: "not-found" };

export function parseRoute(pathname: string): Route {
  const clean = pathname.replace(/\/+$/, "") || "/";
  if (clean === "/" || clean === "/platform" || clean === "/platform/approvals") {
    return { name: "approvals" };
  }
  if (clean === "/platform/sign-in") {
    return { name: "sign-in" };
  }
  const review = /^\/platform\/approvals\/([0-9a-f-]{36})\/([0-9a-f-]{36})$/i.exec(clean);
  if (review) {
    return { name: "review", organizationId: review[1]!, requestId: review[2]! };
  }
  return { name: "not-found" };
}

export const reviewPath = (organizationId: string, requestId: string) => `/platform/approvals/${organizationId}/${requestId}`;

export function navigate(to: string, replace = false): void {
  if (replace) {
    window.history.replaceState(null, "", to);
  } else {
    window.history.pushState(null, "", to);
  }
  window.dispatchEvent(new PopStateEvent("popstate"));
}

export function useRoute(): Route {
  const [route, setRoute] = useState(() => parseRoute(window.location.pathname));
  useEffect(() => {
    const update = () => setRoute(parseRoute(window.location.pathname));
    window.addEventListener("popstate", update);
    return () => window.removeEventListener("popstate", update);
  }, []);
  return route;
}
