import {
    type ApiQueryResponse,
    ContentType,
    Dmart,
    QueryType,
    RequestType,
    DmartScope,
    ResourceType,
    SortType,
} from "@edraj/tsdmart";
import type { Translation } from "@edraj/tsdmart/dmart.model";
import { log } from "@/lib/logger";
import { MANAGEMENT_SPACE, APPLICATIONS_SPACE, DEFAULT_SPACE_ORDINAL } from "@/lib/constants";
import { getCurrentScope } from "@/stores/user";

export async function getSpaces(
    ignoreFilter = false,
    scope: DmartScope = DmartScope.managed,
    hiddenspaces: string[] = []
): Promise<ApiQueryResponse> {
    const _spaces: any = await Dmart.query(
        {
            type: QueryType.spaces,
            space_name: MANAGEMENT_SPACE,
            subpath: "/",
            search: "",
            limit: 100,
        },
        scope
    );

    if (ignoreFilter === false) {
        _spaces.records = _spaces.records.filter((e: any) => !e.attributes.hide_space);
        hiddenspaces.forEach((space) => {
            _spaces.records = _spaces.records.filter(
                (e: any) => !e.shortname.includes(space)
            );
        });
        _spaces.records = _spaces.records.filter(
            (e: any) => !e.shortname.includes(APPLICATIONS_SPACE)
        );
    }

    _spaces.records = _spaces.records.map((e: any) => {
        if (e.attributes.ordinal === null) {
            e.attributes.ordinal = DEFAULT_SPACE_ORDINAL;
        }
        return e;
    });

    _spaces.records.sort((a: any, b: any) => a.attributes.ordinal - b.attributes.ordinal);

    return _spaces;
}

/**
 * Returns the space-level `hide_folders` list (shortnames to suppress from
 * any folder listing of this space). Resolves to an empty array on any
 * failure so callers don't need a try/catch.
 */
export async function getSpaceHideFolders(
    spaceName: string,
    scope: DmartScope = DmartScope.managed
): Promise<string[]> {
    try {
        const response = await getSpaces(false, scope);
        const match = response.records.find(
            (record: any) => record.shortname === spaceName
        );
        const hide = (match as any)?.attributes?.hide_folders;
        return Array.isArray(hide) ? hide : [];
    } catch (error) {
        log.debug("Could not resolve space hide_folders:", error);
        return [];
    }
}

/**
 * Builds the Redisearch negation fragment that excludes a set of shortnames.
 * Returns an empty string when the list is empty.
 *   ["foo", "bar"] -> "-@shortname:foo|bar"
 */
export function buildHideFoldersSearch(hideFolders: string[] | null | undefined): string {
    const names = (hideFolders ?? []).filter(
        (n): n is string => typeof n === "string" && n.length > 0,
    );
    return names.length > 0 ? `-@shortname:${names.join("|")}` : "";
}

/**
 * Joins non-empty search fragments with a single space (DMart ANDs them).
 */
export function mergeSearch(...parts: Array<string | undefined | null>): string {
    return parts
        .filter((p): p is string => !!p && p.trim().length > 0)
        .map((p) => p.trim())
        .join(" ");
}

export async function getSpaceContents(
    spaceName: string,
    subpath = "/",
    scope: DmartScope,
    limit = 100,
    offset = 0,
    exact_subpath = false,
    queryType: QueryType = QueryType.search,
    search = ""
): Promise<ApiQueryResponse> {
    let searchQuery = search;
    if (!searchQuery && scope === DmartScope.public) {
        searchQuery = "-@shortname:schema";
    }
    return (await Dmart.query(
        {
            type: queryType,
            space_name: spaceName,
            subpath: subpath,
            search: searchQuery,
            limit: limit,
            sort_by: "shortname",
            sort_type: SortType.ascending,
            offset: offset,
            retrieve_json_payload: true,
            retrieve_attachments: true,
            exact_subpath: exact_subpath,
        },
        scope
    ))!;
}

export async function getRelatedContents(
    spaceName: string,
    subpath = "/",
    scope: DmartScope,
    currentTags: string[] = [],
    editorShortname?: string,
    limit = 10,
    offset = 0
): Promise<ApiQueryResponse> {
    let searchQuery = "-@shortname:" + editorShortname;

    return (await Dmart.query(
        {
            type: QueryType.search,
            space_name: spaceName,
            subpath: subpath,
            search: searchQuery,
            limit: limit,
            sort_by: "updated_at",
            sort_type: SortType.descending,
            offset: offset,
            retrieve_json_payload: true,
            retrieve_attachments: false,
            exact_subpath: false,
        },
        scope
    ))!;
}

