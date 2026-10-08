/**
 * Prism highlights on the main thread, so what reaches it must stay small.
 * `limitJsonForDisplay` cuts a value down to at most `maxRows` array items and
 * about `maxBytes` of serialised text before it is pretty-printed, and says
 * whether it did, so the caller can add a "truncated" note. The original value
 * is never mutated.
 */
export const DISPLAY_MAX_ROWS = 100;
export const DISPLAY_MAX_BYTES = 200_000;

export interface DisplayLimits {
    maxRows?: number;
    maxBytes?: number;
}

export interface LimitedJson {
    /** What to render; the input itself when nothing had to be cut. */
    value: unknown;
    truncated: boolean;
    /** Items dropped from the top-level array, if the input was one. */
    droppedRows: number;
}

function byteLength(text: string): number {
    if (typeof TextEncoder !== "undefined") {
        return new TextEncoder().encode(text).length;
    }
    return text.length;
}

export function limitJsonForDisplay(value: unknown, limits: DisplayLimits = {}): LimitedJson {
    const maxRows = limits.maxRows ?? DISPLAY_MAX_ROWS;
    const maxBytes = limits.maxBytes ?? DISPLAY_MAX_BYTES;

    let current: unknown = value;
    let truncated = false;
    let droppedRows = 0;

    if (Array.isArray(current) && current.length > maxRows) {
        droppedRows = current.length - maxRows;
        current = current.slice(0, maxRows);
        truncated = true;
    } else if (
        current !== null &&
        typeof current === "object" &&
        !Array.isArray(current) &&
        Array.isArray((current as { records?: unknown }).records) &&
        ((current as { records: unknown[] }).records).length > maxRows
    ) {
        // A dmart query response: keep the envelope, cut the rows.
        const records = (current as { records: unknown[] }).records;
        droppedRows = records.length - maxRows;
        current = { ...(current as Record<string, unknown>), records: records.slice(0, maxRows) };
        truncated = true;
    }

    let text: string;
    try {
        text = typeof current === "string" ? current : JSON.stringify(current) ?? "";
    } catch {
        // Circular or otherwise unserialisable: show what String() gives.
        return { value: String(current), truncated: true, droppedRows };
    }

    if (byteLength(text) > maxBytes) {
        // Cut the text itself; the result is no longer valid JSON, which is
        // fine for a read-only preview that says so.
        return {
            value: `${text.slice(0, maxBytes)}…`,
            truncated: true,
            droppedRows,
        };
    }

    return { value: current, truncated, droppedRows };
}
