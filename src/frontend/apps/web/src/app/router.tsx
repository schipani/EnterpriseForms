import { Outlet, createRootRoute, createRoute, createRouter, lazyRouteComponent } from "@tanstack/react-router";
import { parseRecordsSearch, parseRecordTabSearch } from "../features/form-records/routeSearch";
import { AppShellLayout, AuthenticatedLayout, LandingRedirect } from "./AppShellLayout";

/**
 * Code-based route tree. Every feature route lazy-loads the feature's public entrypoint; the shell never imports
 * feature internals. Route data privilege comes only from the backend (ADR-ARCH-001 §4U.4).
 */
const rootRoute = createRootRoute({
  component: () => (
    <AppShellLayout>
      <Outlet />
    </AppShellLayout>
  ),
  notFoundComponent: lazyRouteComponent(() => import("../features/support"), "NotFoundPage"),
});

const indexRoute = createRoute({ getParentRoute: () => rootRoute, path: "/", component: LandingRedirect });
const loginRoute = createRoute({ getParentRoute: () => rootRoute, path: "/login", component: lazyRouteComponent(() => import("../features/identity"), "LoginPage") });

const appRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/app",
  component: () => (
    <AuthenticatedLayout>
      <Outlet />
    </AuthenticatedLayout>
  ),
});

const dashboardRoute = createRoute({ getParentRoute: () => appRoute, path: "/dashboard", component: lazyRouteComponent(() => import("../features/dashboard"), "DashboardPage") });
const formsRoute = createRoute({ getParentRoute: () => appRoute, path: "/forms", component: lazyRouteComponent(() => import("../features/form-catalog"), "FormsPage") });
const newFormRoute = createRoute({ getParentRoute: () => appRoute, path: "/forms/new", component: lazyRouteComponent(() => import("../features/form-catalog"), "NewFormPage") });
const designerRoute = createRoute({ getParentRoute: () => appRoute, path: "/forms/$formDefinitionId/design", component: lazyRouteComponent(() => import("../features/form-designer"), "DesignerPage") });
const recordsRoute = createRoute({ getParentRoute: () => appRoute, path: "/records", component: lazyRouteComponent(() => import("../features/form-records"), "RecordsPage"), validateSearch: parseRecordsSearch });
const newRecordRoute = createRoute({ getParentRoute: () => appRoute, path: "/forms/$formDefinitionId/records/new", component: lazyRouteComponent(() => import("../features/form-records"), "NewRecordPage") });
const recordRoute = createRoute({ getParentRoute: () => appRoute, path: "/records/$formRecordId", component: lazyRouteComponent(() => import("../features/form-records"), "RecordPage"), validateSearch: parseRecordTabSearch });
const editRecordRoute = createRoute({ getParentRoute: () => appRoute, path: "/records/$formRecordId/edit", component: lazyRouteComponent(() => import("../features/form-records"), "EditRecordPage") });
const recordVersionRoute = createRoute({ getParentRoute: () => appRoute, path: "/records/$formRecordId/versions/$contentRevision", component: lazyRouteComponent(() => import("../features/form-records"), "RecordVersionPage") });
const newChildRoute = createRoute({ getParentRoute: () => appRoute, path: "/records/$formRecordId/related/$formRelationshipDefinitionId/new", component: lazyRouteComponent(() => import("../features/related-forms"), "NewChildRecordPage") });
const uploadsRoute = createRoute({ getParentRoute: () => appRoute, path: "/uploads", component: lazyRouteComponent(() => import("../features/jobs"), "UploadCenterPage") });
const activityRoute = createRoute({ getParentRoute: () => appRoute, path: "/activity", component: lazyRouteComponent(() => import("../features/audit"), "MyActivityPage") });
const brandingRoute = createRoute({ getParentRoute: () => appRoute, path: "/organization/branding", component: lazyRouteComponent(() => import("../features/organizations"), "BrandingPage") });
// Phase C8 M1: administration shell (PG-041 overview, PG-050 people, PG-048/049 teams, PG-046 data protection, Security Administrators).
const organizationOverviewRoute = createRoute({ getParentRoute: () => appRoute, path: "/organization", component: lazyRouteComponent(() => import("../features/organizations"), "OverviewPage") });
const organizationDataProtectionRoute = createRoute({ getParentRoute: () => appRoute, path: "/organization/data-protection", component: lazyRouteComponent(() => import("../features/organizations"), "DataProtectionPage") });
const organizationSecurityAdministratorsRoute = createRoute({ getParentRoute: () => appRoute, path: "/organization/security-administrators", component: lazyRouteComponent(() => import("../features/organizations"), "SecurityAdministratorsPage") });
const organizationPeopleRoute = createRoute({ getParentRoute: () => appRoute, path: "/organization/users", component: lazyRouteComponent(() => import("../features/identity"), "PeopleDirectoryPage") });
const organizationTeamsRoute = createRoute({ getParentRoute: () => appRoute, path: "/organization/teams", component: lazyRouteComponent(() => import("../features/teams"), "TeamsPage") });
const organizationTeamRoute = createRoute({ getParentRoute: () => appRoute, path: "/organization/teams/$teamId", component: lazyRouteComponent(() => import("../features/teams"), "TeamDetailPage") });
const searchApprovalsRoute = createRoute({ getParentRoute: () => appRoute, path: "/organization/search-approvals", component: lazyRouteComponent(() => import("../features/search"), "SearchApprovalsPage") });
const preferencesRoute = createRoute({ getParentRoute: () => appRoute, path: "/me/preferences", component: lazyRouteComponent(() => import("../features/user-preferences"), "PreferencesPage") });
/**
 * PG-069 component gallery (ADR-PB-014: internal, never a production route). It exists only in development builds (the Vite
 * dev server, which the Playwright suites use) or when a build sets VITE_ENABLE_COMPONENT_GALLERY=true; it is not in any
 * navigation. Both conditions are replaced at build time, so a production bundle contains neither the route nor its chunk.
 */
const componentGalleryEnabled = import.meta.env.DEV || import.meta.env.VITE_ENABLE_COMPONENT_GALLERY === "true";
const developmentRoutes = componentGalleryEnabled ? [createRoute({ getParentRoute: () => appRoute, path: "/gallery", component: lazyRouteComponent(() => import("../features/support/developmentGallery"), "GalleryPage") })] : [];
const noPermissionRoute = createRoute({ getParentRoute: () => appRoute, path: "/no-permission", component: lazyRouteComponent(() => import("../features/support"), "NoPermissionPage") });

const routeTree = rootRoute.addChildren([
  indexRoute,
  loginRoute,
  appRoute.addChildren([dashboardRoute, formsRoute, newFormRoute, designerRoute, recordsRoute, newRecordRoute, recordRoute, editRecordRoute, recordVersionRoute, newChildRoute, uploadsRoute, activityRoute, brandingRoute, organizationOverviewRoute, organizationDataProtectionRoute, organizationSecurityAdministratorsRoute, organizationPeopleRoute, organizationTeamsRoute, organizationTeamRoute, searchApprovalsRoute, preferencesRoute, ...developmentRoutes, noPermissionRoute]),
]);

export function createAppRouter() {
  return createRouter({ routeTree, defaultPreload: "intent", scrollRestoration: true });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
