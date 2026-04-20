/**
 * Centralized constants for the application.
 * Replaces hardcoded space names, limits, and defaults scattered across the codebase.
 */

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

export const PUBLIC_ROUTES: PublicRoute[] = [
  "/register",
  "/contact",
  "/home",
  "/",
  { path: "/catalogs", wildcard: true },
];

export function isPublicRoute(path: string): boolean {
  return PUBLIC_ROUTES.some((route) => {
    if (typeof route === "string") return path === route;
    if (route.wildcard) return path.startsWith(route.path);
    return path === route.path;
  });
}
