import { LOGO_PATH, useRuntime, useSession } from "@enterpriseforms/app-runtime";
import { Button, LoadingBlock, menuContentClass, menuItemClass, StatusMessage } from "@enterpriseforms/ui";
import * as DropdownMenu from "@radix-ui/react-dropdown-menu";
import { Link, Navigate, useNavigate, useRouterState } from "@tanstack/react-router";
import { useEffect, useState, type PropsWithChildren } from "react";

/** Root shell: skip link, header with organization logo top-left (ADR-PB-014), main landmark. Navigation lives in the authenticated layout. */
export function AppShellLayout({ children }: PropsWithChildren) {
  const session = useSession();
  const branding = session.bootstrap?.branding ?? null;
  // The form designer is a three-pane workspace (C1 designer rebuild): it uses the full viewport width below the same
  // global header instead of the reading-width container every other page keeps.
  const wide = useRouterState({ select: (state) => /^\/app\/forms\/[^/]+\/design$/.test(state.location.pathname) });
  return (
    <div className="min-h-screen bg-page text-body">
      <a href="#main-content" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-control focus:bg-surface focus:px-3 focus:py-2">
        Skip to main content
      </a>
      <header className="border-b border-line bg-surface">
        <div className={`mx-auto flex w-full ${wide ? "max-w-[100rem]" : "max-w-7xl"} flex-wrap items-center gap-3 px-4 py-2`}>
          <Link to={session.status === "authenticated" ? "/app/dashboard" : "/"} className="flex min-h-11 items-center gap-3 rounded-control">
            {branding?.hasLogo ? (
              <img src={LOGO_PATH} alt={branding.logoAlternativeText || `${branding.organizationDisplayName} logo`} className="h-9 w-auto max-w-40 object-contain" />
            ) : (
              <span aria-hidden="true" className="flex h-9 w-9 items-center justify-center rounded-control bg-primary text-primary-foreground font-bold">
                {(branding?.organizationDisplayName ?? "EF").slice(0, 2).toUpperCase()}
              </span>
            )}
            <span className="text-lg font-semibold">{branding?.organizationDisplayName ?? "Enterprise Forms"}</span>
          </Link>
          <div className="ml-auto flex min-w-0 max-w-full items-center gap-2">{session.status === "authenticated" ? <UserMenu /> : null}</div>
        </div>
        {session.status === "authenticated" ? <PrimaryNav wide={wide} /> : null}
      </header>
      {session.status === "authenticated" && session.error ? (
        <div className={`mx-auto w-full ${wide ? "max-w-[100rem]" : "max-w-7xl"} px-4 pt-4`}>
          <StatusMessage tone="danger" title="You are still signed in" actions={<Button onClick={() => void session.logout().catch(() => undefined)}>Try signing out again</Button>}>
            {session.error}
          </StatusMessage>
        </div>
      ) : null}
      <main id="main-content" tabIndex={-1} className={`mx-auto w-full px-4 ${wide ? "max-w-[100rem] py-4" : "max-w-7xl py-6"}`}>
        {children}
      </main>
      <SessionExpiredNotice />
    </div>
  );
}

function PrimaryNav({ wide }: { wide: boolean }) {
  const session = useSession();
  const items: Array<{ to: string; label: string; show: boolean }> = [
    { to: "/app/dashboard", label: "Dashboard", show: true },
    { to: "/app/records", label: "My Records", show: session.hasCapability("records.discover") },
    { to: "/app/forms", label: "Forms", show: true },
    { to: "/app/uploads", label: "Uploads", show: true },
    { to: "/app/activity", label: "My activity", show: true },
    { to: "/app/organization", label: "Organization", show: session.hasCapability("organization.manage") || session.hasCapability("security.administer") },
    { to: "/app/organization/search-approvals", label: "Search approvals", show: session.hasCapability("security.administer") },
    { to: "/app/security/incidents", label: "Security incidents", show: session.hasCapability("security.administer") },
  ];
  return (
    <nav aria-label="Primary" className={`mx-auto w-full ${wide ? "max-w-[100rem]" : "max-w-7xl"} px-4`}>
      <ul className="flex flex-wrap gap-1 py-1">
        {items
          .filter((item) => item.show)
          .map((item) => (
            <li key={item.to}>
              <Link to={item.to} className="inline-flex min-h-11 items-center rounded-control px-3 text-base font-medium text-muted hover:bg-page hover:text-body" activeProps={{ className: "inline-flex min-h-11 items-center rounded-control px-3 text-base font-medium bg-page text-body underline underline-offset-8 decoration-4", "aria-current": "page" }}>
                {item.label}
              </Link>
            </li>
          ))}
      </ul>
    </nav>
  );
}

