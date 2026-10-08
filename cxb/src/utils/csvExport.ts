/**
 * Pure helpers for the two CSV download paths (the list's "Download" modal and
 * the Query tool). Kept free of the SDK so they can be unit-tested; the
 * network call lives in `@/lib/dmart_services` (`fetchCsv`).
 */

/** Everything the server needs for `POST managed/csv`; `offset` is never sent. */
export interface CsvQuery {
    space_name: string;
    subpath: string;
    type: string;
    limit: number;
    from_date?: string;
    to_date?: string;
    [key: string]: unknown;
}

export interface CsvQueryOptions {
    /** Ignore limit and dates and export every matching record. */
    downloadAll: boolean;
    /** User-typed limit (string from an `<input type=number>`), used when not downloading all. */
    limit?: string | number | null;
    /** `YYYY-MM-DD` bounds, used when not downloading all. */
    startDate?: string | null;
    endDate?: string | null;
    /** The list's known total — a tighter "all" than the hard cap when available. */
    total?: number | null;
}

/** Upper bound for "download all" when the list's total is unknown. */
export const CSV_DOWNLOAD_ALL_CAP = 1_000_000;

/**
 * Build the CSV request from the list's live query.
 *
 * The list query carries paging (`offset`/`limit`) that must not leak into an
 * export — a download from page 3 would otherwise skip the first two pages and
 * stop after one page's worth of rows. `offset` is dropped and `limit` is
 * always set explicitly: the user's limit, the list's total, or the cap.
 */
export function buildCsvQuery(
    base: Record<string, unknown>,
    options: CsvQueryOptions,
    fallback: { space_name: string; subpath: string; type: string },
): CsvQuery {
    const query: Record<string, unknown> = { ...base };
    delete query.offset;
    delete query.retrieve_total;

    if (options.downloadAll) {
        delete query.from_date;
        delete query.to_date;
        const total = typeof options.total === "number" && options.total > 0 ? options.total : null;
        query.limit = total ?? CSV_DOWNLOAD_ALL_CAP;
    } else {
        const parsed = parsePositiveInt(options.limit);
        if (parsed !== null) {
            query.limit = parsed;
        } else if (typeof query.limit !== "number" || query.limit <= 0) {
            query.limit = CSV_DOWNLOAD_ALL_CAP;
        }
        if (options.startDate) query.from_date = options.startDate;
        if (options.endDate) query.to_date = options.endDate;
    }

    if (!query.space_name) query.space_name = fallback.space_name;
    if (!query.subpath) query.subpath = fallback.subpath;
    if (!query.type) query.type = fallback.type;

    return query as CsvQuery;
}

function parsePositiveInt(value: string | number | null | undefined): number | null {
    if (value === null || value === undefined || value === "") return null;
    const n = typeof value === "number" ? value : parseInt(value, 10);
    return Number.isFinite(n) && n > 0 ? Math.floor(n) : null;
}

/**
 * A download filename that the browser will keep. `${space}/${subpath}.csv`
 * contains slashes, which the browser strips down to the last segment — so a
 * root-level export was saved as `.csv` with no name at all.
 */
export function csvFileName(spaceName: string, subpath: string | null | undefined): string {
    const safeSubpath = (subpath ?? "")
        .replace(/\//g, "-")
        .replace(/^-+|-+$/g, "");
    return safeSubpath ? `${spaceName}_${safeSubpath}.csv` : `${spaceName}.csv`;
}
