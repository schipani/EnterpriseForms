import { createMemoryHistory } from "@tanstack/react-router";
import { expect, it } from "vitest";
import { createAppRouter } from "./router";

it("resolves the standalone sharing page and the back link through the app router", () => {
  const formRecordId = "0199a2b3-c4d5-7e6f-8123-456789abcdef";
  const router = createAppRouter(createMemoryHistory({ initialEntries: ["/app/dashboard"] }));
  const sharingPath = `/app/records/${formRecordId}/sharing`;
  const matches = router.matchRoutes(sharingPath, {});
  expect(matches.map((match) => match.routeId)).toContain("/app");
  expect(matches.at(-1)?.routeId).toBe("/app/records/$formRecordId/sharing");

  const record = router.buildLocation({ to: "/app/records/$formRecordId", params: { formRecordId }, search: { tab: "sharing" } });
  expect(record.href).toBe(`/app/records/${formRecordId}?tab=sharing`);
});

it("resolves the C10 share details, ownership transfer and my access pages", () => {
  const formRecordId = "0199a2b3-c4d5-7e6f-8123-456789abcdef";
  const router = createAppRouter(createMemoryHistory({ initialEntries: ["/app/dashboard"] }));
  expect(router.matchRoutes(`/app/records/${formRecordId}/sharing/0199a2b3-c4d5-7e6f-8123-456789abcd01`, {}).at(-1)?.routeId).toBe("/app/records/$formRecordId/sharing/$recordShareId");
  expect(router.matchRoutes(`/app/records/${formRecordId}/ownership`, {}).at(-1)?.routeId).toBe("/app/records/$formRecordId/ownership");
  expect(router.matchRoutes("/app/me/access", {}).at(-1)?.routeId).toBe("/app/me/access");
});
