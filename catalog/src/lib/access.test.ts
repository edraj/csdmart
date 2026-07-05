import { describe, expect, it } from "vitest";
import {
  ROLES,
  canAccessAdminArea,
  canAccessRoute,
  checkAccess,
  hasAnyRole,
  hasRole,
  isSuperAdmin,
  isSuperManager,
  resolvePermission,
  routeAccessFor,
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

  it("hasAnyRole supports the 'all' wildcard and empty inputs", () => {
    expect(hasAnyRole(["distributor"], [ROLES.SUPER_ADMIN, "distributor"])).toBe(true);
    expect(hasAnyRole(["distributor"], [ROLES.SUPER_ADMIN])).toBe(false);
    expect(hasAnyRole([], ["all"])).toBe(true); // wildcard ignores actual roles
    expect(hasAnyRole(["x"], [])).toBe(false); // no allowed roles => denied
  });

  it("canAccessAdminArea = super_admin OR super_manager", () => {
    expect(canAccessAdminArea([ROLES.SUPER_ADMIN])).toBe(true);
    expect(canAccessAdminArea([ROLES.SUPER_MANAGER])).toBe(true);
    expect(canAccessAdminArea([ROLES.SALESREP])).toBe(false);
    expect(canAccessAdminArea([])).toBe(false);
  });
});

describe("resolvePermission precedence", () => {
  const perms: PermissionsMap = {
    "myspace:__all_subpaths__:content": { allowed_actions: ["view"] },
    "__all_spaces__:__all_subpaths__:content": { allowed_actions: ["view", "query"] },
    "myspace:docs:content": { allowed_actions: ["view", "update", "delete"] },
  };

  it("prefers space:__all_subpaths__ over the exact subpath (first existing key wins)", () => {
    expect(resolvePermission(perms, "myspace", "docs", "content")).toBe(
      perms["myspace:__all_subpaths__:content"],
    );
  });

  it("falls back to __all_spaces__:__all_subpaths__ when space-wildcard absent", () => {
    expect(resolvePermission(perms, "other", "docs", "content")).toBe(
      perms["__all_spaces__:__all_subpaths__:content"],
    );
  });

  it("uses the exact subpath key when no wildcard matches", () => {
    const only: PermissionsMap = { "s:docs:content": { allowed_actions: ["view"] } };
    expect(resolvePermission(only, "s", "docs", "content")).toBe(only["s:docs:content"]);
    expect(resolvePermission(only, "s", "/docs/", "content")).toBe(only["s:docs:content"]); // normalized
  });

  it("maps empty/'/' subpath to the __root__ token", () => {
    const root: PermissionsMap = { "s:__root__:folder": { allowed_actions: ["view"] } };
    expect(resolvePermission(root, "s", "", "folder")).toBe(root["s:__root__:folder"]);
    expect(resolvePermission(root, "s", "/", "folder")).toBe(root["s:__root__:folder"]);
  });

  it("returns undefined when nothing matches", () => {
    expect(resolvePermission(perms, "nope", "x", "user")).toBeUndefined();
    expect(resolvePermission({}, "s", "p", "content")).toBeUndefined();
  });
});

describe("checkAccess (legacy first-existing-key semantics)", () => {
  it("a shadowing wildcard entry that lacks the action denies, even if the exact key grants it", () => {
    const perms: PermissionsMap = {
      "myspace:__all_subpaths__:content": { allowed_actions: ["view"] },
      "myspace:docs:content": { allowed_actions: ["view", "update", "delete"] },
    };
    expect(checkAccess(perms, "view", "myspace", "docs", "content")).toBe(true);
    expect(checkAccess(perms, "update", "myspace", "docs", "content")).toBe(false);
  });

  it("grants when the resolved entry includes the action", () => {
    const perms: PermissionsMap = { "s:docs:content": { allowed_actions: ["view", "update"] } };
    expect(checkAccess(perms, "update", "s", "docs", "content")).toBe(true);
    expect(checkAccess(perms, "delete", "s", "docs", "content")).toBe(false);
  });

  it("denies when no permission entry matches or actions are empty", () => {
    expect(checkAccess({}, "view", "s", "p", "content")).toBe(false);
    expect(
      checkAccess({ "s:p:content": {} }, "view", "s", "p", "content"),
    ).toBe(false);
  });
});

describe("route access map", () => {
  it("guards the admin subtree and the roles/permissions pages", () => {
    const admin = [ROLES.SUPER_ADMIN];
    const manager = [ROLES.SUPER_MANAGER];
    const normal = [ROLES.SALESREP];

    for (const p of ["/dashboard/admin", "/dashboard/admin/users", "/dashboard/roles", "/dashboard/permissions"]) {
      expect(canAccessRoute(admin, p)).toBe(true);
      expect(canAccessRoute(manager, p)).toBe(true);
      expect(canAccessRoute(normal, p)).toBe(false);
    }
  });

  it("allows unguarded routes for everyone", () => {
    expect(routeAccessFor("/me")).toBeUndefined();
    expect(canAccessRoute([], "/me")).toBe(true);
    expect(canAccessRoute([], "/dashboard")).toBe(true);
  });
});
