/**
 * Centralized constants for the application.
 * Replaces hardcoded space names, limits, and defaults scattered across the codebase.
 */

import { website } from "@/config";

// --- Space Names ---
export const APPLICATIONS_SPACE = "applications";
export const MANAGEMENT_SPACE = "management";
export const PERSONAL_SPACE = "personal";
export const MESSAGES_SPACE = "messages";

// --- Default Query Limits ---
export const DEFAULT_QUERY_LIMIT = 100;
export const MAX_QUERY_LIMIT = 1000;
export const DEFAULT_PAGINATION_OFFSET = 0;

// --- Default Ordinal ---
export const DEFAULT_SPACE_ORDINAL = 9999;

// --- Default Row Per Page ---
export const DEFAULT_ROW_PER_PAGE = "15";

// --- Subpath Constants ---
export const ROOT_SUBPATH = "__root__";

// --- Truncation ---
export const DEFAULT_TRUNCATION_LENGTH = 100;

// --- Font Loading Timeout ---
export const FONT_LOAD_TIMEOUT_MS = 1000;

// --- Public (unauthenticated) routes ---
type PublicRoute = string | { path: string; wildcard: true };

// Always public: auth pages only. /login is intentionally absent so that
// _module.svelte can probe auth there and redirect signed-in users to /dashboard.
export const ALWAYS_PUBLIC_ROUTES: PublicRoute[] = ["/register"];

// Public-view routes: only reachable unauthenticated when
// website.enable_public_view is true (the public browsing experience).
export const PUBLIC_VIEW_ROUTES: PublicRoute[] = [
  "/contact",
  "/help",
  "/privacy",
  "/home",
  "/",
  { path: "/catalogs", wildcard: true },
];

function matchRoute(path: string, route: PublicRoute): boolean {
  if (typeof route === "string") return path === route;
  if (route.wildcard) return path.startsWith(route.path);
  return path === route.path;
}

export function isPublicRoute(path: string): boolean {
  if (ALWAYS_PUBLIC_ROUTES.some((route) => matchRoute(path, route))) return true;
  if (website.enable_public_view) {
    return PUBLIC_VIEW_ROUTES.some((route) => matchRoute(path, route));
  }
  return false;
}
