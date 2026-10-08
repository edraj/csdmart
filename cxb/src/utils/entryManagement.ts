import { Dmart, RequestType, ResourceType, type ApiResponseRecord, type ResponseEntry, type ActionRequest } from "@edraj/tsdmart";
import { removeEmpty } from "@/utils/renderer/schemaEntryRenderer";
import { Level, showToast } from "@/utils/toast";
import { jsonEditorContentParser } from "@/utils/jsonEditor";
import { isRecord } from "@/utils/compare";
import { normalizeSubpath, parentOf, trashDestination } from "@/utils/subpath";
import { get } from "svelte/store";
import { _ } from "@/i18n";

/** The active locale's message, for a module that has no component context. */
const t = (key: string) => get(_)(key);

/**
 * What an entry action hands back. `errorMessage` is whatever the failure
 * carried — the server's error envelope (an object) or an Error's text — and
 * the callers render either: text inline, an object through Prism.
 */
export interface EntryActionResult {
    success: boolean;
    errorMessage?: unknown;
}

/**
 * The entry as the JSON editor holds it. Open like the entry itself; only
 * the fields the save path touches are named.
 */
export interface EditableEntry extends Record<string, unknown> {
    uuid?: string;
    shortname?: string;
    payload?: EditablePayload;
    password?: unknown;
    old_password?: unknown;
}

export interface EditablePayload extends Record<string, unknown> {
    content_type?: string;
    schema_shortname?: string;
    body?: unknown;
}

/** The parts of an axios/dmart failure the results surface. */
interface RequestFailure {
    response?: { data?: unknown };
    message?: unknown;
}

function asFailure(error: unknown): RequestFailure {
    return typeof error === "object" && error !== null ? (error as RequestFailure) : {};
}

/** `error.response.data.error`, the dmart envelope's error object, when there is one. */
function responseError(error: unknown): unknown {
    const data = asFailure(error).response?.data;
    return isRecord(data) ? data.error : undefined;
}

/**
 * Gets the parent subpath from a given path
 */
export function getParentSubpath(path: string): string {
    const normalizedPath = path.replace(/^\/+|\/+$/g, "");
    const parts = normalizedPath.split("/");

    if (parts.length <= 1 || (parts.length === 1 && parts[0] === "")) {
        return "/";
    }

    return "/" + parts.slice(0, -1).join("/");
}

/**
 * Recursively drops props whose value is an empty string now AND was an empty
 * string (or absent) before. Keeps a field that was cleared from a real value
 * to "" (originalValue is non-empty) — that's a genuine change. Arrays, nested
 * non-object values, null, and non-empty values are left untouched. Mutates
 * `current` in place.
 */
function stripUnchangedEmptyStrings(current: unknown, original: unknown): void {
    if (!isRecord(current)) return;
    const orig: Record<string, unknown> = isRecord(original) ? original : {};
    for (const key of Object.keys(current)) {
        const value = current[key];
        if (value === "" && (orig[key] === "" || orig[key] === undefined)) {
            delete current[key];
        } else if (isRecord(value)) {
            stripUnchangedEmptyStrings(value, orig[key]);
            // A nested object the strip emptied entirely (every prop was an
            // unchanged empty) and that the original never carried is a spurious
            // {} — drop it too, so editing a never-filled nested group doesn't
            // write an empty object. A genuine change keeps at least one prop,
            // so this only fires on no-op subtrees.
            if (orig[key] === undefined && Object.keys(value).length === 0) {
                delete current[key];
            }
        }
    }
}

/**
 * Handles saving entry data with proper content processing
 */
export async function saveEntry(
    jeContent: unknown,
    space_name: string,
    subpath: string,
    resource_type: ResourceType,
    originalJeContent?: unknown
): Promise<EntryActionResult> {
    let content: EditableEntry;
    try {
        content = jsonEditorContentParser<EditableEntry>(jeContent);
    } catch {
        return { success: false, errorMessage: t("invalid_json") };
    }

    const shortname = content.shortname;
    delete content.uuid;
    delete content.shortname;

    if (resource_type === ResourceType.schema) {
        if (content.payload) {
            content.payload.body = removeEmpty(content.payload.body);
        }
    } else if (resource_type === ResourceType.content && subpath === "workflows") {
        content.payload = {
            body: removeEmpty(jsonEditorContentParser(content.payload?.body)),
            schema: 'workflow',
            content_type: "json"
        };
    }

    if (resource_type === ResourceType.user) {
        // Admin UI does not set passwords — /managed/request rejects them. Strip
        // any password/old_password (a loaded $argon2id hash or a stale field) so
        // the update never carries one; users set their own via OTP / reset.
        delete content.password;
        delete content.old_password;
    }

    if (originalJeContent) {
        const originalContent = jsonEditorContentParser<EditableEntry>(originalJeContent);
        // The renderer passes the already-parsed original, so its payload is
        // read off the argument as given.
        const originalPayload = isRecord(originalJeContent) ? originalJeContent.payload : undefined;
        if (isRecord(originalPayload) && originalPayload.content_type === 'json') {
            const originalBody = originalContent.payload?.body;
            const currentBody = content.payload?.body;
            if (isRecord(originalBody) && isRecord(currentBody)) {
                const originalKeys = Object.keys(originalBody);
                const currentKeys = Object.keys(currentBody);
                const removedKeys = originalKeys.filter(key => !currentKeys.includes(key));
                removedKeys.forEach(key => {
                    currentBody[key] = null;
                });
            }
        }
        // Don't send props that are an empty string now and were empty/absent
        // before — editing must not write spurious "" for never-filled or
        // already-empty fields. (A field cleared from a real value to "" is
        // kept by the helper, since that's a genuine change.)
        stripUnchangedEmptyStrings(content, originalContent);
    }
    let _subpath = resource_type === ResourceType.folder ? getParentSubpath(subpath) : subpath
    _subpath = _subpath.replaceAll('-', '/')
    try {
        await Dmart.request({
            space_name: space_name,
            request_type: RequestType.update,
            records: [{
                resource_type: resource_type,
                shortname: shortname ?? "",
                subpath: _subpath,
                attributes: content
            }]
        });
        showToast(Level.info, t("entry_updated"));
        return { success: true };
    } catch (error: unknown) {
        const failure = asFailure(error);
        return { success: false, errorMessage: failure.response?.data || failure.message };
    }
}