export async function getSpaceFolders(
    spaceName: string,
    subpath = "/",
    scope: DmartScope,
    limit = 100,
    offset = 0
): Promise<ApiQueryResponse> {
    return (await Dmart.query(
        {
            type: QueryType.search,
            space_name: spaceName,
            subpath: subpath,
            search: "-@shortname:schema",
            limit: limit,
            sort_by: "shortname",
            sort_type: SortType.ascending,
            offset: offset,
            retrieve_json_payload: true,
            retrieve_attachments: true,
            exact_subpath: false,
            filter_types: [ResourceType.folder],
        },
        scope
    ))!;
}

export async function getSpaceSchema(
    spaceName: string,
    scope: DmartScope,
    limit = 100,
    offset = 0
): Promise<ApiQueryResponse> {
    return (await Dmart.query(
        {
            type: QueryType.search,
            space_name: spaceName,
            subpath: '/schema',
            search: "",
            limit: limit,
            sort_by: "shortname",
            sort_type: SortType.ascending,
            offset: offset,
            retrieve_json_payload: true,
            retrieve_attachments: true,
            exact_subpath: false,
        },
        scope
    ))!;
}

export async function getSpaceContentsByTags(
    spaceName: string,
    subpath = "/",
    scope: DmartScope,
    limit = 100,
    offset = 0,
    tags: string[] = []
): Promise<ApiQueryResponse> {
    let searchQuery = "";
    if (tags.length > 0) {
        const tagQuery = tags.map((tag) => `${tag}`).join(" OR ");
        searchQuery = `@tags:${tagQuery}`;
    }

    return (await Dmart.query(
        {
            type: QueryType.search,
            space_name: spaceName,
            subpath: subpath,
            search: searchQuery,
            limit: limit,
            sort_by: "shortname",
            sort_type: SortType.ascending,
            offset: offset,
            retrieve_json_payload: true,
            retrieve_attachments: true,
            exact_subpath: false,
        },
        scope
    ))!;
}

export async function getSpaceTags(
    spaceName: string
): Promise<ApiQueryResponse> {
    return (await Dmart.query(
        {
            type: QueryType.tags,
            space_name: spaceName,
            subpath: "/",
            search: "",
            limit: 10,
            sort_by: "",
            sort_type: SortType.ascending,
            offset: 0,
            retrieve_json_payload: true,
            retrieve_attachments: true,
            exact_subpath: false,
        },
        getCurrentScope()
    ))!;
}

export async function getChildren(
    space_name: string,
    subpath: string,
    limit: number = 20,
    offset: number = 0,
    restrict_types: Array<ResourceType> = [],
    spaces: any = null,
    ignoreFilter = false
): Promise<ApiQueryResponse> {
    let hideSearch = "";
    if (!ignoreFilter && spaces !== null) {
        const selectedSpace = spaces.records.find(
            (record: any) => record.shortname === space_name
        );
        hideSearch = buildHideFoldersSearch(
            selectedSpace?.attributes?.hide_folders ?? [],
        );
    }

    const folders = await Dmart.query({
        type: QueryType.search,
        space_name: space_name,
        subpath: subpath,
        filter_types: restrict_types,
        exact_subpath: true,
        search: hideSearch,
        limit: limit,
        offset: offset,
    });

    if (folders) {
    folders.records = folders.records.sort((leftSide, rightSide) => {
        if (leftSide.shortname.toLowerCase() < rightSide.shortname.toLowerCase())
            return -1;
        if (leftSide.shortname.toLowerCase() > rightSide.shortname.toLowerCase())
            return 1;
        return 0;
    });
    }
    return folders!;
}

export async function getChildrenAndSubChildren(
    subpathsPTR: any,
    spacename: string,
    base: string,
    _subpaths: any
) {
    for (const _subpath of _subpaths.records) {
        if (_subpath.resource_type === "folder") {
            const childSubpaths = await getChildren(spacename, _subpath.shortname);
            await getChildrenAndSubChildren(
                subpathsPTR,
                spacename,
                `${base}/${_subpath.shortname}`,
                childSubpaths
            );
            subpathsPTR.push(`${base}/${_subpath.shortname}`);
        }
    }
}

export async function createSpace({
    shortname,
    displayname,
    description,
}: {
    shortname: string;
    displayname: Translation;
    description: Translation;
}) {
    try {
        const response = await Dmart.request({
            space_name: shortname.trim(),
            request_type: RequestType.create,
            records: [
                {
                    resource_type: ResourceType.space,
                    shortname: shortname.trim(),
                    subpath: "/",
                    attributes: {
                        is_active: true,
                        displayname: displayname,
                        description: description,
                    },
                },
            ],
        });
        await getSpaces();
        return response.status;
    } catch (error) {
        log.error(`Error creating space "${shortname}":`, error);
        throw error;
    }
}

