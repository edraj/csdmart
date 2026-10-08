import {
    type ActionRequest,
    type ActionResponse,
    Dmart,
    RequestType,
    ResourceType,
} from "@edraj/tsdmart";
import { checkAccess } from "@/stores/permissions";
import type { JsonObject } from "@/lib/types";

/** `value` when it is a non-empty string, else `fallback`. */
function textOr(value: unknown, fallback: string): string {
    return typeof value === "string" && value ? value : fallback;
}

/**
 * The attributes of a role, from the role form's data bag. Every field falls
 * back to its empty value so a partially filled form still makes a valid
 * request.
 */
function roleAttributes(data: JsonObject): JsonObject {
    return {
        is_active: data.is_active ?? true,
        tags: data.tags || [],
        relationships: data.relationships || [],
        permissions: data.permissions || [],
        displayname: data.displayname || {},
        description: data.description || {},
        slug: data.slug || null,
    };
}

/** The attributes of a permission, from the permission form's data bag. */
function permissionAttributes(data: JsonObject): JsonObject {
    return {
        is_active: data.is_active ?? true,
        tags: data.tags || [],
        relationships: data.relationships || [],
        acl: data.acl || [],
        subpaths: data.subpaths || {},
        resource_types: data.resource_types || [],
        actions: data.actions || [],
        conditions: data.conditions || [],
        restricted_fields: data.restricted_fields || [],
        allowed_fields_values: data.allowed_fields_values || {},
        attachments: data.attachments || {},
        slug: data.slug || null,
    };
}

export async function createRole(
    data: JsonObject,
    space_name: string,
    subpath: string,
    resourceType: ResourceType,
    workflow_shortname: string,
    schema_shortname: string
) {
    if (!checkAccess("create", space_name, subpath, resourceType)) {
        throw new Error("Permission denied: cannot create role");
    }
    const attributes = roleAttributes(data);
    if (workflow_shortname && schema_shortname) {
        attributes.workflow_shortname = workflow_shortname;
        attributes.schema_shortname = schema_shortname;
    }

    const actionRequest: ActionRequest = {
        space_name,
        request_type: RequestType.create,
        records: [
            {
                resource_type: resourceType,
                shortname: textOr(data.title, "auto"),
                subpath,
                attributes,
            },
        ],
    };
    const response: ActionResponse = await Dmart.request(actionRequest);
    if (response.status === "success" && response.records.length > 0) {
        return response.records[0].shortname;
    }
    return null;
}

export async function updateRole(
    shortname: string,
    space_name: string,
    subpath: string,
    resourceType: ResourceType,
    data: JsonObject,
    workflow_shortname: string,
    schema_shortname: string
) {
    if (!checkAccess("update", space_name, subpath, resourceType)) {
        throw new Error("Permission denied: cannot update role");
    }
    const attributes = roleAttributes(data);

    if (workflow_shortname && schema_shortname) {
        attributes.workflow_shortname = workflow_shortname;
        attributes.schema_shortname = schema_shortname;
    }

    const actionRequest: ActionRequest = {
        space_name,
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

    const response: ActionResponse = await Dmart.request(actionRequest);
    return response.status === "success" && response.records.length > 0
        ? response.records[0].shortname
        : null;
}

export async function createPermission(
    data: JsonObject,
    space_name: string,
    subpath: string,
    resourceType: ResourceType,
    workflow_shortname: string,
    schema_shortname: string
) {
    if (!checkAccess("create", space_name, subpath, resourceType)) {
        throw new Error("Permission denied: cannot create permission");
    }
    const attributes = permissionAttributes(data);

    if (workflow_shortname && schema_shortname) {
        attributes.workflow_shortname = workflow_shortname;
        attributes.schema_shortname = schema_shortname;
    }

    const actionRequest: ActionRequest = {
        space_name,
        request_type: RequestType.create,
        records: [
            {
                resource_type: resourceType,
                shortname: textOr(data.shortname, "auto"),
                subpath,
                attributes,
            },
        ],
    };

    const response: ActionResponse = await Dmart.request(actionRequest);
    if (response.status === "success" && response.records.length > 0) {
        return response.records[0].shortname;
    }
    return null;
}

export async function updatePermission(
    shortname: string,
    space_name: string,
    subpath: string,
    resourceType: ResourceType,
    data: JsonObject,
    workflow_shortname: string,
    schema_shortname: string
) {
    if (!checkAccess("update", space_name, subpath, resourceType)) {
        throw new Error("Permission denied: cannot update permission");
    }
    const attributes = permissionAttributes(data);

    if (workflow_shortname && schema_shortname) {
        attributes.workflow_shortname = workflow_shortname;
        attributes.schema_shortname = schema_shortname;
    }

    const actionRequest: ActionRequest = {
        space_name,
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

    const response: ActionResponse = await Dmart.request(actionRequest);
    return response.status === "success" && response.records.length > 0
        ? response.records[0].shortname
        : null;
}
