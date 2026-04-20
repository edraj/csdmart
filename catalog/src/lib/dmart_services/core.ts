import {
    type ActionRequest,
    type ActionResponse,
    type ApiQueryResponse,
    Dmart,
    type QueryRequest,
    QueryType,
    RequestType,
    DmartScope,
    ResourceType,
    SortType,
} from "@edraj/tsdmart";
import { cleanSubpath, buildAttachmentSubpath } from "../formUtils";
import { log } from "../logger";
import { ROOT_SUBPATH } from "../constants";
import { getCurrentScope } from "@/stores/user";
import { AUTO_UUID_RULE, resolveAutoShortname } from "@/lib/helpers";

/**
 * Maximum size (bytes) for a single multipart upload. Matches the dmart
 * server's FormOptions.MultipartBodyLengthLimit (50 MB). Files over this
 * limit are rejected before the request is sent so the UI can show a
 * friendly error instead of a generic network failure.
 */
export const MAX_UPLOAD_SIZE = 50 * 1024 * 1024;

/**
 * Throws a descriptive Error when `file` exceeds the server-side upload limit.
 * Returns the file unchanged on success — lets callers chain the validation
 * into an expression.
 */
export function ensureUploadSize(file: File): File {
    if (file.size > MAX_UPLOAD_SIZE) {
        const mb = (file.size / (1024 * 1024)).toFixed(1);
        throw new Error(
            `File "${file.name}" is ${mb} MB; maximum upload size is ` +
            `${MAX_UPLOAD_SIZE / (1024 * 1024)} MB.`
        );
    }
    return file;
}

/**
 * Validates a shortname against the allowed pattern
 * Pattern: ^[a-zA-Z\u0621-\u064a0-9\u0660-\u0669\u064b-\u065f_]{1,64}$
 * Allows: English letters, Arabic letters, numbers (English & Arabic), Arabic diacritics, underscores
 * Length: 1 to 64 characters
 */
export function validateShortname(shortname: string): boolean {
    const shortnamePattern = /^[a-zA-Z\u0621-\u064a0-9\u0660-\u0669\u064b-\u065f_]{1,64}$/;
    return shortnamePattern.test(shortname);
}

/**
 * Get an entity by its shortname
 */
export async function getEntityByShortname(
    shortname: string,
    spaceName: string,
    subpath: string,
    resourceType: ResourceType = ResourceType.content,
    scope: DmartScope = DmartScope.managed,
    retrieve_json_payload: boolean = true,
    retrieve_attachments: boolean = true
) {
    const cleanedSubpath = cleanSubpath(subpath) || ROOT_SUBPATH;

    try {
        return await Dmart.retrieveEntry(
            {
                resource_type: resourceType,
                space_name: spaceName,
                subpath: cleanedSubpath,
                shortname,
                retrieve_json_payload,
                retrieve_attachments,
                validate_schema: true,
            },
            scope
        );
    } catch (error) {
        log.error(`Error retrieving item ${shortname}:`, error);
        return null;
    }
}

/**
 * Get entities of a specific subpath
 */
export async function getEntitiesOfSubpath(
    spaceName: string,
    subpath: string,
    scope: DmartScope = DmartScope.managed,
    offset: number = 0,
    limit: number = 100,
    search: string = "",
    exactSubpath: boolean = false,
    sortBy: string = "created_at",
    sortType: SortType = SortType.descending,
    retrieveJson: boolean = true,
    retrieveAttachments: boolean = true
): Promise<ApiQueryResponse> {
    const queryRequest: QueryRequest = {
        filter_shortnames: [],
        type: QueryType.subpath,
        space_name: spaceName,
        subpath,
        exact_subpath: exactSubpath,
        sort_by: sortBy,
        sort_type: sortType,
        search,
        limit,
        offset,
        retrieve_json_payload: retrieveJson,
        retrieve_attachments: retrieveAttachments,
    };

    return (await Dmart.query(queryRequest, scope))!;
}

/**
 * Get entities of a specific space using search query
 */
export async function getEntitiesOfSpace(
    spaceName: string,
    scope: DmartScope = DmartScope.managed,
    offset: number = 0,
    limit: number = 100,
    search: string = "",
    exactSubpath: boolean = false,
    sortBy: string = "created_at",
    sortType: SortType = SortType.descending,
    retrieveJson: boolean = true,
    retrieveAttachments: boolean = true
): Promise<ApiQueryResponse> {
    const queryRequest: QueryRequest = {
        filter_shortnames: [],
        type: QueryType.search,
        space_name: spaceName,
        subpath: "/",
        exact_subpath: exactSubpath,
        sort_by: sortBy,
        sort_type: sortType,
        search,
        limit,
        offset,
        retrieve_json_payload: retrieveJson,
        retrieve_attachments: retrieveAttachments,
    };

    return (await Dmart.query(queryRequest, scope))!;
}

/**
 * Search entities with full query support
 */
export async function searchEntities(
    spaceName: string,
    subpath: string = "/",
    search: string = "",
    limit: number = 100,
    offset: number = 0,
    sortBy: string = "created_at",
    sortType: SortType = SortType.descending,
    scope: DmartScope = DmartScope.managed,
    retrieveJson: boolean = true,
    retrieveAttachments: boolean = true,
    exactSubpath: boolean = false
): Promise<ApiQueryResponse> {
    const queryRequest: QueryRequest = {
        filter_shortnames: [],
        type: QueryType.search,
        space_name: spaceName,
        subpath,
        exact_subpath: exactSubpath,
        sort_by: sortBy,
        sort_type: sortType,
        search,
        limit,
        offset,
        retrieve_json_payload: retrieveJson,
        retrieve_attachments: retrieveAttachments,
    };

    return (await Dmart.query(queryRequest, scope))!;
}

