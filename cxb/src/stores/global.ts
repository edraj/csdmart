import {writable} from "svelte/store";
import {ResourceType, type ResponseEntry} from "@edraj/tsdmart";

export const currentEntry = writable<{entry: ResponseEntry | null; [key: string]: any} | null>(null);
export const currentListView = writable<{fetchPageRecords: (isSetPage?: boolean, requestExtra?: {}) => Promise<void>; query?: any; [key: string]: any} | null>(null);
/**
 * The sidebar's folder-children cache. Keys come from `sidebarCacheKey()` in
 * `@/utils/subpath` — every writer and reader must build them there, or a
 * folder created from the list never shows up in the tree until a reload.
 * `hasMore` records, per key, whether the server has children beyond the page
 * that was fetched, so the tree can offer "load more".
 */
export const spaceChildren = writable<{
    data: Map<string, any[]>;
    hasMore: Map<string, boolean>;
    refresh: ((spaceName: string, subpath?: string, invalidate?: boolean) => Promise<any>) | null;
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
