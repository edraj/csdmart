import {writable} from "svelte/store";
import {type ApiResponseRecord, type QueryRequest, ResourceType, type ResponseEntry} from "@edraj/tsdmart";

/** The entry the renderer is showing, and how to reload it after an action elsewhere. */
export interface CurrentEntry {
    entry: ResponseEntry | null;
    refreshEntry?: () => Promise<void>;
}

/**
 * The mounted list's hooks for the action bar and the modals: reload the
 * page, and the live query plus total the CSV download starts from.
 */
export interface CurrentListView {
    fetchPageRecords: (isSetPage?: boolean, requestExtra?: Record<string, unknown>) => Promise<void>;
    query?: QueryRequest;
    total?: number;
}

export const currentEntry = writable<CurrentEntry | null>(null);
export const currentListView = writable<CurrentListView | null>(null);
/**
 * The sidebar's folder-children cache. Keys come from `sidebarCacheKey()` in
 * `@/utils/subpath` — every writer and reader must build them there, or a
 * folder created from the list never shows up in the tree until a reload.
 * `hasMore` records, per key, whether the server has children beyond the page
 * that was fetched, so the tree can offer "load more".
 */
export const spaceChildren = writable<{
    data: Map<string, ApiResponseRecord[]>;
    hasMore: Map<string, boolean>;
    refresh: ((spaceName: string, subpath?: string, invalidate?: boolean) => Promise<ApiResponseRecord[]>) | null;
}>({
    data: new Map(),
    hasMore: new Map(),
    refresh: null,
});

export const resourceTypeWithNoPayload = [
    ResourceType.role,
    ResourceType.permission,
];
export const subpathInManagementNoAction = [
    "users",
    "roles",
    "permissions",
    "groups",
];

export const resourcesWithFormAndJson = [
    ResourceType.space,
    ResourceType.user,
    ResourceType.content,
    ResourceType.folder,
    ResourceType.ticket,
];

export enum InputMode {
    form = "form",
    json = "json"
}
