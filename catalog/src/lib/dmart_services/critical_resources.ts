import {
    type ActionRequest,
    type ActionRequestRecord,
    Dmart,
    RequestType,
    ResourceType,
} from "@edraj/tsdmart";
import { log } from "@/lib/logger";
import { MANAGEMENT_SPACE } from "@/lib/constants";
import { errorMessage, errorStatus } from "@/lib/apiError";

/**
 * Resources the app depends on at runtime. They are bootstrapped on demand;
 * a duplicate-create error is expected when the resource already exists and
 * is ignored.
 */
const CRITICAL_RESOURCES: ActionRequest[] = [
    {
        space_name: MANAGEMENT_SPACE,
        request_type: RequestType.create,
        records: [
            {
                resource_type: ResourceType.role,
                subpath: "roles",
                shortname: "catalog_user_role",
                attributes: {
                    is_active: true,
                    displayname: {},
                    description: {},
                    permissions: ["view_users", "view_roles"],
                    payload: {
                        body: {},
                        content_type: "json",
                    },
                },
            },
        ],
    },
];

function isNotFoundError(error: unknown): boolean {
    if (errorStatus(error) === 404) return true;
    const message = errorMessage(error);
    if (message.includes("not found")) return true;
    if (message.includes("does not exist")) return true;
    if (message.includes("not_found")) return true;
    return false;
}

/**
 * Returns the list of critical resource shortnames that are currently
 * missing on the server. Permission errors are treated as "present" so we
 * don't nag users who can't see the resource anyway.
 */
export async function checkCriticalResources(): Promise<{ missing: string[] }> {
    const missing: string[] = [];

    await Promise.all(
        CRITICAL_RESOURCES.flatMap((request) =>
            request.records.map(async (record: ActionRequestRecord) => {
                try {
                    await Dmart.retrieveEntry(
                        {
                            validate_schema: false,
                            resource_type: record.resource_type,
                            space_name: request.space_name,
                            subpath: record.subpath,
                            shortname: record.shortname,
                            retrieve_json_payload: false,
                            retrieve_attachments: false,
                        },
                    );
                } catch (error) {
                    if (isNotFoundError(error)) {
                        missing.push(record.shortname);
                    }
                }
            }),
        ),
    );

    return { missing };
}

/**
 * Attempts to create each critical resource. Errors (including the common
 * "already exists" case) are logged but never re-thrown — callers should not
 * gate flow on this.
 */
export async function ensureCriticalResources(): Promise<void> {
    await Promise.all(
        CRITICAL_RESOURCES.map(async (request) => {
            try {
                await Dmart.request(request);
            } catch (error) {
                log.debug("Critical resource bootstrap skipped:", error);
            }
        }),
    );
}
