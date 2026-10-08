import {_, locale} from "@/i18n";
import {get} from "svelte/store";
import {formatDate} from "@/utils/format";
import {isTimestampKey} from "@/utils/columnsUtils";


/**
 * Utility functions for ListView components
 */

/**
 * The translator and locale a cell is rendered with. ListView reads `$_` and
 * `$locale` once per render and passes them in, instead of each cell doing a
 * `get(store)` subscribe/unsubscribe of its own; callers outside a component
 * can omit it and pay for the store read.
 */
export interface ValueContext {
    t: (key: string) => string;
    locale: string | null | undefined;
}

function defaultContext(): ValueContext {
    return {
        t: (key) => get(_)(key),
        locale: get(locale),
    };
}

/** `obj[k]` when `obj` is something that can be indexed (object or array), else undefined. */
function prop(obj: unknown, k: string): unknown {
    if (!obj || typeof obj !== "object") return undefined;
    return (obj as Record<string, unknown>)[k];
}

function findValue(obj: unknown, k: string): unknown {
    if (!obj || typeof obj !== "object") return undefined;
    const record = obj as Record<string, unknown>;
    if (record[k] !== undefined) return record[k];
    const tk = k.toLowerCase();
    const foundKey = Object.keys(record).find((ok) => ok.toLowerCase() === tk);
    return foundKey ? record[foundKey] : undefined;
}

function localizedDisplayName(item: Record<string, unknown>, loc: string | null | undefined): string {
    const attributes = prop(item, "attributes");
    const dn = prop(attributes, "displayname") ?? item.displayname;
    if (dn && typeof dn === "object") {
        const translation = dn as Record<string, unknown>;
        return String(
            (loc ? translation[loc] : undefined) ||
            translation.en ||
            translation.ar ||
            translation.ku ||
            item.shortname ||
            ""
        );
    }
    return String(prop(prop(prop(attributes, "payload"), "body"), "title") || item.shortname || "");
}

/**
 * The text for one list cell. `item` is a query record (`ApiResponseRecord`)
 * or, for folder-defined columns, whatever JSON the column path points into.
 */
export function getAttributeValue(item: unknown, key: string, ctx: ValueContext = defaultContext()): string {
    if (!item || !key) return "";
    const row: Record<string, unknown> = typeof item === "object" ? (item as Record<string, unknown>) : {};
    const attributes = prop(row, "attributes");
    if (key === "displayname") return localizedDisplayName(row, ctx.locale);
    if (key === "status") {
        return prop(attributes, "is_active") === false
            ? ctx.t("inactive")
            : ctx.t("active");
    }
    if (key === "author") {
        return String(prop(attributes, "owner_shortname") || ctx.t("unknown"));
    }

    let value: unknown;
    if (key.includes(".")) {
        const parts = key.split(".");
        let current: unknown = row;
        for (const part of parts) {
            current = findValue(current, part);
            if (current === undefined || current === null) break;
        }
        value = current;
    } else {
        const payload = prop(attributes, "payload");
        value =
            findValue(prop(payload, "body"), key) ??
            findValue(payload, key) ??
            findValue(attributes, key) ??
            findValue(row, key);
    }

    if (value === null || value === undefined) return ctx.t("not_applicable");

    // A timestamp column is a timestamp whatever path reaches it: the default
    // columns use `attributes.created_at`, folder columns may use the bare
    // key, and either must render as a locale-formatted date rather than raw ISO.
    if (isTimestampKey(key) && !Number.isNaN(new Date(String(value)).getTime())) {
        return formatDate(String(value), "datetime");
    }

    if (typeof value === "object" && !Array.isArray(value)) {
        const translation = value as Record<string, unknown>;
        const loc = ctx.locale;
        const localized =
            (loc ? translation[loc] : undefined) || translation.en || translation.ar || translation.ku;
        if (localized !== undefined) return String(localized);
        return JSON.stringify(value);
    }

    return String(value);
}

/**
 * Gets rows per page setting from localStorage
 */
export function getRowsPerPageSetting(): number {
    if (typeof localStorage !== 'undefined') {
        const stored = localStorage.getItem("rowPerPage");
        if (stored) {
            return parseInt(stored, 10) || 15;
        }
    }
    return 15;
}

/**
 * Filters request headers by removing blacklisted items
 */
export function filterRequestHeaders(headers: Record<string, unknown>): Record<string, unknown> {
    const blacklist = ["sec", "content-type", "accept", "host", "connection"];

    return Object.keys(headers).reduce<Record<string, unknown>>(
        (acc, key) =>
            blacklist.some((item) => key.includes(item))
                ? acc
                : {
                    ...acc,
                    [key]: headers[key],
                },
        {}
    );
}
