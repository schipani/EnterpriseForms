import { Button, LoadingBlock } from "@enterpriseforms/ui";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useState } from "react";
import { ApprovalsPage } from "../pages/ApprovalsPage";
import { OnboardingDetailPage } from "../pages/OnboardingDetailPage";
import { OnboardingNewPage } from "../pages/OnboardingNewPage";
import { OnboardingQueuePage } from "../pages/OnboardingQueuePage";
import { RecoveryDetailPage } from "../pages/RecoveryDetailPage";
import { RecoveryNewPage } from "../pages/RecoveryNewPage";
import { RecoveryQueuePage } from "../pages/RecoveryQueuePage";
import { OrganizationDetailPage, OrganizationsPage } from "../pages/OrganizationsPage";
import { ReviewPage } from "../pages/ReviewPage";
import { SignInPage } from "../pages/SignInPage";
import { formatTime } from "./format";
import { navigate, useRoute } from "./router";
import { SessionProvider, useConsoleSession } from "./session";

export const DEVELOPMENT_BANNER = "Development/Test operator — synthetic approval evidence";

const pageTitles: Record<string, string> = {
  review: "Review request",
  "sign-in": "Sign in",
  organizations: "Organizations",
  organization: "Organization",
  onboarding: "Organization onboarding",
  "onboarding-new": "New organization",
  "onboarding-detail": "Onboarding request",
  recovery: "Owner recovery",
  "recovery-new": "New recovery case",
  "recovery-detail": "Recovery case",
};

const navLinkClass = "flex min-h-11 items-center rounded-control px-3 text-control font-medium underline-offset-4 hover:underline aria-[current=page]:underline";

export function App() {
  // One client per mount; signing out drops every cached queue and request so nothing outlives the session.
  const client = useMemo(() => new QueryClient({ defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false, staleTime: 30_000 } } }), []);
  const onSignedOut = useCallback(() => client.clear(), [client]);
  return (
    <QueryClientProvider client={client}>
      <SessionProvider onSignedOut={onSignedOut}>
        <Shell />
      </SessionProvider>
    </QueryClientProvider>
  );
}

function Shell() {
  const { state, signOut } = useConsoleSession();
  const route = useRoute();
  const [signOutError, setSignOutError] = useState<string | null>(null);

  useEffect(() => {
    if (state.status === "signed-out" && route.name !== "sign-in") {
      navigate("/platform/sign-in", true);
    } else if (state.status === "signed-in" && (route.name === "sign-in" || window.location.pathname === "/")) {
      navigate("/platform/approvals", true);
    }
  }, [state.status, route.name]);

  useEffect(() => {
    document.title = `${pageTitles[route.name] ?? "Operator approvals"} — Platform Console`;
  }, [route.name]);

  const signedIn = state.status === "signed-in" ? state : null;
  return (
    <div className="min-h-screen bg-page text-body">
      <a href="#main-content" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-control focus:bg-surface focus:px-3 focus:py-2">
        Skip to main content
      </a>
      <header>
        <div className="bg-primary text-primary-foreground">
          <div className="mx-auto flex w-full max-w-7xl flex-wrap items-center gap-x-4 gap-y-2 px-4 py-2">
            <a href="/platform/approvals" onClick={(event) => { event.preventDefault(); navigate(signedIn ? "/platform/approvals" : "/platform/sign-in"); }} className="flex min-h-11 items-center gap-3 rounded-control">
              <span aria-hidden="true" className="flex h-9 w-9 items-center justify-center rounded-control bg-primary-foreground font-bold text-primary">
                PC
              </span>
              <span className="text-lg font-semibold">Platform Console</span>
            </a>
            {signedIn ? (
              <nav aria-label="Platform administration" className="flex flex-wrap items-center">
                {[
                  { href: "/platform/organizations", label: "Organizations", current: route.name === "organizations" || route.name === "organization" },
                  { href: "/platform/onboarding", label: "Onboarding", current: route.name === "onboarding" || route.name === "onboarding-new" || route.name === "onboarding-detail" },
                  { href: "/platform/recovery", label: "Owner recovery", current: route.name === "recovery" || route.name === "recovery-new" || route.name === "recovery-detail" },
                  { href: "/platform/approvals", label: "Operator approvals", current: route.name === "approvals" || route.name === "review" },
                ].map((link) => (
                  <a
                    key={link.href}
                    href={link.href}
                    aria-current={link.current ? "page" : undefined}
                    onClick={(event) => { event.preventDefault(); navigate(link.href); }}
                    className={navLinkClass}
                  >
                    {link.label}
                  </a>
                ))}
              </nav>
            ) : null}
            {signedIn ? (
              <div className="ml-auto flex flex-wrap items-center gap-3">
                <div className="min-w-0 text-right text-help" data-testid="operator-session">
                  <p className="truncate text-control font-medium">{signedIn.session.displayName}</p>
                  <p>
                    Session idle expiry {formatTime(new Date(signedIn.idleDeadline).toISOString())} · ends by {formatTime(signedIn.session.absoluteExpiresOn)}
                  </p>
                </div>
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => {
                    setSignOutError(null);
                    void signOut().catch(() => setSignOutError("Sign-out could not be confirmed. You are still signed in; try again."));
                  }}
                >
                  Sign out
                </Button>
              </div>
            ) : null}
          </div>
        </div>
        <div role="note" className="border-b border-[#92400e] bg-[#fffbeb] text-[#78350f]">
          <p className="mx-auto w-full max-w-7xl px-4 py-1.5 text-help font-medium">{DEVELOPMENT_BANNER}. Approvals made here do not prove independent human approval.</p>
        </div>
      </header>
      <main id="main-content" tabIndex={-1} className="mx-auto w-full max-w-7xl px-4 py-6">
        {signOutError ? (
          <p role="alert" className="mb-4 rounded-control border-l-4 border-[#991b1b] bg-[#fef2f2] px-4 py-3 text-sm text-[#7f1d1d]">
            {signOutError}
          </p>
        ) : null}
        {state.status === "loading" ? <LoadingBlock label="Checking your session" /> : null}
        {state.status === "signed-out" ? <SignInPage notice={state.notice} /> : null}
        {signedIn && (route.name === "approvals" || route.name === "sign-in") ? <ApprovalsPage /> : null}
        {signedIn && route.name === "review" ? <ReviewPage key={route.requestId} organizationId={route.organizationId} requestId={route.requestId} /> : null}
        {signedIn && route.name === "organizations" ? <OrganizationsPage /> : null}
        {signedIn && route.name === "organization" ? <OrganizationDetailPage key={route.organizationId} organizationId={route.organizationId} /> : null}
        {signedIn && route.name === "onboarding" ? <OnboardingQueuePage /> : null}
        {signedIn && route.name === "onboarding-new" ? <OnboardingNewPage /> : null}
        {signedIn && route.name === "onboarding-detail" ? <OnboardingDetailPage key={route.requestId} requestId={route.requestId} /> : null}
        {signedIn && route.name === "recovery" ? <RecoveryQueuePage /> : null}
        {signedIn && route.name === "recovery-new" ? <RecoveryNewPage /> : null}
        {signedIn && route.name === "recovery-detail" ? <RecoveryDetailPage key={route.caseId} caseId={route.caseId} /> : null}
        {signedIn && route.name === "not-found" ? (
          <div className="flex flex-col items-start gap-3">
            <h1 className="text-2xl font-semibold">Page not found</h1>
            <Button onClick={() => navigate("/platform/approvals")}>Go to operator approvals</Button>
          </div>
        ) : null}
      </main>
    </div>
  );
}
