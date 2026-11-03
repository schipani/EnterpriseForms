import { LOGO_PATH, PresenceHeartbeat, useRuntime, useSession } from "@enterpriseforms/app-runtime";
import { Button, LoadingBlock, menuContentClass, menuItemClass, NavigationIcons, StatusMessage } from "@enterpriseforms/ui";
import * as DropdownMenu from "@radix-ui/react-dropdown-menu";
import { Link, Navigate, useNavigate, useRouterState } from "@tanstack/react-router";
import { useEffect, useRef, useState, type PropsWithChildren } from "react";

/** The page has scrolled this far (px) before the header collapses to its compact bar; it expands again near the top. */
export const COMPACT_AFTER_PX = 72;
const EXPAND_BELOW_PX = 40;

/**
 * Whether the header is compact. One passive scroll listener, throttled with requestAnimationFrame; state changes only when a
 * threshold is crossed (with a small hysteresis so it cannot flicker), never on every scroll event.
 */
function useCompactHeader(): boolean {
  const [compact, setCompact] = useState(() => typeof window !== "undefined" && window.scrollY > COMPACT_AFTER_PX);
  const current = useRef(compact);
  useEffect(() => {
    let frame = 0;
    const evaluate = () => {
      frame = 0;
      const y = window.scrollY;
      const next = current.current ? y > EXPAND_BELOW_PX : y > COMPACT_AFTER_PX;
      if (next !== current.current) {
        current.current = next;
        setCompact(next);
      }
    };
    const onScroll = () => {
      if (frame === 0) {
        frame = window.requestAnimationFrame(evaluate);
      }
    };
    evaluate();
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => {
      window.removeEventListener("scroll", onScroll);
      if (frame !== 0) {
        window.cancelAnimationFrame(frame);
      }
    };
  }, []);
  return compact;
}

/** Root shell: skip link, header with organization logo top-left (ADR-PB-014), main landmark. Navigation lives in the authenticated layout. */
export function AppShellLayout({ children }: PropsWithChildren) {
  const session = useSession();
  const branding = session.bootstrap?.branding ?? null;
  const authenticated = session.status === "authenticated";
  // The form designer is a three-pane workspace (C1 designer rebuild): it uses the full viewport width below the same
  // global header instead of the reading-width container every other page keeps.
  const wide = useRouterState({ select: (state) => /^\/app\/forms\/[^/]+\/design$/.test(state.location.pathname) });
  const compact = useCompactHeader() && authenticated;
  const width = wide ? "max-w-[100rem]" : "max-w-7xl";
  return (
    <div className="min-h-screen bg-page text-body">
      <a href="#main-content" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-[60] focus:rounded-control focus:bg-surface focus:px-3 focus:py-2">
        Skip to main content
      </a>
      {/* The full header stays in the page flow, so collapsing never moves the content. While the compact bar is pinned over the top
          the full header is hidden from assistive technology and made inert, so there is always exactly one set of controls. */}
      <header className="border-b border-line bg-surface" aria-hidden={compact || undefined} inert={compact}>
        <div className={`mx-auto flex w-full ${width} flex-wrap items-center gap-3 px-4 py-2`}>
          <Link to={authenticated ? "/app/dashboard" : "/"} className="flex min-h-11 items-center gap-3 rounded-control">
            <Brand branding={branding} size="full" />
          </Link>
          <div className="ml-auto flex min-w-0 max-w-full items-center gap-2">{authenticated ? <UserMenu /> : null}</div>
        </div>
        {authenticated ? <PrimaryNav width={width} /> : null}
      </header>
      {authenticated ? <CompactBar active={compact} width={width} branding={branding} /> : null}
      {authenticated && session.error ? (
        <div className={`mx-auto w-full ${width} px-4 pt-4`}>
          <StatusMessage tone="danger" title="You are still signed in" actions={<Button onClick={() => void session.logout().catch(() => undefined)}>Try signing out again</Button>}>
            {session.error}
          </StatusMessage>
        </div>
      ) : null}
      <main id="main-content" tabIndex={-1} className={`mx-auto w-full px-4 ${wide ? "max-w-[100rem] py-4" : "max-w-7xl py-6"}`}>
        {children}
      </main>
      {authenticated ? <PresenceHeartbeat /> : null}
      <SessionExpiredNotice />
    </div>
  );
}

