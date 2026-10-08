import { resolveTotal } from "@shared/query-total";

/**
 * Number of pages for a total and a page size. Never below 1, so a pager
 * always has a current page to point at; a non-positive limit is treated as
 * "everything on one page".
 */
export function pageCount(total: number | null | undefined, limit: number): number {
    if (!Number.isFinite(limit) || limit <= 0) return 1;
    const counted = typeof total === "number" && Number.isFinite(total) && total > 0 ? total : 0;
    return Math.max(1, Math.ceil(counted / limit));
}

/**
 * The page a list should actually show for a requested page.
 *
 * `page` may come straight from the URL, so anything that is not a positive
 * integer becomes 1. With a known total the result is also capped at the last
 * page — that is what rescues a stale `?page=10` after the rows-per-page
 * changed, or a last page that a bulk delete just emptied. An unknown total
 * (null) leaves the request alone apart from the lower bound.
 */
export function clampPage(page: unknown, total: number | null | undefined, limit: number): number {
    const n = typeof page === "number" ? page : Number(page);
    const requested = Number.isFinite(n) && n >= 1 ? Math.floor(n) : 1;
    if (total === null || total === undefined) return requested;
    return Math.min(requested, pageCount(total, limit));
}

/**
 * Whether a paged listing has records beyond the ones loaded so far.
 *
 * Prefers the server's total; when the server did not count (the -1 sentinel,
 * or a missing field) it falls back to "the last page came back full".
 */
export function hasMoreRecords(total: unknown, loaded: number, pageLength: number, pageSize: number): boolean {
    const counted = resolveTotal(total, -1);
    if (counted >= 0) return loaded < counted;
    return pageSize > 0 && pageLength >= pageSize;
}
