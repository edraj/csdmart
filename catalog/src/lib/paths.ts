// Every URL the public catalog builds goes through here, so that the three
// places that used to disagree (dash-encoding a subpath, prefixing the
// <base href>, and the shape of a /catalogs/... route) agree by construction.
//
// Route shape (see src/routes/catalogs/**):
//   /catalogs/{space}
//   /catalogs/{space}/{subpath}
//   /catalogs/{space}/{subpath}/{shortname}/{resource_type}
//
// `{subpath}` is a single route segment, so an API subpath such as "/a/b" is
// carried as "a-b". The root subpath "/" has no segments and is carried as a
// lone "-" so the segment is never empty.

/** The route segment that stands for the root subpath "/". */
export const ROOT_SUBPATH_SEGMENT = "-";

/**
 * API subpath → route segment.
 *   "/"      → "-"
 *   "/a/b"   → "a-b"
 *   "a/b/"   → "a-b"
 *   "" | null → "-"
 */
export function encodeSubpath(subpath: string | null | undefined): string {
  const parts = (subpath ?? "").split("/").filter((p) => p.length > 0);
  return parts.length === 0 ? ROOT_SUBPATH_SEGMENT : parts.join("-");
}

/**
 * Route segment → API subpath, always with a leading slash and no trailing one.
 *   "-"      → "/"
 *   "a-b"    → "/a/b"
 *   "-a-b"   → "/a/b"   (tolerates the legacy leading-dash links)
 *   "" | null → "/"
 */
export function decodeSubpath(segment: string | null | undefined): string {
  const parts = (segment ?? "").split("-").filter((p) => p.length > 0);
  return parts.length === 0 ? "/" : `/${parts.join("/")}`;
}

export type CatalogTarget =
  | { space: string; subpath?: undefined; shortname?: undefined; resourceType?: undefined }
  | { space: string; subpath: string; shortname?: undefined; resourceType?: undefined }
  | { space: string; subpath: string; shortname: string; resourceType: string };

/**
 * App-relative path (no <base> prefix) for a space, folder or entry in the
 * public catalog. Pass the result to routify's `$goto`, or through `withBase`
 * for an `href` / `absoluteUrl` for a share link.
 */
export function catalogPath(target: CatalogTarget): string {
  const segments = ["catalogs", target.space];
  if (target.subpath !== undefined) {
    segments.push(encodeSubpath(target.subpath));
    if (target.shortname !== undefined) {
      segments.push(target.shortname, target.resourceType);
    }
  }
  return "/" + segments.map(encodeURIComponent).join("/");
}

export interface Breadcrumb {
  name: string;
  /** App-relative path, or null for the current (non-navigable) crumb. */
  path: string | null;
}

/**
 * Breadcrumb trail for a folder or entry in the public catalog. Each folder
 * crumb links to the cumulative path, so "/a/b" yields
 *   space → /catalogs/space, a → /catalogs/space/a, b → (current)
 * and with a shortname the folder "b" becomes a link and the entry is current.
 * The optional leading "Catalogs" crumb links to the catalog index.
 */
export function catalogBreadcrumbs(input: {
  space: string;
  subpath: string;
  shortname?: string;
  catalogsLabel?: string;
}): Breadcrumb[] {
  const crumbs: Breadcrumb[] = [];
  if (input.catalogsLabel) {
    crumbs.push({ name: input.catalogsLabel, path: "/catalogs" });
  }
  crumbs.push({ name: input.space, path: catalogPath({ space: input.space }) });

  const parts = input.subpath.split("/").filter((p) => p.length > 0);
  parts.forEach((part, index) => {
    crumbs.push({
      name: part,
      path: catalogPath({
        space: input.space,
        subpath: parts.slice(0, index + 1).join("/"),
      }),
    });
  });

  if (input.shortname !== undefined) {
    crumbs.push({ name: input.shortname, path: null });
  }

  // The last crumb is where the user already is.
  crumbs[crumbs.length - 1] = { ...crumbs[crumbs.length - 1], path: null };
  return crumbs;
}

// ---------------------------------------------------------------------------
// <base href> handling
//
// index.html ships `<base href="/cat/">` so the SPA can be embedded under a
// sub-path. Route literals in code ("/login", "/catalogs/x") are written
// app-relative; anything that ends up in an `href`, `window.location`, or a
// share link must be prefixed, and a live `window.location.pathname` must be
// stripped before it is compared against a route literal.
// ---------------------------------------------------------------------------

function basePrefix(): string {
  const baseHref =
    typeof document !== "undefined"
      ? document.querySelector("base")?.getAttribute("href") || "/"
      : "/";
  return baseHref.replace(/^\/|\/$/g, "");
}

/** App-relative path → path under the <base href>: "/login" → "/cat/login". */
export function withBase(path: string): string {
  const prefix = basePrefix();
  return prefix ? `/${prefix}${path}` : path;
}

/**
 * Inverse of withBase. Segment-aware: with prefix "cat", "/catalogs" is left
 * alone (a plain startsWith would maul it into "alogs"); only "/cat" itself
 * and "/cat/..." are prefixed paths.
 */
export function stripBase(path: string): string {
  const prefix = basePrefix();
  if (!prefix) return path;
  if (path !== `/${prefix}` && !path.startsWith(`/${prefix}/`)) return path;
  return path.slice(prefix.length + 1) || "/";
}

/** Absolute URL for a share link: origin + <base> + app-relative path. */
export function absoluteUrl(path: string, origin?: string): string {
  const o =
    origin ?? (typeof window !== "undefined" ? window.location.origin : "");
  return `${o}${withBase(path)}`;
}