type Branding = NonNullable<ReturnType<typeof useSession>["bootstrap"]>["branding"] | null;

function Brand({ branding, size }: { branding: Branding; size: "full" | "small" }) {
  const small = size === "small";
  return (
    <>
      {branding?.hasLogo ? (
        <img src={LOGO_PATH} alt={branding.logoAlternativeText || `${branding.organizationDisplayName} logo`} className={`${small ? "h-8" : "h-9"} w-auto max-w-40 object-contain`} />
      ) : (
        <span aria-hidden="true" className={`flex ${small ? "h-8 w-8 text-sm" : "h-9 w-9"} items-center justify-center rounded-control bg-primary font-bold text-primary-foreground`}>
          {(branding?.organizationDisplayName ?? "EF").slice(0, 2).toUpperCase()}
        </span>
      )}
      {small ? <span className="sr-only">{branding?.organizationDisplayName ?? "Enterprise Forms"}</span> : <span className="text-lg font-semibold">{branding?.organizationDisplayName ?? "Enterprise Forms"}</span>}
    </>
  );
}

/** The primary destinations, each shown only to people with its capability (every destination is authorized again by the server). */
export function usePrimaryNavItems(): Array<{ to: string; label: string }> {
  const session = useSession();
  const items: Array<{ to: string; label: string; show: boolean }> = [
    { to: "/app/dashboard", label: "Dashboard", show: true },
    { to: "/app/records", label: "My Records", show: session.hasCapability("records.discover") },
    { to: "/app/forms", label: "Forms", show: true },
    { to: "/app/uploads", label: "Uploads", show: true },
    { to: "/app/activity", label: "My activity", show: true },
    { to: "/app/organization", label: "Organization", show: session.hasCapability("organization.manage") || session.hasCapability("security.administer") },
    { to: "/app/security/incidents", label: "Security incidents", show: session.hasCapability("security.administer") },
  ];
  return items.filter((item) => item.show);
}

/**
 * One primary link. The router marks the active route with `aria-current="page"` (an item is active on its sub pages too, so
 * Organization stays active on every Organization page); the style reads that attribute, so the active item shows a solid
 * underline in the primary token (black in the default theme) under its label, a background and semibold text. Focus has its own
 * outline and is not confused with the active indicator.
 */
function PrimaryLink({ to, label }: { to: string; label: string }) {
  return (
    <Link
      to={to}
      activeProps={{ "aria-current": "page" }}
      className="group inline-flex min-h-11 items-center rounded-control px-3 text-base font-medium text-muted hover:bg-page hover:text-body aria-[current=page]:bg-page aria-[current=page]:font-semibold aria-[current=page]:text-body"
    >
      <span className="border-b-4 border-transparent group-aria-[current=page]:border-primary">{label}</span>
    </Link>
  );
}

function PrimaryNav({ width }: { width: string }) {
  const items = usePrimaryNavItems();
  return (
    <nav aria-label="Primary" className={`mx-auto w-full ${width} px-4`}>
      <ul className="flex flex-wrap gap-1 py-1">
        {items.map((item) => (
          <li key={item.to}>
            <PrimaryLink to={item.to} label={item.label} />
          </li>
        ))}
      </ul>
    </nav>
  );
}

/**
 * The compact, pinned header (after about 72px of scrolling): small logo, the primary links on one row from `xl` (a Menu button
 * below that, so nothing overlaps or overflows at tablet and phone widths), account access and a visible Sign out. It is pinned
 * over the page with `fixed`, below menus and dialogs (z-40 against their z-50) and above the content. Until the page has scrolled
 * it is slid away and `visibility: hidden`, which removes it from the accessibility tree and the tab order.
 */
