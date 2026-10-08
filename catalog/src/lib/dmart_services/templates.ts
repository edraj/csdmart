import {
    type ActionRequest,
    type ActionResponse,
    type ApiQueryResponse,
    type ApiResponseRecord,
    ContentType,
    Dmart,
    QueryType,
    RequestType,
    ResourceType,
    DmartScope,
    SortType,
} from "@edraj/tsdmart";
import { getSpaces, createTemplatesSchema } from "./spaces";
import { log } from "@/lib/logger";
import { APPLICATIONS_SPACE, MAX_QUERY_LIMIT } from "@/lib/constants";
import {
    isJsonObject,
    type EntryDetail,
    type EntryRecord,
    type JsonObject,
    type LocalizedText,
    type TemplateBody,
} from "@/lib/types";

export async function getTemplates(
    space_name: string = "applications",
    scope: DmartScope = DmartScope.managed,
    limit = 100,
    offset = 0,
    exact_subpath = false
): Promise<ApiQueryResponse> {
    const response = await Dmart.query(
        {
            type: QueryType.search,
            space_name: space_name,
            subpath: "/templates",
            search: "-@shortname:schema",
            limit: limit,
            sort_by: "shortname",
            sort_type: SortType.ascending,
            offset: offset,
            retrieve_json_payload: true,
            retrieve_attachments: true,
            exact_subpath: exact_subpath,
        },
        scope
    );
    return response!;
}

/**
 * Fetches a single template by shortname from a space
 */
export async function getTemplate(
    space_name: string,
    template_shortname: string,
    scope: DmartScope = DmartScope.managed
): Promise<EntryDetail | null> {
    try {
        const response = await Dmart.retrieveEntry(
            {
                resource_type: ResourceType.content,
                space_name: space_name,
                subpath: "/templates",
                shortname: template_shortname,
                retrieve_json_payload: true,
                retrieve_attachments: false,
                validate_schema: false,
            },
            scope
        );
        // No attachments were requested, so the SDK's loosely typed
        // `attachments` is dropped rather than reinterpreted.
        return response ? { ...response, attachments: undefined } : null;
    } catch (error) {
        log.error(`Failed to fetch template ${template_shortname} from ${space_name}:`, error);
        return null;
    }
}

/**
 * Fetches templates from all managed spaces that have a /templates folder
 */
export async function getAllTemplates(): Promise<ApiQueryResponse> {
    // First get all spaces (ignoreFilter=true to include "applications")
    const spacesResponse = await getSpaces(true, DmartScope.managed, []);
    if (spacesResponse.status !== "success" || !spacesResponse.records) {
        return {
            status: "success",
            records: [],
            attributes: { total: 0, returned: 0 },
        } as ApiQueryResponse;
    }

    const allTemplates: ApiResponseRecord[] = [];
    const spaces = spacesResponse.records;

    // Fetch templates from each space
    for (const space of spaces) {
        try {
            const response = await getTemplates(space.shortname, DmartScope.managed, MAX_QUERY_LIMIT);
            if (response.status === "success" && response.records) {
                // Add space_name info to each template
                const templatesWithSpace = response.records.map((t) => ({
                    ...t,
                    attributes: {
                        ...t.attributes,
                        space_name: space.shortname,
                    },
                }));
                allTemplates.push(...templatesWithSpace);
            }
        } catch (error) {
            log.warn(`Failed to fetch templates from ${space.shortname}:`, error);
        }
    }

    return {
        status: "success",
        records: allTemplates,
        attributes: { total: allTemplates.length },
    } as ApiQueryResponse;
}

export async function ensureTemplatesFolder(spaceName: string): Promise<boolean> {
    try {
        // Check if /templates folder exists
        const checkResponse = await Dmart.query(
            {
                type: QueryType.search,
                space_name: spaceName,
                subpath: "/",
                search: "@shortname:templates",
                limit: 1,
            },
            DmartScope.managed
        );

        if (checkResponse && checkResponse.status === "success" && checkResponse.records.length > 0) {
            return true; // Folder already exists
        }

        // Create the /templates folder
        const createRequest: ActionRequest = {
            space_name: spaceName,
            request_type: RequestType.create,
            records: [
                {
                    resource_type: ResourceType.folder,
                    shortname: "templates",
                    subpath: "/",
                    attributes: {
                        is_active: true,
                        displayname: { en: "Templates", ar: "القوالب", ku: "داڕێژەکان" },
                    },
                },
            ],
        };

        const response = await Dmart.request(createRequest);
        return response.status === "success";
    } catch (error) {
        log.error("Error ensuring templates folder:", error);
        return false;
    }
}

