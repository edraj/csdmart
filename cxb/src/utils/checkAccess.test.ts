// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { checkAccess } from "./checkAccess";

/**
 * checkAccess is the client-side gate in front of every management action, so
 * the cases that matter most are the ones where it must answer *false* — a
 * wrong `true` paints an action the server will then reject, and a wrong
 * `false` hides a legitimate one.
 *
 * It reads the `permissions` blob the SDK caches in localStorage, keyed
 * "<space>:<subpath>:<resource_type>" with two wildcard forms layered on top.
 * This is a UI affordance, not a security boundary: the server re-checks every
 * request. These tests pin the matching rules, not any authorisation guarantee.
 */
function setPermissions(value: unknown): void {
  localStorage.setItem(
    "permissions",
    typeof value === "string" ? value : JSON.stringify(value),
  );
}

describe("checkAccess", () => {
  beforeEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
  });

  describe("denies by default", () => {
    it("returns false when nothing is cached", () => {
      expect(checkAccess("view", "myspace", "/posts", "content")).toBe(false);
    });

    it("returns false for an empty permissions object", () => {
      setPermissions({});
      expect(checkAccess("view", "myspace", "/posts", "content")).toBe(false);
    });

    // Malformed JSON is the interesting one: a bad cache must not throw into
    // the caller's render path, and must not fall open.
    it("returns false for malformed JSON rather than throwing", () => {
      setPermissions("{not json");
      expect(() => checkAccess("view", "myspace", "/posts", "content")).not.toThrow();
      expect(checkAccess("view", "myspace", "/posts", "content")).toBe(false);
    });

    it("returns false when localStorage itself throws", () => {
      vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
        throw new Error("SecurityError");
      });
      expect(checkAccess("view", "myspace", "/posts", "content")).toBe(false);
    });
  });

  describe("exact key", () => {
    it("allows an action listed for space:subpath:resource_type", () => {
      setPermissions({
        "myspace:/posts:content": { allowed_actions: ["view", "update"] },
      });
      expect(checkAccess("view", "myspace", "/posts", "content")).toBe(true);
      expect(checkAccess("update", "myspace", "/posts", "content")).toBe(true);
    });

    it("denies an action not in allowed_actions", () => {
      setPermissions({
        "myspace:/posts:content": { allowed_actions: ["view"] },
      });
      expect(checkAccess("delete", "myspace", "/posts", "content")).toBe(false);
    });

    it("does not leak across a different subpath", () => {
      setPermissions({
        "myspace:/posts:content": { allowed_actions: ["delete"] },
      });
      expect(checkAccess("delete", "myspace", "/drafts", "content")).toBe(false);
    });

    it("does not leak across a different space", () => {
      setPermissions({
        "myspace:/posts:content": { allowed_actions: ["delete"] },
      });
      expect(checkAccess("delete", "other", "/posts", "content")).toBe(false);
    });

    it("does not leak across a different resource type", () => {
      setPermissions({
        "myspace:/posts:content": { allowed_actions: ["delete"] },
      });
      expect(checkAccess("delete", "myspace", "/posts", "folder")).toBe(false);
    });
  });

  describe("wildcards", () => {
    it("honours __all_subpaths__ within a space", () => {
      setPermissions({
        "myspace:__all_subpaths__:content": { allowed_actions: ["view"] },
      });
      expect(checkAccess("view", "myspace", "/anything/deep", "content")).toBe(true);
    });

    it("scopes __all_subpaths__ to its own space", () => {
      setPermissions({
        "myspace:__all_subpaths__:content": { allowed_actions: ["view"] },
      });
      expect(checkAccess("view", "other", "/anything", "content")).toBe(false);
    });

    it("honours __all_spaces__:__all_subpaths__", () => {
      setPermissions({
        "__all_spaces__:__all_subpaths__:content": { allowed_actions: ["query"] },
      });
      expect(checkAccess("query", "anyspace", "/any/path", "content")).toBe(true);
    });

    it("still respects resource type under the global wildcard", () => {
      setPermissions({
        "__all_spaces__:__all_subpaths__:content": { allowed_actions: ["query"] },
      });
      expect(checkAccess("query", "anyspace", "/any", "folder")).toBe(false);
    });

    // Any one matching key granting the action is enough — the keys are ORed,
    // so a narrow deny cannot revoke a broad allow.
    it("grants when any matching key allows, even if another does not", () => {
      setPermissions({
        "myspace:__all_subpaths__:content": { allowed_actions: [] },
        "myspace:/posts:content": { allowed_actions: ["update"] },
      });
      expect(checkAccess("update", "myspace", "/posts", "content")).toBe(true);
    });

    it("denies when every matching key withholds the action", () => {
      setPermissions({
        "myspace:__all_subpaths__:content": { allowed_actions: ["view"] },
        "myspace:/posts:content": { allowed_actions: ["view"] },
      });
      expect(checkAccess("delete", "myspace", "/posts", "content")).toBe(false);
    });
  });
});
