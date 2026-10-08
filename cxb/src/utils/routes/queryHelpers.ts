/**
 * Utility functions for query route operations
 */
import type { QueryRequest, ResourceType } from "@edraj/tsdmart";

/**
 * Formats a date string to ISO format with time set to start/end of day
 * @param dateString - Date string in YYYY-MM-DD format
 * @param isEndOfDay - If true, sets time to 23:59:59, otherwise 00:00:00
 * @returns ISO date string with timezone
 */
export function formatDateForQuery(dateString: string, isEndOfDay: boolean = false): string {
    const timeString = isEndOfDay ? "T23:59:59.999Z" : "T00:00:00.000Z";
    return `${dateString}${timeString}`;
}

/**
 * Adds date filters to a query object if dates are provided
 * @param queryObject - The query object to modify
 * @param fromDate - Start date string (optional)
 * @param toDate - End date string (optional)
 */
export function addDateFilters(
    queryObject: Pick<QueryRequest, "from_date" | "to_date">,
    fromDate?: string,
    toDate?: string,
): void {
    if (fromDate) {
        queryObject.from_date = formatDateForQuery(fromDate);
    }
    if (toDate) {
        queryObject.to_date = formatDateForQuery(toDate, true);
    }
}

/** What the Query and Export tools collect from their forms. */
export interface BaseQueryParams {
    space_name: string;
    subpath: string;
    resource_type?: ResourceType | null;
    resource_shortnames?: string;
    search?: string;
    offset?: number;
    limit?: number;
    retrieve_json_payload?: boolean;
    retrieve_attachments?: boolean;
}

/** The common part of a `QueryRequest`; the caller adds `type` and the rest. */
export type BaseQuery = Pick<
    QueryRequest,
    | "space_name"
    | "subpath"
    | "filter_types"
    | "filter_shortnames"
    | "retrieve_json_payload"
    | "retrieve_attachments"
    | "search"
    | "offset"
    | "limit"
>;

/**
 * Creates a base query object with common properties
 * @param params - Query parameters
 * @returns Base query object
 */
export function createBaseQuery(params: BaseQueryParams): BaseQuery {
    return {
        space_name: params.space_name,
        subpath: params.subpath,
        filter_types: params.resource_type ? [params.resource_type] : [],
        filter_shortnames: params.resource_shortnames ? params.resource_shortnames.split(",") : [],
        retrieve_json_payload: params.retrieve_json_payload || false,
        retrieve_attachments: params.retrieve_attachments || false,
        search: params.search || "",
        offset: params.offset || 0,
        limit: params.limit || 10,
    };
}
