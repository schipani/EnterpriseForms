import { RequestCoordinator } from "@enterpriseforms/work-queue";
import { expect, it, vi } from "vitest";
import { createApiClient } from "./index";

it("adds the synthetic revoke intent only when the caller explicitly opts in", async () => {
  const transport = vi.fn(async () => new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }));
  const coordinator = new RequestCoordinator({ fetchImpl: transport });
  coordinator.setSecurityContext({ organizationId: "org", csrfToken: "csrf", authorizationEpoch: "1" });
  const api = createApiClient(coordinator);

  await api.shares.revoke("record", "share");
  await api.shares.revoke("record", "share", true);

  const request = expect.objectContaining({
    method: "POST",
    body: "{}",
    headers: expect.objectContaining({ "X-CSRF-Token": "csrf", "Idempotency-Key": expect.any(String) }),
  });
  expect(transport).toHaveBeenNthCalledWith(1, "/api/v1/form-records/record/shares/share/revoke", request);
  expect(transport).toHaveBeenNthCalledWith(2, "/api/v1/form-records/record/shares/share/revoke?useDevelopmentStepUp=true", request);
});
