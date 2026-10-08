/**
 * Subpath helpers shared by every place that builds a request from a list
 * record or a trash path.
 *
 * dmart subpaths are slash-separated and the API returns them WITH a leading
 * slash (`/a/b`, root is `/`). cxb routes carry the same path with the slashes
 * replaced by dashes (`a-b`) because a slash cannot live inside one URL
 * segment, and a few callers hand the route form straight to these helpers —
 * so every function here accepts either spelling and normalises first.
 *
 * Shortnames match `^[a-zA-Z0-9_]+$`, so a dash can only ever be an encoded
 * slash; decoding it is lossless.
 */

export const ROOT_SUBPATH = "/";

/**
 * Canonical form: leading slash, no trailing slash, no doubled slashes, dashes
 * decoded. `""`, `"/"`, `"//"`, `"-"` all normalise to `/`.
 */
export function normalizeSubpath(subpath: string | null | undefined): string {
    const decoded = (subpath ?? "").replaceAll("-", "/");
    const parts = decoded.split("/").filter((p) => p.length > 0);
    return parts.length === 0 ? ROOT_SUBPATH : `/${parts.join("/")}`;
}

/** Path segments of a subpath, excluding the root. `/a/b` → `["a", "b"]`. */
export function subpathSegments(subpath: string | null | undefined): string[] {
    const normalized = normalizeSubpath(subpath);
    return normalized === ROOT_SUBPATH ? [] : normalized.slice(1).split("/");
}

/** Parent of a subpath. `/a/b` → `/a`, `/a` → `/`, `/` → `/`. */
export function parentOf(subpath: string | null | undefined): string {
    const segments = subpathSegments(subpath);
    segments.pop();
    return segments.length === 0 ? ROOT_SUBPATH : `/${segments.join("/")}`;
}

/** Join a subpath and a child shortname: (`/a`, `b`) → `/a/b`; (`/`, `b`) → `/b`. */
export function joinSubpath(subpath: string | null | undefined, child: string): string {
    return normalizeSubpath(`${normalizeSubpath(subpath)}/${child}`);
}

/** Route spelling for `$goto` params: `/a/b` → `a-b`, `/` → `""`. */
export function toRouteSubpath(subpath: string | null | undefined): string {
    return subpathSegments(subpath).join("-");
}

/**
 * The subpath a request about this record must carry.
 *
 * A list is not always exact: the Trash page and folders with
 * `expand_children` query a whole subtree, so a record's own `subpath` can be
 * deeper than the list's. The record wins whenever it says where it lives; the
 * list subpath is only the fallback for records that do not (events, counters).
 */
export function recordSubpath(
    record: { subpath?: string | null } | null | undefined,
    listSubpath: string | null | undefined,
): string {
    const own = record?.subpath;
    if (typeof own === "string" && own.trim().length > 0) {
        return normalizeSubpath(own);
    }
    return normalizeSubpath(listSubpath);
}

/** Where the user's trash lives: `/people/<user>/trash`. */
export function trashRoot(userShortname: string): string {
    return `/people/${userShortname}/trash`;
}

/**
 * Trash destination for an entry that lived at `space:subpath`:
 * `/people/<user>/trash/<space>/<subpath>` — `/people/u/trash/space` for a
 * root-level entry.
 */
export function trashDestination(userShortname: string, spaceName: string, subpath: string | null | undefined): string {
    return normalizeSubpath(`${trashRoot(userShortname)}/${spaceName}/${normalizeSubpath(subpath)}`);
}

export interface TrashRestoreTarget {
    space_name: string;
    subpath: string;
}

/**
 * Inverse of {@link trashDestination}: from a trash subpath (either spelling,
 * with or without the leading slash) recover the space and subpath the entry
 * came from. Returns null when the path is not inside a trash folder or does
 * not name a space.
 *
 *   `/people/u/trash/space/a/b` → `{ space_name: "space", subpath: "/a/b" }`
 *   `people/u/trash/space`      → `{ space_name: "space", subpath: "/" }`
 *   `/people/u/trash`           → null
 */
export function trashRestoreTarget(trashSubpath: string | null | undefined): TrashRestoreTarget | null {
    const segments = subpathSegments(trashSubpath);
    // ["people", "<user>", "trash", "<space>", ...rest]
    if (segments.length < 4 || segments[0] !== "people" || segments[2] !== "trash") {
        return null;
    }
    const space_name = segments[3];
    const rest = segments.slice(4);
    return {
        space_name,
        subpath: rest.length === 0 ? ROOT_SUBPATH : `/${rest.join("/")}`,
    };
}

/**
 * The one cache key for the sidebar's children cache. Writers used to store
 * `space:/a/b` while the reader looked up `space:/a-b`; routing both spellings
 * through here makes them agree.
 */
export function sidebarCacheKey(spaceName: string, subpath: string | null | undefined): string {
    return `${spaceName}:${normalizeSubpath(subpath)}`;
}
