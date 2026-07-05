import { describe, expect, it } from "vitest";
import {
  ROLES,
  canAccessAdminSection,
  checkAccess,
  hasRole,
  isSuperAdmin,
  isSuperManager,
  resolvePermission,
  type PermissionsMap,
} from "./access";

describe("role helpers", () => {
  it("hasRole / isSuperAdmin / isSuperManager", () => {
    expect(hasRole(["a", "b"], "b")).toBe(true);
    expect(hasRole(["a"], "b")).toBe(false);
    expect(hasRole(undefined as any, "b")).toBe(false);
    expect(isSuperAdmin([ROLES.SUPER_ADMIN])).toBe(true);
    expect(isSuperAdmin([ROLES.SUPER_MANAGER])).toBe(false);
    expect(isSuperManager([ROLES.SUPER_MANAGER])).toBe(true);
  });
});

describe("resolvePermission precedence (most specific key wins)", () => {
  const perms: PermissionsMap = {
    "myspace:__all_subpaths__:content": { allowed_actions: ["view"] },
    "__all_spaces__:__all_subpaths__:content": { allowed_actions: ["view", "query"] },
    "myspace:docs:content": { allowed_actions: ["view", "update", "delete"] },
  };

  it("prefers the exact subpath key over the space wildcard", () => {
    expect(resolvePermission(perms, "myspace", "docs", "content")).toBe(
      perms["myspace:docs:content"],
    );
  });

  it("falls back to space:__all_subpaths__ when no exact key exists", () => {
    expect(resolvePermission(perms, "myspace", "other", "content")).toBe(
      perms["myspace:__all_subpaths__:content"],
    );
  });

  it("falls back to __all_spaces__:__all_subpaths__ when space keys are absent", () => {
    expect(resolvePermission(perms, "other", "docs", "content")).toBe(
      perms["__all_spaces__:__all_subpaths__:content"],
    );
  });

  it("a wildcard entry cannot shadow the exact entry's restricted_fields", () => {
    // The exact entry carries a deny-list; the broad wildcard doesn't. The
    // exact one must win or the field restriction is silently disabled.
    const p: PermissionsMap = {
      "management:__all_subpaths__:user": { allowed_actions: ["query"] },
      "management:users:user": {
        allowed_actions: ["update"],
        restricted_fields: ["roles"],
      },
    };
    expect(
      resolvePermission(p, "management", "users", "user")?.restricted_fields,
    ).toEqual(["roles"]);
  });

  it("normalizes slash spellings on both sides", () => {
    const only: PermissionsMap = { "s:docs:content": { allowed_actions: ["view"] } };
    expect(resolvePermission(only, "s", "docs", "content")).toBe(only["s:docs:content"]);
    expect(resolvePermission(only, "s", "/docs/", "content")).toBe(only["s:docs:content"]);
    const slashed: PermissionsMap = { "s:/docs:content": { allowed_actions: ["view"] } };
    expect(resolvePermission(slashed, "s", "docs", "content")).toBe(slashed["s:/docs:content"]);
  });

  it("maps empty/'/' subpath to the __root__ token", () => {
    const root: PermissionsMap = { "s:__root__:folder": { allowed_actions: ["view"] } };
    expect(resolvePermission(root, "s", "", "folder")).toBe(root["s:__root__:folder"]);
    expect(resolvePermission(root, "s", "/", "folder")).toBe(root["s:__root__:folder"]);
  });

  it("walks ancestors like the backend: a grant on the parent covers nested subpaths", () => {
    const p: PermissionsMap = { "s:users:user": { allowed_actions: ["update"] } };
    expect(resolvePermission(p, "s", "users/archived", "user")).toBe(p["s:users:user"]);
    const rootGrant: PermissionsMap = { "s:/:content": { allowed_actions: ["view"] } };
    expect(resolvePermission(rootGrant, "s", "a/b/c", "content")).toBe(rootGrant["s:/:content"]);
  });

  it("prefers the deepest matching ancestor", () => {
    const p: PermissionsMap = {
      "s:users:user": { allowed_actions: ["query"] },
      "s:users/archived:user": { allowed_actions: ["query"], restricted_fields: ["email"] },
    };
    expect(
      resolvePermission(p, "s", "users/archived", "user")?.restricted_fields,
    ).toEqual(["email"]);
  });

  it("returns undefined when nothing matches", () => {
    expect(resolvePermission(perms, "nope", "x", "user")).toBeUndefined();
    expect(resolvePermission({}, "s", "p", "content")).toBeUndefined();
  });
});

describe("checkAccess (OR across all applicable keys, like the backend)", () => {
  it("grants via the exact key even when a wildcard entry lacks the action", () => {
    const perms: PermissionsMap = {
      "myspace:__all_subpaths__:content": { allowed_actions: ["view"] },
      "myspace:docs:content": { allowed_actions: ["view", "update", "delete"] },
    };
    expect(checkAccess(perms, "view", "myspace", "docs", "content")).toBe(true);
    expect(checkAccess(perms, "update", "myspace", "docs", "content")).toBe(true);
    expect(checkAccess(perms, "attach", "myspace", "docs", "content")).toBe(false);
  });

  it("grants via a wildcard entry even when the exact key lacks the action", () => {
    const perms: PermissionsMap = {
      "myspace:__all_subpaths__:content": { allowed_actions: ["query"] },
      "myspace:docs:content": { allowed_actions: ["view"] },
    };
    expect(checkAccess(perms, "query", "myspace", "docs", "content")).toBe(true);
  });

  it("honors the __all_spaces__:{subpath} key form the backend emits", () => {
    const perms: PermissionsMap = {
      "__all_spaces__:roles:role": { allowed_actions: ["query", "update"] },
    };
    expect(checkAccess(perms, "query", "management", "roles", "role")).toBe(true);
    expect(checkAccess(perms, "delete", "management", "roles", "role")).toBe(false);
  });

  it("walks ancestors like the backend's hierarchical subpath grant", () => {
    const perms: PermissionsMap = {
      "management:users:user": { allowed_actions: ["update"] },
    };
    expect(
      checkAccess(perms, "update", "management", "users/archived", "user"),
    ).toBe(true);
  });

  it("denies when no permission entry matches or actions are empty", () => {
    expect(checkAccess({}, "view", "s", "p", "content")).toBe(false);
    expect(
      checkAccess({ "s:p:content": {} }, "view", "s", "p", "content"),
    ).toBe(false);
  });
});

describe("canAccessAdminSection", () => {
  it("grants with query access to any one management resource", () => {
    expect(
      canAccessAdminSection({
        "management:users:user": { allowed_actions: ["query"] },
      }),
    ).toBe(true);
    expect(
      canAccessAdminSection({
        "management:configs:content": { allowed_actions: ["query"] },
      }),
    ).toBe(true);
    expect(
      canAccessAdminSection({
        "__all_spaces__:__all_subpaths__:role": { allowed_actions: ["query"] },
      }),
    ).toBe(true);
  });

  it("denies with an empty map or non-query grants only", () => {
    expect(canAccessAdminSection({})).toBe(false);
    expect(
      canAccessAdminSection({
        "management:users:user": { allowed_actions: ["view"] },
      }),
    ).toBe(false);
  });
});
