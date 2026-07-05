/**
 * Route guards — bounce users who lack the required permissions.
 *
 * All checks go through checkAccess(permissions, action, space, subpath, rt)
 * reading from the permissions map populated by GET /profile after login.
 * No hardcoded role names — any role that has the right permissions passes.
 */
import { get } from "svelte/store";
import { permissions } from "@/stores/permissions";
import { canAccessAdminSection, checkAccess } from "@/lib/access";
import { withBasePrefix } from "@/lib/basePath";

function redirectTo(path: string): void {
  const target = withBasePrefix(path);
  if (window.location.pathname !== target) {
    window.location.href = target;
  }
}

/**
 * Guards the admin area. Returns `true` when the user has query access to at
 * least one management resource — the shared canAccessAdminSection predicate,
 * which the /dashboard landing redirect and the header menu also use (they
 * must agree or the two redirects loop).
 * Default redirect is `/dashboard` (NOT `/dashboard/admin`, which would loop).
 */
export function guardAdminArea(redirect = "/dashboard"): boolean {
  if (canAccessAdminSection(get(permissions))) return true;
  redirectTo(redirect);
  return false;
}

/**
 * Guards a page by a specific permission check.
 * Replaces the old role-based guardRoles — call it with the exact
 * action/space/subpath/resourceType the page requires.
 */
export function guardAccess(
  action: string,
  space: string,
  subpath: string,
  resourceType: string,
  redirect = "/dashboard",
): boolean {
  const perms = get(permissions);
  if (checkAccess(perms, action, space, subpath, resourceType)) return true;
  redirectTo(redirect);
  return false;
}
