import {_, locale} from "@/i18n";
import {get} from "svelte/store";
import {formatDate} from "@/lib/helpers";
import {isTimestampKey} from "@/utils/columnsUtils";


/**
 * Utility functions for ListView components
 */

function findValue(obj: any, k: string): any {
    if (!obj || typeof obj !== "object") return undefined;
    if (obj[k] !== undefined) return obj[k];
    const tk = k.toLowerCase();
    const foundKey = Object.keys(obj).find((ok) => ok.toLowerCase() === tk);
    return foundKey ? obj[foundKey] : undefined;
}

function localizedDisplayName(item: any): string {
    const dn = item?.attributes?.displayname ?? item?.displayname;
    if (dn && typeof dn === "object") {
        const loc = get(locale);
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

export function getAttributeValue(item: any, key: string): string {
    if (!item || !key) return "";
    if (key === "displayname") return localizedDisplayName(item);
    if (key === "status") {
        return item.attributes?.is_active === false
            ? get(_)("inactive")
            : get(_)("active");
    }
    if (key === "author") {
        return item.attributes?.owner_shortname || get(_)("unknown");
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

    if (value === null || value === undefined) return get(_)("not_applicable");

    // A timestamp column is a timestamp whatever path reaches it: the default
    // columns use `attributes.created_at`, folder columns may use the bare
    // key, and either must render as a formatted date rather than raw ISO.
    if (isTimestampKey(key) && !Number.isNaN(new Date(String(value)).getTime())) {
        return formatDate(String(value));
    }

    if (typeof value === "object" && !Array.isArray(value)) {
        const loc = get(locale);
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