export async function deleteSpace(shortname: string) {
    try {
        await Dmart.request({
            space_name: shortname,
            request_type: RequestType.delete,
            records: [
                {
                    resource_type: ResourceType.space,
                    shortname: shortname,
                    subpath: "/",
                    attributes: {},
                },
            ],
        });
        await getSpaces();
    } catch (error) {
        log.error(`Error deleting space "${shortname}":`, error);
        throw error;
    }
}

export async function editSpace(
    shortname: string,
    attributes: Record<string, any>
) {
    try {
        await Dmart.request({
            space_name: shortname,
            request_type: RequestType.update,
            records: [
                {
                    resource_type: ResourceType.space,
                    shortname: shortname,
                    subpath: "/",
                    attributes: attributes,
                },
            ],
        });
        await getSpaces();
    } catch (error) {
        log.error(`Error editing space "${shortname}":`, error);
        throw error;
    }
}

/**
 * Helper function to check if an error indicates "not found"
 */
function isNotFoundError(error: any): boolean {
    // Check for various indicators of "not found" errors
    if (error?.status === 404) return true;
    if (error?.status === 400 && error?.message?.includes("not found")) return true;
    if (error?.message?.includes("entry not found")) return true;
    if (error?.message?.includes("does not exist")) return true;
    if (error?.message?.includes("not_found")) return true;
    return false;
}

/**
 * Check if an entry exists, and create it if it doesn't.
 * Deduplicates the repeated check-then-create pattern used for schemas and workflows.
 *
 * @param checkConfig - Configuration for the existence check
 * @param createConfig - Configuration for creation if the entry doesn't exist
 * @param label - Human-readable label for logging
 * @param scope - API scope
 * @returns true if the entry exists or was created successfully
 */
async function ensureEntryExists(
    checkConfig: {
        resourceType: ResourceType;
        spaceName: string;
        subpath: string;
        shortname: string;
    },
    createConfig: {
        resourceType: ResourceType;
        spaceName: string;
        subpath: string;
        shortname: string;
        attributes: Record<string, any>;
    },
    label: string,
    scope: DmartScope = DmartScope.managed
): Promise<boolean> {
    try {
        // Check if entry already exists
        try {
            await Dmart.retrieveEntry(
                {
                    resource_type: checkConfig.resourceType,
                    space_name: checkConfig.spaceName,
                    subpath: checkConfig.subpath,
                    shortname: checkConfig.shortname,
                    retrieve_json_payload: false,
                    retrieve_attachments: false,
                    validate_schema: false,
                },
                scope
            );
            // Entry already exists
            log.debug(`${label} already exists, skipping creation`);
            return true;
        } catch (error: any) {
            // Entry not found, proceed to create
            if (!isNotFoundError(error)) {
                log.error(`Error checking ${label} existence:`, error);
            }
        }

        // Create the entry
        const response = await Dmart.request({
            space_name: createConfig.spaceName,
            request_type: RequestType.create,
            records: [
                {
                    resource_type: createConfig.resourceType,
                    shortname: createConfig.shortname,
                    subpath: createConfig.subpath,
                    attributes: createConfig.attributes,
                },
            ],
        });
        return response.status === "success";
    } catch (error) {
        log.error(`Error creating ${label}:`, error);
        return false;
    }
}

/**
 * Check if required application folders exist in the applications space
 * Returns an object with missing folders
 */
