/**
 * Pure, framework-agnostic role & permission access helpers.
 *
 * No Svelte / DOM imports — fully unit-testable under vitest (node env).
 * Mirrors the legacy dmart-frontend model documented in
 * `dmart-frontend/backend/docs/routes-and-access.md`.
 *
 * Roles live in `localStorage["roles"]` (string[]); the resolved permissions
 * map lives in `localStorage["permissions"]` keyed `"{space}:{subpath}:{rt}"`.
 * Svelte-bound wrappers live in `@/stores/permissions`.
 */
import { ROOT_SUBPATH } from "./constants";

// --- Role names: single source of truth (full catalog, doc §2) ---
export const ROLES = {
  SUPER_ADMIN: "super_admin",
  SUPER_MANAGER: "super_manager",
  DISTRIBUTOR_ADMIN: "distributor_admin",
  DISTRIBUTOR: "distributor",
  SALESREP: "salesrep",
  // channel / outlet roles (doc §2.2)
  DISTRIBUTOR_DIRECT_SALES: "distributor_direct_sales",
  FRANCHISE_STORE: "franchise_store",
  ROS: "ros",
  SUB_DEALER: "sub_dealer",
  SUPERMARKET: "supermarket",
  UNIVERSAL_POS: "universal_point_of_sale_pos",
  ZAIN_CASH: "zain_cash",
  EXCHANGE_OFFICE: "exchange_office",
  MOBILE_SHOP: "mobile_shop",
  FRANCHISE: "franchise",
  // product-group roles (doc §2.2)
  VOUCHER_POS: "voucher_pos",
  ACTIVATING_POS: "activating_pos",
  ZAIN_LITE: "zain_lite",
} as const;

export type RoleName = (typeof ROLES)[keyof typeof ROLES];

/** Wildcard accepted by {@link hasAnyRole} / RoleGate — grants any signed-in user. */
export const ALL_WILDCARD = "all";

// Magic permission-map keys (DMART).
export const ALL_SPACES = "__all_spaces__";
export const ALL_SUBPATHS = "__all_subpaths__";

/**
 * A resolved permission entry, as written to `localStorage["permissions"]` by
 * `Dmart.getProfile()`. Structurally compatible with tsdmart's `Permission`;
 * declared locally so this module stays dependency-free and testable.
 */
export interface Permission {
  allowed_actions?: string[];
  conditions?: string[];
  restricted_fields?: string[];
  allowed_fields_values?: Record<string, any>;
}

export type PermissionsMap = Record<string, Permission>;

// --- Role helpers (roles passed explicitly → pure) ---

export function hasRole(roles: string[], role: string): boolean {
  return Array.isArray(roles) && roles.includes(role);
}

export function hasAnyRole(roles: string[], allowed: string[]): boolean {
  if (!Array.isArray(allowed) || allowed.length === 0) return false;
  if (allowed.includes(ALL_WILDCARD)) return true;
  return Array.isArray(roles) && allowed.some((r) => roles.includes(r));
}

export function isSuperAdmin(roles: string[]): boolean {
  return hasRole(roles, ROLES.SUPER_ADMIN);
}

export function isSuperManager(roles: string[]): boolean {
  return hasRole(roles, ROLES.SUPER_MANAGER);
}

/** Admin area is reachable by super_admin OR super_manager (product decision). */
export function canAccessAdminArea(roles: string[]): boolean {
  return isSuperAdmin(roles) || isSuperManager(roles);
}

// --- Permission helpers ---

/**
 * Candidate subpath spellings to probe. DMART's stored permission keys are not
 * guaranteed to use a particular slash convention, so we try the bare form
 * (`users`) and the leading-slash form (`/users`); the empty/root subpath maps
 * to the `__root__` token (and `/`).
 */
function subpathVariants(subpath: string | null | undefined): string[] {
  const trimmed =
    subpath == null ? "" : String(subpath).replace(/^\/+|\/+$/g, "");
  if (trimmed.length === 0) return [ROOT_SUBPATH, "/"];
  return [trimmed, `/${trimmed}`];
}

