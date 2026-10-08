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

function findValue(obj: any, k: string): any {
    if (!obj || typeof obj !== "object") return undefined;
    if (obj[k] !== undefined) return obj[k];
    const tk = k.toLowerCase();
    const foundKey = Object.keys(obj).find((ok) => ok.toLowerCase() === tk);
    return foundKey ? obj[foundKey] : undefined;
}

function localizedDisplayName(item: any, loc: string | null | undefined): string {
    const dn = item?.attributes?.displayname ?? item?.displayname;
    if (dn && typeof dn === "object") {
        return (
            (loc ? dn[loc] : undefined) ||
            dn.en ||
            dn.ar ||
            dn.ku ||
            item?.shortname ||
            ""
        );
    }
    return item?.attributes?.payload?.body?.title || item?.shortname || "";
}

export function getAttributeValue(item: any, key: string, ctx: ValueContext = defaultContext()): string {
    if (!item || !key) return "";
    if (key === "displayname") return localizedDisplayName(item, ctx.locale);
    if (key === "status") {
        return item.attributes?.is_active === false
            ? ctx.t("inactive")
            : ctx.t("active");
    }
    if (key === "author") {
        return item.attributes?.owner_shortname || ctx.t("unknown");
    }

    let value: any;
    if (key.includes(".")) {
        const parts = key.split(".");
        let current: any = item;
        for (const part of parts) {
            current = findValue(current, part);
            if (current === undefined || current === null) break;
        }
        value = current;
    } else {
        value =
            findValue(item.attributes?.payload?.body, key) ??
            findValue(item.attributes?.payload, key) ??
            findValue(item.attributes, key) ??
            findValue(item, key);
    }

    if (value === null || value === undefined) return ctx.t("not_applicable");

    // A timestamp column is a timestamp whatever path reaches it: the default
    // columns use `attributes.created_at`, folder columns may use the bare
    // key, and either must render as a locale-formatted date rather than raw ISO.
    if (isTimestampKey(key) && !Number.isNaN(new Date(String(value)).getTime())) {
        return formatDate(String(value), "datetime");
    }

    if (typeof value === "object" && !Array.isArray(value)) {
        const loc = ctx.locale;
        const localized =
            (loc ? value[loc] : undefined) || value.en || value.ar || value.ku;
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
export function filterRequestHeaders(headers: any): any {
    const blacklist = ["sec", "content-type", "accept", "host", "connection"];

    return Object.keys(headers).reduce(
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
