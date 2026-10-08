import { beforeEach, describe, expect, it, vi } from "vitest";

const getAvatar = vi.fn<(shortname: string) => Promise<string | null>>();
let scope = "public";

vi.mock("./profile", () => ({ getAvatar: (s: string) => getAvatar(s) }));
vi.mock("@/stores/user", () => ({ getCurrentScope: () => scope }));

import { avatarCacheSize, clearAvatarCache, getAvatarCached, getAvatarsCached } from "./avatars";

beforeEach(() => {
  clearAvatarCache();
  getAvatar.mockReset();
  scope = "public";
});

describe("getAvatarCached", () => {
  it("looks an owner up once and shares the promise", async () => {
    getAvatar.mockResolvedValue("/a.png");
    const [a, b] = await Promise.all([getAvatarCached("alice"), getAvatarCached("alice")]);
    expect(a).toBe("/a.png");
    expect(b).toBe("/a.png");
    expect(getAvatar).toHaveBeenCalledTimes(1);
    expect(await getAvatarCached("alice")).toBe("/a.png");
    expect(getAvatar).toHaveBeenCalledTimes(1);
  });

  it("resolves null for a blank owner without a request", async () => {
    expect(await getAvatarCached("")).toBeNull();
    expect(await getAvatarCached(undefined)).toBeNull();
    expect(getAvatar).not.toHaveBeenCalled();
  });

  it("turns a failure into null and does not retry in the same scope", async () => {
    getAvatar.mockRejectedValue(new Error("403"));
    expect(await getAvatarCached("bob")).toBeNull();
    expect(await getAvatarCached("bob")).toBeNull();
    expect(getAvatar).toHaveBeenCalledTimes(1);
  });

  it("keys by scope so signing in refreshes the lookups", async () => {
    getAvatar.mockResolvedValue(null);
    await getAvatarCached("carol");
    scope = "managed";
    getAvatar.mockResolvedValue("/c.png");
    expect(await getAvatarCached("carol")).toBe("/c.png");
    expect(getAvatar).toHaveBeenCalledTimes(2);
    expect(avatarCacheSize()).toBe(2);
  });

  it("clearAvatarCache(shortname) forgets one owner in every scope", async () => {
    getAvatar.mockResolvedValue("/d.png");
    await getAvatarCached("dave");
    await getAvatarCached("erin");
    clearAvatarCache("dave");
    expect(avatarCacheSize()).toBe(1);
    await getAvatarCached("dave");
    expect(getAvatar).toHaveBeenCalledTimes(3);
  });
});

describe("getAvatarsCached", () => {
  it("de-duplicates owners and never rejects", async () => {
    getAvatar.mockImplementation(async (s) => (s === "bad" ? Promise.reject(new Error("x")) : `/${s}.png`));
    const urls = await getAvatarsCached(["a", "b", "a", "", null, "bad"]);
    expect(getAvatar).toHaveBeenCalledTimes(3);
    expect(urls.get("a")).toBe("/a.png");
    expect(urls.get("b")).toBe("/b.png");
    expect(urls.get("bad")).toBeNull();
    expect(urls.has("")).toBe(false);
  });
});