function CompactBar({ active, width, branding }: { active: boolean; width: string; branding: Branding }) {
  const items = usePrimaryNavItems();
  const signOut = useSignOut();
  return (
    <div
      data-testid="compact-header"
      data-active={active ? "true" : "false"}
      className={`fixed inset-x-0 top-0 z-40 h-14 border-b border-line bg-surface shadow-sm transition-transform duration-150 motion-reduce:transition-none ${active ? "visible translate-y-0" : "invisible -translate-y-full"}`}
    >
      <div className={`mx-auto flex h-full w-full ${width} items-center gap-2 px-4`}>
        <Link to="/app/dashboard" className="flex min-h-11 shrink-0 items-center gap-2 rounded-control">
          <Brand branding={branding} size="small" />
        </Link>
        <nav aria-label="Primary" className="hidden min-w-0 flex-1 xl:block">
          <ul className="flex flex-nowrap items-center gap-1">
            {items.map((item) => (
              <li key={item.to} className="shrink-0">
                <PrimaryLink to={item.to} label={item.label} />
              </li>
            ))}
          </ul>
        </nav>
        <div className="ml-auto flex min-w-0 items-center gap-2 xl:ml-0">
          <MobileMenu items={items} />
          <div className="hidden md:block">
            <UserMenu />
          </div>
          <Button variant="secondary" size="sm" className="min-h-11!" onClick={signOut}>
            Sign out
          </Button>
        </div>
      </div>
    </div>
  );
}

/** Below `xl` the primary links, and below `md` also the account actions, live in one accessible menu. Choosing a destination closes it. */
function MobileMenu({ items }: { items: Array<{ to: string; label: string }> }) {
  const navigate = useNavigate();
  return (
    <div className="xl:hidden">
      <DropdownMenu.Root>
        <DropdownMenu.Trigger asChild>
          <Button variant="secondary" size="sm" className="min-h-11!" aria-label="Main menu" icon={NavigationIcons.menu}>
            Menu
          </Button>
        </DropdownMenu.Trigger>
        <DropdownMenu.Portal>
          <DropdownMenu.Content align="end" sideOffset={6} className={`${menuContentClass} min-w-56`}>
            <DropdownMenu.Label className="px-3 py-2 text-xs text-muted">Go to</DropdownMenu.Label>
            {items.map((item) => (
              <DropdownMenu.Item key={item.to} className={`${menuItemClass} min-h-11`} onSelect={() => void navigate({ to: item.to })}>
                {item.label}
              </DropdownMenu.Item>
            ))}
            <div className="md:hidden">
              <DropdownMenu.Separator className="my-1 h-px bg-line" />
              <DropdownMenu.Item className={`${menuItemClass} min-h-11`} onSelect={() => void navigate({ to: "/app/user" })}>
                My Profile
              </DropdownMenu.Item>
              <DropdownMenu.Item className={`${menuItemClass} min-h-11`} onSelect={() => void navigate({ to: "/app/user/regional" })}>
                Preferences
              </DropdownMenu.Item>
            </div>
          </DropdownMenu.Content>
        </DropdownMenu.Portal>
      </DropdownMenu.Root>
    </div>
  );
}

/** The one sign-out operation (the session's secure logout); a failure to confirm revocation is shown by the shell and keeps the user signed in. */
function useSignOut(): () => void {
  const session = useSession();
  const navigate = useNavigate();
  return () => {
    void session.logout().then(
      () => navigate({ to: "/login" }),
      () => undefined, // shown by the shell (sign-out not confirmed; still signed in)
    );
  };
}

function UserMenu() {
  const session = useSession();
  const navigate = useNavigate();
  const signOut = useSignOut();
  const name = session.session?.displayName ?? "User";
  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>
        <Button variant="secondary" size="sm" className="min-h-11! max-w-full whitespace-normal! text-left" aria-label={`Account menu for ${name}`}>
          <span className="min-w-0 break-words">{name}</span>
        </Button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={6} className={`${menuContentClass} min-w-56`}>
          <DropdownMenu.Label className="px-3 py-2 text-xs text-muted">
            {session.session?.organizationDisplayName} · {session.identityProviderKind === "TestIdentityProvider" ? "development identity" : session.identityProviderKind}
          </DropdownMenu.Label>
          <DropdownMenu.Item className={menuItemClass} onSelect={() => void navigate({ to: "/app/user" })}>
            My Profile
          </DropdownMenu.Item>
          <DropdownMenu.Item className={menuItemClass} onSelect={() => void navigate({ to: "/app/user/regional" })}>
            Preferences
          </DropdownMenu.Item>
          <DropdownMenu.Separator className="my-1 h-px bg-line" />
          <DropdownMenu.Item className={menuItemClass} onSelect={signOut}>
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
