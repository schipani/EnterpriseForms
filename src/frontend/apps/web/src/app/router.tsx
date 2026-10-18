import { Outlet, createRootRoute, createRoute, createRouter } from "@tanstack/react-router";
import { AppShellLayout } from "./AppShellLayout";
import { EnvironmentStatusPage } from "./EnvironmentStatusPage";

// Code-based route tree (TanStack Router selected per CLAUDE-DESIGN.MD §1 after repository inspection — see
// docs/decisions/PHASE0_DECISION_LOG.md). Feature routes register under /app/<feature> through public entrypoints.
const rootRoute = createRootRoute({
  component: () => (
    <AppShellLayout>
      <Outlet />
    </AppShellLayout>
  ),
});

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/",
  component: EnvironmentStatusPage,
});

const routeTree = rootRoute.addChildren([indexRoute]);

export function createAppRouter() {
  return createRouter({ routeTree, defaultPreload: "intent" });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