export async function createTemplate(
    shortname: string,
    data: {
        title: string;
        content: string;
        space_name?: string;
        schema_shortname?: string;
    }
) {
    const body: TemplateBody = {
        title: data.title,
        content: data.content,
    };

    // Add optional fields if provided
    if (data.space_name) {
        body.space_name = data.space_name;
    }
    if (data.schema_shortname) {
        body.schema_shortname = data.schema_shortname;
    }

    // Determine target space and subpath
    // If schema is provided, save in that space's /templates folder
    // Otherwise, default to "applications" space
    const targetSpace = data.schema_shortname && data.space_name
        ? data.space_name
        : APPLICATIONS_SPACE;

    // Ensure templates folder exists in target space (create if not exists)
    const folderCreated = await ensureTemplatesFolder(targetSpace);
    if (!folderCreated) {
        log.error("Failed to create templates folder in", targetSpace);
        return false;
    }

    // Ensure templates schema exists in applications space (create if not exists)
    try {
        await createTemplatesSchema(DmartScope.managed);
    } catch (error) {
        log.error("Failed to create templates schema:", error);
        // Continue anyway - the schema might already exist
    }

    const request: ActionRequest = {
        space_name: targetSpace,
        request_type: RequestType.create,
        records: [
            {
                resource_type: ResourceType.content,
                shortname: shortname,
                subpath: "/templates",
                attributes: {
                    is_active: true,
                    schema_shortname: "templates",
                    payload: {
                        content_type: ContentType.json,
                        body,
                    },
                },
            },
        ],
    };
    const response: ActionResponse = await Dmart.request(request);
    return response.status === "success" && response.records.length > 0;
}

/** What an edit of a template may change. */
export type TemplateUpdate = {
    title: string;
    content: string;
    is_active?: boolean;
    displayname?: LocalizedText;
    tags?: string[];
};

export async function updateTemplates(
    shortname: string,
    space_name: string,
    subpath: string,
    data: TemplateUpdate
) {
    const attributes = {
        is_active: data.is_active,
        displayname: data.displayname,
        relationships: [],
        tags: data.tags,
        payload: {
            content_type: ContentType.json,
            body: {
                title: data.title,
                content: data.content,
            },
        },
    };

    const actionRequest = {
        space_name,
        request_type: RequestType.update,
        records: [
            {
                resource_type: ResourceType.content,
                shortname,
                subpath,
                attributes,
            },
        ],
    };

    const response = await Dmart.request(actionRequest);
    return response.status === "success"
        ? shortname
        : null;
}

export async function deleteTemplate(
    shortname: string,
    spaceName: string,
    subpath: string
) {
    const actionRequest: ActionRequest = {
        space_name: spaceName,
        request_type: RequestType.delete,
        records: [
            {
                resource_type: ResourceType.content,
                shortname: shortname,
                subpath: subpath,
                attributes: {},
            },
        ],
    };

    const response: ActionResponse = await Dmart.request(actionRequest);
    return response.status === "success";
}

/** A template carried as an attachment of a schema. */
export interface SchemaTemplate {
    shortname: string;
    title: string;
    schema: string;
    description: string;
    attachment_data?: EntryRecord;
}

/**
 * The attachment list of a schema record, when it is stored as a list
 * (either at the top level or under `attributes`).
 */
function attachmentListOf(schemaRecord: EntryRecord): EntryRecord[] | null {
    const attributes: JsonObject = schemaRecord.attributes;
    const raw: unknown = schemaRecord.attachments ?? attributes.attachments;
    const attachments: EntryRecord[] | null = Array.isArray(raw) ? raw : null;
    return attachments;
}

function isMarkdownAttachment(attachment: EntryRecord): boolean {
    const contentType = attachment.attributes?.payload?.content_type;
    return contentType === "markdown" || contentType === "md";
}

