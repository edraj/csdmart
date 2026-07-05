/**
 * Pure, framework-agnostic role & permission access helpers.
 *
 * No Svelte / DOM imports — fully unit-testable under vitest (node env).
 *
 * Roles live in `localStorage["roles"]` (string[]); the resolved permissions
 * map lives in `localStorage["permissions"]` keyed `"{space}:{subpath}:{rt}"`.
 * Svelte-bound wrappers live in `@/stores/permissions`.
 */
import { ROOT_SUBPATH } from "./constants";

// --- Role names: single source of truth ---
export const ROLES = {
  SUPER_ADMIN: "super_admin",
  SUPER_MANAGER: "super_manager",
} as const;

export type RoleName = (typeof ROLES)[keyof typeof ROLES];

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

export function isSuperAdmin(roles: string[]): boolean {
  return hasRole(roles, ROLES.SUPER_ADMIN);
}

export function isSuperManager(roles: string[]): boolean {
  return hasRole(roles, ROLES.SUPER_MANAGER);
}

// --- Permission helpers ---

/**
 * Candidate subpath spellings to probe for one subpath segment chain. DMART's
 * stored permission keys are not guaranteed to use a particular slash
 * convention, so each level is tried in the bare form (`users`) and the
 * leading-slash form (`/users`); the empty/root subpath maps to the
 * `__root__` token (and `/`).
 *
 * The backend grants hierarchically (a permission on `users` also covers
 * `users/archived` — see PermissionService's subpath walk), so the chain
 * includes every ancestor of the requested subpath, deepest first, ending at
 * the root spellings.
 */
function subpathVariants(subpath: string | null | undefined): string[] {
  const trimmed =
    subpath == null ? "" : String(subpath).replace(/^\/+|\/+$/g, "");
  if (trimmed.length === 0) return [ROOT_SUBPATH, "/"];
  const out: string[] = [];
  const segments = trimmed.split("/");
  for (let depth = segments.length; depth >= 1; depth--) {
    const sp = segments.slice(0, depth).join("/");
    out.push(sp, `/${sp}`);
  }
  out.push(ROOT_SUBPATH, "/");
  return out;
}

/**
 * All permission-map keys that can apply to `(space, subpath, resourceType)`,
 * most specific first:
 *   1. `space:subpath:rt`            (each ancestor level, both slash spellings)
 *   2. `__all_spaces__:subpath:rt`   (ditto; the backend emits this form when a
 *                                     permission pairs `__all_spaces__` with
 *                                     concrete subpaths)
 *   3. `space:__all_subpaths__:rt`
 *   4. `__all_spaces__:__all_subpaths__:rt`
 *
 * Shared by {@link resolvePermission} and {@link checkAccess} so action checks
 * and field-restriction lookups can never consult different key sets.
 */
function candidateKeys(
  space: string,
  subpath: string,
  resourceType: string,
): string[] {
  const variants = subpathVariants(subpath);
  return [
    ...variants.map((sp) => `${space}:${sp}:${resourceType}`),
    ...variants.map((sp) => `${ALL_SPACES}:${sp}:${resourceType}`),
    `${space}:${ALL_SUBPATHS}:${resourceType}`,
    `${ALL_SPACES}:${ALL_SUBPATHS}:${resourceType}`,
  ];
}

/**
 * Resolve the applicable permission entry for `(space, subpath, resourceType)`.
 * The MOST SPECIFIC existing key wins (exact subpath before ancestors before
 * wildcards) — restricted_fields / allowed_fields_values are deny-lists, so
 * letting a broad wildcard entry shadow an exact entry would silently disable
 * the restrictions attached to the entry that actually governs the location.
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
  for (const key of candidateKeys(space, subpath, resourceType)) {
    if (permissions[key]) return permissions[key];
  }
  return undefined;
}

/**
 * True when the user's permissions allow `action` on `(space, subpath, rt)`.
 *
 * Uses OR logic across all matching keys (any hit grants access), matching
 * the DMART backend's actual check: the backend walks every applicable
 * permission and grants if any of them allows the action.
 */
export function checkAccess(
  permissions: PermissionsMap,
  action: string,
  space: string,
  subpath: string,
  resourceType: string,
): boolean {
  if (!permissions || Object.keys(permissions).length === 0) return false;
  return candidateKeys(space, subpath, resourceType).some(
    (key) => permissions[key]?.allowed_actions?.includes(action) ?? false,
  );
}

/**
 * Single predicate for "may this user enter the admin section" — used by the
 * /dashboard landing redirect, guardAdminArea, and the header's admin menu.
 * These MUST agree: when the landing page sends a user to /dashboard/admin and
 * the guard bounces them back to /dashboard, the two full-page redirects loop
 * forever.
 *
 * Permission-based (query access to at least one management resource), not
 * role-based — roles and the permissions map can disagree (stale localStorage,
 * custom deployments), and the guard is the one that ultimately decides.
 */
export function canAccessAdminSection(permissions: PermissionsMap): boolean {
  return (
    checkAccess(permissions, "query", "management", "users", "user") ||
    checkAccess(permissions, "query", "management", "roles", "role") ||
    checkAccess(
      permissions,
      "query",
      "management",
      "permissions",
      "permission",
    ) ||
    checkAccess(permissions, "query", "management", "configs", "content")
  );
}
