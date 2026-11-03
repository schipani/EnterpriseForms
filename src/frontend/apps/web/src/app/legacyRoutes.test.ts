import { createMemoryHistory } from "@tanstack/react-router";
import { describe, expect, it } from "vitest";
import { createAppRouter } from "./router";

async function land(path: string) {
  const router = createAppRouter(createMemoryHistory({ initialEntries: [path] }));
  await router.load();
  const { pathname, searchStr, hash } = router.state.location;
  return `${pathname}${searchStr}${hash ? `#${hash}` : ""}`;
}

describe("earlier addresses redirect to the canonical User routes", () => {
  it.each([
    ["/app/me/profile", "/app/user"],
    ["/app/me/preferences", "/app/user/regional"],
    ["/app/settings/preferences", "/app/user/regional"],
    ["/app/settings/preferences/language", "/app/user/language"],
    ["/app/settings/preferences/text-size", "/app/user/text-size"],
  ])("%s -> %s", async (from, to) => {
    expect(await land(from)).toBe(to);
  });

  it("keeps the query string and the hash", async () => {
    expect(await land("/app/me/preferences?from=menu&x=1#top")).toBe("/app/user/regional?from=menu&x=1#top");
  });

  it("does not loop: a canonical route stays where it is", async () => {
    for (const path of ["/app/user", "/app/user/regional", "/app/user/language", "/app/user/text-size"]) {
      expect(await land(path)).toBe(path);
    }
  });
});