function UserMenu() {
  const session = useSession();
  const navigate = useNavigate();
  const name = session.session?.displayName ?? "User";
  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>
        <Button variant="secondary" size="sm" className="max-w-full whitespace-normal! text-left" aria-label={`Account menu for ${name}`}>
          <span className="min-w-0 break-words">{name}</span>
        </Button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={6} className={`${menuContentClass} min-w-56`}>
          <DropdownMenu.Label className="px-3 py-2 text-xs text-muted">
            {session.session?.organizationDisplayName} · {session.identityProviderKind === "TestIdentityProvider" ? "development identity" : session.identityProviderKind}
          </DropdownMenu.Label>
          <DropdownMenu.Item className={menuItemClass} onSelect={() => void navigate({ to: "/app/me/preferences" })}>
            Text size preferences
          </DropdownMenu.Item>
          <DropdownMenu.Separator className="my-1 h-px bg-line" />
          <DropdownMenu.Item
            className={menuItemClass}
            onSelect={() => {
              void session.logout().then(
                () => navigate({ to: "/login" }),
                () => undefined, // shown by the shell (sign-out not confirmed; still signed in)
              );
            }}
          >
            Sign out
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

/** Guard for /app/*: waits for the session probe, redirects anonymous users to the login page. */
export function AuthenticatedLayout({ children }: PropsWithChildren) {
  const session = useSession();
  if (session.status === "loading") {
    return <LoadingBlock label="Checking your session" />;
  }
  if (session.status === "anonymous") {
    return <Navigate to="/login" />;
  }
  if (session.status === "unavailable") {
    return (
      <StatusMessage tone="danger" title="The application cannot start" actions={<Button onClick={() => void session.refresh()}>Retry</Button>}>
        {session.error}
      </StatusMessage>
    );
  }
  return <>{children}</>;
}

export function LandingRedirect() {
  const session = useSession();
  if (session.status === "loading") {
    return <LoadingBlock label="Checking your session" />;
  }
  return <Navigate to={session.status === "authenticated" ? "/app/dashboard" : "/login"} />;
}

/** Session-expired handling: the page stays, queued work is failed by the coordinator, the user re-authenticates. */
function SessionExpiredNotice() {
  const session = useSession();
  const { coordinator } = useRuntime();
  const navigate = useNavigate();
  const [shown, setShown] = useState(false);
  useEffect(() => {
    if (session.status === "expired") {
      coordinator.clearSecurityContext();
      setShown(true);
    }
  }, [session.status, coordinator]);
  if (!shown || session.status !== "expired") {
    return null;
  }
  return (
    <div role="alertdialog" aria-modal="true" aria-labelledby="expired-title" className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
      <div className="w-full max-w-md rounded-panel border border-input bg-surface p-5">
        <h2 id="expired-title" className="text-xl font-semibold">
          Your session has ended
        </h2>
        <p className="mt-2 text-muted">Sign in again to continue. Unsaved edits on this page are kept until you leave it; uploads in progress were stopped and can be resumed from the Upload Center.</p>
        <div className="mt-4 flex justify-end gap-2">
          <Button
            onClick={() => {
              setShown(false);
              void navigate({ to: "/login" });
            }}
          >
            Sign in again
          </Button>
        </div>
      </div>
    </div>
  );
}