export async function checkApplicationsFolders(
    scope: DmartScope = DmartScope.managed
): Promise<{ exists: boolean; missing: string[]; missingWorkflow?: boolean; missingReportSchema?: boolean; missingWorkflowSchema?: boolean; missingMetaSchema?: boolean; missingTemplatesSchema?: boolean; error?: string }> {
    const requiredFolders = ["reports", "polls", "surveys", "workflows", "schema"];
    const missing: string[] = [];
    let missingWorkflow = false;
    let missingReportSchema = false;
    let missingWorkflowSchema = false;
    let missingMetaSchema = false;
    let missingTemplatesSchema = false;

    try {
        for (const folder of requiredFolders) {
            try {
                await Dmart.retrieveEntry(
                    {
                        validate_schema: false,
                        resource_type: ResourceType.folder,
                        space_name: APPLICATIONS_SPACE,
                        subpath: "/",
                        shortname: folder,
                        retrieve_json_payload: false,
                        retrieve_attachments: false
                    },
                    scope
                );
                // Folder exists
            } catch (error: any) {
                // If we get a 403 or permission error, we ignore it
                if (error?.status === 403 || error?.message?.includes("permission")) {
                    return { exists: true, missing: [], error: "permission_denied" };
                }
                // Only mark as missing if it's a "not found" error
                if (isNotFoundError(error)) {
                    missing.push(folder);
                }
            }
        }

        // Check if report_workflow exists in workflows folder
        if (!missing.includes("workflows")) {
            try {
                await Dmart.retrieveEntry(
                    {
                        validate_schema: false,
                        resource_type: ResourceType.content,
                        space_name: APPLICATIONS_SPACE,
                        subpath: "/workflows",
                        shortname: "report_workflow",
                        retrieve_json_payload: false,
                        retrieve_attachments: false,
                    },
                    scope
                );
                // Workflow exists
            } catch (error: any) {
                // If permission error, ignore
                if (error?.status === 403 || error?.message?.includes("permission")) {
                    missingWorkflow = false;
                } else if (isNotFoundError(error)) {
                    missingWorkflow = true;
                }
                // Other errors don't mark as missing (assume exists to avoid unnecessary creation)
            }
        } else {
            // If workflows folder is missing, workflow is also missing
            missingWorkflow = true;
        }

        // Check if schemas exist in schema folder
        if (!missing.includes("schema")) {
            // Check report schema
            try {
                await Dmart.retrieveEntry(
                    {
                        validate_schema: false,
                        resource_type: ResourceType.schema,
                        space_name: APPLICATIONS_SPACE,
                        subpath: "/schema",
                        shortname: "report",
                        retrieve_json_payload: false,
                        retrieve_attachments: false,
                    },
                    scope
                );
                // Report schema exists
            } catch (error: any) {
                // If permission error, ignore
                if (error?.status === 403 || error?.message?.includes("permission")) {
                    missingReportSchema = false;
                } else if (isNotFoundError(error)) {
                    missingReportSchema = true;
                }
                // Other errors don't mark as missing
            }

            // Check workflow schema
            try {
                await Dmart.retrieveEntry(
                    {
                        validate_schema: false,
                        resource_type: ResourceType.schema,
                        space_name: APPLICATIONS_SPACE,
                        subpath: "/schema",
                        shortname: "workflow",
                        retrieve_json_payload: false,
                        retrieve_attachments: false,
                    },
                    scope
                );
                // Workflow schema exists
            } catch (error: any) {
                // If permission error, ignore
                if (error?.status === 403 || error?.message?.includes("permission")) {
                    missingWorkflowSchema = false;
                } else if (isNotFoundError(error)) {
                    missingWorkflowSchema = true;
                }
                // Other errors don't mark as missing
            }

            // Check meta_schema (the schema used for template entries)
            try {
                await Dmart.retrieveEntry(
                    {
                        validate_schema: false,
                        resource_type: ResourceType.schema,
                        space_name: APPLICATIONS_SPACE,
                        subpath: "/schema",
                        shortname: "meta_schema",
                        retrieve_json_payload: false,
                        retrieve_attachments: false,
                    },
                    scope
                );
                // Meta schema exists
            } catch (error: any) {
                // If permission error, ignore
                if (error?.status === 403 || error?.message?.includes("permission")) {
                    missingMetaSchema = false;
                } else if (isNotFoundError(error)) {
                    missingMetaSchema = true;
                }
                // Other errors don't mark as missing
            }

            // Check templates schema (the schema used for template definitions)
            try {
                await Dmart.retrieveEntry(
                    {
                        validate_schema: false,
                        resource_type: ResourceType.schema,
                        space_name: APPLICATIONS_SPACE,
                        subpath: "/schema",
                        shortname: "templates",
                        retrieve_json_payload: false,
                        retrieve_attachments: false,
                    },
                    scope
                );
                // Templates schema exists
            } catch (error: any) {
                // If permission error, ignore
                if (error?.status === 403 || error?.message?.includes("permission")) {
                    missingTemplatesSchema = false;
                } else if (isNotFoundError(error)) {
                    missingTemplatesSchema = true;
                }
                // Other errors don't mark as missing
            }
        } else {
            // If schema folder is missing, schemas are also missing
            missingReportSchema = true;
            missingWorkflowSchema = true;
            missingMetaSchema = true;
            missingTemplatesSchema = true;
        }

        const result = { 
            exists: missing.length === 0 && !missingWorkflow && !missingReportSchema && !missingWorkflowSchema && !missingMetaSchema && !missingTemplatesSchema, 
            missing, 
            missingWorkflow, 
            missingReportSchema,
            missingWorkflowSchema,
            missingMetaSchema,
            missingTemplatesSchema
        };
        log.debug("checkApplicationsFolders result:", result);
        return result;
    } catch (error: any) {
        // If we get a 403 or permission error, we ignore it
        if (error?.status === 403 || error?.message?.includes("permission")) {
            return { exists: true, missing: [], error: "permission_denied" };
        }
        return { exists: false, missing: requiredFolders, missingWorkflow: true, missingReportSchema: true, missingWorkflowSchema: true, missingMetaSchema: true, missingTemplatesSchema: true, error: error?.message };
    }
}

