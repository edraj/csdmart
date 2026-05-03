import {_, locale} from "@/i18n";
import {Dmart, QueryType, SortyType} from "@edraj/tsdmart";
import {getSpaces} from "@/lib/dmart_services";
import {get} from "svelte/store";
import {formatDate} from "@/lib/helpers";


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
    if (key === "updated_at" || key === "created_at") {
        const ts = item.attributes?.[key];
        return ts ? formatDate(ts) : get(_)("not_applicable");
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
 * Calculates the number of pages for pagination
 */
export function calculateNumberOfPages(total: number, rowsPerPage: number): number {
    return Math.ceil(total / rowsPerPage);
}

/**
 * Stores rows per page setting in localStorage
 */
export function storeRowsPerPageSetting(rowsPerPage: number): void {
    if (typeof localStorage !== 'undefined') {
        localStorage.setItem("rowPerPage", rowsPerPage.toString());
    }
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
 * Normalizes subpath for folder navigation
 */
export function normalizeSubpath(recordSubpath: string, recordShortname: string, currentSubpath: string): string {
    let _subpath = `${recordSubpath}/${recordShortname}`.replace(/\/+/g, "/");

    if (_subpath.length > 0 && currentSubpath[0] === "/") {
        _subpath = _subpath.substring(1);
    }
    if (_subpath.length > 0 && _subpath[_subpath.length - 1] === "/") {
        _subpath = _subpath.slice(0, -1);
    }

    return _subpath.replaceAll("/", "-");
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

/**
 * Builds query object for data fetching
 */
export function buildQueryObject(params: {
    shortname?: string;
    type: QueryType;
    space_name: string;
    subpath: string;
    exact_subpath: boolean;
    numberRowsPerPage: number;
    stringSortBy: string;
    stringSortOrder: string;
    numberActivePage: number;
    search: string;
    scope: string;
    requestExtra?: any;
}): any {
    return {
        filter_shortnames: params.shortname ? [params.shortname] : [],
        type: params.type,
        space_name: params.space_name,
        subpath: params.subpath,
        exact_subpath: params.exact_subpath,
        limit: params.numberRowsPerPage,
        sort_by: params.stringSortBy.toString(),
        sort_type: SortyType[params.stringSortOrder],
        offset: params.numberRowsPerPage * (params.numberActivePage - 1),
        search: params.search.trim(),
        ...params.requestExtra,
        retrieve_json_payload: true
    };
}

/**
 * Applies folder hiding logic for root subpath
 */
export async function applyFolderHiding(search: string, subpath: string, space_name: string, spaces: any[]): Promise<string> {
    if (subpath !== "/") {
        return search;
    }

    if (spaces === null || spaces.length === 0) {
        await getSpaces();
    }

    const currentSpace = spaces.find((e) => e.shortname === space_name);
    const hideFolders = currentSpace?.attributes?.hide_folders;

    if (hideFolders?.length) {
        return search + ` -@shortname:${hideFolders.join('|')}`;
    }

    return search;
}

/**
 * Fetches page records with all the necessary logic
 */
export async function fetchPageRecords(params: {
    searchListView: string;
    subpath: string;
    spaces: any[];
    space_name: string;
    shortname?: string;
    type: QueryType;
    exact_subpath: boolean;
    objectDatatable: any;
    scope: string;
    requestExtra?: any;
}): Promise<{ total: number; records: any[] }> {
    let _search = await applyFolderHiding(
        params.searchListView,
        params.subpath,
        params.space_name,
        params.spaces
    );

    const queryObject = buildQueryObject({
        shortname: params.shortname,
        type: params.type,
        space_name: params.space_name,
        subpath: params.subpath,
        exact_subpath: params.exact_subpath,
        numberRowsPerPage: params.objectDatatable.numberRowsPerPage,
        stringSortBy: params.objectDatatable.stringSortBy,
        stringSortOrder: params.objectDatatable.stringSortOrder,
        numberActivePage: params.objectDatatable.numberActivePage,
        search: _search,
        scope: params.scope,
        requestExtra: params.requestExtra
    });

    const resp = await Dmart.query(queryObject, params.scope);

    if (!resp) {
        return { total: 0, records: [] };
    }

    return {
        total: resp.attributes.total,
        records: resp.records
    };
}