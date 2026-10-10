import { andSearch } from "@shared/search-compose";
import {
    type ActionRequest,
    type ActionResponse,
    type ApiQueryResponse,
    ContentType,
    Dmart,
    type QueryRequest,
    QueryType,
    DmartScope,
    RequestType,
    ResourceType,
    SortType,
} from "@edraj/tsdmart";
import { user, getCurrentScope } from "@/stores/user";
import { get } from "svelte/store";
import { getFileType } from "../helpers";
import { getSpaces } from "./spaces";
import { log } from "@/lib/logger";
import { MESSAGES_SPACE } from "@/lib/constants";
import { ensureUploadSize } from "./core";
import type { EntryRecord, JsonObject } from "@/lib/types";

export type StreamEntitiesOptions = {
    // Restrict the fan-out to a single space (matches the "Current space:" tag chip).
    spaceFilter?: string;
    // Restrict the query to an exact subpath within each space (matches the
    // "Current folder:" tag chip). Falsy = whole space.
    subpathFilter?: string;
    // Per-space row cap. Defaults to 50 to keep the dropdown DOM bounded
    // when a space contains many entries.
    limitPerSpace?: number;
    // Cap on simultaneous in-flight per-space queries. Without this, every
    // keystroke against a tenant with many spaces fires N concurrent
    // requests; 6 keeps both client connection budget and server load
    // bounded while still feeling instant on typical setups.
    maxConcurrent?: number;
};

/**
 * Fans a search out across spaces, invoking `onBatch` as each space's query
 * resolves so callers can render results progressively. Returns a `cancel`
 * handle so a newer search can ignore stale in-flight batches.
 *
 * Per-space queries are isolated: if one space's query rejects, the failure
 * is logged and the rest of the fan-out continues. The aggregate `done`
 * promise still resolves so callers can flip their loading state.
 *
 * Concurrency is capped at `maxConcurrent` (default 6). Queries beyond the
 * cap queue and start as earlier ones resolve, so a tenant with 50 spaces
 * doesn't open 50 simultaneous HTTP connections per keystroke.
 */
export function streamEntitiesAcrossSpaces(
    search: string,
    onBatch: (records: EntryRecord[], space: string) => void,
    options: StreamEntitiesOptions = {}
): { done: Promise<void>; cancel: () => void } {
    let cancelled = false;
    const limit = options.limitPerSpace ?? 50;
    const maxConcurrent = Math.max(1, options.maxConcurrent ?? 6);

    const done = (async () => {
        let spaces: string[];
        if (options.spaceFilter) {
            spaces = [options.spaceFilter];
        } else {
            const result = await getSpaces();
            if (cancelled) return;
            spaces = result.records.map((space) => space.shortname);
        }

        const subpath = options.subpathFilter
            ? options.subpathFilter.startsWith("/")
                ? options.subpathFilter
                : `/${options.subpathFilter}`
            : "/";
        const exactSubpath = !!options.subpathFilter;

        // Pull-based concurrency limiter: spawn `maxConcurrent` workers,
        // each draining the same `cursor` index so no global queue
        // bookkeeping is needed. When all workers exit, fan-out is done.
        let cursor = 0;
        const querySpace = async (space: string) => {
            if (cancelled) return;
            const queryRequest: QueryRequest = {
                filter_shortnames: [],
                type: QueryType.subpath,
                space_name: space,
                subpath,
                exact_subpath: exactSubpath,
                sort_by: "shortname",
                sort_type: SortType.ascending,
                search,
                limit,
                retrieve_json_payload: true,
                retrieve_attachments: true,
            };
            try {
                const response: ApiQueryResponse = (await Dmart.query(queryRequest))!;
                if (cancelled) return;
                onBatch(response?.records ?? [], space);
            } catch (error) {
                if (cancelled) return;
                log.error(`Search failed for space "${space}":`, error);
                onBatch([], space);
            }
        };
        const worker = async () => {
            while (!cancelled) {
                const i = cursor++;
                if (i >= spaces.length) return;
                await querySpace(spaces[i]);
            }
        };
        const workerCount = Math.min(maxConcurrent, spaces.length);
        await Promise.all(Array.from({ length: workerCount }, () => worker()));
    })();

    return {
        done,
        cancel: () => {
            cancelled = true;
        },
    };
}

/** Newest entries per space that getMyEntities() returns. */
export const MY_ENTITIES_LIMIT_PER_SPACE = 100;

/**
 * Everything the current user (or `shortname`) owns, across every visible
 * space: one bounded, metadata-only query per space through the same 6-wide
 * worker pool as streamEntitiesAcrossSpaces (review perf #14). The list page
 * shows titles, tags, state and dates, so neither payload nor attachments
 * are requested.
 */
export async function getMyEntities(
    shortname: string = "",
    options: { limitPerSpace?: number; maxConcurrent?: number } = {},
) {
    const result = await getSpaces(false, DmartScope.managed, [MESSAGES_SPACE]);
    const spaces = result.records.map((space) => space.shortname);
    const owner = shortname || get(user).shortname;
    const limit = options.limitPerSpace ?? MY_ENTITIES_LIMIT_PER_SPACE;
    const maxConcurrent = Math.max(1, options.maxConcurrent ?? 6);

    const results: ApiQueryResponse["records"][] = new Array(spaces.length);
    let cursor = 0;
    const worker = async () => {
        while (true) {
            const i = cursor++;
            if (i >= spaces.length) return;
            const queryRequest: QueryRequest = {
                filter_shortnames: [],
                type: QueryType.subpath,
                space_name: spaces[i],
                subpath: "/",
                exact_subpath: false,
                sort_by: "updated_at",
                sort_type: SortType.descending,
                search: `@owner_shortname:${owner}`,
                limit,
                offset: 0,
                retrieve_json_payload: false,
                retrieve_attachments: false,
            };
            try {
                const response = await Dmart.query(queryRequest);
                results[i] = response?.records ?? [];
            } catch (error) {
                log.error(`Could not list entries in space "${spaces[i]}":`, error);
                results[i] = [];
            }
        }
    };
    await Promise.all(Array.from({ length: Math.min(maxConcurrent, spaces.length) }, worker));

    return results.flat();
}



