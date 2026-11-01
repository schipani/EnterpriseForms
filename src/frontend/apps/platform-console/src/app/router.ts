import { useEffect, useState } from "react";

/** Minimal History-API router: a few routes, no dependency. Paths live under /platform so the console is a distinct site section. */
export type Route =
  | { name: "sign-in" }
  | { name: "approvals" }
  | { name: "review"; organizationId: string; requestId: string }
  | { name: "organizations" }
  | { name: "organization"; organizationId: string }
  | { name: "onboarding" }
  | { name: "onboarding-new" }
  | { name: "onboarding-detail"; requestId: string }
  | { name: "not-found" };

export function parseRoute(pathname: string): Route {
  const clean = pathname.replace(/\/+$/, "") || "/";
  if (clean === "/" || clean === "/platform" || clean === "/platform/approvals") {
    return { name: "approvals" };
  }
  if (clean === "/platform/sign-in") {
    return { name: "sign-in" };
  }
  if (clean === "/platform/organizations") {
    return { name: "organizations" };
  }
  if (clean === "/platform/onboarding") {
    return { name: "onboarding" };
  }
  if (clean === "/platform/onboarding/new") {
    return { name: "onboarding-new" };
  }
  const organization = /^\/platform\/organizations\/([0-9a-f-]{36})$/i.exec(clean);
  if (organization) {
    return { name: "organization", organizationId: organization[1]! };
  }
  const onboarding = /^\/platform\/onboarding\/([0-9a-f-]{36})$/i.exec(clean);
  if (onboarding) {
    return { name: "onboarding-detail", requestId: onboarding[1]! };
  }
  const review = /^\/platform\/approvals\/([0-9a-f-]{36})\/([0-9a-f-]{36})$/i.exec(clean);
  if (review) {
    return { name: "review", organizationId: review[1]!, requestId: review[2]! };
  }
  return { name: "not-found" };
}

export const reviewPath = (organizationId: string, requestId: string) => `/platform/approvals/${organizationId}/${requestId}`;

export const organizationPath = (organizationId: string) => `/platform/organizations/${organizationId}`;
export const onboardingPath = (requestId: string) => `/platform/onboarding/${requestId}`;

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