/**
 * Create a generic entity
 */
export async function createEntity(
    spaceName: string,
    subpath: string,
    resourceType: ResourceType,
    attributes: any,
    shortname: string = AUTO_UUID_RULE
) {
    const recordAttributes = attributes ?? {};
    const { shortname: resolvedShortname } = resolveAutoShortname(
        shortname,
        recordAttributes,
    );

    if (!validateShortname(resolvedShortname)) {
        throw new Error(
            "Invalid shortname format. Shortname must be 1-64 characters and can only contain: English letters (a-z, A-Z), Arabic letters, numbers (0-9), Arabic numerals (٠-٩), Arabic diacritics, and underscores (_)."
        );
    }

    const actionRequest: ActionRequest = {
        space_name: spaceName,
        request_type: RequestType.create,
        records: [
            {
                resource_type: resourceType,
                shortname: resolvedShortname,
                subpath,
                attributes: recordAttributes,
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
        log.error(`Error creating entity in ${spaceName}/${subpath}:`, error);
        throw error;
    }
}

/**
 * Update an existing entity
 */
export async function updateEntity(
    shortname: string,
    spaceName: string,
    subpath: string,
    resourceType: ResourceType,
    attributes: any,
) {
    const actionRequest: ActionRequest = {
        space_name: spaceName,
        request_type: RequestType.update,
        records: [
            {
                resource_type: resourceType,
                shortname,
                subpath,
                attributes,
            },
        ],
    };

    try {
        const response = await Dmart.request(actionRequest);
        return response.status === "success";
    } catch (error) {
        log.error(`Error updating entity ${shortname}:`, error);
        throw error;
    }
}

/**
 * Delete an entity
 */
export async function deleteEntity(
    shortname: string,
    spaceName: string,
    subpath: string,
    resourceType: ResourceType
) {
    const actionRequest: ActionRequest = {
        space_name: spaceName,
        request_type: RequestType.delete,
        records: [
            {
                resource_type: resourceType,
                shortname: shortname,
                subpath: subpath,
                attributes: {},
            },
        ],
    };

    try {
        const response: ActionResponse = await Dmart.request(actionRequest);
        return response.status === "success";
    } catch (error) {
        log.error(`Error deleting entity ${shortname}:`, error);
        throw error;
    }
}

/**
 * Create an attachment for an entity
 */
export async function createAttachment(
    shortname: string,
    spaceName: string,
    subpath: string,
    resourceType: ResourceType,
    file: File
) {
    ensureUploadSize(file);
    const targetSubpath = buildAttachmentSubpath(subpath, shortname);

    const attributes: Record<string, any> = { is_active: true };
    const { shortname: attachmentShortname } = resolveAutoShortname(
        AUTO_UUID_RULE,
        attributes,
    );

    try {
        const response = await Dmart.uploadWithPayload({
            space_name: spaceName,
            subpath: targetSubpath,
            shortname: attachmentShortname,
            resource_type: resourceType,
            payload_file: file,
            attributes,
        });
        return response.status === "success" && response.records.length > 0
            ? response.records[0].shortname
            : null;
    } catch (error) {
        log.error(`Error creating attachment for ${shortname}:`, error);
        return null;
    }
}

/**
 * Update an entity's attachment by replacing the file
 */
export async function updateAttachment(
    shortname: string,
    spaceName: string,
    subpath: string,
    resourceType: ResourceType,
    attachmentShortname: string,
    file: File
) {
    try {
        // First delete the old attachment
        const deleted = await deleteAttachment(shortname, spaceName, subpath, attachmentShortname, resourceType);

        if (!deleted) {
            log.warn(`Could not delete previous attachment ${attachmentShortname}`);
        }

        // Then upload the new one
        return await createAttachment(shortname, spaceName, subpath, resourceType, file);
    } catch (error) {
        log.error(`Error updating attachment ${attachmentShortname} for ${shortname}:`, error);
        return null;
    }
}

/**
 * Delete an attachment from an entity
 */
export async function deleteAttachment(
    entityShortname: string,
    spaceName: string,
    entitySubpath: string,
    attachmentShortname: string,
    resourceType: ResourceType
) {
    const targetSubpath = buildAttachmentSubpath(entitySubpath, entityShortname);

    const actionRequest: ActionRequest = {
        space_name: spaceName,
        request_type: RequestType.delete,
        records: [
            {
                resource_type: resourceType,
                shortname: attachmentShortname,
                subpath: targetSubpath,
                attributes: {},
            },
        ],
    };

    try {
        const response: ActionResponse = await Dmart.request(actionRequest);
        return response.status === "success" && response.records.length > 0;
    } catch (error) {
        log.error(`Error deleting attachment ${attachmentShortname}:`, error);
        return false;
    }
}

/**
 * Get all attachments of an entity
 */
export async function getEntityAttachments(
    shortname: string,
    spaceName: string,
    subpath: string,
    scope: DmartScope = getCurrentScope()
) {
    const targetSubpath = buildAttachmentSubpath(subpath, shortname);

    const query: QueryRequest = {
        filter_shortnames: [],
        type: QueryType.attachments_aggregation,
        space_name: spaceName,
        subpath: targetSubpath,
        limit: 100,
        sort_by: "shortname",
        sort_type: SortType.ascending,
        offset: 0,
        search: "",
        retrieve_json_payload: true,
        retrieve_attachments: true,
    };

    try {
        const response = await Dmart.query(query, scope);
        return response?.records || [];
    } catch (error) {
        log.error(`Error getting attachments for ${shortname}:`, error);
        return [];
    }
}

// Aliases for backward compatibility with existing Svelte components
export const getEntity = getEntityByShortname;
export const replaceEntity = updateEntity;