/**
 * Create a folder in a space
 */
export async function createFolder(
    spaceName: string,
    folderShortname: string,
    displayname: Translation,
    description: Translation,
    scope: DmartScope = DmartScope.managed
): Promise<boolean> {
    try {
        // Check if folder already exists
        try {
            await Dmart.retrieveEntry(
                {
                    resource_type: ResourceType.folder,
                    space_name: spaceName,
                    subpath: "/",
                    shortname: folderShortname,
                    retrieve_json_payload: false,
                    retrieve_attachments: false,
                    validate_schema: false,
                },
                scope
            );
            // Folder already exists, return success
            log.debug(`Folder ${folderShortname} already exists, skipping creation`);
            return true;
        } catch (error: any) {
            // Entry not found, proceed to create
            if (!isNotFoundError(error)) {
                log.error(`Error checking folder ${folderShortname} existence:`, error);
            }
        }

        const response = await Dmart.request({
            space_name: spaceName,
            request_type: RequestType.create,
            records: [
                {
                    resource_type: ResourceType.folder,
                    shortname: folderShortname,
                    subpath: "/",
                    attributes: {
                        is_active: true,
                        displayname: displayname,
                        description: description,
                    },
                },
            ],
        });
        return response.status === "success";
    } catch (error) {
        log.error(`Error creating folder ${folderShortname}:`, error);
        return false;
    }
}

/**
 * Create missing application folders
 */