/** The first non-empty string among `values`. */
function firstText(...values: unknown[]): string | undefined {
    for (const value of values) {
        if (typeof value === "string" && value) return value;
    }
    return undefined;
}

/**
 * Reads the template content out of a `template` attachment. The content
 * may be stored as a plain string, or as an object with `content`/`body`.
 */
function templateFromAttachment(templateAttachment: EntryRecord): SchemaTemplate | null {
    const attributes = templateAttachment.attributes;
    const templateBody = attributes?.payload?.body;

    if (!templateBody) {
        return null;
    }

    const attributeTitle = firstText(
        attributes?.displayname?.en,
        (attributes as JsonObject).title,
    );

    // Handle different formats of template storage
    let templateContent: string;
    let templateTitle: string;

    if (typeof templateBody === "string") {
        // Direct string content
        templateContent = templateBody;
        templateTitle = attributeTitle || "Template";
    } else if (isJsonObject(templateBody)) {
        // Object format with content property
        templateContent =
            firstText(templateBody.content, templateBody.body) || JSON.stringify(templateBody);
        templateTitle = firstText(templateBody.title) || attributeTitle || "Template";
    } else {
        return null;
    }

    return {
        shortname: "template",
        title: templateTitle,
        schema: templateContent,
        description: attributes?.description?.en || "",
        attachment_data: templateAttachment,
    };
}

/**
 * Extracts template from schema attachments
 * Schemas can have attachments, and if a schema has an attachment with shortname 'template',
 * it is treated as the template for that schema.
 *
 * @param schemaRecord - The schema record object from getSpaceSchema response
 * @returns The template object if found, null otherwise
 */
export function getTemplateFromSchemaAttachment(
    schemaRecord: EntryRecord | null | undefined,
): SchemaTemplate | null {
    if (!schemaRecord) {
        return null;
    }

    const attachments = attachmentListOf(schemaRecord);
    if (!attachments) {
        return null;
    }

    // Find attachment with shortname 'template'
    const templateAttachment = attachments.find((att) => att.shortname === "template");
    if (!templateAttachment) {
        return null;
    }

    return templateFromAttachment(templateAttachment);
}

/**
 * Checks if a schema has a template attachment
 *
 * @param schemaRecord - The schema record object
 * @returns true if the schema has a 'template' attachment
 */
export function hasTemplateAttachment(schemaRecord: EntryRecord | null | undefined): boolean {
    if (!schemaRecord) {
        return false;
    }

    const attachments = attachmentListOf(schemaRecord);
    if (!attachments) {
        return false;
    }

    return attachments.some((att) => att.shortname === "template");
}

/**
 * Checks if a schema has a markdown template attachment
 * A markdown template attachment has shortname 'template' and content_type of 'markdown' or 'md'
 *
 * @param schemaRecord - The schema record object
 * @returns true if the schema has a markdown 'template' attachment
 */
export function hasMarkdownTemplateAttachment(schemaRecord: EntryRecord | null | undefined): boolean {
    if (!schemaRecord) {
        return false;
    }

    const attachments = attachmentListOf(schemaRecord);
    if (!attachments) {
        return false;
    }

    const templateAttachment = attachments.find((att) => att.shortname === "template");
    if (!templateAttachment) {
        return false;
    }

    return isMarkdownAttachment(templateAttachment);
}

/**
 * Extracts markdown template from schema attachments
 * Similar to getTemplateFromSchemaAttachment but specifically for markdown templates
 *
 * @param schemaRecord - The schema record object from getSpaceSchema response
 * @returns The markdown template object if found, null otherwise
 */
export function getMarkdownTemplateFromSchemaAttachment(
    schemaRecord: EntryRecord | null | undefined,
): SchemaTemplate | null {
    if (!schemaRecord) {
        return null;
    }

    const attachments = attachmentListOf(schemaRecord);
    if (!attachments) {
        return null;
    }

    // Find attachment with shortname 'template' and markdown content type
    const templateAttachment = attachments.find(
        (att) => att.shortname === "template" && isMarkdownAttachment(att)
    );
    if (!templateAttachment) {
        return null;
    }

    return templateFromAttachment(templateAttachment);
}