// getEntityAttachmentsCount used to live here: one attachments-aggregation
// query per card on the space page. The browse pages now count comments,
// reactions and media from the `attachments` the listing query already
// returns (lib/catalogItems attachmentCounts), so nothing calls it.

export async function attachAttachmentsToEntity(
    shortname: string,
    spaceName: string,
    subpath: string,
    attachment: File,
    metadata?: {
        shortname?: string;
        displayname?: Record<string, string>;
        description?: Record<string, string>;
    }
) {
    ensureUploadSize(attachment);
    const fileType = getFileType(attachment);
    const resourceType = fileType ? fileType.resourceType : ResourceType.media;

    let cleanSubpath = subpath ? (subpath.startsWith("/") ? subpath.substring(1) : subpath) : "";
    if (cleanSubpath === "__root__") cleanSubpath = "";
    const targetSubpath = cleanSubpath ? `${cleanSubpath}/${shortname}` : shortname;

    const trimmedShortname = metadata?.shortname?.trim();
    const displaynamePayload = metadata?.displayname && Object.keys(metadata.displayname).length > 0
        ? metadata.displayname
        : undefined;
    const descriptionPayload = metadata?.description && Object.keys(metadata.description).length > 0
        ? metadata.description
        : undefined;

    const attributes: JsonObject = { is_active: true };
    if (displaynamePayload) attributes.displayname = displaynamePayload;
    if (descriptionPayload) attributes.description = descriptionPayload;

    const response = await Dmart.uploadWithPayload({
        space_name: spaceName,
        subpath: targetSubpath,
        shortname: trimmedShortname || "auto",
        resource_type: resourceType,
        payload_file: attachment,
        attributes: Object.keys(attributes).length > 1 ? attributes : undefined,
    });
    return response.status === "success" && response.records.length > 0;
}

/**
 * Free-text search inside ONE space (the space browse page). Folders and
 * schemas are excluded the same way the page's main listing excludes them,
 * and the server applies the sort so results match the listing's order.
 */
export async function searchInSpace(
    spaceName: string,
    search: string,
    limit: number = 20,
    sortBy: string = "created_at",
    sortType: SortType = SortType.descending,
    scope: DmartScope = getCurrentScope()
) {
    const queryRequest: QueryRequest = {
        filter_shortnames: [],
        type: QueryType.search,
        space_name: spaceName,
        subpath: "/",
        exact_subpath: false,
        sort_by: sortBy,
        sort_type: sortType,
        search: andSearch(search, "-@shortname:schema -@resource_type:folder|schema"),
        limit,
        offset: 0,
        retrieve_json_payload: true,
        retrieve_attachments: true,
    };
    const response: ApiQueryResponse = (await Dmart.query(queryRequest, scope))!;
    return response?.records ?? [];
}

/**
 * Free-text search across every visible space (the catalog index page). Each
 * record is tagged with the `space_name` it came from — the query response
 * does not carry it, and without it a result cannot be linked to.
 */
export async function searchInCatalog(search: string = "", limit: number = 20, offset: number = 0) {
    const result = await getSpaces(false, getCurrentScope());
    const spaces = result.records.map((space) => space.shortname);

    const promises = spaces.map(async (space) => {
        const queryRequest: QueryRequest = {
            filter_shortnames: [],
            type: QueryType.subpath,
            space_name: space,
            subpath: "/",
            exact_subpath: false,
            sort_by: "created_at",
            sort_type: SortType.ascending,
            search,
            limit,
            offset,
            retrieve_json_payload: true,
            retrieve_attachments: false,
        };

        const response: ApiQueryResponse = (await Dmart.query(
            queryRequest,
            getCurrentScope()
        ))!;
        return (response?.records ?? []).map((record) => ({
            ...record,
            space_name: space,
        }));
    });

    const allRecordsArrays = await Promise.all(promises);

    return allRecordsArrays.flat();
}

/**
 * A translation as the meta form collects it: a language left blank may be
 * null until the user types in it.
 */
type TranslationInput = Record<string, string | null | undefined>;

/** What the folder dialog collects for a new folder. */
export type FolderCreateInput = {
    shortname?: string;
    displayname?: TranslationInput | null;
    description?: TranslationInput | null;
    is_active?: boolean;
    /** The folder's `payload.body` (listing settings). */
    folderContent?: JsonObject;
};

export async function createFolder(
    spaceName: string,
    subpath: string,
    data: FolderCreateInput
) {
    const actionRequest: ActionRequest = {
        space_name: spaceName,
        request_type: RequestType.create,
        records: [
            {
                resource_type: ResourceType.folder,
                shortname: data.shortname || "auto",
                subpath: subpath.startsWith("/") ? subpath : `/${subpath}`,
                attributes: {
                    displayname: data.displayname || {},
                    description: data.description || {},
                    is_active: data.is_active !== undefined ? data.is_active : true,
                    payload: {
                        body: data.folderContent || {},
                        content_type: ContentType.json,
                    },
                },
            },
        ],
    };

    try {
        const response: ActionResponse = await Dmart.request(actionRequest);
        if (response.status === "success" && response.records.length > 0) {
            return response.records[0].shortname;
        }
        return null;
    } catch (error) {
        log.error(`Error creating folder in ${spaceName}/${subpath}:`, error);
        throw error;
    }
}