export async function createApplicationsFolders(
    scope: DmartScope = DmartScope.managed
): Promise<{ 
    success: boolean; 
    created: string[]; 
    failed: string[]; 
    workflowCreated?: boolean; 
    workflowFailed?: boolean; 
    reportSchemaCreated?: boolean; 
    reportSchemaFailed?: boolean;
    workflowSchemaCreated?: boolean;
    workflowSchemaFailed?: boolean;
    metaSchemaCreated?: boolean;
    metaSchemaFailed?: boolean;
    templatesSchemaCreated?: boolean;
    templatesSchemaFailed?: boolean;
}> {
    const foldersToCreate = [
        {
            shortname: "reports",
            displayname: { en: "Reports", ar: "التقارير", ku: "" },
            description: { en: "User reports management", ar: "إدارة تقارير المستخدمين", ku: "" },
        },
        {
            shortname: "polls",
            displayname: { en: "Polls", ar: "استطلاعات الرأي", ku: "" },
            description: { en: "Community polls", ar: "استطلاعات الرأي المجتمعية", ku: "" },
        },
        {
            shortname: "surveys",
            displayname: { en: "Surveys", ar: "الاستبيانات", ku: "" },
            description: { en: "User surveys", ar: "استبيانات المستخدمين", ku: "" },
        },
        {
            shortname: "workflows",
            displayname: { en: "Workflows", ar: "سير العمل", ku: "" },
            description: { en: "Application workflows", ar: "سير العمل للتطبيقات", ku: "" },
        },
        {
            shortname: "schema",
            displayname: { en: "Schemas", ar: "المخططات", ku: "" },
            description: { en: "Application schemas", ar: "مخططات التطبيقات", ku: "" },
        },
    ];

    const created: string[] = [];
    const failed: string[] = [];

    for (const folder of foldersToCreate) {
        const success = await createFolder(
            APPLICATIONS_SPACE,
            folder.shortname,
            folder.displayname,
            folder.description,
            scope
        );
        if (success) {
            created.push(folder.shortname);
        } else {
            failed.push(folder.shortname);
        }
    }

    // Create report_workflow if workflows folder was created or already exists
    let workflowCreated = false;
    let workflowFailed = false;
    log.debug("Checking if workflows folder failed:", failed.includes("workflows"));
    if (!failed.includes("workflows")) {
        log.debug("Creating report_workflow...");
        try {
            const workflowSuccess = await createReportWorkflow(scope);
            log.debug("report_workflow creation result:", workflowSuccess);
            if (workflowSuccess) {
                workflowCreated = true;
            } else {
                workflowFailed = true;
            }
        } catch (error) {
            log.error("Error creating report_workflow:", error);
            workflowFailed = true;
        }
    } else {
        log.debug("Skipping report_workflow creation because workflows folder failed");
    }

    // Create report schema if schema folder was created or already exists
    let reportSchemaCreated = false;
    let reportSchemaFailed = false;
    log.debug("Checking if schema folder failed:", failed.includes("schema"));
    if (!failed.includes("schema")) {
        log.debug("Creating report schema...");
        try {
            const schemaSuccess = await createReportSchema(scope);
            log.debug("report schema creation result:", schemaSuccess);
            if (schemaSuccess) {
                reportSchemaCreated = true;
            } else {
                reportSchemaFailed = true;
            }
        } catch (error) {
            log.error("Error creating report schema:", error);
            reportSchemaFailed = true;
        }
    } else {
        log.debug("Skipping report schema creation because schema folder failed");
    }

    // Create workflow schema if schema folder was created or already exists
    let workflowSchemaCreated = false;
    let workflowSchemaFailed = false;
    if (!failed.includes("schema")) {
        log.debug("Creating workflow schema...");
        try {
            const workflowSchemaSuccess = await createWorkflowSchema(scope);
            log.debug("workflow schema creation result:", workflowSchemaSuccess);
            if (workflowSchemaSuccess) {
                workflowSchemaCreated = true;
            } else {
                workflowSchemaFailed = true;
            }
        } catch (error) {
            log.error("Error creating workflow schema:", error);
            workflowSchemaFailed = true;
        }
    } else {
        log.debug("Skipping workflow schema creation because schema folder failed");
    }

    // Create meta_schema if schema folder was created or already exists
    let metaSchemaCreated = false;
    let metaSchemaFailed = false;
    if (!failed.includes("schema")) {
        log.debug("Creating meta_schema...");
        try {
            const metaSchemaSuccess = await createMetaSchema(scope);
            log.debug("meta_schema creation result:", metaSchemaSuccess);
            if (metaSchemaSuccess) {
                metaSchemaCreated = true;
            } else {
                metaSchemaFailed = true;
            }
        } catch (error) {
            log.error("Error creating meta_schema:", error);
            metaSchemaFailed = true;
        }
    } else {
        log.debug("Skipping meta_schema creation because schema folder failed");
    }

    // Create templates schema if schema folder was created or already exists
    let templatesSchemaCreated = false;
    let templatesSchemaFailed = false;
    if (!failed.includes("schema")) {
        log.debug("Creating templates schema...");
        try {
            const templatesSchemaSuccess = await createTemplatesSchema(scope);
            log.debug("templates schema creation result:", templatesSchemaSuccess);
            if (templatesSchemaSuccess) {
                templatesSchemaCreated = true;
            } else {
                templatesSchemaFailed = true;
            }
        } catch (error) {
            log.error("Error creating templates schema:", error);
            templatesSchemaFailed = true;
        }
    } else {
        log.debug("Skipping templates schema creation because schema folder failed");
    }

    return { 
        success: failed.length === 0 && !workflowFailed && !reportSchemaFailed && !workflowSchemaFailed && !metaSchemaFailed && !templatesSchemaFailed, 
        created, 
        failed, 
        workflowCreated, 
        workflowFailed, 
        reportSchemaCreated, 
        reportSchemaFailed,
        workflowSchemaCreated,
        workflowSchemaFailed,
        metaSchemaCreated,
        metaSchemaFailed,
        templatesSchemaCreated,
        templatesSchemaFailed
    };
}

/**
 * Create the report_workflow entry in the workflows folder
 */