/**
 * Resolve the applicable permission entry for `(space, subpath, resourceType)`,
 * mirroring the legacy `useACL` precedence — the FIRST existing key wins, so a
 * matching wildcard entry shadows the exact one (faithful legacy behavior):
 *   1. `space:__all_subpaths__:rt`
 *   2. `__all_spaces__:__all_subpaths__:rt`
 *   3. `space:subpath:rt`            (both slash spellings)
 *   4. `__all_spaces__:subpath:rt`   (both slash spellings; backend supports it)
 *
 * Returns `undefined` when no key matches.
 */
export function resolvePermission(
  permissions: PermissionsMap,
  space: string,
  subpath: string,
  resourceType: string,
): Permission | undefined {
  if (!permissions) return undefined;
  const variants = subpathVariants(subpath);
  const keys = [
    `${space}:${ALL_SUBPATHS}:${resourceType}`,
    `${ALL_SPACES}:${ALL_SUBPATHS}:${resourceType}`,
    ...variants.map((sp) => `${space}:${sp}:${resourceType}`),
    ...variants.map((sp) => `${ALL_SPACES}:${sp}:${resourceType}`),
  ];
  for (const key of keys) {
    if (permissions[key]) return permissions[key];
  }
  return undefined;
}

/**
 * True when the user's permissions allow `action` on `(space, subpath, rt)`.
 *
 * Uses OR logic across all matching keys (any hit grants access), matching
 * the DMART backend's actual check. Key order (3 variants, per dev-team spec):
 *   1. `space:__all_subpaths__:rt`
 *   2. `__all_spaces__:__all_subpaths__:rt`
 *   3. `space:subpath:rt`  (both bare and /leading-slash forms)
 */
export function checkAccess(
  permissions: PermissionsMap,
  action: string,
  space: string,
  subpath: string,
  resourceType: string,
): boolean {
  if (!permissions || Object.keys(permissions).length === 0) return false;
  const variants = subpathVariants(subpath);
  const keys = [
    `${space}:${ALL_SUBPATHS}:${resourceType}`,
    `${ALL_SPACES}:${ALL_SUBPATHS}:${resourceType}`,
    ...variants.map((sp) => `${space}:${sp}:${resourceType}`),
  ];
  return keys.some(
    (key) => permissions[key]?.allowed_actions?.includes(action) ?? false,
  );
}

// --- Central route → allowed-roles map ---

export interface RouteAccessRule {
  test: (path: string) => boolean;
  allowed: string[];
}

/**
 * Single declarative source for which roles may reach which admin routes.
 * The admin subtree (`/dashboard/admin/*`, incl. users/configs/settings) and
 * the sibling roles/permissions pages all require super_admin OR super_manager.
 */
export const ROUTE_ACCESS: RouteAccessRule[] = [
  {
    test: (p) => p.startsWith("/dashboard/admin"),
    allowed: [ROLES.SUPER_ADMIN, ROLES.SUPER_MANAGER],
  },
  {
    test: (p) => p === "/dashboard/roles",
    allowed: [ROLES.SUPER_ADMIN, ROLES.SUPER_MANAGER],
  },
  {
    test: (p) => p === "/dashboard/permissions",
    allowed: [ROLES.SUPER_ADMIN, ROLES.SUPER_MANAGER],
  },
];

/** The matching access rule for a path (declaration order), if any. */
export function routeAccessFor(path: string): RouteAccessRule | undefined {
  return ROUTE_ACCESS.find((rule) => rule.test(path));
}

/** True when `roles` may access `path`. Unguarded paths are allowed. */
export function canAccessRoute(roles: string[], path: string): boolean {
  const rule = routeAccessFor(path);
  return rule ? hasAnyRole(roles, rule.allowed) : true;
}

// --- Spec-compatible aliases & additions ---

/** Alias for {@link resolvePermission} — matches the spec's `getPermission` name. */
export const getPermission = resolvePermission;

/**
 * True when the user has field-level access (field is NOT in `restricted_fields`).
 * Positive counterpart to `isFieldRestricted` in `@/lib/access-fields`.
 * Default is ALLOW when no permission entry matches.
 */
export function checkFieldAccess(
  permissions: PermissionsMap,
  field: string,
  space: string,
  subpath: string,
  resourceType: string,
): boolean {
  const perm = resolvePermission(permissions, space, subpath, resourceType);
  if (!perm) return true;
  return !(perm.restricted_fields ?? []).includes(field);
}
