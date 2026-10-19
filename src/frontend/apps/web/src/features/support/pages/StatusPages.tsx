import { Button, EmptyState, PageHeader } from "@enterpriseforms/ui";
import { Link } from "@tanstack/react-router";

export function NotFoundPage() {
  return (
    <>
      <PageHeader title="Page not found" />
      <EmptyState
        title="There is nothing at this address"
        actions={
          <Link to="/app/dashboard">
            <Button variant="secondary">Go to the dashboard</Button>
          </Link>
        }
      >
        The link may be out of date, or the item may not be visible to you.
      </EmptyState>
    </>
  );
}

export function NoPermissionPage() {
  return (
    <>
      <PageHeader title="No permission" />
      <EmptyState
        title="You are not authorized to view this"
        actions={
          <Link to="/app/dashboard">
            <Button variant="secondary">Go to the dashboard</Button>
          </Link>
        }
      >
        Access is decided by the independent authorization authority. Ask the record owner or your organization owner for access.
      </EmptyState>
    </>
  );
}