export async function createReportWorkflow(scope: DmartScope = DmartScope.managed): Promise<boolean> {
    const workflowPayload = {
        name: "",
        states: [
            {
                name: "Pending",
                next: [
                    {
                        roles: ["super_admin"],
                        state: "accepted",
                        action: "accept"
                    },
                    {
                        roles: ["super_admin"],
                        state: "investigation",
                        action: "investigate"
                    },
                    {
                        roles: ["super_admin"],
                        state: "refused",
                        action: "refuse"
                    }
                ],
                state: "pending",
                resolutions: []
            },
            {
                name: "Investigation",
                next: [
                    {
                        roles: ["super_admin"],
                        state: "accepted",
                        action: "accept"
                    },
                    {
                        roles: ["super_admin"],
                        state: "refused",
                        action: "refuse"
                    }
                ],
                state: "investigation",
                resolutions: []
            },
            {
                name: "Accepted",
                next: [],
                state: "accepted",
                resolutions: [
                    {
                        ar: "التعامل معها",
                        en: "Handled",
                        key: "handled"
                    }
                ]
            },
            {
                name: "Refused",
                next: [],
                state: "refused",
                resolutions: [
                    {
                        ar: "البريد العشوائي",
                        en: "Spamming",
                        key: "spam"
                    }
                ]
            }
        ],
        illustration: "",
        initial_state: [
            {
                name: "pending",
                roles: ["super_admin"]
            }
        ]
    };

    return ensureEntryExists(
        { resourceType: ResourceType.content, spaceName: APPLICATIONS_SPACE, subpath: "/workflows", shortname: "report_workflow" },
        {
            resourceType: ResourceType.content, spaceName: APPLICATIONS_SPACE, subpath: "workflows", shortname: "report_workflow",
            attributes: {
                is_active: true,
                displayname: { en: "Report Workflow", ar: "سير عمل التقارير", ku: "" },
                description: { en: "Workflow for managing user reports", ar: "سير العمل لإدارة تقارير المستخدمين", ku: "" },
                payload: { content_type: ContentType.json, body: workflowPayload },
            },
        },
        "report_workflow",
        scope
    );
}


/**
 * Create the report schema in the schema folder
 */
export async function createReportSchema(scope: DmartScope = DmartScope.managed): Promise<boolean> {
    const schemaPayload = {
        type: "object",
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        required: [
            "entry",
            "space_name",
            "subpath",
            "report_type"
        ],
        properties: {
            entry: {
                type: "string",
                title: "",
                description: "Identifier for the entry"
            },
            subpath: {
                type: "string",
                title: "",
                description: "Relative path within the space"
            },
            space_name: {
                type: "string",
                title: "",
                description: "Logical space or namespace where the entry exists"
            },
            report_type: {
                type: "string",
                title: "",
                description: "Type of report being submitted"
            }
        },
        additionalProperties: false
    };

    return ensureEntryExists(
        { resourceType: ResourceType.schema, spaceName: APPLICATIONS_SPACE, subpath: "/schema", shortname: "report" },
        {
            resourceType: ResourceType.schema, spaceName: APPLICATIONS_SPACE, subpath: "schema", shortname: "report",
            attributes: {
                is_active: true,
                displayname: { en: "Report Schema", ar: "مخطط التقرير", ku: "" },
                description: { en: "Schema for user reports", ar: "المخطط لتقارير المستخدمين", ku: "" },
                schema_shortname: "meta_schema",
                payload: {
                    content_type: ContentType.json,
                    body: schemaPayload,
                },
            },
        },
        "report schema",
        scope
    );
}


/**
 * Create the workflow schema in the schema folder
 */
export async function createWorkflowSchema(scope: DmartScope = DmartScope.managed): Promise<boolean> {
    const schemaPayload = {
        type: "object",
        title: "Ticket Workflow Schema",
        properties: {
            name: {
                type: "string",
                title: "name",
                description: "name description"
            },
            states: {
                type: "array",
                items: {
                    type: "object",
                    title: "transition",
                    properties: {
                        name: {
                            type: "string",
                            title: "name",
                            description: "name description"
                        },
                        next: {
                            type: "array",
                            items: {
                                type: "object",
                                properties: {
                                    role: {
                                        type: "string",
                                        title: "role",
                                        description: "user role"
                                    },
                                    state: {
                                        type: "string",
                                        title: "Next state",
                                        description: "The next state of the ticket once the action is applied"
                                    },
                                    action: {
                                        type: "string",
                                        title: "action",
                                        description: "The action required to apply this change"
                                    },
                                    resolutions: {
                                        type: "string",
                                        title: "Next state",
                                        description: "The next state of the ticket once the action is applied"
                                    }
                                },
                                additionalProperties: false
                            },
                            title: "next",
                            description: "List of next possible sstate stransitions"
                        },
                        state: {
                            type: "string",
                            title: "state",
                            description: "current description"
                        }
                    },
                    description: "name description",
                    additionalProperties: false
                },
                title: "",
                description: ""
            },
            illustration: {
                type: "string",
                title: "description",
                description: "description description"
            },
            initial_state: {
                type: "array",
                items: {
                    type: "object",
                    required: [],
                    properties: {
                        name: {
                            type: "string",
                            title: "",
                            description: ""
                        },
                        roles: {
                            type: "array",
                            items: {
                                type: "string",
                                additionalProperties: false
                            },
                            title: "",
                            description: ""
                        }
                    },
                    additionalProperties: false
                },
                description: "state description",
                title: ""
            }
        },
        description: "Ticket Workflow Schema description",
        additionalProperties: false
    };

    return ensureEntryExists(
        { resourceType: ResourceType.schema, spaceName: APPLICATIONS_SPACE, subpath: "/schema", shortname: "workflow" },
        {
            resourceType: ResourceType.schema, spaceName: APPLICATIONS_SPACE, subpath: "schema", shortname: "workflow",
            attributes: {
                is_active: true,
                displayname: { en: "Workflow Schema", ar: "مخطط سير العمل", ku: "" },
                description: { en: "Schema for ticket workflows", ar: "المخطط لسير عمل التذاكر", ku: "" },
                schema_shortname: "meta_schema",
                payload: {
                    content_type: ContentType.json,
                    body: schemaPayload,
                },
            },
        },
        "workflow schema",
        scope
    );
}