/**
 * Deletes an entry
 */
export async function deleteEntry(
    entry: ResponseEntry,
    space_name: string,
    subpath: string,
    resource_type: ResourceType,
    force: boolean = false
): Promise<EntryActionResult> {
    // The renderer's `subpath` is the folder's OWN path for a folder entry, and
    // the containing path for anything else; a request always names the parent.
    const targetSubpath = resource_type === ResourceType.folder
        ? parentOf(subpath)
        : normalizeSubpath(subpath);

    try {
        const body: ActionRequest & { force?: boolean } = {
            space_name: space_name,
            request_type: RequestType.delete,
            force,
            records: [{
                resource_type: resource_type,
                shortname: entry.shortname,
                subpath: targetSubpath,
                attributes: {}
            }]
        };
        await Dmart.request(body);
        showToast(Level.info, t("entry_deleted"));
        return { success: true };
    } catch (error: unknown) {
        showToast(Level.warn, t("entry_delete_failed"));
        return { success: false, errorMessage: responseError(error) ?? asFailure(error).message };
    }
}

/**
 * Moves an entry to trash
 */
export async function moveEntryToTrash(
    entry: ResponseEntry,
    space_name: string,
    subpath: string,
    resource_type: ResourceType,
    userShortname: string
): Promise<EntryActionResult> {
    try {
        const moveResourceType = resource_type;
        // Same convention as deleteEntry: a folder's `subpath` is its own path.
        const moveNewSubpath = moveResourceType === ResourceType.folder
            ? parentOf(subpath)
            : normalizeSubpath(subpath);

        const moveAttrb = {
            src_space_name: space_name,
            src_subpath: moveNewSubpath,
            src_shortname: entry.shortname,
            dest_space_name: 'personal',
            dest_subpath: trashDestination(userShortname, space_name, moveNewSubpath),
            dest_shortname: entry.shortname,
        };

        await Dmart.request({
            space_name: space_name,
            request_type: RequestType.move,
            records: [
                {
                    resource_type: moveResourceType,
                    shortname: entry.shortname,
                    subpath: moveNewSubpath,
                    attributes: moveAttrb,
                },
            ],
        });
        showToast(Level.info, t("entry_trashed"));
        return { success: true };
    } catch (error: unknown) {
        showToast(Level.warn, t("entry_trash_failed"));
        const envelopeError = responseError(error);
        return {
            success: false,
            errorMessage: (isRecord(envelopeError) ? envelopeError.message : undefined) ?? asFailure(error).message,
        };
    }
}

/**
 * Gets payload schema for a given schema shortname
 */
export async function getPayloadSchema(schemaShortname: string, space_name: string): Promise<ResponseEntry | null> {
    if (schemaShortname === "folder_rendering") {
        return await Dmart.retrieveEntry({ resource_type: ResourceType.schema, space_name: "management", subpath: "schema", shortname: schemaShortname, retrieve_json_payload: true, retrieve_attachments: false, validate_schema: true });
    }
    return await Dmart.retrieveEntry({ resource_type: ResourceType.schema, space_name, subpath: "schema", shortname: schemaShortname, retrieve_json_payload: true, retrieve_attachments: false, validate_schema: true });
}

/**
 * Moves multiple entries to trash
 */
export async function bulkMoveEntryToTrash(
    entries: ApiResponseRecord[],
    space_name: string,
    userShortname: string
): Promise<EntryActionResult> {
    try {
        const records = entries.map((entry) => {
            // A list record names its type as a plain string; the request wants the enum.
            const moveResourceType = entry.resource_type as ResourceType;
            // A list record's `subpath` is already the containing path — for a
            // folder record as much as for a content one — so it is sent as is.
            // Trimming it produced `-a` for `/a/b` and `/` for `/a`.
            const moveNewSubpath = normalizeSubpath(entry.subpath);

            const moveAttrb = {
                src_space_name: space_name,
                src_subpath: moveNewSubpath,
                src_shortname: entry.shortname,
                dest_space_name: 'personal',
                dest_subpath: trashDestination(userShortname, space_name, moveNewSubpath),
                dest_shortname: entry.shortname,
            };

            return {
                resource_type: moveResourceType,
                shortname: entry.shortname,
                subpath: moveNewSubpath,
                attributes: moveAttrb,
            };
        });

        await Dmart.request({
            space_name: space_name,
            request_type: RequestType.move,
            records: records,
        });
        showToast(Level.info, t("entries_trashed"));
        return { success: true };
    } catch (error: unknown) {
        showToast(Level.warn, t("entries_trash_failed"));
        const envelopeError = responseError(error);
        return {
            success: false,
            errorMessage: (isRecord(envelopeError) ? envelopeError.message : undefined) ?? asFailure(error).message,
        };
    }
}