/**
 * Create the meta_schema in the schema folder
 * This schema is used to validate template entry payloads
 */
export async function createMetaSchema(scope: DmartScope = DmartScope.managed): Promise<boolean> {
    const metaSchemaPayload = {
        "type": "object",
        "required": ["data", "template"],
        "properties": {
            "data": {
                "type": "object",
                "additionalProperties": true
            },
            "template": {
                "type": "string"
            }
        },
        "additionalProperties": false
    };

    return ensureEntryExists(
        { resourceType: ResourceType.schema, spaceName: APPLICATIONS_SPACE, subpath: "/schema", shortname: "meta_schema" },
        {
            resourceType: ResourceType.schema, spaceName: APPLICATIONS_SPACE, subpath: "schema", shortname: "meta_schema",
            attributes: {
                is_active: true,
                displayname: { en: "Meta Schema", ar: "المخطط التعريفي", ku: "" },
                description: { en: "Schema for validating template entry payloads", ar: "المخطط للتحقق من بيانات إدخالات القوالب", ku: "" },
                payload: {
                    content_type: ContentType.json,
                    body: metaSchemaPayload,
                },
            },
        },
        "meta_schema",
        scope
    );
}

/**
 * Create the templates schema in the schema folder
 * This schema is used to validate template definitions
 */
export async function createTemplatesSchema(scope: DmartScope = DmartScope.managed): Promise<boolean> {
    const templatesSchemaPayload = {
        "type": "object",
        "required": ["title", "content"],
        "properties": {
            "title": {
                "type": "string"
            },
            "content": {
                "type": "string"
            },
            "space_name": {
                "type": "string"
            },
            "schema_shortname": {
                "type": "string"
            }
        },
        "additionalProperties": false
    };

    return ensureEntryExists(
        { resourceType: ResourceType.schema, spaceName: APPLICATIONS_SPACE, subpath: "/schema", shortname: "templates" },
        {
            resourceType: ResourceType.schema, spaceName: APPLICATIONS_SPACE, subpath: "schema", shortname: "templates",
            attributes: {
                is_active: true,
                displayname: { en: "Templates Schema", ar: "مخطط القوالب", ku: "" },
                description: { en: "Schema for validating template definitions", ar: "المخطط للتحقق من تعريفات القوالب", ku: "" },
                payload: {
                    content_type: ContentType.json,
                    body: templatesSchemaPayload,
                },
            },
        },
        "templates schema",
        scope
    );
}

/**
 * Create the templates schema in a specific space's schema folder
 * This schema is used for template-based entries
 */
export async function ensureTemplatesSchemaInSpace(
    spaceName: string,
    scope: DmartScope = DmartScope.managed
): Promise<boolean> {
    const templatesSchemaPayload = {
        "type": "object",
        "required": ["data", "template"],
        "properties": {
            "data": {
                "type": "object",
                "additionalProperties": true
            },
            "template": {
                "type": "string"
            }
        },
        "additionalProperties": false
    };

    return ensureEntryExists(
        { resourceType: ResourceType.schema, spaceName, subpath: "/schema", shortname: "templates" },
        {
            resourceType: ResourceType.schema, spaceName, subpath: "schema", shortname: "templates",
            attributes: {
                is_active: true,
                displayname: { en: "Templates Schema", ar: "مخطط القوالب", ku: "" },
                description: { en: "Schema for validating template entries", ar: "المخطط للتحقق من إدخالات القوالب", ku: "" },
                payload: { content_type: ContentType.json, body: templatesSchemaPayload },
            },
        },
        `templates schema in ${spaceName}`,
        scope
    );
}
